using System.Windows;
using System.IO;
using Scanner.Desktop;

namespace Scanner.TestClient;

public partial class App : Application
{
    private AppInstanceCoordinator? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--ui-smoke");
        if (!smoke)
        {
        _instance = AppInstanceCoordinator.TryAcquire("TestClient", () => Dispatcher.Invoke(() =>
        {
            if (MainWindow is null) return;
            MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        }));
        if (_instance is null) { Shutdown(); return; }
        }
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        var settingsPath = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.Combine(Path.GetFullPath(e.Args[dataIndex + 1]), "client-settings.json") : null;
        MainWindow = new MainWindow(settingsPath);
        if (smoke) { MainWindow.ShowInTaskbar = false; MainWindow.WindowStartupLocation = WindowStartupLocation.Manual; MainWindow.Left = MainWindow.Top = -15000; }
        MainWindow.ContentRendered += (_, _) => AppInstanceCoordinator.SignalReadyFromArguments(e.Args);
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
