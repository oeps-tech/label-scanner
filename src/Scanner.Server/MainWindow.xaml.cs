using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scanner.Desktop;
using Scanner.Server.Services;
using Scanner.Server.ViewModels;

namespace Scanner.Server;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _closing, _disposed;
    private bool _showingDetails;
    private double _detailsWidth = 1180, _detailsHeight = 880;
    private LabelVersionsWindow? _labelVersions;
    public MainWindow(MainViewModel model, AppPaths paths, string[] args)
    {
        InitializeComponent(); _model = model; DataContext = model;
        ApplyDetailsLayout();
        model.PropertyChanged += ModelPropertyChanged;
        Closed += (_, _) => model.PropertyChanged -= ModelPropertyChanged;
        var smoke = args.Contains("--ui-smoke");
        if (smoke) { ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -15000; Top = -15000; }
        Loaded += async (_, _) =>
        {
            await model.InitializeAsync(smoke);
            AppInstanceCoordinator.SignalReadyFromArguments(args);
            if (smoke)
            {
                try
                {
                    await Task.Delay(200); UpdateLayout();
                    var content = (FrameworkElement)Content;
                    var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var output = File.Create(Path.Combine(paths.Root, "server-ui-smoke.png"))) encoder.Save(output);
                    await File.WriteAllTextAsync(Path.Combine(paths.Root, "server-ui-smoke.txt"), $"PASS: WPF window loaded; dimensions {ActualWidth} x {ActualHeight}; camera disconnected; Data Matrix only; no preloaded event.\n");
                }
                catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(paths.Root, "server-ui-smoke.txt"), "FAIL: " + ex); Environment.ExitCode = 1; }
                Close();
            }
        };
        Closing += OnClosing;
    }
    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowDetails)) ApplyDetailsLayout();
    }
    private void OpenLabelVersions(object sender, RoutedEventArgs e)
    {
        if (_labelVersions is not null) { _labelVersions.Activate(); return; }
        _labelVersions = new LabelVersionsWindow { Owner = this };
        _labelVersions.Closed += (_, _) => _labelVersions = null;
        _labelVersions.Show();
    }
    private void ApplyDetailsLayout()
    {
        var previousBounds = !IsLoaded ? Rect.Empty : WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (_model.ShowDetails)
        {
            MaxWidth = MaxHeight = double.PositiveInfinity;
            MinWidth = 800; MinHeight = 600;
            Width = _detailsWidth; Height = _detailsHeight;
            ResizeMode = ResizeMode.CanResize;
        }
        else
        {
            if (_showingDetails)
            {
                var bounds = WindowState == WindowState.Normal ? new Rect(0, 0, Width, Height) : RestoreBounds;
                if (!bounds.IsEmpty) { _detailsWidth = bounds.Width; _detailsHeight = bounds.Height; }
            }
            WindowState = WindowState.Normal;
            MinWidth = 900; MinHeight = 350;
            MaxWidth = Width = 900; MaxHeight = Height = 350;
            ResizeMode = ResizeMode.CanMinimize;
        }
        if (!previousBounds.IsEmpty)
        {
            Left = previousBounds.Left + (previousBounds.Width - Width) / 2;
            Top = previousBounds.Top + (previousBounds.Height - Height) / 2;
        }
        _showingDetails = _model.ShowDetails;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_disposed) return; e.Cancel = true; if (_closing) return; _closing = true;
        try { await _model.DisposeAsync(); } finally { _disposed = true; _ = Dispatcher.BeginInvoke(() => Close()); }
    }
}
