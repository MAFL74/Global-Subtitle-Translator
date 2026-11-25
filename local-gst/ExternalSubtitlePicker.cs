using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Local_GST;

internal static class ExternalSubtitlePicker
{
    public static ExternalSubtitleOption? Choose(Window owner, IReadOnlyList<ExternalSubtitleOption> options)
    {
        var window = new Window
        {
            Title = "Velg ekstern undertekst",
            Owner = owner,
            Width = 700,
            Height = 430,
            MinWidth = 520,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Background = Brush(owner, "WindowBrush", "#171A21"),
            Foreground = Brush(owner, "TextBrush", "#E8ECF2")
        };
        var field = Brush(owner, "FieldBrush", "#2B313D");
        var border = Brush(owner, "BorderBrushTheme", "#3A4351");
        var text = Brush(owner, "TextBrush", "#E8ECF2");
        var selection = Brush(owner, "SelectionBrush", "#3C4A5D");
        var selectionText = Brush(owner, "SelectionTextBrush", "#F4F7FB");
        var list = new ListBox { Margin = new Thickness(0, 12, 0, 12), DisplayMemberPath = null, Background = field, Foreground = text, BorderBrush = border };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, text));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        var itemRoot = new FrameworkElementFactory(typeof(Border));
        itemRoot.Name = "ItemBorder";
        itemRoot.SetValue(Border.BackgroundProperty, field);
        var itemContent = new FrameworkElementFactory(typeof(ContentPresenter));
        itemContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        itemContent.SetValue(TextElement.ForegroundProperty, text);
        itemRoot.AppendChild(itemContent);
        var itemTemplate = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = itemRoot };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, selection, "ItemBorder"));
        selected.Setters.Add(new Setter(TextElement.ForegroundProperty, selectionText, "ItemBorder"));
        itemTemplate.Triggers.Add(selected);
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
        list.ItemContainerStyle = itemStyle;
        foreach (var option in options) list.Items.Add(option);
        list.SelectedIndex = 0;
        var message = new TextBlock
        {
            Text = "Velg hvilken utgave som passer best til videofilens release-type. Treffet lastes ned som engelsk SRT.",
            TextWrapping = TextWrapping.Wrap
        };
        var choose = MakeButton("Bruk valgt undertekst", field, border, text);
        choose.MinWidth = 170; choose.IsDefault = true; choose.IsEnabled = options.Count > 0;
        var cancel = MakeButton("Avbryt", field, border, text);
        cancel.MinWidth = 90; cancel.IsCancel = true; cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(choose);
        buttons.Children.Add(cancel);
        var layout = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(message, Dock.Top);
        layout.Children.Add(buttons);
        layout.Children.Add(message);
        layout.Children.Add(list);
        window.Content = layout;
        choose.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        return window.ShowDialog() == true ? list.SelectedItem as ExternalSubtitleOption : null;
    }

    private static Brush Brush(Window owner, string key, string fallback) => owner.Resources[key] as Brush
        ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private static Button MakeButton(string title, Brush background, Brush border, Brush foreground)
    {
        var button = new Button { Content = title, Padding = new Thickness(12, 7, 12, 7), Background = background, BorderBrush = border, Foreground = foreground };
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, background);
        root.SetValue(Border.BorderBrushProperty, border);
        root.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(TextElement.ForegroundProperty, foreground);
        content.SetValue(FrameworkElement.MarginProperty, button.Padding);
        root.AppendChild(content);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = root };
        return button;
    }
}
