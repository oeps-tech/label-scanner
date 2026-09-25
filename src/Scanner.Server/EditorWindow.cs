using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scanner.Contracts;

namespace Scanner.Server;

/// <summary>Advanced profile/template editing with parse validation and an explicit apply boundary.</summary>
public sealed class EditorWindow : Window
{
    private readonly TextBox _editor;
    private readonly TextBlock _error;
    public string Result => _editor.Text;
    public EditorWindow(string title, string explanation, string text, bool readOnly = false, Func<string, string?>? validate = null, System.Windows.Media.Imaging.BitmapSource? referenceImage = null)
    {
        Title = title; Width = 820; Height = 660; MinWidth = 550; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var heading = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var close = new Button { Content = readOnly ? "Close" : "Cancel", MinWidth = 100, IsCancel = true };
        close.Click += (_, _) => Close(); bottom.Children.Add(close);
        _editor = new TextBox { Text = text, IsReadOnly = readOnly, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 13,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        _error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        if (!readOnly)
        {
            var apply = new Button { Content = "Apply", MinWidth = 100, Style = (Style)FindResource("PrimaryButton") };
            apply.Click += (_, _) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(Result);
                    var error = validate?.Invoke(Result);
                    if (error is not null) { _error.Text = error; return; }
                    DialogResult = true;
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException) { _error.Text = ex.Message; }
            };
            bottom.Children.Add(apply);
        }
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        DockPanel.SetDock(_error, Dock.Bottom); panel.Children.Add(_error);
        if (referenceImage is null) panel.Children.Add(_editor);
        else
        {
            Width = 1060;
            var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var reference = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            reference.Children.Add(new TextBlock { Text = "Supplied reference · initial region geometry", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            reference.Children.Add(new Image { Source = referenceImage, Stretch = Stretch.Uniform });
            layout.Children.Add(_editor); Grid.SetColumn(reference, 1); layout.Children.Add(reference); panel.Children.Add(layout);
        }
        Content = panel;
    }
    public static string Pretty<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true });
}
