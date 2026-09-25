using System.IO;
using System.Windows;
using Scanner.Desktop;
using Scanner.Server.Services;
using Scanner.Server.ViewModels;

namespace Scanner.Server;

public partial class App : Application
{
    private AppInstanceCoordinator? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var smoke = e.Args.Contains("--ui-smoke");
            if (!smoke)
            {
                _instance = AppInstanceCoordinator.TryAcquire("Server", () => Dispatcher.BeginInvoke(() => { if (MainWindow is { } window) { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); } }));
                if (_instance is null) { Shutdown(); return; }
            }
            var paths = new AppPaths(e.Args);
            var model = new MainViewModel(paths);
            MainWindow = new MainWindow(model, paths, e.Args); MainWindow.Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "OEPS Scanner could not start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
