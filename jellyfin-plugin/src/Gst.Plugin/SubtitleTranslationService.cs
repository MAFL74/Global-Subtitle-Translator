using Gst.Core;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.GlobalSubtitleTranslator;

public sealed class SubtitleTranslationService
{
    private static readonly SemaphoreSlim TranslationGate = new(1, 1);
    private readonly Func<PluginConfiguration> _configuration;
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;

    public SubtitleTranslationService(Func<PluginConfiguration> configuration, ILogger logger, ILibraryManager libraryManager)
    {
        _configuration = configuration;
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public async Task<TranslationResult> TranslateIfNeededAsync(string mediaPath, CancellationToken cancellationToken, bool queueLibraryScan = true)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !mediaPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || !File.Exists(mediaPath))
            return TranslationResult.Skipped;

        var configuration = _configuration();
        if (HasExcludedFilename(mediaPath, configuration.ExcludedFilenameKeywords))
        {
            _logger.LogInformation("GST hopper over {MediaPath}: filnavnet inneholder et ekskludert ord", mediaPath);
            return TranslationResult.AlreadyCurrent;
        }
        if (HasExcludedSubtitle(mediaPath, configuration.ExcludedSubtitleLanguages))
        {
            _logger.LogInformation("GST hopper over {MediaPath}: en ekskludert undertekst finnes allerede", mediaPath);
            return TranslationResult.AlreadyCurrent;
        }

        var profiles = configuration.Profiles.Where(profile => profile.Enabled).ToArray();
        if (profiles.Length == 0) return TranslationResult.Skipped;

        await TranslationGate.WaitAsync(cancellationToken);
        var translationGateAcquired = false;
        try
        {
            translationGateAcquired = true;
            var changed = false;
            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = GetOutputPath(mediaPath, profile.TargetLanguage);
                if (IsCurrent(outputPath, mediaPath))
                {
                    _logger.LogDebug("GST hopper over oppdatert teksting: {OutputPath}", outputPath);
                    continue;
                }

                var temporaryDirectory = Path.Combine(Path.GetTempPath(), "gst", Guid.NewGuid().ToString("N"));
                var extractedPath = Path.Combine(temporaryDirectory, "source.srt");
                var temporaryOutput = outputPath + $".gst-{Guid.NewGuid():N}.tmp";
                try
                {
                    configuration.CurrentStage = "Reading subtitle track";
                    Directory.CreateDirectory(temporaryDirectory);
                    var extractor = new MkvSubtitleExtractor();
                    var tracks = await extractor.GetTracksAsync(mediaPath, cancellationToken);
                    var track = MkvSubtitleExtractor.ChooseTrack(tracks, profile.SourceLanguage);
                    if (track is null)
                    {
                        _logger.LogInformation("GST fant ingen lesbar teksting i {MediaPath} for kilde {SourceLanguage}", mediaPath, profile.SourceLanguage);
                        continue;
                    }

                    await extractor.ExtractAsync(mediaPath, track, extractedPath, cancellationToken);
                    configuration.CurrentStage = "Reading SRT subtitles";
                    var source = SrtCodec.Read(extractedPath);
                    if (source.Cues.Count == 0)
                    {
                        _logger.LogInformation("GST fant ingen dialoglinjer i {MediaPath}", mediaPath);
                        continue;
                    }

                    _logger.LogInformation("GST oversetter {CueCount} linjer: {MediaPath} ({SourceLanguage} -> {TargetLanguage})", source.Cues.Count, mediaPath, profile.SourceLanguage, profile.TargetLanguage);
                    var provider = new LocalGstTranslationProvider(_configuration, Path.GetFileName(mediaPath));
                    _logger.LogInformation("GST sender oversettelsen til {Worker} (offline)", provider.Name);
                    configuration.CurrentCueIndex = 0;
                    configuration.CurrentCueCount = source.Cues.Count;
                    configuration.CurrentStage = "Translating locally";
                    var translated = await provider.TranslateAsync(
                        new TranslationRequest(source.Cues, new LanguagePair(profile.SourceLanguage, profile.TargetLanguage)),
                        cancellationToken);
                    if (translated.Count == 0)
                    {
                        _logger.LogInformation("GST fikk ingen gjenværende tekst etter lokal behandling i {MediaPath}; output ble ikke skrevet", mediaPath);
                        continue;
                    }
                    SrtCodec.Write(temporaryOutput, translated);
                    configuration.CurrentStage = "Writing translated SRT";
                    File.Move(temporaryOutput, outputPath, true);
                    changed = true;
                    Plugin.Instance?.RecordTranslationCompleted(mediaPath);
                    _logger.LogInformation("GST skrev ferdig teksting: {OutputPath}", outputPath);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Plugin.Instance?.RecordTranslationError(mediaPath, ex);
                    _logger.LogError(ex, "GST feilet for {MediaPath}", mediaPath);
                    TryDelete(temporaryOutput);
                    return IsRetryableFailure(ex) ? TranslationResult.RetryableFailure : TranslationResult.Failed;
                }
                finally
                {
                    TryDelete(temporaryOutput);
                    try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
                }
            }

