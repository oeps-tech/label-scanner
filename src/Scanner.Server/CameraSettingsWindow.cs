using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Scanner.Server.Services;

namespace Scanner.Server;

public sealed class CameraSettingsWindow : Window
{
    private readonly Dictionary<string, TextBox> _numbers = [];
    private readonly CheckBox _autofocus = new() { Content = "Auto focus" };
    private readonly CheckBox _autoExposure = new() { Content = "Auto exposure" };
    private readonly CheckBox _autoWhiteBalance = new() { Content = "Auto white balance" };
    public CameraSettings Result { get; private set; }
    public CameraSettingsWindow(CameraSettings current, JsonElement? device, WorkerProcess worker, Func<CameraSettings, Task> preview)
    {
        Result = current; Title = "Camera settings"; Width = 620; Height = 730; MinWidth = 500; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Capture settings are independent of remote preview. Applied values come from the device; a camera may reject a request.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        AddNumber(panel, "width", "Capture width (pixels)", current.Width);
        AddNumber(panel, "height", "Capture height (pixels)", current.Height);
        AddNumber(panel, "fps", "Frame rate", current.Fps);
        _autofocus.IsChecked = current.Autofocus; panel.Children.Add(_autofocus);
        AddNumber(panel, "focus", "Manual focus", current.Focus);
        _autoExposure.IsChecked = current.AutoExposure; panel.Children.Add(_autoExposure);
        AddNumber(panel, "exposure", "Manual exposure", current.Exposure);
        AddNumber(panel, "gain", "Gain", current.Gain);
        _autoWhiteBalance.IsChecked = current.AutoWhiteBalance; panel.Children.Add(_autoWhiteBalance);
        AddNumber(panel, "white_balance", "White balance", current.WhiteBalance);
        AddNumber(panel, "power_line_frequency", "Power-line frequency (50 / 60 Hz)", current.PowerLineFrequency);
        _numbers["power_line_frequency"].IsEnabled = false;
        _numbers["power_line_frequency"].ToolTip = "OpenCV's Windows capture backend does not expose power-line frequency. Use the device's native settings if available.";
        foreach (var name in new[] { "focus", "exposure", "gain", "white_balance" })
        {
            bool supported = device is { } data && IsSupported(data, name);
            _numbers[name].IsEnabled = supported;
            _numbers[name].ToolTip = supported ? "Device units; actual value is read back after applying." : "Not verified as supported by the connected camera.";
        }
        _autofocus.IsEnabled = device is { } af && IsSupported(af, "autofocus");
        _autoExposure.IsEnabled = device is { } ae && IsSupported(ae, "auto_exposure");
        _autoWhiteBalance.IsEnabled = device is { } aw && IsSupported(aw, "auto_white_balance");
        panel.Children.Add(new TextBlock { Text = "Device readback", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        var readback = new TextBox { Text = device is { } d ? EditorWindow.Pretty(d) : "Connect a camera to inspect capabilities and actual applied settings.", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 145, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(readback);
        void Receive(JsonElement message)
        {
            if (!message.TryGetProperty("camera", out _)) return;
            Dispatcher.BeginInvoke(() =>
            {
                readback.Text = EditorWindow.Pretty(message);
                if (!message.TryGetProperty("capabilities", out _)) return;
                foreach (var name in new[] { "focus", "exposure", "gain", "white_balance" }) _numbers[name].IsEnabled = IsSupported(message, name);
                _autofocus.IsEnabled = IsSupported(message, "autofocus"); _autoExposure.IsEnabled = IsSupported(message, "auto_exposure"); _autoWhiteBalance.IsEnabled = IsSupported(message, "auto_white_balance");
            });
        }
        worker.Status += Receive; Closed += (_, _) => worker.Status -= Receive;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick };
        panel.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var defaults = new Button { Content = "Restore defaults" };
        defaults.Click += (_, _) => { var def = new CameraSettings(); _numbers["width"].Text = def.Width.ToString(); _numbers["height"].Text = def.Height.ToString(); _numbers["fps"].Text = def.Fps.ToString(CultureInfo.InvariantCulture); _autofocus.IsChecked = _autoExposure.IsChecked = _autoWhiteBalance.IsChecked = true; foreach (var name in new[] { "focus", "exposure", "gain", "white_balance" }) _numbers[name].Clear(); };
        var test = new Button { Content = "Preview" };
        test.Click += async (_, _) => { try { test.IsEnabled = false; Result = Read(current); await preview(Result); error.Text = "Preview settings applied. Cancel restores the previous requested settings."; } catch (Exception ex) { error.Text = ex.Message; } finally { test.IsEnabled = true; } };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => Close();
        var apply = new Button { Content = "Apply", Style = (Style)FindResource("PrimaryButton") };
        apply.Click += (_, _) => { try { Result = Read(current); DialogResult = true; } catch (Exception ex) { error.Text = ex.Message; } };
        foreach (var button in new[] { defaults, test, cancel, apply }) buttons.Children.Add(button);
        panel.Children.Add(buttons); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static bool IsSupported(JsonElement device, string name)
    {
        if (!device.TryGetProperty("capabilities", out var caps) || !caps.TryGetProperty(name, out var value)) return false;
        return value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True);
    }
    private void AddNumber(Panel panel, string name, string label, double? value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var text = new TextBlock { Text = label, Width = 290, VerticalAlignment = VerticalAlignment.Center };
        var input = new TextBox { Text = value?.ToString(CultureInfo.InvariantCulture) ?? "", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(text); row.Children.Add(input); _numbers[name] = input; panel.Children.Add(row);
    }
    private CameraSettings Read(CameraSettings previous)
    {
        double? N(string name) => string.IsNullOrWhiteSpace(_numbers[name].Text) ? null : double.Parse(_numbers[name].Text, CultureInfo.InvariantCulture);
        var result = previous with { Width = checked((int)(N("width") ?? 0)), Height = checked((int)(N("height") ?? 0)), Fps = N("fps") ?? 0,
            Autofocus = _autofocus.IsChecked == true, Focus = N("focus"), AutoExposure = _autoExposure.IsChecked == true, Exposure = N("exposure"), Gain = N("gain"), AutoWhiteBalance = _autoWhiteBalance.IsChecked == true, WhiteBalance = N("white_balance") };
        new ServerSettings { Camera = result }.Validate(); return result;
    }
}
