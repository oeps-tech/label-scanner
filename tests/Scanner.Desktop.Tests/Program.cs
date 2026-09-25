using System.IO;
using System.Windows;
using System.Windows.Threading;
using Scanner.Contracts;
using Scanner.Server.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool yes, string reason) { if (!yes) throw new Exception(reason); _checks++; }
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Scanner.Desktop;component/Theme.xaml", UriKind.Relative) });
            var root = Path.Combine(Path.GetTempPath(), "oeps-datamatrix-ui-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(["--data-dir", root]);
            File.WriteAllText(paths.SettingsFile, "{\"camera\":{\"index\":1,\"width\":640,\"height\":480,\"fps\":30},\"profile\":{\"legacy\":true},\"local_preview_enabled\":false}");
            var model = new Scanner.Server.ViewModels.MainViewModel(paths);
            var server = new Scanner.Server.MainWindow(model, paths, []);
            Layout(server, 1100, 780);
            Check(!model.PreviewEnabled && model.SelectedCamera?.Index == 1, "Existing camera/preview settings migrate without obsolete profile dependency");
            Check(!Directory.Exists(Path.Combine(root, "images")) && !File.Exists(Path.Combine(root, "scanner.sqlite3")) && !Directory.Exists(Path.Combine(root, "models")), "No evidence/model/audit directories created");
            var client = new Scanner.TestClient.MainWindow(Path.Combine(root, "client.json"));
            Layout(client, 720, 560);
            var vm = (Scanner.TestClient.MainViewModel)client.DataContext;
            vm.Display(new LabelData(1, "OEPSA010123", "0926-TRAY-Q5R2", "0.123456789012345678901234567890"));
            Check(vm.Quantity == "0.123456789012345678901234567890", "Client keeps quantity precision");
            Check(vm.Json.Contains("\"quantity\": \"0."), "Client shows quantity as JSON string");
            Close(client); Close(server);
            Check(!File.Exists(Path.Combine(root, "scanner.sqlite3")), "Shutdown does not create audit storage");
            Console.WriteLine($"PASS: {_checks} desktop layout, settings migration, data display and shutdown assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Layout(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        Check(Math.Abs(content.ActualWidth + content.Margin.Left + content.Margin.Right - width) < .01, "Window XAML loads and lays out without a native window");
    }
    private static void Close(Window window)
    {
        var closed = false; window.Closed += (_, _) => closed = true;
        window.Close(); window.Close();
        for (var i = 0; i < 50 && !closed; i++)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame); timer.Stop();
        }
        Check(closed, "Asynchronous shutdown completes");
    }
}
