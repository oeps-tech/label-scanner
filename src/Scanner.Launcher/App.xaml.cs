using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Scanner.Desktop;
using Scanner.Updates;

namespace Scanner.Launcher;

public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var app = Argument(e.Args, "--app") == "TestClient" ? "TestClient" : "Server";
        if (AppInstanceCoordinator.IsAppRunning(app)) { AppInstanceCoordinator.ActivateExisting(app); Shutdown(); return; }
        _mutex = new Mutex(false, AppInstanceCoordinator.LauncherMutexName("Package"), out var created);
        if (!created) { MessageBox.Show("OEPS Scanner is already starting. Please wait for its window.", "OEPS Scanner"); Shutdown(); return; }
        MainWindow = new LaunchWindow(e.Args, app); MainWindow.Show();
    }
    internal static string? Argument(string[] args, string key)
    {
        var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}

internal sealed class LaunchWindow : Window
{
    private readonly string[] _args;
    private readonly string _app;
    private readonly string _userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OEPS", "Scanner", "installation");
    private readonly TextBlock _status = new() { Text = "Validating the installed application…", TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Content = "Retry", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly CancellationTokenSource _closing = new();
    private bool _busy, _recover;

    public LaunchWindow(string[] args, string app)
    {
        _args = args; _app = app;
        Title = "OEPS Scanner — starting " + app; Width = 520; Height = 220; MinWidth = 440; MinHeight = 200; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new DockPanel { Margin = new Thickness(22) }; DockPanel.SetDock(_retry, Dock.Bottom); panel.Children.Add(_retry); panel.Children.Add(_status); Content = panel;
        _retry.Click += async (_, _) => await StartAsync(); Loaded += async (_, _) => await StartAsync(); Closed += (_, _) => _closing.Cancel();
    }
    private async Task StartAsync()
    {
        if (_busy) return;
        _busy = true; _retry.IsEnabled = false;
        try
        {
            var store = new UpdateStore(_userRoot);
            var state = store.LoadState();
            InstalledVersion? candidate = null;
            var localPackage = _recover ? null : App.Argument(_args, "--install-package");
            string? updateError = null;
            try
            {
                if (localPackage is not null)
                {
                    var path = Path.GetFullPath(localPackage);
                    var name = Path.GetFileName(path);
                    const string prefix = "OEPS.Scanner-", suffix = "-win-x64.zip";
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidDataException("Invalid versioned package filename.");
                    var version = SemanticVersion.Parse(name[prefix.Length..^suffix.Length]);
                    if (state.Current is null || version.CompareTo(SemanticVersion.Parse(state.Current.Version)) > 0)
                    {
                        _status.Text = "Checking SHA-256 and staging the bundled package…";
                        var checksum = UpdateStore.ParseChecksum(await File.ReadAllTextAsync(path + ".sha256", _closing.Token), name);
                        candidate = await store.StagePackageAsync(path, checksum, version, cancellationToken: _closing.Token);
                    }
                }
                // A working offline station starts without a network dependency. Updates are explicit.
                if (!_recover && _args.Contains("--check-updates", StringComparer.Ordinal))
                {
                    _status.Text = "Checking published OEPS Scanner releases…";
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                    var latest = await new GitHubReleaseClient(http, "oeps-tech", "label-scanner", "OEPS.Scanner").GetLatestReleaseAsync(_closing.Token);
                    var current = candidate?.Version ?? state.Current?.Version;
                    if (latest is not null && (current is null || latest.Version.CompareTo(SemanticVersion.Parse(current)) > 0)
                        && MessageBox.Show(this, $"Install OEPS Scanner {latest.VersionText}? Current version: {current ?? "none"}.", "Update available", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        candidate = await store.DownloadAndStageAsync(http, latest, _closing.Token);
                }
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return; }
            catch (Exception ex) { updateError = ex.Message; Log("Package/update: " + ex.Message); }
            var candidates = new[] { candidate, state.Current, state.Previous }.OfType<InstalledVersion>().DistinctBy(x => x.DirectoryName).ToArray();
            if (candidates.Length == 0) throw new InvalidDataException("No working application is installed. Open a full release installer or provide --install-package with its ZIP and SHA-256 file. " + updateError);
            var errors = new List<string>();
            foreach (var installed in candidates)
            {
                if (AppInstanceCoordinator.IsAppRunning(_app)) { AppInstanceCoordinator.ActivateExisting(_app); Close(); return; }
                _status.Text = $"Starting {_app} v{installed.Version}…";
                try
                {
                    var directory = Path.GetDirectoryName(store.GetExecutablePath(installed))!;
                    await LaunchAsync(Path.Combine(directory, $"Scanner.{_app}.exe"));
                    store.MarkWorking(installed); Close(); return;
                }
                catch (StartupStillRunningException) { _recover = true; throw; }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(installed.Version + ": " + ex.Message); Log(errors[^1]); }
            }
            throw new InvalidOperationException("Startup and saved-version recovery failed. " + string.Join(" ", errors));
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = ex.Message; Log(ex.Message); _retry.IsEnabled = true; }
        finally { _busy = false; }
    }
    private async Task LaunchAsync(string executable)
    {
        var eventName = @"Local\OEPS.Scanner.Ready." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
        var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "runtime"));
        if (File.Exists(Path.Combine(runtime, "dotnet.exe"))) { start.Environment["DOTNET_ROOT_X64"] = runtime; start.Environment["DOTNET_ROOT"] = runtime; }
        start.ArgumentList.Add("--startup-ready"); start.ArgumentList.Add(eventName);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the app.");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (ready.WaitOne(0)) return;
            if (process.HasExited) throw new InvalidOperationException($"Application exited before ready (exit code {process.ExitCode}).");
            await Task.Delay(100, _closing.Token);
        }
        if (process.HasExited) throw new InvalidOperationException($"Application exited before ready (exit code {process.ExitCode}).");
        throw new StartupStillRunningException("The app has not reported ready. Its window may show an error. Close that app before retrying; the saved working version is preserved.");
    }
    private void Log(string message)
    {
        try { Directory.CreateDirectory(_userRoot); var path = Path.Combine(_userRoot, "launcher.log"); if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024) File.WriteAllText(path, ""); File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}"); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed class StartupStillRunningException(string message) : Exception(message);
}