            if (changed && queueLibraryScan) _libraryManager.QueueLibraryScan();
            return changed ? TranslationResult.Translated : TranslationResult.AlreadyCurrent;
        }
        finally
        {
            if (translationGateAcquired) TranslationGate.Release();
        }
    }

    public static string GetOutputPath(string mediaPath, string targetLanguage)
        => SubtitleFileNaming.GetTranslatedPath(mediaPath, targetLanguage);

    public static bool HasExcludedSubtitle(string mediaPath, IEnumerable<string>? excludedLanguageTags)
    {
        var tags = new[] { "nb", "no" }
            .Concat(excludedLanguageTags ?? [])
            .Select(tag => (tag ?? string.Empty).Trim())
            .Where(tag => Regex.IsMatch(tag, "^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*$"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tags.Length == 0) return false;

        // GST marks Bokmål as .no.AI.srt; accept common and legacy suffixes too.
        var outputPath = GetOutputPath(mediaPath, "nb");
        const string outputSuffix = ".no.AI.srt";
        if (!outputPath.EndsWith(outputSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        var basePath = outputPath[..^outputSuffix.Length];
        return tags.Any(tag => File.Exists(basePath + "." + tag + ".srt")
            || File.Exists(basePath + "." + tag + ".AI-RIP.srt"));
    }

    public static bool HasExcludedFilename(string mediaPath, IEnumerable<string>? excludedKeywords)
    {
        var fileName = Path.GetFileNameWithoutExtension(mediaPath);
        var keywords = new[] { "sample" }
            .Concat(excludedKeywords ?? [])
            .Select(keyword => (keyword ?? string.Empty).Trim())
            .Where(keyword => keyword.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return keywords.Any(keyword => fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCurrent(string outputPath, string mediaPath) =>
        File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 &&
        File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(mediaPath);

    public static bool IsRetryableFailure(Exception error)
    {
        if (error is InvalidDataException)
        {
            return false;
        }

        if (error is TimeoutException or IOException or SocketException or OperationCanceledException
            or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }

        if (error is HttpRequestException httpError)
        {
            return !httpError.StatusCode.HasValue
                || httpError.StatusCode.Value == HttpStatusCode.RequestTimeout
                || (int)httpError.StatusCode.Value == 425
                || httpError.StatusCode.Value == HttpStatusCode.TooManyRequests
                || (int)httpError.StatusCode.Value >= 500;
        }

        if (error.InnerException is not null)
        {
            return IsRetryableFailure(error.InnerException);
        }

        if (error is InvalidOperationException)
        {
            return error.Message.Contains("Får ikke kontakt med GPU-worker", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("GPU-worker mistet oversettelsesjobben", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("CUDA eller den lokale MADLAD-modellen er ikke klar", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("Local GST CPU-worker kjører ikke", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void TryDelete(string path) { try { if (!string.IsNullOrEmpty(path)) File.Delete(path); } catch { } }

    public enum TranslationResult { Skipped, AlreadyCurrent, Translated, Failed, RetryableFailure }
}
