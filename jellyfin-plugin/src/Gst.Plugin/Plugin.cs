using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.GlobalSubtitleTranslator;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public override string Name => "Global Subtitle Translator";
    public override Guid Id => Guid.Parse("4e3a1a30-7d1b-4f6d-a5b4-3e9f4a3c9d22");

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Plugin> _logger;
    private readonly SubtitleTranslationService _translationService;
    private readonly GlobalSubtitleTranslatorScanner _scanner;
    private readonly CancellationTokenSource _routineCancellation = new();
    private readonly CancellationTokenSource _newFileCancellation = new();
    private readonly CancellationTokenSource _smartScanCancellation = new();
    private readonly CancellationTokenSource _playbackCancellation = new();
    private readonly SemaphoreSlim _newFileGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _queuedNewFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateOnly> _lastRoutineRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _routineScheduler;
    private readonly Task _smartScanScheduler;

    public void RecordTranslationCompleted(string mediaPath)
    {
        Configuration.LastTranslatedFile = mediaPath;
        Configuration.LastTranslatedAtUtc = DateTime.UtcNow;
        Configuration.LastErrorFile = string.Empty;
        Configuration.LastErrorMessage = string.Empty;
        Configuration.LastErrorAtUtc = null;
        SaveConfigurationSafely();
    }

    public void RecordTranslationError(string mediaPath, Exception error)
    {
        Configuration.LastErrorFile = mediaPath;
        Configuration.LastErrorMessage = error.Message.Length > 500 ? error.Message[..500] : error.Message;
        Configuration.LastErrorAtUtc = DateTime.UtcNow;
        SaveConfigurationSafely();
    }

    public void RecordScanStarted(string routineName, int fileCount)
    {
        Configuration.LastScanAtUtc = DateTime.UtcNow;
        Configuration.LastScanSummary = $"{routineName} · {fileCount} MKV-filer";
        SaveConfigurationSafely();
    }

    private void SaveConfigurationSafely()
    {
        try { SaveConfiguration(); }
        catch (Exception ex) { _logger.LogError(ex, "GST kunne ikke lagre plugin-innstillingene; fortsetter uten å stoppe oversettelsesjobben"); }
    }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ISessionManager sessionManager, ILibraryManager libraryManager, ILogger<Plugin> logger) : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _translationService = new(() => Configuration, logger, libraryManager);
        _scanner = new(() => Configuration, logger, libraryManager);
        MigrateLegacyScanConfiguration();
        _sessionManager.PlaybackStart += OnPlaybackStart;
        libraryManager.ItemAdded += OnItemAdded;
        _routineScheduler = Task.Run(() => RunRoutineSchedulerAsync(_routineCancellation.Token));
        _smartScanScheduler = Task.Run(() => RunSmartScanAsync(_smartScanCancellation.Token));
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs args)
    {
        string? path;
        string normalizedPath;
        try
        {
            if (!Configuration.TranslateNewFilesAutomatically) return;
            path = args.Item?.Path;
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || !IsInConfiguredScanDirectory(path)) return;
            normalizedPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "GST kunne ikke behandle filbanen fra Jellyfin-varslet");
            return;
        }

        if (!_queuedNewFiles.TryAdd(normalizedPath, 0))
        {
            return;
        }

        _ = Task.Run(() => TranslateNewFileAsync(normalizedPath, _newFileCancellation.Token), _newFileCancellation.Token);
    }

    private bool IsInConfiguredScanDirectory(string path)
    {
        var directories = GetConfiguredDirectories();
        var fullPath = Path.GetFullPath(path);
        return directories.Any(directory => fullPath.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private string[] GetConfiguredDirectories()
    {
        var configuration = Configuration;
        var directories = configuration.ScanRoutines
            .Where(routine => routine.Enabled)
            .SelectMany(routine => routine.Directories)
            .Concat(configuration.ScanDirectories)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(configuration.MediaRoot, directory)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (directories.Length == 0)
        {
            directories = [Path.GetFullPath(configuration.MediaRoot)];
        }
        return directories;
    }

    private async Task RunSmartScanAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSmartScanLoopAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SmartScan fikk en uventet feil og starter løkken på nytt om ett minutt");
                try { await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task RunSmartScanLoopAsync(CancellationToken cancellationToken)
    {
        var previousSnapshots = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var pendingScans = new Dictionary<string, PendingSmartScan>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var configuration = Configuration;
                string[] directories;
                try
                {
                    directories = GetConfiguredDirectories();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    _logger.LogError(ex, "SmartScan kunne ikke lese de konfigurerte mappene; prøver igjen om ett minutt");
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    continue;
                }
                var configuredSet = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
                var removedBaselines = configuration.SmartScanBaselines.RemoveAll(
                    baseline => !configuredSet.Contains(baseline.DirectoryPath));
                foreach (var removed in previousSnapshots.Keys.Where(path => !configuredSet.Contains(path)).ToArray())
                {
                    previousSnapshots.Remove(removed);
                    pendingScans.Remove(removed);
                }

                var configurationChanged = removedBaselines > 0;
                var directoriesToScan = new List<string>();
                if (configuration.TranslateNewFilesAutomatically)
                {
                    foreach (var directory in directories)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Directory.Exists(directory))
                        {
                            continue;
                        }

                        string[] snapshot;
                        try
                        {
                            snapshot = CaptureMkvSnapshot(directory);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            _logger.LogWarning(ex, "SmartScan kunne ikke lese MKV-filene i {Directory}", directory);
                            continue;
                        }

                        var baseline = configuration.SmartScanBaselines.FirstOrDefault(item =>
                            string.Equals(item.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase));
                        if (baseline is null)
                        {
                            configuration.SmartScanBaselines.Add(new SmartScanDirectoryBaseline
                            {
                                DirectoryPath = directory,
                                MkvCount = snapshot.Length
                            });
                            previousSnapshots[directory] = snapshot;
                            configurationChanged = true;
                            _logger.LogInformation("SmartScan lagret startverdi for {Directory}: {MkvCount} MKV-filer", directory, snapshot.Length);
                            continue;
                        }

                        var hasRetryableFailure = pendingScans.TryGetValue(directory, out var existingPending)
                            && existingPending.RetryCount > 0;
                        if (snapshot.Length < baseline.MkvCount)
                        {
                            _logger.LogInformation("SmartScan oppdaterer filantall for {Directory}: {OldCount} -> {NewCount}", directory, baseline.MkvCount, snapshot.Length);
                            baseline.MkvCount = snapshot.Length;
                            if (!hasRetryableFailure)
                            {
                                pendingScans.Remove(directory);
                            }
                            previousSnapshots[directory] = snapshot;
                            configurationChanged = true;
                            if (!hasRetryableFailure)
                            {
                                continue;
                            }
                        }

                        if (snapshot.Length == baseline.MkvCount && !hasRetryableFailure)
                        {
                            pendingScans.Remove(directory);
                            previousSnapshots[directory] = snapshot;
                            continue;
                        }

                        if (!pendingScans.TryGetValue(directory, out var pending))
                        {
                            pending = new PendingSmartScan(snapshot.Length, snapshot);
                            pendingScans[directory] = pending;
                            _logger.LogInformation("SmartScan oppdaget flere MKV-filer i {Directory}: {OldCount} -> {NewCount}; venter på stabil filstørrelse", directory, baseline.MkvCount, snapshot.Length);
                            previousSnapshots[directory] = snapshot;
                            continue;
                        }

                        if (snapshot.Length < pending.DetectedCount)
                        {
                            pending.DetectedCount = snapshot.Length;
                            pending.LastSnapshot = snapshot;
                            pending.StableIntervals = 0;
                        }
                        else if (SnapshotsEqual(snapshot, pending.LastSnapshot))
                        {
                            pending.StableIntervals++;
                        }
                        else
                        {
                            pending.DetectedCount = snapshot.Length;
                            pending.LastSnapshot = snapshot;
                            pending.StableIntervals = 0;
                        }

                        previousSnapshots[directory] = snapshot;
                        if (pending.StableIntervals >= 1 && DateTime.UtcNow >= pending.RetryAfterUtc)
                        {
                            directoriesToScan.Add(directory);
                        }
                    }
                }

                if (configuration.TranslateNewFilesAutomatically)
                {
                    configuration.SmartScanLastCheckAtUtc = DateTime.UtcNow;
                    configurationChanged = true;
                }

                if (configurationChanged)
                {
                    SaveConfigurationSafely();
                }

                if (directoriesToScan.Count > 0)
                {
                    foreach (var directory in directoriesToScan)
                    {
                        _logger.LogInformation("SmartScan starter skanning av {Directory}", directory);
                        try
                        {
                            var summary = await _scanner.ScanAsync([directory], cancellationToken, routineName: "SmartScan");
                            if (summary.RetryableFailureCount > 0)
                            {
                                if (pendingScans.TryGetValue(directory, out var pending))
                                {
                                    ScheduleSmartScanRetry(directory, pending, summary.RetryableFailureCount);
                                }
                                continue;
                            }

                            if (summary.FailedCount > 0)
                            {
                                _logger.LogWarning("SmartScan fullførte {Directory} med {FailedCount} permanent feil; de forsøkes igjen ved neste rutine eller nye filendring", directory, summary.FailedCount);
                            }

                            var baseline = configuration.SmartScanBaselines.FirstOrDefault(item =>
                                string.Equals(item.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase));
                            if (baseline is not null && previousSnapshots.TryGetValue(directory, out var snapshot))
                            {
                                baseline.MkvCount = snapshot.Length;
                            }
                            pendingScans.Remove(directory);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "SmartScan feilet for {Directory}", directory);
                            if (SubtitleTranslationService.IsRetryableFailure(ex)
                                && pendingScans.TryGetValue(directory, out var pending))
                            {
                                ScheduleSmartScanRetry(directory, pending, 0);
                            }
                            else
                            {
                                var baseline = configuration.SmartScanBaselines.FirstOrDefault(item =>
                                    string.Equals(item.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase));
                                if (baseline is not null && previousSnapshots.TryGetValue(directory, out var snapshot))
                                {
                                    baseline.MkvCount = snapshot.Length;
                                }
                                pendingScans.Remove(directory);
                            }
                        }
                    }

                    SaveConfigurationSafely();
                }

                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string[] CaptureMkvSnapshot(string directory) => Directory
        .EnumerateFiles(directory, "*.mkv", SearchOption.AllDirectories)
        .Select(path =>
        {
            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        })
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool SnapshotsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    private void ScheduleSmartScanRetry(string directory, PendingSmartScan pending, int failedFileCount)
    {
        pending.RetryCount++;
        var delay = pending.RetryCount switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromHours(1)
        };
        pending.RetryAfterUtc = DateTime.UtcNow.Add(delay);
        _logger.LogWarning(
            "SmartScan beholder {Directory} i kø etter {FailureCount} midlertidig feil; prøver igjen om {RetryMinutes} minutt(er)",
            directory,
            failedFileCount,
            delay.TotalMinutes);
    }

    private sealed class PendingSmartScan(int detectedCount, string[] lastSnapshot)
    {
        public int DetectedCount { get; set; } = detectedCount;
        public string[] LastSnapshot { get; set; } = lastSnapshot;
        public int StableIntervals { get; set; }
        public int RetryCount { get; set; }
        public DateTime RetryAfterUtc { get; set; } = DateTime.MinValue;
    }

    private async Task TranslateNewFileAsync(string path, CancellationToken cancellationToken)
    {
        var retryCount = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!Configuration.TranslateNewFilesAutomatically || !File.Exists(path))
                {
                    return;
                }

                var isStable = false;
                try
                {
                    isStable = await WaitForStableFileAsync(path, cancellationToken);
                }
                catch (Exception ex) when (SubtitleTranslationService.IsRetryableFailure(ex))
                {
                    _logger.LogWarning(ex, "GST venter på at den nye MKV-filen skal bli lesbar: {MediaPath}", path);
                }

                if (!isStable)
                {
                    if (!File.Exists(path)) return;
                    retryCount++;
                    var delay = GetRetryDelay(retryCount);
                    _logger.LogWarning("GST venter fortsatt på at den nye MKV-filen skal bli ferdig; prøver igjen om {RetryMinutes} minutt(er): {MediaPath}", delay.TotalMinutes, path);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                SubtitleTranslationService.TranslationResult result;
                await _newFileGate.WaitAsync(cancellationToken);
                try
                {
                    Configuration.IsScanRunning = true;
                    Configuration.CurrentRoutine = "Ny fil automatisk";
                    Configuration.CurrentFile = path;
                    Configuration.CurrentFileIndex = 1;
                    Configuration.CurrentFileCount = 1;
                    Configuration.CurrentStage = "Waiting for the file to finish copying";
                    _logger.LogInformation("GST starter automatisk oversettelse av ny fil: {MediaPath}", path);
                    result = await _translationService.TranslateIfNeededAsync(path, cancellationToken);
                }
                finally
                {
                    _newFileGate.Release();
                    Configuration.IsScanRunning = false;
                    Configuration.CurrentRoutine = string.Empty;
                    Configuration.CurrentFile = string.Empty;
                    Configuration.CurrentFileIndex = 0;
                    Configuration.CurrentFileCount = 0;
                    Configuration.CurrentStage = string.Empty;
                    Configuration.CurrentCueIndex = 0;
                    Configuration.CurrentCueCount = 0;
                }

                if (result != SubtitleTranslationService.TranslationResult.RetryableFailure) return;

                retryCount++;
                var retryDelay = GetRetryDelay(retryCount);
                _logger.LogWarning("GST beholder den nye filen i kø etter en midlertidig feil; prøver igjen om {RetryMinutes} minutt(er): {MediaPath}", retryDelay.TotalMinutes, path);
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordTranslationError(path, ex);
            _logger.LogError(ex, "GST feilet ved automatisk oversettelse av ny fil: {MediaPath}", path);
        }
        finally
        {
            _queuedNewFiles.TryRemove(path, out _);
        }
    }

    private static TimeSpan GetRetryDelay(int retryCount) => retryCount switch
    {
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1)
    };

    private static async Task<bool> WaitForStableFileAsync(string path, CancellationToken cancellationToken)
    {
        long previousLength = -1;
        var stableChecks = 0;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var length = new FileInfo(path).Length;
                if (length > 0 && length == previousLength)
                {
                    stableChecks++;
                    if (stableChecks >= 2)
                    {
                        return true;
                    }
                }
                else
                {
                    stableChecks = 0;
                    previousLength = length;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return false;
    }

    private void MigrateLegacyScanConfiguration()
    {
        if (Configuration.ScanRoutines.Count > 0 || Configuration.ScanDirectories.Count == 0)
        {
            return;
        }

        Configuration.ScanRoutines.Add(new ScanRoutine
        {
            Name = "Movies and TV",
            Directories = [.. Configuration.ScanDirectories],
            Time = "03:00",
            Enabled = true
        });
        SaveConfigurationSafely();
        _logger.LogInformation("GST migrerte tidligere skannemapper til en GST-rutine");
    }

    private async Task RunRoutineSchedulerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunRoutineSchedulerLoopAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GST-rutineplanleggeren fikk en uventet feil og prøver igjen om ett minutt");
                try { await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task RunRoutineSchedulerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                var now = DateTime.Now;
                foreach (var routine in Configuration.ScanRoutines.ToArray())
                {
                    if (!routine.Enabled || !TimeOnly.TryParse(routine.Time, out var scheduledTime))
                    {
                        continue;
                    }

                    if (scheduledTime.Hour != now.Hour || scheduledTime.Minute != now.Minute)
                    {
                        continue;
                    }

                    var today = DateOnly.FromDateTime(now);
                    if (_lastRoutineRuns.TryGetValue(routine.Id, out var lastRun) && lastRun == today)
                    {
                        continue;
                    }
                    _lastRoutineRuns[routine.Id] = today;

                    try
                    {
                        _logger.LogInformation("Starter GST-rutinen {RoutineName} kl. {RoutineTime}", routine.Name, routine.Time);
                        await _scanner.ScanAsync(routine.Directories, cancellationToken, routineName: routine.Name);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "GST-rutinen {RoutineName} feilet", routine.Name);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs args)
    {
        if (!Configuration.TranslateOnPlaybackRequest || string.IsNullOrWhiteSpace(args.Item?.Path)) return;
        _ = Task.Run(async () =>
        {
            try { await _translationService.TranslateIfNeededAsync(args.Item.Path, _playbackCancellation.Token); }
            catch (OperationCanceledException) when (_playbackCancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                RecordTranslationError(args.Item.Path, ex);
                _logger.LogError(ex, "GST feilet ved automatisk oversettelse ved avspillingsstart");
            }
        });
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _libraryManager.ItemAdded -= OnItemAdded;
        _routineCancellation.Cancel();
        _newFileCancellation.Cancel();
        _smartScanCancellation.Cancel();
        _playbackCancellation.Cancel();
        try { _routineScheduler.GetAwaiter().GetResult(); } catch { }
        try { _smartScanScheduler.GetAwaiter().GetResult(); } catch { }
        _newFileGate.Dispose();
        _routineCancellation.Dispose();
        _newFileCancellation.Dispose();
        _smartScanCancellation.Dispose();
        _playbackCancellation.Dispose();
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        // Jellyfin caches embedded plugin pages. Tie the route to the assembly
        // version so every plugin update automatically gets a fresh page URL.
        new PluginPageInfo
        {
            Name = "gstconfig-v" + (typeof(Plugin).Assembly.GetName().Version?.ToString(3).Replace('.', '-') ?? "1-0-0"),
            EmbeddedResourcePath = "Jellyfin.Plugin.GlobalSubtitleTranslator.Configuration.config.html"
        }
    ];
}
