using MediaBrowser.Model.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GlobalSubtitleTranslator;

public sealed class GlobalSubtitleTranslatorTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly GlobalSubtitleTranslatorScanner _scanner;
    private readonly ILogger<GlobalSubtitleTranslatorTask> _logger;
    private readonly ILibraryManager _libraryManager;

    public GlobalSubtitleTranslatorTask(ILibraryManager libraryManager, ILogger<GlobalSubtitleTranslatorTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _scanner = new(() => Plugin.Instance?.Configuration ?? new PluginConfiguration(), logger, libraryManager);
    }

    public string Name => "Global Subtitle Translator – selected folders";
    public string Key => "GlobalSubtitleTranslatorScan";
    public string Description => "GST routine: recursively scans selected folders for .mkv files, skips up-to-date .srt files, and translates only missing or outdated subtitles. MKV files are never modified.";
    public string Category => "Global Subtitle Translator";
    public bool IsHidden => true;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (configuration.ScanRoutines.Count > 0)
        {
            var routines = configuration.ScanRoutines.Where(routine => routine.Enabled).ToArray();
            _logger.LogInformation("GST starter {RoutineCount} aktive rutine(r) manuelt", routines.Length);
            for (var i = 0; i < routines.Length; i++)
            {
                var routine = routines[i];
                var index = i;
                var routineProgress = new Progress<double>(value => progress.Report((index + value / 100.0) * 100.0 / routines.Length));
                try
                {
                    await _scanner.ScanAsync(routine.Directories, cancellationToken, routineProgress, routine.Name);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GST-rutinen {RoutineName} feilet; fortsetter med neste rutine", routine.Name);
                }
            }

            progress.Report(100);
            return;
        }

        await _scanner.ScanAsync(configuration.ScanDirectories, cancellationToken, progress, "standard");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

/// <summary>Runs a one-off scan of only the folder or MKV file selected in GST settings.</summary>
public sealed class GlobalSubtitleTranslatorOneOffScanTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly GlobalSubtitleTranslatorScanner _scanner;
    private readonly ILogger<GlobalSubtitleTranslatorOneOffScanTask> _logger;

    public GlobalSubtitleTranslatorOneOffScanTask(ILibraryManager libraryManager, ILogger<GlobalSubtitleTranslatorOneOffScanTask> logger)
    {
        _logger = logger;
        _scanner = new(() => Plugin.Instance?.Configuration ?? new PluginConfiguration(), logger, libraryManager);
    }

    public string Name => "Global Subtitle Translator – scan selected folder or file";
    public string Key => "GlobalSubtitleTranslatorOneOffScan";
    public string Description => "Scans only the manually selected folder recursively or one selected MKV file. Skips MKV files that already have Norwegian subtitles or match excluded filename keywords. Translation runs locally through a GST worker.";
    public string Category => "Global Subtitle Translator";
    public bool IsHidden => true;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var selectedPath = configuration.ManualScanDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            throw new InvalidOperationException("Velg en mappe eller MKV-fil i GST-innstillingene før du starter skanningen.");
        }

        var path = Path.GetFullPath(Path.IsPathRooted(selectedPath)
            ? selectedPath
            : Path.Combine(configuration.MediaRoot, selectedPath));
        if (!Directory.Exists(path) && !(File.Exists(path) && string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FileNotFoundException($"Den valgte mappen eller MKV-filen finnes ikke på Jellyfin-serveren: {path}");
        }

        var selectionName = Directory.Exists(path)
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(path);
        var routineName = $"Engangsskann: {selectionName}";
        _logger.LogInformation("GST starter engangsskann av {SelectedPath}", path);
        await _scanner.ScanAsync([path], cancellationToken, progress, routineName);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
