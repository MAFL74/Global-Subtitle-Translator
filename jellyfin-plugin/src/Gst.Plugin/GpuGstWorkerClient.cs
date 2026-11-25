using Gst.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.GlobalSubtitleTranslator;

/// <summary>Uses an optional CUDA worker on the trusted LAN. Only SRT data is sent.</summary>
internal static class GpuGstWorkerClient
{
    private const int WorkerPort = 8765;
    private static readonly TimeSpan JobSubmitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan JobPollTimeout = TimeSpan.FromSeconds(15);
    private const int MaxConsecutivePollFailures = 3;
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        TranslationRequest request,
        PluginConfiguration configuration,
        string mediaName,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(configuration.GpuWorkerIp?.Trim(), out var address) || !IsPrivateIpv4(address))
            throw new InvalidOperationException("The GPU worker address must be a private IPv4 address, for example 192.168.1.50.");

        var baseUri = new UriBuilder(Uri.UriSchemeHttp, address.ToString(), WorkerPort).Uri;
        configuration.CurrentStage = "Connecting to GPU worker";
        using (var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            healthTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var healthResponse = await Client.GetAsync(new Uri(baseUri, "health"), healthTimeout.Token);
                healthResponse.EnsureSuccessStatusCode();
                using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync(healthTimeout.Token));
                var root = health.RootElement;
                if (!ReadBool(root, "cuda_available") || !ReadBool(root, "model_available"))
                    throw new InvalidOperationException("GPU-worker svarer, men CUDA eller den lokale MADLAD-modellen er ikke klar.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Får ikke kontakt med GPU-worker på {address}:{WorkerPort}. Kontroller at worker-tjenesten kjører.");
            }
            catch (HttpRequestException error)
            {
                throw new InvalidOperationException($"Får ikke kontakt med GPU-worker på {address}:{WorkerPort}: {error.Message}", error);
            }
        }

        var id = Guid.NewGuid().ToString("N");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "gst-gpu", id);
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "source.srt");
        var outputPath = Path.Combine(temporaryDirectory, "translated.srt");
        var jobUri = new Uri(baseUri, "v1/jobs/" + id);
        var submissionAttempted = false;
        try
        {
            SrtCodec.Write(inputPath, request.Cues);
            var payload = new GpuJobRequest
            {
                Id = id,
                SourceLanguage = request.LanguagePair.SourceLanguage,
                TargetLanguage = request.LanguagePair.TargetLanguage,
                MediaName = Path.GetFileName(mediaName),
                IncludeParentheticalCaptions = configuration.IncludeParentheticalCaptions,
                MinimumAcceptancePercent = Math.Clamp(configuration.MinimumAcceptancePercent, 0, 100),
                InputSrt = await File.ReadAllTextAsync(inputPath, cancellationToken)
            };
            configuration.CurrentCueIndex = 0;
            configuration.CurrentCueCount = request.Cues.Count;
            configuration.CurrentStage = "Sending subtitles to GPU worker";
            submissionAttempted = true;
            using (var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"))
            using (var response = await SendWithTimeoutAsync(
                token => Client.PostAsync(new Uri(baseUri, "v1/jobs"), content, token),
                cancellationToken,
                JobSubmitTimeout,
                "på å ta imot oversettelsesjobben"))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"GPU-worker avviste jobben ({(int)response.StatusCode}): {Short(detail)}",
                        null,
                        response.StatusCode);
                }
            }

            var timeout = TimeSpan.FromMinutes(Math.Clamp(configuration.LocalWorkerTimeoutMinutes, 1, 720));
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var consecutivePollFailures = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (started.Elapsed > timeout)
                    throw new TimeoutException($"GPU-oversettelsen overskred tidsgrensen på {timeout.TotalMinutes:0} minutter.");

                HttpResponseMessage response;
                try
                {
                    response = await SendWithTimeoutAsync(
                        token => Client.GetAsync(jobUri, token),
                        cancellationToken,
                        JobPollTimeout,
                        "på jobbstatus");
                    consecutivePollFailures = 0;
                }
                catch (TimeoutException) when (++consecutivePollFailures < MaxConsecutivePollFailures)
                {
                    configuration.CurrentStage = "Reconnecting to GPU worker";
                    await timer.WaitForNextTickAsync(cancellationToken);
                    continue;
                }
                catch (HttpRequestException) when (++consecutivePollFailures < MaxConsecutivePollFailures)
                {
                    configuration.CurrentStage = "Reconnecting to GPU worker";
                    await timer.WaitForNextTickAsync(cancellationToken);
                    continue;
                }

                using (response)
                {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException("GPU-worker mistet oversettelsesjobben.");
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var result = document.RootElement;
                var status = ReadString(result, "status");
                if (status is not ("queued" or "running" or "completed" or "failed" or "cancelled"))
                    throw new InvalidDataException($"GPU-worker returnerte ukjent jobbstatus: {Short(status)}.");
                configuration.CurrentCueIndex = ReadInt(result, "completed");
                configuration.CurrentCueCount = ReadInt(result, "total", request.Cues.Count);
                configuration.CurrentStage = "Translating on GPU";

                if (status == "failed")
                    throw new InvalidOperationException("GPU-worker feilet: " + Short(ReadString(result, "error")));
                if (status == "cancelled")
                    throw new OperationCanceledException("GPU-jobben ble avbrutt.", cancellationToken);
                if (status == "completed")
                {
                    var translatedSrt = ReadString(result, "output_srt");
                    if (string.IsNullOrWhiteSpace(translatedSrt))
                        throw new InvalidDataException("GPU-worker fullførte uten å returnere en SRT-fil.");
                    await File.WriteAllTextAsync(outputPath, translatedSrt, new UTF8Encoding(false), cancellationToken);
                    configuration.CurrentCueIndex = request.Cues.Count;
                    return MatchCues(request.Cues, SrtCodec.Read(outputPath).Cues);
                }

                await timer.WaitForNextTickAsync(cancellationToken);
                }
            }
        }
        finally
        {
            // Even if POST timed out after the worker accepted it, delete the
            // known job id so an orphan cannot keep occupying the GPU queue.
            if (submissionAttempted) await DeleteRemoteJobAsync(jobUri);
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
        }
    }

    private static async Task<HttpResponseMessage> SendWithTimeoutAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        string operation)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        try
        {
            return await send(requestTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"GPU-worker svarte ikke {operation} innen {timeout.TotalSeconds:0} sekunder.");
        }
    }

    private static IReadOnlyList<SubtitleCue> MatchCues(IReadOnlyList<SubtitleCue> source, IReadOnlyList<SubtitleCue> translated)
    {
        var outputByTiming = translated
            .GroupBy(cue => (cue.Start, cue.End))
            .ToDictionary(group => group.Key, group => new Queue<SubtitleCue>(group));
        var sourceTimings = source.Select(cue => (cue.Start, cue.End)).ToHashSet();
        if (outputByTiming.Keys.Any(timing => !sourceTimings.Contains(timing)))
            throw new InvalidDataException("GPU-worker endret SRT-tidsstemplene.");

        var result = new List<SubtitleCue>(translated.Count);
        foreach (var original in source)
        {
            var key = (original.Start, original.End);
            if (!outputByTiming.TryGetValue(key, out var matches) || matches.Count == 0) continue;
            result.Add(original with { Text = matches.Dequeue().Text });
        }
        return result;
    }

    private static async Task DeleteRemoteJobAsync(Uri jobUri)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await Client.DeleteAsync(jobUri, timeout.Token);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int ReadInt(JsonElement root, string name, int fallback = 0)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Short(string value) => value.Length > 4000 ? value[..4000] : value;

    private sealed class GpuJobRequest
    {
        public string Id { get; set; } = string.Empty;
        public string SourceLanguage { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public string MediaName { get; set; } = string.Empty;
        public bool IncludeParentheticalCaptions { get; set; }
        public int MinimumAcceptancePercent { get; set; } = 95;
        public string InputSrt { get; set; } = string.Empty;
    }
}
