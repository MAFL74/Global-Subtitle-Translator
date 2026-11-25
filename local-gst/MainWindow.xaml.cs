using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace Local_GST;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> LanguageSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "eng", "english", "no", "nor", "nob", "nno", "norwegian", "nb", "nn",
        "sv", "swe", "da", "dan", "de", "deu", "ger", "fr", "fra", "fre", "es", "spa",
        "it", "ita", "fi", "fin"
    };

    private readonly string _scriptPath = Path.Combine(AppContext.BaseDirectory, "local_gst.py");
    private readonly Dictionary<string, LiveCueRow> _liveRowsByKey = new(StringComparer.Ordinal);
    private ObservableCollection<LiveCueRow> LiveRows { get; } = new();
    private string? _lastAutomaticOutput;
    private string? _tracksForInput;
    private Process? _activeProcess;
    private CancellationTokenSource? _runCancellation;
    private LocalGstSettings _settings = new();
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        ModelPathTextBox.Text = Path.Combine(FindProjectDirectory(), "models", "opus-mt-eng-nor");
        PythonPathTextBox.Text = FindPython() ?? "python";
        TrackComboBox.Items.Add(TrackChoice.Automatic);
        TrackComboBox.SelectedIndex = 0;
        LiveTextGrid.ItemsSource = LiveRows;
        _initialized = true;
        UpdateCurrentFileDisplay(InputPathTextBox.Text);
        var theme = LoadThemePreference();
        ThemeComboBox.SelectedIndex = theme == "dark" ? 1 : 0;
        ApplyTheme(theme);
        _settings = LocalGstSettings.Load();
        AppendLog("Klar. Lokal uttrekking/oversettelse prøves først; ved manglende MKV-tekstspor kan eksterne SRT-kilder søkes.");
        AppendLog("Velg en SRT-fil eller MKV-fil. MKV-spor hentes med ffprobe/mkvextract/ffmpeg.");
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Velg undertekstfil eller MKV",
            Filter = "Undertekster og video (*.srt;*.mkv)|*.srt;*.mkv|SRT (*.srt)|*.srt|MKV (*.mkv)|*.mkv|Alle filer (*.*)|*.*",
            CheckFileExists = true
        };
        if (!string.IsNullOrWhiteSpace(InputPathTextBox.Text))
        {
            var directory = Path.GetDirectoryName(InputPathTextBox.Text);
            if (directory is not null && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) != true) return;
        InputPathTextBox.Text = dialog.FileName;
        SetAutomaticOutput(dialog.FileName);
        if (IsMkv(dialog.FileName)) _ = LoadTracksAsync();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Lagre oversatt undertekst",
            Filter = "SubRip-undertekst (*.srt)|*.srt",
            DefaultExt = ".srt",
            AddExtension = true,
            FileName = Path.GetFileName(OutputPathTextBox.Text)
        };
        var directory = Path.GetDirectoryName(OutputPathTextBox.Text);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(this) == true) OutputPathTextBox.Text = dialog.FileName;
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Velg lokal mappe med oversettelsesmodellen",
            Multiselect = false
        };
        if (Directory.Exists(ModelPathTextBox.Text)) dialog.InitialDirectory = ModelPathTextBox.Text;
        if (dialog.ShowDialog(this) == true) ModelPathTextBox.Text = dialog.FolderName;
    }

    private void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Velg Python 3.10+",
            Filter = "Python (python.exe)|python.exe|Alle programmer (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) PythonPathTextBox.Text = dialog.FileName;
    }

    private void InputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdateInputMode();
        var input = InputPathTextBox.Text.Trim();
        UpdateCurrentFileDisplay(input);
        if (!string.IsNullOrWhiteSpace(input) &&
            (string.IsNullOrWhiteSpace(OutputPathTextBox.Text) ||
             string.Equals(OutputPathTextBox.Text, _lastAutomaticOutput, StringComparison.OrdinalIgnoreCase)))
        {
            SetAutomaticOutput(input);
        }
    }

    private void UpdateCurrentFileDisplay(string path)
    {
        var value = path.Trim();
        CurrentFileTextBlock.Text = string.IsNullOrWhiteSpace(value)
            ? "Ingen fil valgt"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(value));
        CurrentFileTextBlock.ToolTip = string.IsNullOrWhiteSpace(value) ? "Filen som behandles" : value;
    }

    private void SourceLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && IsMkv(InputPathTextBox.Text) && TrackComboBox.Items.Count > 1)
            SelectPreferredTrack();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Tag?.ToString() == "dark" ? "dark" : "light";
        ApplyTheme(theme);
        try
        {
            var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalGST");
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(Path.Combine(settingsDirectory, "theme.txt"), theme);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SubtitleSettingsWindow.Show(this, _settings) == true)
        {
            _settings = LocalGstSettings.Load();
            AppendLog("Innstillingene for undertekstleverandører og sync-sjekk ble lagret.");
        }
    }

    private string LoadThemePreference()
    {
        try
        {
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalGST", "theme.txt");
            return File.Exists(settingsPath) && File.ReadAllText(settingsPath).Trim().Equals("dark", StringComparison.OrdinalIgnoreCase)
                ? "dark"
                : "light";
        }
        catch (IOException) { return "light"; }
        catch (UnauthorizedAccessException) { return "light"; }
    }

    private void ApplyTheme(string theme)
    {
        var dark = theme == "dark";
        SetThemeBrush("WindowBrush", dark ? "#171A21" : "#F3F5F8");
        SetThemeBrush("CardBrush", dark ? "#222731" : "#FFFFFF");
        SetThemeBrush("FieldBrush", dark ? "#2B313D" : "#FFFFFF");
        SetThemeBrush("SurfaceBrush", dark ? "#1C212A" : "#FAFBFD");
        SetThemeBrush("BorderBrushTheme", dark ? "#3A4351" : "#E1E6ED");
        SetThemeBrush("TextBrush", dark ? "#E8ECF2" : "#202938");
        SetThemeBrush("MutedBrush", dark ? "#AAB4C3" : "#647084");
        SetThemeBrush("OutputBrush", dark ? "#8ED5A9" : "#246B4B");
        SetThemeBrush("StatusErrorBrush", dark ? "#FF9292" : "#A12222");
        SetThemeBrush("StatusSuccessBrush", dark ? "#8ED5A9" : "#246B4B");
        SetThemeBrush("SelectionBrush", dark ? "#3C4A5D" : "#D8E5FF");
        SetThemeBrush("SelectionTextBrush", dark ? "#F4F7FB" : "#17243A");

        SetSystemThemeBrush(System.Windows.SystemColors.WindowBrushKey, dark ? "#2B313D" : "#FFFFFF");
        SetSystemThemeBrush(System.Windows.SystemColors.WindowTextBrushKey, dark ? "#E8ECF2" : "#202938");
        SetSystemThemeBrush(System.Windows.SystemColors.ControlBrushKey, dark ? "#2B313D" : "#FFFFFF");
        SetSystemThemeBrush(System.Windows.SystemColors.ControlTextBrushKey, dark ? "#E8ECF2" : "#202938");
        SetSystemThemeBrush(System.Windows.SystemColors.MenuBrushKey, dark ? "#2B313D" : "#FFFFFF");
        SetSystemThemeBrush(System.Windows.SystemColors.MenuTextBrushKey, dark ? "#E8ECF2" : "#202938");
        SetSystemThemeBrush(System.Windows.SystemColors.HighlightBrushKey, dark ? "#3C4A5D" : "#D8E5FF");
        SetSystemThemeBrush(System.Windows.SystemColors.HighlightTextBrushKey, dark ? "#F4F7FB" : "#17243A");
    }

    private void SetThemeBrush(string key, string color)
    {
        var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Resources[key] = brush;
    }

    private void SetSystemThemeBrush(object key, string color)
    {
        var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Resources[key] = brush;
    }

    private void UpdateInputMode()
    {
        var isMkv = IsMkv(InputPathTextBox.Text);
        if (!isMkv)
        {
            _tracksForInput = null;
            TrackComboBox.Items.Clear();
            TrackComboBox.Items.Add(TrackChoice.Automatic);
            TrackComboBox.SelectedIndex = 0;
        }
        else if (!string.Equals(_tracksForInput, SafeFullPath(InputPathTextBox.Text), StringComparison.OrdinalIgnoreCase))
        {
            _tracksForInput = null;
            TrackComboBox.Items.Clear();
            TrackComboBox.Items.Add(TrackChoice.Automatic);
            TrackComboBox.SelectedIndex = 0;
        }
        TrackComboBox.IsEnabled = isMkv && TrackComboBox.Items.Count > 1;
        RefreshTracksButton.IsEnabled = isMkv && _activeProcess is null && _runCancellation is null;
        SearchExternalButton.IsEnabled = isMkv && _activeProcess is null && _runCancellation is null;
        ExtractButton.IsEnabled = isMkv && _activeProcess is null && _runCancellation is null;
        BatchButton.IsEnabled = _activeProcess is null && _runCancellation is null;
    }

    private static string? SafeFullPath(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void SetAutomaticOutput(string inputPath)
    {
        try
        {
            var input = Path.GetFullPath(inputPath);
            var stem = Path.GetFileNameWithoutExtension(input);
            var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && LanguageSuffixes.Contains(parts[^1]))
                stem = string.Join('.', parts[..^1]);
            var output = Path.Combine(Path.GetDirectoryName(input)!, $"{stem}.no.srt");
            OutputPathTextBox.Text = output;
            _lastAutomaticOutput = output;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep the current output field while the user is still entering a path.
        }
    }

    private async void RefreshTracks_Click(object sender, RoutedEventArgs e) => await LoadTracksAsync();

    private async void SearchExternal_Click(object sender, RoutedEventArgs e)
    {
        var media = InputPathTextBox.Text.Trim();
        if (!IsMkv(media) || !File.Exists(media)) return;
        _runCancellation = new CancellationTokenSource();
        SetBusy(true, "Søker i valgte eksterne undertekstkilder…");
        try { await SearchAndSelectExternalSubtitleAsync(media, _runCancellation.Token); }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            SetBusy(false);
            UpdateInputMode();
        }
    }

    private async Task LoadTracksAsync()
    {
        var input = InputPathTextBox.Text.Trim();
        if (!IsMkv(input) || !File.Exists(input)) return;
        if (!File.Exists(_scriptPath))
        {
            SetStatus("Python-motoren mangler ved siden av programmet.", isError: true);
            return;
        }

        SetBusy(true, "Leser spor fra MKV…");
        try
        {
            var startInfo = CreatePythonStartInfo();
            startInfo.ArgumentList.Add(_scriptPath);
            startInfo.ArgumentList.Add(Path.GetFullPath(input));
            startInfo.ArgumentList.Add("--list-tracks");
            startInfo.ArgumentList.Add("--json");
            var (exitCode, standardOutput, standardError) = await RunCaptureAsync(startInfo);
            if (exitCode != 0)
            {
                AppendLog(standardError.Trim());
                SetStatus("Kunne ikke lese MKV-spor. Se aktivitetsloggen.", isError: true);
                return;
            }

            var tracks = JsonSerializer.Deserialize<List<TrackInfo>>(standardOutput) ?? [];
            TrackComboBox.Items.Clear();
            TrackComboBox.Items.Add(TrackChoice.Automatic);
            foreach (var track in tracks.Where(track => track.IsText))
                TrackComboBox.Items.Add(new TrackChoice(track));
            _tracksForInput = SafeFullPath(input);
            TrackComboBox.SelectedIndex = 0;
            var textTrackCount = tracks.Count(track => track.IsText);
            TrackComboBox.IsEnabled = textTrackCount > 0;
            if (textTrackCount == 0)
            {
                AppendLog(tracks.Count == 0
                    ? "Ingen innebygde undertekstspor ble funnet."
                    : "MKV-filen har undertekstspor, men ingen støttede tekstspor. Bildeundertekster kan ikke oversettes i denne flyten.");
                await SearchAndSelectExternalSubtitleAsync(input);
            }
            else
            {
                SelectPreferredTrack();
                AppendLog($"Fant {textTrackCount} støttede tekstspor av {tracks.Count} totalt.");
                SetStatus("Velg spor, eller la automatisk valg stå.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            AppendLog(ex.Message);
            SetStatus("Kunne ikke lese MKV-spor. Se aktivitetsloggen.", isError: true);
        }
        finally
        {
            SetBusy(false);
            UpdateInputMode();
        }
    }

    private void SelectPreferredTrack()
    {
        if (TrackComboBox.Items.Count == 0) return;
        var sourceLanguage = GetSelectedTag(SourceLanguageComboBox) ?? "en";
        var candidate = TrackComboBox.Items
            .OfType<TrackChoice>()
            .Skip(1)
            .FirstOrDefault(choice =>
                choice.Track is { IsText: true } track &&
                (sourceLanguage == "auto" ||
                 track.Language.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase) ||
                 (sourceLanguage == "en" && track.Language.Equals("eng", StringComparison.OrdinalIgnoreCase))) &&
                !(track.Title ?? string.Empty).Contains("sdh", StringComparison.OrdinalIgnoreCase));
        if (candidate is null && sourceLanguage == "auto")
            candidate = TrackComboBox.Items.OfType<TrackChoice>().Skip(1)
                .FirstOrDefault(choice => choice.Track is { IsText: true });
        TrackComboBox.SelectedItem = candidate ?? TrackComboBox.Items[0];
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunWorkflowAsync(extractOnly: false);

    private async void ExtractOnly_Click(object sender, RoutedEventArgs e) => await RunWorkflowAsync(extractOnly: true);

    private async void BatchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Velg sesong- eller seriemappe",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(InputPathTextBox.Text))
        {
            var initial = IsMkv(InputPathTextBox.Text)
                ? Path.GetDirectoryName(InputPathTextBox.Text)
                : InputPathTextBox.Text;
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        }
        if (dialog.ShowDialog(this) != true) return;

        var searchOption = IncludeSubdirectoriesCheckBox.IsChecked == true
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        List<string> videos;
        try
        {
            videos = Directory.EnumerateFiles(dialog.FolderName, "*.mkv", searchOption)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Kunne ikke lese mappen: {ex.Message}", isError: true);
            return;
        }

        if (videos.Count == 0)
        {
            SetStatus("Fant ingen MKV-filer i den valgte mappen.", isError: true);
            return;
        }

        var missing = videos.Where(video => !File.Exists(GetNorwegianOutputPath(video))).ToList();
        var completedVideos = videos.Where(video => File.Exists(GetNorwegianOutputPath(video))).ToList();
        var alreadyDone = completedVideos.Count;
        foreach (var video in completedVideos)
            AppendLog($"Hoppet over, norsk undertekst finnes allerede: {Path.GetRelativePath(dialog.FolderName, GetNorwegianOutputPath(video))}");
        if (missing.Count == 0)
        {
            SetStatus($"Alle {videos.Count} MKV-filene har allerede norsk .no.srt.");
            AppendLog($"Mappejobb: {dialog.FolderName} · {videos.Count} ferdige, ingenting å gjøre.");
            return;
        }

        var scope = IncludeSubdirectoriesCheckBox.IsChecked == true ? "inkludert undermapper" : "bare valgt mappe";
        if (MessageBox.Show(this,
                $"Fant {videos.Count} MKV-filer ({scope}).\n{alreadyDone} har allerede norsk .no.srt og hoppes over.\n\n{missing.Count} mangler norsk undertekst og behandles én etter én.\n\nVed behov velges beste eksterne treff automatisk. Vil du fortsette?",
                "Behandle sesong eller serie", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        if (!Directory.Exists(ModelPathTextBox.Text.Trim()))
        {
            SetStatus("Modellmappen finnes ikke. Velg en gyldig lokal modellmappe.", isError: true);
            return;
        }
        if (!File.Exists(_scriptPath))
        {
            SetStatus("Python-motoren local_gst.py mangler ved siden av programmet.", isError: true);
            return;
        }

        var originalInput = InputPathTextBox.Text;
        var succeeded = 0;
        var failed = 0;
        _runCancellation = new CancellationTokenSource();
        var token = _runCancellation.Token;
        SetBusy(true, "Starter mappejobb…");
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Minimum = 0;
        ProgressBar.Maximum = missing.Count;
        ProgressBar.Value = 0;
        AppendLog($"Mappejobb startet: {dialog.FolderName} · {missing.Count} manglende undertekster.");
        try
        {
            for (var index = 0; index < missing.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var video = missing[index];
                var norwegianOutput = GetNorwegianOutputPath(video);
                InputPathTextBox.Text = video;
                OutputPathTextBox.Text = norwegianOutput;
                SetStatus($"Behandler {index + 1} av {missing.Count}: {Path.GetFileName(video)}");
                AppendLog($"[{index + 1}/{missing.Count}] {Path.GetRelativePath(dialog.FolderName, video)}");
                try
                {
                    var englishSidecar = GetEnglishSidecarCandidates(video).FirstOrDefault(File.Exists);
                    var translationInput = englishSidecar ?? video;
                    if (englishSidecar is not null)
                        AppendLog($"Gjenbruker eksisterende engelsk undertekst: {Path.GetFileName(englishSidecar)}");
                    var exitCode = await RunBatchTranslationAsync(translationInput, norwegianOutput, token);
                    if (exitCode != 0 && englishSidecar is null)
                    {
                        AppendLog("Lokal oversettelse fant ikke et brukbart innebygd spor. Prøver aktiverte eksterne kilder.");
                        var englishSubtitle = await SearchAndSelectExternalSubtitleAsync(video, token, unattended: true);
                        if (englishSubtitle is null || !File.Exists(englishSubtitle))
                        {
                            failed++;
                            AppendLog("Hoppet over: ingen brukbar ekstern undertekst ble funnet.", isError: true);
                        }
                        else if (await RunBatchTranslationAsync(englishSubtitle, norwegianOutput, token) == 0)
                        {
                            succeeded++;
                            CleanupEnglishSidecars(video);
                            AppendLog($"Ferdig: {Path.GetFileName(norwegianOutput)}");
                        }
                        else
                        {
                            failed++;
                            AppendLog("Oversettelsen fra ekstern SRT feilet.", isError: true);
                        }
                    }
                    else
                    {
                        if (exitCode == 0)
                        {
                            succeeded++;
                            CleanupEnglishSidecars(video);
                            AppendLog($"Ferdig: {Path.GetFileName(norwegianOutput)}");
                        }
                        else
                        {
                            failed++;
                            AppendLog("Oversettelse fra eksisterende engelsk SRT feilet; kildeteksten beholdes.", isError: true);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
                {
                    failed++;
                    AppendLog($"Feil for {Path.GetFileName(video)}: {ex.Message}", isError: true);
                }
                ProgressBar.Value = index + 1;
            }
            SetStatus($"Mappejobb ferdig: {succeeded} ferdige, {failed} feilet, {alreadyDone} hoppet over.", failed > 0);
            AppendLog($"Mappejobb ferdig: {succeeded} ferdige, {failed} feilet, {alreadyDone} allerede ferdige.");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Mappejobb avbrutt: {succeeded} ferdige, {failed} feilet. Resten er ikke behandlet.", isError: true);
            AppendLog("Mappejobb avbrutt av bruker.");
        }
        finally
        {
            InputPathTextBox.Text = originalInput;
            _activeProcess = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
            SetBusy(false);
            UpdateInputMode();
        }
    }

    private async Task<int> RunBatchTranslationAsync(string input, string output, CancellationToken token)
    {
        var startInfo = CreatePythonStartInfo();
        startInfo.ArgumentList.Add(_scriptPath);
        startInfo.ArgumentList.Add(Path.GetFullPath(input));
        startInfo.ArgumentList.Add(Path.GetFullPath(output));
        startInfo.ArgumentList.Add("--source-language");
        startInfo.ArgumentList.Add(input.EndsWith(".en.srt", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : GetSelectedTag(SourceLanguageComboBox) ?? "en");
        startInfo.ArgumentList.Add("--live-events");
        startInfo.ArgumentList.Add("--target-language");
        startInfo.ArgumentList.Add(GetSelectedTag(TargetLanguageComboBox) ?? "nb");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(Path.GetFullPath(ModelPathTextBox.Text.Trim()));
        startInfo.ArgumentList.Add("--batch-size");
        startInfo.ArgumentList.Add(GetSelectedTag(BatchSizeComboBox) ?? "16");
        if (IncludeParentheticalCheckBox.IsChecked == true)
            startInfo.ArgumentList.Add("--include-parenthetical-captions");
        if (DiagnosticsCheckBox.IsChecked == true)
        {
            var diagnosticsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalGST", "Diagnostics");
            startInfo.ArgumentList.Add("--diagnostics-path");
            startInfo.ArgumentList.Add(Path.Combine(diagnosticsDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Path.GetFileNameWithoutExtension(input)}.json"));
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _activeProcess = process;
        if (!process.Start()) throw new InvalidOperationException("Kunne ikke starte Python-prosessen.");
        CancelButton.IsEnabled = true;
        var stdoutTask = PumpOutputAsync(process.StandardOutput, isError: false);
        var stderrTask = PumpOutputAsync(process.StandardError, isError: true);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        _activeProcess = null;
        return process.ExitCode;
    }

    private async Task RunWorkflowAsync(bool extractOnly)
    {
        var input = InputPathTextBox.Text.Trim();
        var output = extractOnly ? GetExtractedOutputPath(input) : OutputPathTextBox.Text.Trim();
        var model = ModelPathTextBox.Text.Trim();
        if (!File.Exists(input))
        {
            SetStatus("Velg en eksisterende SRT- eller MKV-fil.", isError: true);
            return;
        }
        if (extractOnly && !IsMkv(input))
        {
            SetStatus("Uttrekk-knappen krever en MKV-fil.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(output) || (!extractOnly && string.IsNullOrWhiteSpace(model)))
        {
            SetStatus(extractOnly ? "Kan ikke bestemme output for uttrekket." : "Velg outputfil og lokal modellmappe.", isError: true);
            return;
        }
        if (!extractOnly && !Directory.Exists(model))
        {
            SetStatus("Modellmappen finnes ikke. Velg mappen der de lokale modellfilene ligger.", isError: true);
            return;
        }
        if (!File.Exists(_scriptPath))
        {
            SetStatus("Python-motoren local_gst.py mangler ved siden av programmet.", isError: true);
            return;
        }

        var overwriteOutput = File.Exists(output);
        if (overwriteOutput && MessageBox.Show(
                this,
                $"Filen finnes allerede:\n{output}\n\nVil du erstatte den?",
                "Bekreft overskriving",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var sourceLanguage = GetSelectedTag(SourceLanguageComboBox) ?? "en";
        var targetLanguage = GetSelectedTag(TargetLanguageComboBox) ?? "nb";
        var startInfo = CreatePythonStartInfo();
        startInfo.ArgumentList.Add(_scriptPath);
        startInfo.ArgumentList.Add(Path.GetFullPath(input));
        startInfo.ArgumentList.Add(Path.GetFullPath(output));
        startInfo.ArgumentList.Add("--source-language");
        startInfo.ArgumentList.Add(sourceLanguage);
        if (extractOnly)
        {
            startInfo.ArgumentList.Add("--extract-only");
        }
        else
        {
            startInfo.ArgumentList.Add("--live-events");
            startInfo.ArgumentList.Add("--target-language");
            startInfo.ArgumentList.Add(targetLanguage);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(Path.GetFullPath(model));
            startInfo.ArgumentList.Add("--batch-size");
            startInfo.ArgumentList.Add(GetSelectedTag(BatchSizeComboBox) ?? "16");
            if (IncludeParentheticalCheckBox.IsChecked == true)
                startInfo.ArgumentList.Add("--include-parenthetical-captions");
            if (DiagnosticsCheckBox.IsChecked == true)
            {
                var diagnosticsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocalGST", "Diagnostics");
                var diagnosticsName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileNameWithoutExtension(input)}.json";
                startInfo.ArgumentList.Add("--diagnostics-path");
                startInfo.ArgumentList.Add(Path.Combine(diagnosticsDirectory, diagnosticsName));
            }
        }
        if (overwriteOutput) startInfo.ArgumentList.Add("--overwrite");
        if (IsMkv(input) && TrackComboBox.SelectedItem is TrackChoice { Track: not null } selected)
        {
            startInfo.ArgumentList.Add("--track-index");
            startInfo.ArgumentList.Add(selected.Track.StreamIndex.ToString());
        }

        _runCancellation = new CancellationTokenSource();
        SetBusy(true, "Starter lokal oversettelse…");
        AppendLog(extractOnly
            ? $"Henter ut spor fra {Path.GetFileName(input)} → {Path.GetFileName(output)}"
            : $"Starter: {Path.GetFileName(input)} → {Path.GetFileName(output)}");
        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _activeProcess = process;
            if (!process.Start()) throw new InvalidOperationException("Kunne ikke starte Python-prosessen.");
            CancelButton.IsEnabled = true;
            var stdoutTask = PumpOutputAsync(process.StandardOutput, isError: false);
            var stderrTask = PumpOutputAsync(process.StandardError, isError: true);
            try
            {
                await process.WaitForExitAsync(_runCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdoutTask, stderrTask);
                SetStatus("Oversettelsen ble avbrutt. Kildemediet er urørt.", isError: true);
                return;
            }
            await Task.WhenAll(stdoutTask, stderrTask);
            if (process.ExitCode == 0)
            {
                if (extractOnly)
                {
                    InputPathTextBox.Text = output;
                    SetAutomaticOutput(output);
                    AppendLog("Den uttrukne SRT-filen er valgt som ny kilde. Du kan starte lokal oversettelse nå.");
                    SetStatus($"SRT hentet ut: {output}");
                }
                else
                {
                    SetStatus($"Ferdig. Oversatt undertekst er lagret i {output}");
                }
            }
            else if (IsMkv(input))
            {
                AppendLog("Lokal uttrekking fant ikke et brukbart spor. Søker etter ekstern undertekst; videofilen sendes ikke.");
                var subtitle = await SearchAndSelectExternalSubtitleAsync(input, _runCancellation.Token);
                if (subtitle is not null)
                {
                    SetStatus(extractOnly
                        ? "Ekstern engelsk undertekst er valgt."
                        : "Ekstern undertekst er valgt som kilde. Trykk Start lokal oversettelse for å fortsette.");
                }
            }
            else
                SetStatus($"Oversettelsen feilet (kode {process.ExitCode}). Se aktivitetsloggen.", isError: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppendLog(ex.Message);
            SetStatus("Kunne ikke starte lokal oversettelse.", isError: true);
        }
        finally
        {
            _activeProcess = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
            SetBusy(false);
            UpdateInputMode();
        }
    }

    private async Task<string?> SearchAndSelectExternalSubtitleAsync(string media, CancellationToken cancellationToken = default, bool unattended = false)
    {
        try
        {
            var options = await ExternalSubtitleFallback.SearchAsync(media, _settings, message =>
            {
                SetStatus(message);
                AppendLog(message);
            }, cancellationToken, unattended);
            options = options.Where(option => option.MatchScore >= _settings.MinimumReleaseMatchScore).ToArray();
            if (options.Count == 0)
            {
                const string none = "Fant ingen undertekster som oppfyller språk- og matchkravene på de valgte eksterne kildene.";
                AppendLog(none);
                SetStatus(none, isError: true);
                return null;
            }

            var remaining = options.ToList();
            var choiceStatus = $"Søket er ferdig: {remaining.Count} treff. Velg en utgave i vinduet.";
            SetStatus(choiceStatus);
            ProgressBar.Visibility = Visibility.Collapsed;
            AppendLog(choiceStatus);
            while (remaining.Count > 0)
            {
                AppendLog($"Fant {remaining.Count} eksterne undertekstutgaver. Velg riktig release-type.");
                var selected = unattended
                    ? remaining.OrderByDescending(option => option.MatchScore).First()
                    : ExternalSubtitlePicker.Choose(this, remaining);
                if (selected is null)
                {
                    SetStatus("Valg av ekstern undertekst ble avbrutt.");
                    return null;
                }

                SetStatus($"Laster ned valgt undertekst fra {selected.Provider}…");
                AppendLog($"Valgt: {selected.Provider} · {selected.Release} · match {selected.MatchScore}%.");
                string? temporarySubtitle;
                try { temporarySubtitle = await ExternalSubtitleFallback.DownloadAsync(media, selected, _settings, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or UriFormatException)
                {
                    AppendLog($"Nedlasting fra {selected.Provider} feilet: {ex.Message}");
                    temporarySubtitle = null;
                }
                if (temporarySubtitle is null)
                {
                    remaining.Remove(selected);
                    AppendLog($"{selected.Provider}: nedlasting feilet eller filen var ikke en gyldig SRT. Velg et annet treff eller avbryt.");
                    continue;
                }

                var comparison = ExternalSubtitleFallback.CompareDuration(media, temporarySubtitle);
                if (comparison is { } duration)
                {
                    var report = $"Videolengde {FormatDuration(duration.MediaSeconds)} · siste cue {FormatDuration(duration.SubtitleSeconds)} · avvik {FormatDuration(duration.DifferenceSeconds)}.";
                    AppendLog(report);
                    if (duration.DifferenceSeconds > _settings.DurationToleranceSeconds)
                    {
                        var useAnyway = unattended
                            ? false
                            : MessageBox.Show(this,
                                $"Undertekstens lengde avviker mer enn grensen på {_settings.DurationToleranceSeconds} sekunder.\n\n{report}\n\nVil du bruke denne underteksten likevel? Velg Nei for å prøve et annet treff.",
                                "Mulig sync-avvik", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                        if (!useAnyway)
                        {
                            File.Delete(temporarySubtitle);
                            remaining.Remove(selected);
                            if (unattended) AppendLog($"{selected.Provider}: avvist automatisk fordi lengdeavviket overskrider grensen.");
                            continue;
                        }
                    }
                }
                else AppendLog("Videolengde kunne ikke leses; SRT ble kontrollert for siste cue, men varighetssammenligning ble hoppet over.");

                var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(media))!;
                var stem = Path.GetFileNameWithoutExtension(media);
                var destination = Path.Combine(outputDirectory, stem + ".en.srt");
                if (File.Exists(destination))
                {
                    AppendLog("En eksisterende .en.srt finnes og blir beholdt urørt. Det nye treffet lagres separat.");
                    var providerName = string.Concat(selected.Provider.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    destination = Path.Combine(outputDirectory, $"{stem}.{providerName}.en.srt");
                    var suffix = 2;
                    while (File.Exists(destination))
                        destination = Path.Combine(outputDirectory, $"{stem}.{providerName}.{suffix++}.en.srt");
                }
                File.Move(temporarySubtitle, destination);
                InputPathTextBox.Text = destination;
                SetAutomaticOutput(destination);
                AppendLog($"Engelsk SRT validert og valgt som kilde: {Path.GetFileName(destination)}.");
                return destination;
            }
            const string exhausted = "Ingen av de valgte eksterne treffene kunne brukes.";
            AppendLog(exhausted);
            SetStatus(exhausted, isError: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Søket etter eksterne undertekster ble avbrutt.", isError: true);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            AppendLog($"Eksternt undertekstsøk feilet: {ex.Message}");
            SetStatus("Kunne ikke søke etter eksterne undertekster. Se aktivitetsloggen.", isError: true);
            return null;
        }
    }

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private async Task PumpOutputAsync(StreamReader reader, bool isError)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!isError && TryParseWorkerEvent(line, out var workerEvent))
            {
                await Dispatcher.InvokeAsync(() => ApplyWorkerEvent(workerEvent));
                continue;
            }
            await Dispatcher.InvokeAsync(() =>
            {
                var isWarning = isError &&
                    (line.Contains("UserWarning", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("warnings.warn", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("recommended:", StringComparison.OrdinalIgnoreCase));
                AppendLog(line, isError && !isWarning);
            });
        }
    }

    private static bool TryParseWorkerEvent(string line, out WorkerEvent workerEvent)
    {
        workerEvent = new WorkerEvent();
        if (!line.StartsWith('{')) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkerEvent>(line);
            if (parsed is null || (parsed.Type != "source" && parsed.Type != "batch" && parsed.Type != "failure")) return false;
            workerEvent = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ApplyWorkerEvent(WorkerEvent workerEvent)
    {
        if (workerEvent.Type == "failure")
        {
            foreach (var row in LiveRows)
                if (row.OutputText == LiveCueRow.WaitingText)
                    row.OutputText = "Ikke behandlet – jobben ble stoppet.";
            foreach (var item in workerEvent.Items)
                if (_liveRowsByKey.TryGetValue(GetCueKey(item.Number, item.Timing), out var failedRow))
                    failedRow.OutputText = item.Output ?? "Stoppet – se aktivitetsloggen.";
            if (!string.IsNullOrWhiteSpace(workerEvent.Message)) AppendLog(workerEvent.Message, isError: true);
            if (!string.IsNullOrWhiteSpace(workerEvent.DiagnosticsPath))
                AppendLog($"Feilsøkingsrapport: {workerEvent.DiagnosticsPath}");
            return;
        }

        if (workerEvent.Type == "source")
        {
            LiveRows.Clear();
            _liveRowsByKey.Clear();
            foreach (var item in workerEvent.Items)
            {
                var row = new LiveCueRow(item.Number, item.Timing, item.Input ?? string.Empty);
                LiveRows.Add(row);
                _liveRowsByKey[GetCueKey(item.Number, item.Timing)] = row;
            }
            ProgressBar.Minimum = 0;
            ProgressBar.Maximum = Math.Max(1, workerEvent.Total);
            ProgressBar.Value = 0;
            ProgressBar.IsIndeterminate = true;
            StatusTextBlock.Text = $"Leste {workerEvent.Total} cues. Laster lokal modell…";
            AppendLog($"Kildetekst klar: {workerEvent.Total} cues.");
            return;
        }

        foreach (var item in workerEvent.Items)
        {
            if (_liveRowsByKey.TryGetValue(GetCueKey(item.Number, item.Timing), out var row))
                row.OutputText = item.Output ?? string.Empty;
        }
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Minimum = 0;
        ProgressBar.Maximum = Math.Max(1, workerEvent.Total);
        ProgressBar.Value = Math.Min(workerEvent.Completed, ProgressBar.Maximum);
        StatusTextBlock.Text = $"Oversetter {workerEvent.Completed} av {workerEvent.Total} cues…";
        if (workerEvent.Completed > 0 && workerEvent.Completed <= LiveRows.Count)
            LiveTextGrid.ScrollIntoView(LiveRows[workerEvent.Completed - 1]);
    }

    private static string GetCueKey(string number, string timing) => $"{number}\0{timing}";

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        await Task.CompletedTask;
    }

    private ProcessStartInfo CreatePythonStartInfo()
    {
        var python = string.IsNullOrWhiteSpace(PythonPathTextBox.Text) ? "python" : PythonPathTextBox.Text.Trim();
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        return startInfo;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCaptureAsync(ProcessStartInfo startInfo)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Kunne ikke starte Python-prosessen.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        StartButton.IsEnabled = !busy;
        ExtractButton.IsEnabled = !busy && IsMkv(InputPathTextBox.Text);
        CancelButton.IsEnabled = busy && (_activeProcess is not null || _runCancellation is not null);
        RefreshTracksButton.IsEnabled = !busy && IsMkv(InputPathTextBox.Text);
        SearchExternalButton.IsEnabled = !busy && IsMkv(InputPathTextBox.Text);
        BatchButton.IsEnabled = !busy;
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.IsIndeterminate = busy;
        if (status is not null) StatusTextBlock.Text = status;
        if (busy)
        {
            ProgressBar.IsIndeterminate = true;
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.SteelBlue;
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = (System.Windows.Media.Brush?)Resources[isError ? "StatusErrorBrush" : "StatusSuccessBrush"]
            ?? System.Windows.Media.Brushes.SeaGreen;
    }

    private void AppendLog(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {(isError ? "FEIL: " : string.Empty)}{message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private static string? FindPython()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string FindProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Local-GST.csproj")))
                return directory.FullName;
        }
        return AppContext.BaseDirectory;
    }

    private static bool IsMkv(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    private string GetExtractedOutputPath(string inputPath)
    {
        var input = Path.GetFullPath(inputPath);
        var stem = Path.GetFileNameWithoutExtension(input);
        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && LanguageSuffixes.Contains(parts[^1]))
            stem = string.Join('.', parts[..^1]);

        var language = GetSelectedTag(SourceLanguageComboBox) ?? "en";
        if (language == "auto" && TrackComboBox.SelectedItem is TrackChoice { Track: not null } selected)
            language = selected.Track.Language;
        language = language.ToLowerInvariant() switch
        {
            "eng" or "english" => "en",
            "nob" or "norwegian" => "no",
            "nno" => "nn",
            "swe" => "sv",
            "dan" => "da",
            "deu" or "ger" => "de",
            "fra" or "fre" => "fr",
            "spa" => "es",
            "ita" => "it",
            "fin" => "fi",
            "auto" => "und",
            var code => code
        };
        return Path.Combine(Path.GetDirectoryName(input)!, $"{stem}.{language}.srt");
    }

    private static string GetNorwegianOutputPath(string inputPath)
    {
        var input = Path.GetFullPath(inputPath);
        var stem = Path.GetFileNameWithoutExtension(input);
        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && LanguageSuffixes.Contains(parts[^1]))
            stem = string.Join('.', parts[..^1]);
        return Path.Combine(Path.GetDirectoryName(input)!, $"{stem}.no.srt");
    }

    private IEnumerable<string> GetEnglishSidecarCandidates(string videoPath)
    {
        var video = Path.GetFullPath(videoPath);
        var directory = Path.GetDirectoryName(video)!;
        var stem = Path.GetFileNameWithoutExtension(video);
        var providers = new[] { "Addic7ed", "OpenSubtitles", "SubDL", "SubSource", "Gestdown", "iSubtitles" }
            .Concat(_settings.CustomProviders.Select(provider => provider.Name));
        yield return Path.Combine(directory, $"{stem}.en.srt");
        foreach (var provider in providers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var safeProvider = string.Concat(provider.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (!string.IsNullOrWhiteSpace(safeProvider))
                yield return Path.Combine(directory, $"{stem}.{safeProvider}.en.srt");
        }
    }

    private void CleanupEnglishSidecars(string videoPath)
    {
        foreach (var sidecar in GetEnglishSidecarCandidates(videoPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(sidecar);
                AppendLog($"Fjernet brukt engelsk mellomfil etter vellykket oversettelse: {Path.GetFileName(sidecar)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"Kunne ikke fjerne engelsk mellomfil {Path.GetFileName(sidecar)}: {ex.Message}");
            }
        }
    }

    private static string? GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private sealed record TrackInfo
    {
        [JsonPropertyName("stream_index")]
        public int StreamIndex { get; init; }
        [JsonPropertyName("language")]
        public string Language { get; init; } = "und";
        [JsonPropertyName("title")]
        public string? Title { get; init; }
        [JsonPropertyName("codec")]
        public string? Codec { get; init; }
        [JsonPropertyName("is_text")]
        public bool IsText { get; init; }
    }

    private sealed class WorkerEvent
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;
        [JsonPropertyName("completed")]
        public int Completed { get; init; }
        [JsonPropertyName("total")]
        public int Total { get; init; }
        [JsonPropertyName("items")]
        public List<WorkerItem> Items { get; init; } = [];
        [JsonPropertyName("message")]
        public string? Message { get; init; }
        [JsonPropertyName("diagnostics_path")]
        public string? DiagnosticsPath { get; init; }
    }

    private sealed class WorkerItem
    {
        [JsonPropertyName("number")]
        public string Number { get; init; } = string.Empty;
        [JsonPropertyName("timing")]
        public string Timing { get; init; } = string.Empty;
        [JsonPropertyName("input")]
        public string? Input { get; init; }
        [JsonPropertyName("output")]
        public string? Output { get; init; }
    }

    private sealed class LiveCueRow : INotifyPropertyChanged
    {
        public const string WaitingText = "Venter på oversettelse…";
        private string _outputText = WaitingText;
        public string Number { get; }
        public string Timing { get; }
        public string InputText { get; }
        public string OutputText
        {
            get => _outputText;
            set
            {
                if (_outputText == value) return;
                _outputText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputText)));
            }
        }

        public LiveCueRow(string number, string timing, string inputText)
        {
            Number = number;
            Timing = timing;
            InputText = inputText;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class TrackChoice
    {
        public static TrackChoice Automatic { get; } = new(null);
        public TrackInfo? Track { get; }
        public string DisplayName => Track is null
            ? "Automatisk (velg engelsk, foretrekk ikke-SDH)"
            : $"{Track.StreamIndex}: {Track.Language} · {Track.Codec ?? "ukjent"}{(string.IsNullOrWhiteSpace(Track.Title) ? string.Empty : $" · {Track.Title}")}";

        public TrackChoice(TrackInfo? track) => Track = track;
    }
}
