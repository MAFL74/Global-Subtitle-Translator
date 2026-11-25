using Jellyfin.Plugin.GlobalSubtitleTranslator;
using Gst.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Http;

if (Environment.GetEnvironmentVariable("GST_RESILIENCE_HANG_CHILD") == "1")
{
    var pidPath = Environment.GetEnvironmentVariable("GST_RESILIENCE_CHILD_PID_FILE");
    if (!string.IsNullOrWhiteSpace(pidPath)) File.WriteAllText(pidPath, Environment.ProcessId.ToString());
    Thread.Sleep(TimeSpan.FromMinutes(5));
    return;
}

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

var scratch = Path.Combine(Path.GetTempPath(), "gst-resilience-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var media = Path.Combine(scratch, "Speak.No.Evil.mkv");
    File.WriteAllText(SubtitleTranslationService.GetOutputPath(media, "nb").Replace(".AI", ""), "subtitle");
    Check(SubtitleTranslationService.HasExcludedSubtitle(media, ["nb", "no", "nb", "no"]), "existing .no.srt skips translation and duplicate language tags are harmless");

    File.Delete(SubtitleTranslationService.GetOutputPath(media, "nb").Replace(".AI-RIP", ""));
    File.WriteAllText(SubtitleTranslationService.GetOutputPath(media, "nb"), "subtitle");
    Check(SubtitleTranslationService.HasExcludedSubtitle(media, []), "existing GST .no.AI.srt is recognized by default");

    var customSubs = Path.Combine(scratch, "episode.mkv");
    foreach (var language in new[] { "en", "dk", "se", "sv" })
    {
        File.WriteAllText(Path.Combine(scratch, $"episode.{language}.srt"), "subtitle");
        Check(SubtitleTranslationService.HasExcludedSubtitle(customSubs, ["nb", "no", language.ToUpperInvariant(), language]), $"manually excluded .{language}.srt language is recognized");
        File.Delete(Path.Combine(scratch, $"episode.{language}.srt"));
    }
    Check(SubtitleTranslationService.HasExcludedFilename(Path.Combine(scratch, "Movie.SAMPLE.mkv"), ["sample"]), "excluded filename keywords are case insensitive");
    Check(!SubtitleTranslationService.HasExcludedFilename(Path.Combine(scratch, "Movie.mkv"), ["sample"]), "ordinary filename is not filtered");

    Check(SubtitleTranslationService.IsRetryableFailure(new TimeoutException()), "timeouts are retryable");
    Check(SubtitleTranslationService.IsRetryableFailure(new HttpRequestException("worker busy", null, HttpStatusCode.TooManyRequests)), "HTTP 429 is retryable");
    Check(SubtitleTranslationService.IsRetryableFailure(new HttpRequestException("worker unavailable", null, HttpStatusCode.ServiceUnavailable)), "HTTP 503 is retryable");
    Check(!SubtitleTranslationService.IsRetryableFailure(new HttpRequestException("bad request", null, HttpStatusCode.BadRequest)), "HTTP 400 is not retried forever");
    Check(!SubtitleTranslationService.IsRetryableFailure(new InvalidDataException("invalid subtitle output")), "invalid subtitle output is not retried forever");

    await CheckExtractorTimeoutKillsProcessAsync(scratch, TimeSpan.FromSeconds(1), cancel: false);
    await CheckExtractorTimeoutKillsProcessAsync(scratch, TimeSpan.FromMinutes(1), cancel: true);
    passed += 2;
}
finally
{
    try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
}

Console.WriteLine($"All {passed} GST resilience checks passed.");

static async Task CheckExtractorTimeoutKillsProcessAsync(string scratch, TimeSpan timeout, bool cancel)
{
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the test executable.");
    var pidPath = Path.Combine(scratch, "child-" + Guid.NewGuid().ToString("N") + ".pid");
    Environment.SetEnvironmentVariable("GST_RESILIENCE_HANG_CHILD", "1");
    Environment.SetEnvironmentVariable("GST_RESILIENCE_CHILD_PID_FILE", pidPath);
    try
    {
        var extractor = new MkvSubtitleExtractor(exe, "ffmpeg", processTimeout: timeout);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(TimeSpan.FromSeconds(1));

        var thrown = false;
        try
        {
            await extractor.GetTracksAsync(Path.Combine(scratch, "unused.mkv"), cancellation.Token);
        }
        catch (TimeoutException) when (!cancel)
        {
            thrown = true;
        }
        catch (OperationCanceledException) when (cancel)
        {
            thrown = true;
        }
        if (!thrown) throw new InvalidOperationException(cancel ? "Extractor did not honor cancellation." : "Extractor did not time out.");

        var waitUntil = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(pidPath) && DateTime.UtcNow < waitUntil) await Task.Delay(50);
        if (!File.Exists(pidPath)) throw new InvalidOperationException("Hanging child process did not start.");
        var childPid = int.Parse(await File.ReadAllTextAsync(pidPath));
        try
        {
            using var child = Process.GetProcessById(childPid);
            if (!child.WaitForExit(5000)) throw new InvalidOperationException($"Extractor left child process {childPid} running.");
        }
        catch (ArgumentException)
        {
            // The process exited before we could obtain its handle.
        }

        Console.WriteLine(cancel
            ? "PASS: extractor cancellation kills its child process"
            : "PASS: extractor timeout kills its child process");
    }
    finally
    {
        Environment.SetEnvironmentVariable("GST_RESILIENCE_HANG_CHILD", null);
        Environment.SetEnvironmentVariable("GST_RESILIENCE_CHILD_PID_FILE", null);
    }
}
