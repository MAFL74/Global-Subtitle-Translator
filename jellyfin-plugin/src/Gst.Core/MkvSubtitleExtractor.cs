using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace Gst.Core;

public sealed record EmbeddedSubtitleTrack(int StreamIndex, string Language, string? Title, string? CodecName = null);

public sealed class MkvSubtitleExtractor
{
    private readonly TimeSpan _processTimeout;

    public MkvSubtitleExtractor(
        string ffprobePath = "ffprobe",
        string ffmpegPath = "ffmpeg",
        string? mkvExtractPath = null,
        TimeSpan? processTimeout = null)
    {
        FfprobePath = ResolveTool(ffprobePath);
        FfmpegPath = ResolveTool(ffmpegPath);
        MkvExtractPath = mkvExtractPath ?? (OperatingSystem.IsWindows() && File.Exists(@"C:\Program Files\MKVToolNix\mkvextract.exe")
            ? @"C:\Program Files\MKVToolNix\mkvextract.exe" : "mkvextract");
        _processTimeout = processTimeout ?? TimeSpan.FromMinutes(10);
        if (_processTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(processTimeout));
    }

    public string FfprobePath { get; }
    public string FfmpegPath { get; }
    public string MkvExtractPath { get; }

    public async Task<IReadOnlyList<EmbeddedSubtitleTrack>> GetTracksAsync(string mediaPath, CancellationToken token = default)
    {
        var result = await RunAsync(FfprobePath, new[]
        {
            "-v", "error", "-select_streams", "s",
            "-show_entries", "stream=index,codec_name:stream_tags=language,title",
            "-of", "json", mediaPath
        }, token, _processTimeout);
        if (result.ExitCode != 0) throw new IOException($"ffprobe feilet med kode {result.ExitCode}: {result.Error}");

        var tracks = new List<EmbeddedSubtitleTrack>();
        using var document = JsonDocument.Parse(result.Output);
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (!stream.TryGetProperty("index", out var indexValue) || !indexValue.TryGetInt32(out var index)) continue;
            var tags = stream.TryGetProperty("tags", out var tagValue) ? tagValue : default;
            tracks.Add(new EmbeddedSubtitleTrack(index,
                tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("language", out var languageValue)
                    ? languageValue.GetString() ?? "und" : "und",
                tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("title", out var titleValue)
                    ? titleValue.GetString() : null,
                stream.TryGetProperty("codec_name", out var codecValue) ? codecValue.GetString() : null));
        }
        return tracks;
    }

    public async Task ExtractAsync(string mediaPath, EmbeddedSubtitleTrack track, string outputPath, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        try
        {
            var result = await RunAsync(MkvExtractPath, new[]
            {
                mediaPath, "tracks", $"{track.StreamIndex}:{outputPath}"
            }, token, _processTimeout);
            if (result.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            // The official Jellyfin image normally has ffmpeg, but not MKVToolNix.
        }

        var fallback = await RunAsync(FfmpegPath, new[]
        {
            "-hide_banner", "-loglevel", "error", "-i", mediaPath,
            "-map", $"0:{track.StreamIndex}", "-c:s", "srt", "-y", outputPath
        }, token, _processTimeout);
        if (fallback.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new IOException(string.IsNullOrWhiteSpace(fallback.Error) ? "Kunne ikke hente ut undertekstsporet." : fallback.Error);
    }

    public static EmbeddedSubtitleTrack? ChooseTrack(IEnumerable<EmbeddedSubtitleTrack> tracks, string sourceLanguage)
    {
        var candidates = tracks.Where(IsTextTrack);
        if (sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return candidates.OrderBy(t => (t.Title ?? string.Empty).Contains("sdh", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(t => t.Language.Equals("und", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault();
        }
        return candidates.Where(t => t.Language.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase)
                              || (sourceLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) && t.Language.Equals("eng", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => (t.Title ?? string.Empty).Contains("sdh", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
    }

    private static bool IsTextTrack(EmbeddedSubtitleTrack track) => track.CodecName is null
        or "subrip" or "ass" or "ssa" or "webvtt" or "mov_text" or "text" or "srt";

    private static string ResolveTool(string tool) => tool is "ffmpeg" or "ffprobe"
        && !File.Exists(tool)
        ? (File.Exists($"/usr/lib/jellyfin-ffmpeg/{tool}") ? $"/usr/lib/jellyfin-ffmpeg/{tool}" : tool)
        : tool;

    private static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken token, TimeSpan processTimeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(processTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }

            using (var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try { await process.WaitForExitAsync(cleanupTimeout.Token); }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
            }

            if (token.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} brukte mer enn {processTimeout.TotalMinutes:0.#} minutter.");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
