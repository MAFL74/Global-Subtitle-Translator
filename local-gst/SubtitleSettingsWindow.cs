using System.IO;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Local_GST;

internal sealed class SubtitleSettingsWindow : Window
{
    private readonly LocalGstSettings _settings;
    private readonly CheckBox _addic7ed = new() { Content = "Addic7ed (TV-serier, offentlig nettsøk)" };
    private readonly CheckBox _openSubtitles = new() { Content = "OpenSubtitles (film og TV, API)" };
    private readonly CheckBox _subDl = new() { Content = "SubDL (film og TV, API)" };
    private readonly CheckBox _subSource = new() { Content = "SubSource (film og TV, API)" };
    private readonly CheckBox _gestdown = new() { Content = "Gestdown (TV-serier, API)" };
    private readonly CheckBox _isubtitles = new() { Content = "iSubtitles (film og TV, nettsøk)" };
    private readonly PasswordBox _openKey = new();
    private readonly TextBox _openUser = new();
    private readonly PasswordBox _openPassword = new();
    private readonly PasswordBox _subDlKey = new();
    private readonly PasswordBox _subSourceKey = new();
    private readonly TextBox _duration = new();
    private readonly TextBox _matchScore = new();
    private readonly ListBox _customProviders = new();
    private readonly Brush _fieldBrush;
    private readonly Brush _textBrush;
    private readonly Brush _borderBrush;
    private readonly Brush _cardBrush;

    private SubtitleSettingsWindow(Window owner, LocalGstSettings settings)
    {
        Owner = owner;
        _settings = settings;
        Title = "Innstillinger";
        Width = 760;
        Height = 760;
        MinWidth = 620;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = GetBrush(owner, "WindowBrush", "#171A21");
        _fieldBrush = GetBrush(owner, "FieldBrush", "#2B313D");
        _textBrush = GetBrush(owner, "TextBrush", "#E8ECF2");
        _borderBrush = GetBrush(owner, "BorderBrushTheme", "#3A4351");
        _cardBrush = GetBrush(owner, "CardBrush", "#222731");
        Foreground = _textBrush;

        _addic7ed.IsChecked = settings.Addic7edEnabled;
        _openSubtitles.IsChecked = settings.OpenSubtitlesEnabled;
        _subDl.IsChecked = settings.SubDlEnabled;
        _subSource.IsChecked = settings.SubSourceEnabled;
        _gestdown.IsChecked = settings.GestdownEnabled;
        _isubtitles.IsChecked = settings.ISubtitlesEnabled;
        _openKey.Password = settings.OpenSubtitlesApiKey;
        _openUser.Text = settings.OpenSubtitlesUsername;
        _openPassword.Password = settings.OpenSubtitlesPassword;
        _subDlKey.Password = settings.SubDlApiKey;
        _subSourceKey.Password = settings.SubSourceApiKey;
        _duration.Text = settings.DurationToleranceSeconds.ToString();
        _matchScore.Text = settings.MinimumReleaseMatchScore.ToString();
        foreach (var check in new[] { _addic7ed, _openSubtitles, _subDl, _subSource, _gestdown, _isubtitles })
        { check.Foreground = _textBrush; check.Margin = new Thickness(0, 3, 0, 4); }
        _customProviders.ItemsSource = settings.CustomProviders;

        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Section("Eksterne undertekstkilder"));
        body.Children.Add(_addic7ed);
        body.Children.Add(_openSubtitles);
        AddLabeled(body, "OpenSubtitles API-nøkkel", _openKey);
        AddLabeled(body, "OpenSubtitles brukernavn (valgfritt; kan gi høyere nedlastingskvote)", _openUser);
        AddLabeled(body, "OpenSubtitles passord (valgfritt)", _openPassword);
        body.Children.Add(_subDl);
        AddLabeled(body, "SubDL API-nøkkel", _subDlKey);
        body.Children.Add(_subSource);
        AddLabeled(body, "SubSource API-nøkkel", _subSourceKey);
        body.Children.Add(_gestdown);
        body.Children.Add(_isubtitles);
        body.Children.Add(new TextBlock
        {
            Text = "API-nøkler og passord lagres kryptert for denne Windows-brukeren. Gestdown dekker TV-serier; SubSource krever en API-nøkkel. iSubtitles vises ved manuelt søk fordi treffene ikke alltid kan matches sikkert mot riktig episode. Nettsøk kan endres eller begrenses av tjenestene.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16), Foreground = GetBrush(owner, "MutedBrush", "#AAB4C3")
        });

