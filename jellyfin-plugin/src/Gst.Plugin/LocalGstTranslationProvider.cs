using Gst.Core;
using System.Diagnostics;
using System.Text.Json;

namespace Jellyfin.Plugin.GlobalSubtitleTranslator;

/// <summary>
/// Sends SRT jobs to the CPU worker through the host/container shared folder.
/// No network translation provider is used.
/// </summary>
public sealed class LocalGstTranslationProvider : ITranslationProvider
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WorkerStartupWait = TimeSpan.FromSeconds(30);
    private readonly Func<PluginConfiguration> _configuration;
    private readonly string _mediaName;

    public LocalGstTranslationProvider(Func<PluginConfiguration> configuration, string mediaName)
    {
        _configuration = configuration;
        _mediaName = Path.GetFileName(mediaName);
    }

    public string Name => _configuration().GpuWorkerEnabled ? "Local GST GPU worker (LAN/offline)" : "Local GST CPU worker (offline)";

    public async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateLanguagePair(request.LanguagePair);
        var configuration = _configuration();
        if (configuration.GpuWorkerEnabled)
        {
            return await GpuGstWorkerClient.TranslateAsync(request, configuration, _mediaName, cancellationToken);
        }

        var queueDirectory = Path.GetFullPath(configuration.LocalWorkerQueueDirectory);
        Directory.CreateDirectory(queueDirectory);
        CleanupOldJobs(queueDirectory);

        var heartbeatPath = Path.Combine(queueDirectory, ".worker.heartbeat");
        var workerWaitUntil = DateTime.UtcNow + WorkerStartupWait;
        while (!IsWorkerReady(heartbeatPath))
        {
            if (DateTime.UtcNow >= workerWaitUntil)
                throw new InvalidOperationException("Local GST CPU-worker kjører ikke på Jellyfin-verten.");
            await Task.Delay(PollInterval, cancellationToken);
        }

        var id = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(queueDirectory, $"{id}.input.srt");
        var outputPath = Path.Combine(queueDirectory, $"{id}.translated.srt");
        var jobPath = Path.Combine(queueDirectory, $"{id}.job.json");
        var pendingPath = jobPath + ".pending";
        var runningPath = Path.Combine(queueDirectory, $"{id}.running.json");
        var resultPath = Path.Combine(queueDirectory, $"{id}.result.json");
        var progressPath = Path.Combine(queueDirectory, $"{id}.progress.json");
        var cancelPath = Path.Combine(queueDirectory, $"{id}.cancel");
        var published = false;

        try
        {
            SrtCodec.Write(inputPath, request.Cues);
            var job = new LocalJobRequest
            {
                Id = id,
                InputFile = Path.GetFileName(inputPath),
                OutputFile = Path.GetFileName(outputPath),
                SourceLanguage = request.LanguagePair.SourceLanguage,
                TargetLanguage = request.LanguagePair.TargetLanguage,
                IncludeParentheticalCaptions = configuration.IncludeParentheticalCaptions,
                MinimumAcceptancePercent = Math.Clamp(configuration.MinimumAcceptancePercent, 0, 100)
            };
            await File.WriteAllTextAsync(
                pendingPath,
                JsonSerializer.Serialize(job, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(pendingPath, jobPath);
            published = true;

            using var timer = new PeriodicTimer(PollInterval);
            var timeout = TimeSpan.FromMinutes(Math.Clamp(configuration.LocalWorkerTimeoutMinutes, 1, 720));
            var started = Stopwatch.StartNew();
            while (!File.Exists(resultPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (started.Elapsed > timeout)
                    throw new TimeoutException($"Lokal CPU-oversettelse overskred tidsgrensen på {timeout.TotalMinutes:0} minutter.");
                if (File.Exists(cancelPath))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                UpdateProgress(configuration, progressPath, request.Cues.Count);
                await timer.WaitForNextTickAsync(cancellationToken);
            }

            using var resultDocument = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken));
            var resultRoot = resultDocument.RootElement;
            var status = resultRoot.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            if (!string.Equals(status, "completed", StringComparison.Ordinal))
            {
                var error = resultRoot.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : "arbeideren returnerte ingen feildetaljer";
                throw new InvalidOperationException($"Lokal oversetter feilet: {error}");
            }
            if (!File.Exists(outputPath))
            {
                throw new FileNotFoundException("Lokal oversetter rapporterte fullføring, men output-SRT mangler.", outputPath);
            }

            var translated = SrtCodec.Read(outputPath).Cues;
            configuration.CurrentCueIndex = request.Cues.Count;
            var outputByTiming = translated
                .GroupBy(cue => (cue.Start, cue.End))
                .ToDictionary(group => group.Key, group => new Queue<SubtitleCue>(group));
            var sourceTimings = request.Cues.Select(cue => (cue.Start, cue.End)).ToHashSet();
            if (outputByTiming.Keys.Any(timing => !sourceTimings.Contains(timing)))
            {
                throw new InvalidDataException("Lokal oversetter endret SRT-tidsstemplene.");
            }

            // Captions removed by the local engine have no corresponding cue in
            // its output. Every retained subtitle keeps its original timing and index.
            var result = new List<SubtitleCue>(translated.Count);
            foreach (var source in request.Cues)
            {
                var key = (source.Start, source.End);
                if (!outputByTiming.TryGetValue(key, out var matches) || matches.Count == 0)
                {
                    continue;
                }
                var output = matches.Dequeue();
                result.Add(source with { Text = output.Text });
            }
            return result;
        }
        catch (OperationCanceledException) when (published)
        {
            try { await File.WriteAllTextAsync(cancelPath, "cancel"); } catch (IOException) { }
            throw;
        }
        catch (TimeoutException) when (published)
        {
            try { await File.WriteAllTextAsync(cancelPath, "timeout"); } catch (IOException) { }
            throw;
        }
        finally
        {
            // If cancellation happened during active inference, leave the job
            // files for the worker to finish its cancellation handshake.
            if (File.Exists(resultPath) || !published)
            {
                TryDelete(inputPath);
                TryDelete(outputPath);
                TryDelete(jobPath);
                TryDelete(pendingPath);
                TryDelete(runningPath);
                TryDelete(resultPath);
                TryDelete(cancelPath);
                TryDelete(Path.Combine(queueDirectory, $"{id}.worker.log"));
                TryDelete(Path.Combine(queueDirectory, $"{id}.diagnostics.json"));
                TryDelete(progressPath);
                TryDelete(Path.Combine(queueDirectory, $"{id}.translated.srt.stats.json"));
            }
        }
    }

    private static void UpdateProgress(PluginConfiguration configuration, string progressPath, int fallbackTotal)
    {
        try
        {
            if (!File.Exists(progressPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(progressPath));
            var root = document.RootElement;
            configuration.CurrentCueIndex = root.TryGetProperty("completed", out var completed) ? completed.GetInt32() : 0;
            configuration.CurrentCueCount = root.TryGetProperty("total", out var total) ? total.GetInt32() : fallbackTotal;
        }
        catch (IOException) { }
        catch (JsonException) { }
    }

    private static bool IsWorkerReady(string heartbeatPath)
    {
        try
        {
            return File.Exists(heartbeatPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeatPath) < TimeSpan.FromSeconds(30);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void ValidateLanguagePair(LanguagePair pair)
    {
        var source = pair.SourceLanguage.Trim().ToLowerInvariant();
        var target = pair.TargetLanguage.Trim().ToLowerInvariant();
        if (source is not ("en" or "eng" or "english" or "auto") ||
            target is not ("nb" or "nob" or "no" or "nor" or "norwegian" or "norwegian bokmål"))
        {
            throw new NotSupportedException("Den installerte lokale modellen støtter foreløpig bare engelsk til norsk bokmål.");
        }
    }

    private static void CleanupOldJobs(string queueDirectory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-2);
        foreach (var file in Directory.EnumerateFiles(queueDirectory, "*.result.json"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                var id = Path.GetFileName(file).Replace(".result.json", string.Empty, StringComparison.Ordinal);
                foreach (var suffix in new[] { ".result.json", ".input.srt", ".translated.srt", ".worker.log", ".diagnostics.json", ".cancel" })
                    TryDelete(Path.Combine(queueDirectory, id + suffix));
                TryDelete(Path.Combine(queueDirectory, id + ".translated.srt.stats.json"));
            }
            catch (IOException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private sealed class LocalJobRequest
    {
        public required string Id { get; init; }
        public required string InputFile { get; init; }
        public required string OutputFile { get; init; }
        public required string SourceLanguage { get; init; }
        public required string TargetLanguage { get; init; }
        public bool IncludeParentheticalCaptions { get; init; }
        public int MinimumAcceptancePercent { get; init; } = 95;
    }
}
