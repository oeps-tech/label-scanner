using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Scanner.Contracts;
using Scanner.Server.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool yes, string reason) { if (!yes) throw new Exception(reason); _checks++; }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--list-readers"))
            {
                foreach (var reader in BluetoothReaderDiscovery.Discover()) Console.WriteLine(reader.DisplayName + " | " + reader.DeviceId);
                return 0;
            }
            if (args.Contains("--probe-reader"))
            {
                var reader = BluetoothReaderDiscovery.Discover().Single();
                ProbeReaderAsync(reader).GetAwaiter().GetResult();
                return 0;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Scanner.Desktop;component/Theme.xaml", UriKind.Relative) });
            BarcodeReaderChecks.Run(Check);
            var root = Path.Combine(Path.GetTempPath(), "oeps-datamatrix-ui-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(["--data-dir", root]);
            File.WriteAllText(paths.SettingsFile, "{\"camera\":{\"index\":1,\"width\":640,\"height\":480,\"fps\":30},\"profile\":{\"legacy\":true},\"local_preview_enabled\":false}");
            var model = new Scanner.Server.ViewModels.MainViewModel(paths);
            var server = new Scanner.Server.MainWindow(model, paths, []);
            Layout(server, 1100, 780);
            Check(!model.PreviewEnabled && model.SelectedCamera?.Index == 1, "Existing camera/preview settings migrate without obsolete profile dependency");
            Check(!Directory.Exists(Path.Combine(root, "images")) && !File.Exists(Path.Combine(root, "scanner.sqlite3")) && !Directory.Exists(Path.Combine(root, "models")), "No evidence/model/audit directories created");
            VerifyServerRouting(model);
            var client = new Scanner.TestClient.MainWindow(Path.Combine(root, "client.json"));
            Layout(client, 720, 560);
            var vm = (Scanner.TestClient.MainViewModel)client.DataContext;
            vm.Display(new LabelData(1, "OEPSA010123", "0926-TRAY-Q5R2", "0.123456789012345678901234567890"));
            Check(vm.Quantity == "0.123456789012345678901234567890", "Client keeps quantity precision");
            Check(vm.Json.Contains("\"quantity\": \"0."), "Client shows quantity as JSON string");
            vm.Display(new LabelData(2, 857, "S/2680", 1923, "WH/OUT/00567"));
            Layout(client, 720, 560);
            Check(vm.Fields.Count == 5 && vm.Fields.Any(f => f.Name == "Document name" && f.Value == "S/2680")
                && vm.Fields.Any(f => f.Name == "OUT number" && f.Value == "WH/OUT/00567"), "Client displays all version 2 data including document_name");
            Check(!vm.Json.Contains("quantity") && !vm.Json.Contains("oeps_pn"), "Switching to version 2 leaves no stale material data");
            vm.Display(new LabelData(1, "OEPSA010123", "0926-TRAY-Q5R2", "0"));
            Check(vm.Fields.Count == 4 && vm.Quantity == "0" && !vm.Json.Contains("document_name"), "Client switches back to version 1 cleanly");
            vm.Display(LabelData.FromSupplier(null, "74LVC4245AD,118", "200"));
            Check(vm.Fields.Any(f => f.Name == "MPN" && f.Value == "74LVC4245AD,118") && vm.Json.Contains("\"oeps_pn\": null"), "Client shows supplier values and explicit missing OEPS PN");
            vm.Display(LabelData.FromRaw("\tOEPS-1234\r\nnext\x1d"));
            Check(vm.LabelVersion == "X" && vm.Fields.Count == 2 && vm.Fields[1].Value == "\tOEPS-1234\r\nnext\x1d" && !vm.Json.Contains("mpn"), "Raw display preserves controls and removes stale supplier fields");
            vm.Display(LabelData.FromCommand("For Odoo OUT"));
            Check(vm.LabelVersion == "C" && vm.Fields.Count == 2 && vm.Fields[1] == new Scanner.TestClient.DisplayField("Command", "For Odoo OUT")
                && !vm.Json.Contains("raw") && !vm.Json.Contains("quantity"), "Client shows command C without stale raw or quantity fields");
            vm.Display(new LabelData(1, "OEPSA010123", "0926-TRAY-Q5R2", "1"));
            Check(vm.Fields.Count == 4 && !vm.Json.Contains("command"), "Client switches back from command to material data");
            Check(LabelVersionReference.All.Select(v => v.Version).SequenceEqual(new[] { "1", "2", "3", "C", "X" }), "Label reference lists all versions");
            foreach (var reference in LabelVersionReference.All)
                Check(LabelClassifier.Classify(reference.ExamplePayload).Data == PayloadValidator.ReadClientJson(reference.ExampleJson), "Label reference examples agree with live classification and JSON schema");
            var referenceWindow = new Scanner.Server.LabelVersionsWindow();
            Layout(referenceWindow, 840, 850);
            Close(referenceWindow);
            Close(client); Close(server);
            Check(!File.Exists(Path.Combine(root, "scanner.sqlite3")), "Shutdown does not create audit storage");
            Console.WriteLine($"PASS: {_checks} desktop layout, settings migration, data display and shutdown assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task ProbeReaderAsync(BarcodeReaderChoice reader)
    {
        await using var connection = new BarcodeReaderConnection();
        connection.StatusChanged += (_, message) => Console.WriteLine(message);
        connection.Diagnostic += Console.WriteLine;
        connection.ScanReceived += raw => Console.WriteLine($"Received scan: {raw.Length} characters; version {LabelClassifier.Classify(raw).Data.VersionText}");
        await connection.ConnectAsync(reader);
        Console.WriteLine("Scan one label now; listening for 30 seconds.");
        await Task.Delay(30000);
        await connection.DisconnectAsync();
    }
    private static void VerifyServerRouting(Scanner.Server.ViewModels.MainViewModel model)
    {
        // Feed decoded frames through the shipping GUI pipeline, without opening a physical camera.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = model.GetType();
        var sockets = (Scanner.Core.ScannerSocketHost)type.GetField("_sockets", flags)!.GetValue(model)!;
        var connected = type.GetField("_connected", flags)!;
        sockets.StartAsync(0).GetAwaiter().GetResult();
        using var client = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(5000);
        try
        {
            client.ConnectAsync(new Uri($"ws://127.0.0.1:{sockets.Port}/labels"), timeout.Token).GetAwaiter().GetResult();
            Check(SpinWait.SpinUntil(() => sockets.LabelClientCount == 1, 2000), "Frame-routing test client connects");
            connected.SetValue(model, true);
            const string supplier = "{pbn:pick,on:order,pc:stock,pm:INA826AIDR,qty:100}";
            const string noQuantity = "{pbn:pick,on:order,pc:stock,pm:INA826AIDR}";
            const string noIdentifiers = "[)>\x1e" + "06\x1dQ100\x1e\x04";
            const string raw = "\tunknown\r\nline\x1d";
            var frame = JsonSerializer.SerializeToElement(new { generation = 0, capture_fps = 30, captured_at = DateTimeOffset.UtcNow,
                frame_sequence = 1, read_ms = 1, decode_ms = 1, codes = new[] { noQuantity, noQuantity, supplier, supplier, noIdentifiers, noIdentifiers, raw, raw }.Select(text => new { raw = text }).ToArray() });
            type.GetMethod("ProcessFrame", flags)!.Invoke(model, [frame]);
            var first = WebSocketMessages.ReceiveAsync(client, ProtocolJson.MaximumLabelBytes, timeout.Token).GetAwaiter().GetResult()!;
            var second = WebSocketMessages.ReceiveAsync(client, ProtocolJson.MaximumLabelBytes, timeout.Token).GetAwaiter().GetResult()!;
            var third = WebSocketMessages.ReceiveAsync(client, ProtocolJson.MaximumLabelBytes, timeout.Token).GetAwaiter().GetResult()!;
            var fourth = WebSocketMessages.ReceiveAsync(client, ProtocolJson.MaximumLabelBytes, timeout.Token).GetAwaiter().GetResult()!;
            Check(PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(first.Bytes)) == LabelData.FromRaw(noQuantity), "Actual server frame path sends supplier without quantity as X");
            Check(PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(second.Bytes)) == LabelData.FromSupplier(null, "INA826AIDR", "100"), "Actual server frame path sends valid supplier with null OEPS PN");
            Check(PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(third.Bytes)) == LabelData.FromRaw(noIdentifiers), "Actual server frame path sends supplier without either identifier as X, preserving controls");
            Check(PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(fourth.Bytes)) == LabelData.FromRaw(raw), "Actual server frame path sends untouched raw X");
            var unexpected = WebSocketMessages.ReceiveAsync(client, ProtocolJson.MaximumLabelBytes, timeout.Token);
            Check(!unexpected.Wait(100), "Every distinct code is sent once; repeated codes are suppressed");
            Check(model.Diagnostics[0].Status.StartsWith("Raw X") && model.Diagnostics[4].Status.StartsWith("Raw X")
                && model.Diagnostics.Count(d => d.Status.Contains("suppressed")) == 4, "Server diagnostics show raw fallback and shared cooldown for every reading");
            client.Abort();
            try { unexpected.GetAwaiter().GetResult(); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
        finally { connected.SetValue(model, false); client.Abort(); sockets.StopAsync().GetAwaiter().GetResult(); }
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
