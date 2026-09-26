using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scanner.Updates;

namespace Scanner.Desktop;

public partial class ReleaseFooter : UserControl
{
    private readonly string _version = (typeof(ReleaseFooter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0").Split('+')[0];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private CancellationTokenSource? _stop;
    private bool _checking;

    public ReleaseFooter()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + _version;
        _timer.Tick += async (_, _) => await CheckAsync();
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--ui-smoke")) return;
            _stop?.Dispose(); _stop = new(); _timer.Start(); await CheckAsync();
        };
        Unloaded += (_, _) => { _timer.Stop(); _stop?.Cancel(); };
    }
    private async Task CheckAsync()
    {
        if (_checking || _stop is null || _stop.IsCancellationRequested) return;
        _checking = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var latest = await new GitHubReleaseClient(http, "oeps-tech", "label-scanner", "OEPS.Scanner").GetLatestReleaseAsync(_stop.Token);
            if (!_stop.IsCancellationRequested && latest is not null && latest.Version.CompareTo(SemanticVersion.Parse(_version)) > 0)
            {
                UpdateNotice.Visibility = Visibility.Visible;
                UpdateLink.ToolTip = $"Version {latest.VersionText} is available. Close both apps and reopen an installed shortcut to update.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.IO.InvalidDataException or FormatException) { }
        finally { _checking = false; }
    }
    private void ShowUpdateInstructions(object sender, RoutedEventArgs e) => MessageBox.Show(Window.GetWindow(this),
        "Close both OEPS Scanner windows, then reopen an installed shortcut. The launcher will offer the new version. You can choose No to keep using the installed version.",
        "OEPS Scanner update", MessageBoxButton.OK, MessageBoxImage.Information);
}
