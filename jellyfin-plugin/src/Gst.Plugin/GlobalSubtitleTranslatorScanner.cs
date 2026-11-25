using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GlobalSubtitleTranslator;

public sealed class GlobalSubtitleTranslatorScanner
{
    private static readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SubtitleTranslationService _service;
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;

    public GlobalSubtitleTranslatorScanner(
        Func<PluginConfiguration> configuration,
        ILogger logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _service = new SubtitleTranslationService(configuration, logger, libraryManager);
    }

    public async Task<ScanSummary> ScanAsync(
        IEnumerable<string>? configuredDirectories,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null,
        string routineName = "GST")
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var root = configuration.MediaRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _logger.LogWarning("GST kan ikke skanne mediaroten {MediaRoot}", root);
            progress?.Report(100);
            return new ScanSummary(0, 0, 0);
        }

        var selectedPaths = (configuredDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(root, path))
            .ToArray();
        var scanPaths = selectedPaths.Length == 0 ? [root] : selectedPaths;
        var validDirectories = scanPaths.Where(Directory.Exists).ToArray();
        var validFiles = scanPaths
            .Where(File.Exists)
            .Where(path => string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var path in scanPaths.Where(path => !Directory.Exists(path) && !validFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("GST hopper over valgt sti fordi den ikke finnes eller ikke er en MKV-fil: {ScanPath}", path);
        }

        var discoveredFiles = validFiles
            .Concat(validDirectories.SelectMany(directory => Directory.EnumerateFiles(directory, "*.mkv", SearchOption.AllDirectories)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = discoveredFiles
            .Where(path => !SubtitleTranslationService.HasExcludedFilename(path, configuration.ExcludedFilenameKeywords))
            .Where(path => !SubtitleTranslationService.HasExcludedSubtitle(path, configuration.ExcludedSubtitleLanguages))
            .ToArray();
        var skippedCount = discoveredFiles.Length - files.Length;
        _logger.LogInformation("GST-rutinen {RoutineName} skanner {DirectoryCount} mappe(r) og {SelectedFileCount} valgt(e) fil(er), fant {FileCount} MKV-fil(er) etter filter og hopper over {SkippedCount} på grunn av filnavn eller eksisterende teksting", routineName, validDirectories.Length, validFiles.Length, files.Length, skippedCount);

        await _scanGate.WaitAsync(cancellationToken);
        var translatedAny = false;
        var translatedCount = 0;
        var failedCount = 0;
        var retryableFailureCount = 0;
        configuration.IsScanRunning = true;
        configuration.CurrentScanStartedAtUtc = DateTime.UtcNow;
        configuration.CurrentRoutine = routineName;
        configuration.CurrentFile = string.Empty;
        configuration.CurrentFileIndex = 0;
        configuration.CurrentFileCount = files.Length;
        configuration.CurrentStage = "Preparing scan";
        try
        {
            Plugin.Instance?.RecordScanStarted(routineName, files.Length);
            for (var i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                configuration.CurrentFile = files[i];
                configuration.CurrentFileIndex = i + 1;
                try
                {
                    var result = await _service.TranslateIfNeededAsync(files[i], cancellationToken, queueLibraryScan: false);
                    if (result == SubtitleTranslationService.TranslationResult.Translated)
                    {
                        translatedAny = true;
                        translatedCount++;
                    }
                    else if (result is SubtitleTranslationService.TranslationResult.Failed or SubtitleTranslationService.TranslationResult.RetryableFailure)
                    {
                        failedCount++;
                        if (result == SubtitleTranslationService.TranslationResult.RetryableFailure)
                        {
                            retryableFailureCount++;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    if (SubtitleTranslationService.IsRetryableFailure(ex))
                    {
                        retryableFailureCount++;
                    }

                    try { Plugin.Instance?.RecordTranslationError(files[i], ex); }
                    catch (Exception recordError) { _logger.LogWarning(recordError, "GST kunne ikke lagre feildetaljer for {MediaPath}", files[i]); }
                    _logger.LogError(ex, "GST hopper over den feilede filen og fortsetter skanningen: {MediaPath}", files[i]);
                }
                progress?.Report(files.Length == 0 ? 100 : (i + 1) * 100.0 / files.Length);
            }

            if (translatedAny)
            {
                _libraryManager.QueueLibraryScan();
                _logger.LogInformation("GST-rutinen {RoutineName} er ferdig og ber Jellyfin oppdatere biblioteket", routineName);
            }

            _logger.LogInformation("GST-rutinen {RoutineName} fullførte: oversatt {TranslatedCount}, feil {FailedCount}, midlertidige feil {RetryableFailureCount}", routineName, translatedCount, failedCount, retryableFailureCount);
        }
        finally
        {
            configuration.IsScanRunning = false;
            configuration.CurrentScanStartedAtUtc = null;
            configuration.CurrentRoutine = string.Empty;
            configuration.CurrentFile = string.Empty;
            configuration.CurrentFileIndex = 0;
            configuration.CurrentFileCount = 0;
            configuration.CurrentStage = string.Empty;
            configuration.CurrentCueIndex = 0;
            configuration.CurrentCueCount = 0;
            _scanGate.Release();
        }

        progress?.Report(100);
        return new ScanSummary(failedCount, retryableFailureCount, translatedCount);
    }
}

public sealed record ScanSummary(int FailedCount, int RetryableFailureCount, int TranslatedCount);