        body.Children.Add(Section("Egne API-kilder"));
        body.Children.Add(new TextBlock
        {
            Text = "Egendefinert GET-API må støtte {query}, {filename} og {language} i URL-en, og returnere JSON med results[]-elementer som har release, language og download_url. API-nøkkel kan sendes i valgt header.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = GetBrush(owner, "MutedBrush", "#AAB4C3")
        });
        _customProviders.Height = 100;
        _customProviders.Background = _fieldBrush;
        _customProviders.Foreground = _textBrush;
        _customProviders.ItemTemplate = new DataTemplate(typeof(CustomSubtitleProvider))
        {
            VisualTree = BuildCustomProviderTemplate()
        };
        body.Children.Add(_customProviders);
        var customButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 16) };
        customButtons.Children.Add(MakeButton("Legg til manuelt…", AddCustomProvider));
        customButtons.Children.Add(MakeButton("Fjern valgt", RemoveCustomProvider, 8));
        body.Children.Add(customButtons);

        body.Children.Add(Section("Sync-sjekk"));
        AddLabeled(body, "Tillatt forskjell mellom videolengde og siste SRT-cue (sekunder)", _duration);
        AddLabeled(body, "Minimum release-matchscore (0–100; 0 viser alle treff)", _matchScore);
        body.Children.Add(new TextBlock
        {
            Text = "Etter nedlasting sammenlignes siste cue med videolengden. Større avvik flagges før du bruker underteksten.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14), Foreground = GetBrush(owner, "MutedBrush", "#AAB4C3")
        });

        var save = MakeButton("Lagre", SaveSettings);
        save.IsDefault = true;
        var cancel = MakeButton("Avbryt", (_, _) => DialogResult = false, 8);
        cancel.IsCancel = true;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16) };
        footer.Children.Add(save); footer.Children.Add(cancel);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        root.Children.Add(scroll);
        Content = root;
    }

    public static bool? Show(Window owner, LocalGstSettings settings)
    {
        var draft = new LocalGstSettings
        {
            Addic7edEnabled = settings.Addic7edEnabled,
            OpenSubtitlesEnabled = settings.OpenSubtitlesEnabled,
            OpenSubtitlesApiKey = settings.OpenSubtitlesApiKey,
            OpenSubtitlesUsername = settings.OpenSubtitlesUsername,
            OpenSubtitlesPassword = settings.OpenSubtitlesPassword,
            SubDlEnabled = settings.SubDlEnabled,
            SubDlApiKey = settings.SubDlApiKey,
            SubSourceEnabled = settings.SubSourceEnabled,
            SubSourceApiKey = settings.SubSourceApiKey,
            GestdownEnabled = settings.GestdownEnabled,
            ISubtitlesEnabled = settings.ISubtitlesEnabled,
            DurationToleranceSeconds = settings.DurationToleranceSeconds,
            MinimumReleaseMatchScore = settings.MinimumReleaseMatchScore,
            CustomProviders = settings.CustomProviders.Select(p => new CustomSubtitleProvider
            {
                Name = p.Name, SearchUrlTemplate = p.SearchUrlTemplate, ApiKeyHeader = p.ApiKeyHeader, ApiKey = p.ApiKey, Enabled = p.Enabled
            }).ToList()
        };
        var result = new SubtitleSettingsWindow(owner, draft).ShowDialog();
        if (result == true)
        {
            settings.Addic7edEnabled = draft.Addic7edEnabled;
            settings.OpenSubtitlesEnabled = draft.OpenSubtitlesEnabled;
            settings.OpenSubtitlesApiKey = draft.OpenSubtitlesApiKey;
            settings.OpenSubtitlesUsername = draft.OpenSubtitlesUsername;
            settings.OpenSubtitlesPassword = draft.OpenSubtitlesPassword;
            settings.SubDlEnabled = draft.SubDlEnabled;
            settings.SubDlApiKey = draft.SubDlApiKey;
            settings.SubSourceEnabled = draft.SubSourceEnabled;
            settings.SubSourceApiKey = draft.SubSourceApiKey;
            settings.GestdownEnabled = draft.GestdownEnabled;
            settings.ISubtitlesEnabled = draft.ISubtitlesEnabled;
            settings.DurationToleranceSeconds = draft.DurationToleranceSeconds;
            settings.MinimumReleaseMatchScore = draft.MinimumReleaseMatchScore;
            settings.CustomProviders = draft.CustomProviders;
        }
        return result;
    }

    private static TextBlock Section(string text) => new()
    { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) };

    private void AddLabeled(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 3), Foreground = _textBrush });
        control.Margin = new Thickness(0, 0, 0, 8);
        control.Padding = new Thickness(8, 6, 8, 6);
        control.Background = _fieldBrush; control.Foreground = _textBrush;
        if (control is TextBox tb) tb.BorderBrush = _borderBrush;
        panel.Children.Add(control);
    }

    private Button MakeButton(string title, RoutedEventHandler handler, double leftMargin = 0)
    {
        var button = new Button { Content = title, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(leftMargin, 0, 0, 0),
            Background = _fieldBrush, Foreground = _textBrush, BorderBrush = _borderBrush, BorderThickness = new Thickness(1) };
        button.Click += handler;
        button.Template = ButtonTemplate(_fieldBrush, _borderBrush, _textBrush);
        return button;
    }

    private static ControlTemplate ButtonTemplate(Brush background, Brush border, Brush foreground)
    {
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, background);
        factory.SetValue(Border.BorderBrushProperty, border);
        factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(TextElement.ForegroundProperty, foreground);
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 5, 10, 5));
        factory.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = factory };
    }

    private void AddCustomProvider(object? sender, RoutedEventArgs e)
    {
        var editor = new CustomProviderEditor(this, _fieldBrush, _textBrush, _borderBrush);
        if (editor.ShowDialog() == true && editor.Provider is not null)
        {
            _settings.CustomProviders.Add(editor.Provider);
            _customProviders.Items.Refresh();
            _customProviders.SelectedItem = editor.Provider;
        }
    }

    private void RemoveCustomProvider(object? sender, RoutedEventArgs e)
    {
        if (_customProviders.SelectedItem is CustomSubtitleProvider provider)
            _settings.CustomProviders.Remove(provider);
        _customProviders.Items.Refresh();
    }

    private void SaveSettings(object? sender, RoutedEventArgs e)
    {
        _settings.Addic7edEnabled = _addic7ed.IsChecked == true;
        _settings.OpenSubtitlesEnabled = _openSubtitles.IsChecked == true;
        _settings.OpenSubtitlesApiKey = _openKey.Password.Trim();
        _settings.OpenSubtitlesUsername = _openUser.Text.Trim();
        _settings.OpenSubtitlesPassword = _openPassword.Password;
        _settings.SubDlEnabled = _subDl.IsChecked == true;
        _settings.SubDlApiKey = _subDlKey.Password.Trim();
        if (!int.TryParse(_duration.Text, out var seconds) || seconds is < 10 or > 1800)
        {
            MessageBox.Show(this, "Angi en grense mellom 10 og 1800 sekunder.", "Ugyldig sync-grense", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(_matchScore.Text, out var matchScore) || matchScore is < 0 or > 100)
        {
            MessageBox.Show(this, "Matchscore må være mellom 0 og 100.", "Ugyldig matchscore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.DurationToleranceSeconds = seconds;
        _settings.MinimumReleaseMatchScore = matchScore;
        try { _settings.Save(); DialogResult = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, $"Kunne ikke lagre innstillingene: {ex.Message}", "Lagring feilet", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static Brush GetBrush(Window owner, string key, string fallback) => owner.Resources[key] as Brush
        ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private static FrameworkElementFactory BuildCustomProviderTemplate()
    {
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(CustomSubtitleProvider.Enabled)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        check.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(CustomSubtitleProvider.Name)));
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        return check;
    }

    private sealed class CustomProviderEditor : Window
    {
        private readonly TextBox _name = new();
        private readonly TextBox _url = new();
        private readonly TextBox _header = new();
        private readonly PasswordBox _key = new();
        private readonly Brush _field, _text, _border;
        public CustomSubtitleProvider? Provider { get; private set; }

        public CustomProviderEditor(Window owner, Brush field, Brush text, Brush border)
        {
            Owner = owner; _field = field; _text = text; _border = border;
            Title = "Legg til egen API-kilde"; Width = 640; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = field; Foreground = text;
            _header.Text = "X-API-Key";
            var panel = new StackPanel { Margin = new Thickness(18) };
            Add(panel, "Navn", _name);
            Add(panel, "GET URL-mal (støtter {query}, {filename}, {language})", _url);
            Add(panel, "API-nøkkel-header (la stå tomt uten nøkkel)", _header);
            Add(panel, "API-nøkkel", _key);
            panel.Children.Add(new TextBlock { Text = "Svaret må være JSON: { results: [{ release, language, download_url, match_score? }] }", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var add = Make("Legg til", (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text) || !Uri.TryCreate(_url.Text.Replace("{query}", "title").Replace("{filename}", "video.mkv").Replace("{language}", "en"), UriKind.Absolute, out _))
                { MessageBox.Show(this, "Fyll inn navn og gyldig HTTPS/HTTP URL-mal.", "Ugyldig kilde", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                Provider = new CustomSubtitleProvider { Name = _name.Text.Trim(), SearchUrlTemplate = _url.Text.Trim(), ApiKeyHeader = _header.Text.Trim(), ApiKey = _key.Password, Enabled = true };
                DialogResult = true;
            });
            var cancel = Make("Avbryt", (_, _) => DialogResult = false); cancel.IsCancel = true;
            buttons.Children.Add(add); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom); var root = new DockPanel(); root.Children.Add(buttons); root.Children.Add(panel); Content = root;
        }

        private void Add(Panel panel, string label, Control input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 3) });
            input.Margin = new Thickness(0, 0, 0, 6); input.Padding = new Thickness(7, 5, 7, 5); input.Background = _field; input.Foreground = _text;
            if (input is TextBox textBox) textBox.BorderBrush = _border;
            panel.Children.Add(input);
        }
        private Button Make(string text, RoutedEventHandler click)
        { var b = new Button { Content = text, Margin = new Thickness(6), Padding = new Thickness(12, 6, 12, 6), Background = _field, Foreground = _text, BorderBrush = _border }; b.Click += click; b.Template = ButtonTemplate(_field, _border, _text); return b; }
    }
}
