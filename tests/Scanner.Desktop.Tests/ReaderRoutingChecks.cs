using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Scanner.Contracts;
using Scanner.Core;
using Scanner.Desktop;
using Scanner.Server.Services;
using Scanner.Server.ViewModels;

internal static class ReaderRoutingChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "oeps-reader-routing-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(["--data-dir", root]);
        var selected = new BarcodeReaderChoice("AE3B00256A8D_C00000000", "BCST-73-6A8D-SPP", "COM3");
        File.WriteAllText(paths.SettingsFile, ProtocolJson.Serialize(new ServerSettings { CameraEnabled = false, BarcodeReaderDeviceId = selected.DeviceId }));
        var fake = new FakeReader(); long now = 100000;
        IReadOnlyList<BarcodeReaderChoice> readers = [selected];
        var model = new MainViewModel(paths, fake, () => readers, () => now);
        var camera = (ScannerSocketHost)Field(model, "_sockets");
        var reader = (ScannerSocketHost)Field(model, "_readerSockets");
        using var timeout = new CancellationTokenSource(20000);
        using var cameraClient = new ClientWebSocket();
        using var readerClient = new ClientWebSocket();
        using var secondReader = new ClientWebSocket();
        try
        {
            camera.StartAsync(0).GetAwaiter().GetResult(); reader.StartAsync(0).GetAwaiter().GetResult();
            typeof(MainViewModel).GetField("_initialized", Private)!.SetValue(model, true);
            Execute(model.RefreshBarcodeReadersCommand);
            check(model.SelectedBarcodeReader == selected && fake.Opens == 0, "Startup discovers saved reader without opening hardware when no clients");
            check(model.BarcodeReaderPort == 8766 && model.Port == 8765 && model.BarcodeReaderFastScans, "Separate default ports and fast CRLF mode");
            readers = [selected with { PortName = "COM8" }];
            Execute(model.ConnectBarcodeReaderCommand);
            check(fake.Opened?.PortName == "COM8" && model.KeepBarcodeReaderConnected && fake.FastScans, "Manual connect resolves COM reassignment and enables debug override");
            fake.Emit("no-client");
            check(model.Diagnostics.Single().Status.Contains("no clients"), "No-client readings do not create a replay backlog");
            Execute(model.ConnectBarcodeReaderCommand);
            check(!model.KeepBarcodeReaderConnected && fake.Disconnects == 1, "Manual disconnect clears debug override");

            Connect(cameraClient, camera.Port, timeout.Token);
            Until(() => camera.LabelClientCount == 1); Step();
            check(fake.Opens == 1, "Camera-only client does not open barcode reader");
            Connect(readerClient, reader.Port, timeout.Token);
            Until(() => fake.Opens == 2 && model.BarcodeReaderButtonText == "Disconnect");
            check(!model.KeepBarcodeReaderConnected, "Reader-port client opens reader immediately without debug override");
            check(!(bool)Field(model, "_connected") && !(bool)Field(model, "_cameraPending"), "Reader client does not open disabled camera");
            Connect(secondReader, reader.Port, timeout.Token); Until(() => reader.LabelClientCount == 2); Step();
            check(fake.Opens == 2, "Additional reader clients share one hardware connection");
            check(!model.ConnectBarcodeReaderCommand.CanExecute(null), "Active clients keep reader connected");

            var cameraWaiting = WebSocketMessages.ReceiveAsync(cameraClient, ProtocolJson.MaximumLabelBytes, timeout.Token);
            var fast = new BarcodeScanBuffer(true);
            fast.Append(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("same\r\n", 30))), 0);
            while (fast.TakeCompleted(0) is { } raw) fake.Emit(raw);
            for (var i = 0; i < 30; i++)
            {
                check(Receive(readerClient, timeout.Token) == LabelData.FromRaw("same"), "Every rapid identical scan reaches first reader client");
                check(Receive(secondReader, timeout.Token) == LabelData.FromRaw("same"), "Every rapid identical scan reaches second reader client");
            }
            check(!cameraWaiting.IsCompleted, "Reader scans never reach camera clients");
            foreach (var payload in new[] { "1*OEPSA010123*0926-TRAY-Q5R2*0*AE1BA040", "2*857*S/2680*1923*WH/OUT/00567*6B6312A1",
                "C*For Odoo OUT*14C79D4D", "C*Disable*7434FE41", "C*Disable*00000000",
                "{pbn:pick,on:order,pc:stock,pm:INA826AIDR,qty:100}", "{pbn:pick,on:order,pc:stock,pm:INA826AIDR}", "\tX\r\n\0é😀\x1d\x1e\x04" })
            {
                fake.Emit(payload); fake.Emit(payload);
                for (var i = 0; i < 2; i++)
                {
                    check(Receive(readerClient, timeout.Token) == LabelClassifier.Classify(payload).Data, "Reader sends repeated 1/2/3/C/X with unchanged schema and contents");
                    _ = Receive(secondReader, timeout.Token);
                }
                check(!model.Diagnostics.Single().Status.Contains("suppressed"), "No version is subject to reader duplicate suppression");
            }
            EmitCamera(model, "same");
            var cameraMessage = cameraWaiting.GetAwaiter().GetResult()!;
            check(PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(cameraMessage.Bytes)) == LabelData.FromRaw("same"), "Reader scan cannot suppress same value on camera output");
            EmitCamera(model, "same");
            check(model.Diagnostics.Single().Status.Contains("suppressed"), "Camera retains 500 ms suppression");
            var readerWaiting = WebSocketMessages.ReceiveAsync(readerClient, ProtocolJson.MaximumLabelBytes, timeout.Token);
            EmitCamera(model, "camera-only");
            check(Receive(cameraClient, timeout.Token) == LabelData.FromRaw("camera-only") && !readerWaiting.IsCompleted, "Camera scan never reaches reader output");
            fake.Emit("same");
            check(PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(readerWaiting.GetAwaiter().GetResult()!.Bytes)) == LabelData.FromRaw("same"), "Camera cooldown cannot suppress reader output");
            _ = Receive(secondReader, timeout.Token);
            EmitCamera(model, "C*For Odoo OUT*14C79D4D");
            check(Receive(cameraClient, timeout.Token) == LabelData.FromCommand("For Odoo OUT"), "Camera routes valid command C independently of reader scans");
            EmitCamera(model, "C*For Odoo OUT*14C79D4D");
            check(model.Diagnostics.Single().Status.Contains("suppressed"), "Command camera repeat uses 500 ms suppression");
            EmitCamera(model, "C*Disable*00000000");
            check(Receive(cameraClient, timeout.Token) == LabelData.FromRaw("C*Disable*00000000"), "Camera routes corrupt command as X");

            readerClient.Abort(); Until(() => reader.LabelClientCount == 1);
            now += 61000; Update(model);
            check(fake.Disconnects == 1, "Remaining reader client prevents idle disconnect");
            secondReader.Abort(); Until(() => reader.LabelClientCount == 0 && model.BarcodeReaderStatus.StartsWith("No clients"));
            now += 59999; Update(model);
            check(fake.Disconnects == 1, "Reader remains open during 60-second grace period");
            now++; Update(model);
            check(fake.Disconnects == 2 && model.BarcodeReaderButtonText == "Connect", "Reader disconnects at 60 seconds with no clients even while camera client remains");
            model.KeepBarcodeReaderConnected = true; Until(() => model.BarcodeReaderButtonText == "Disconnect");
            now += 120000; Update(model);
            check(fake.Disconnects == 2, "Reader debug override keeps it connected without clients");
            model.KeepBarcodeReaderConnected = false; Step(); Update(model);
            now += 60000; Update(model);
            check(fake.Disconnects == 3, "Clearing debug override restores one-minute idle close");

            using var reconnect = new ClientWebSocket();
            fake.FailOpen = true; Connect(reconnect, reader.Port, timeout.Token);
            Until(() => model.BarcodeReaderStatus.Contains("busy"));
            check(model.BarcodeReaderButtonText == "Connect", "Open failure is visible and retryable with client still connected");
            fake.FailOpen = false; now += 5000; Update(model);
            check(model.BarcodeReaderButtonText == "Disconnect", "Automatic retry opens reader without client reconnecting");
            var opens = fake.Opens; fake.LoseConnection(); now += 4999; Update(model);
            check(fake.Opens == opens, "Lost connection retries with backoff rather than a tight loop");
            now++; Update(model); check(fake.Opens == opens + 1, "Lost reader reconnects while reader clients remain");
            fake.ReportError("Invalid encoding"); check(model.BarcodeReaderStatus == "Invalid encoding", "Reader diagnostic remains visible");

            model.Port = camera.Port; model.BarcodeReaderPort = camera.Port;
            Execute(model.ApplyNetworkCommand);
            check(model.Notice.Contains("different") && camera.IsRunning && reader.IsRunning, "Equal ports rejected without stopping listeners");
            var readerOld = reader.Port; var cameraOld = camera.Port;
            using var occupied = new TcpListener(IPAddress.Any, 0) { ExclusiveAddressUse = true }; occupied.Start();
            model.BarcodeReaderPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            Execute(model.ApplyNetworkCommand);
            check(reader.IsRunning && reader.Port == readerOld && camera.Port == cameraOld && camera.LabelClientCount == 1,
                $"Busy reader port rolls back without disconnecting camera clients (reader {reader.Port}/{readerOld}, running {reader.IsRunning}; camera {camera.Port}/{cameraOld}, clients {camera.LabelClientCount}; {model.Notice})");
            model.BarcodeReaderPort = FreePort(); Execute(model.ApplyNetworkCommand);
            check(reader.Port == model.BarcodeReaderPort && reader.Port != readerOld && camera.Port == cameraOld, "Reader port can change independently");
            var newReader = reader.Port;
            model.Port = newReader; model.BarcodeReaderPort = cameraOld; Execute(model.ApplyNetworkCommand);
            check(camera.Port == newReader && reader.Port == cameraOld, "Both port assignments can be swapped safely");
            var persisted = ProtocolJson.Deserialize<ServerSettings>(File.ReadAllText(paths.SettingsFile));
            check(persisted.Port == newReader && persisted.BarcodeReaderPort == cameraOld, "Both listener ports are persisted only after successful bind");
            reconnect.Abort();
        }
        finally { cameraClient.Abort(); readerClient.Abort(); secondReader.Abort(); Pump(model.DisposeAsync().AsTask()); }
        check(fake.Disposed && !camera.IsRunning && !reader.IsRunning, "Shutdown disposes reader and both listeners");
        VerifyReaderOnlyAndMigration(check);
    }
    private static void VerifyReaderOnlyAndMigration(Action<bool, string> check)
    {
        var paths = new AppPaths(["--data-dir", Path.Combine(Path.GetTempPath(), "oeps-reader-only-" + Guid.NewGuid().ToString("N"))]);
        File.WriteAllText(paths.SettingsFile, "{\"port\":8766,\"camera\":{\"index\":2}}");
        var fake = new FakeReader();
        var selected = new BarcodeReaderChoice("reader", "Reader", "COM7");
        var model = new MainViewModel(paths, fake, () => new[] { selected });
        var camera = (ScannerSocketHost)Field(model, "_sockets");
        var reader = (ScannerSocketHost)Field(model, "_readerSockets");
        using var client = new ClientWebSocket(); using var timeout = new CancellationTokenSource(5000);
        try
        {
            check(model.Port == 8766 && model.BarcodeReaderPort == 8767 && model.SelectedCamera?.Index == 2,
                "Legacy camera port 8766 migrates without collision or resetting camera settings");
            check(model.CameraEnabled, "Camera stays enabled in reader-only isolation test");
            reader.StartAsync(0).GetAwaiter().GetResult();
            typeof(MainViewModel).GetField("_initialized", Private)!.SetValue(model, true);
            Execute(model.RefreshBarcodeReadersCommand);
            Connect(client, reader.Port, timeout.Token); Until(() => fake.Opens == 1); Step();
            check(!(bool)Field(model, "_connected") && !(bool)Field(model, "_cameraPending") && !((WorkerProcess)Field(model, "_worker")).IsRunning,
                "Reader-only client opens no camera or camera worker even when camera is enabled");
            check(camera.LabelClientCount == 0 && reader.LabelClientCount == 1, "Reader client belongs only to reader listener");
        }
        finally { client.Abort(); Pump(model.DisposeAsync().AsTask()); }
    }
    private static object Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void EmitCamera(MainViewModel model, string raw)
    {
        var field = typeof(MainViewModel).GetField("_connected", Private)!; field.SetValue(model, true);
        var generation = (long)Field(model, "_generation");
        var frame = JsonSerializer.SerializeToElement(new { generation, capture_fps = 30, captured_at = DateTimeOffset.UtcNow,
            frame_sequence = 1, read_ms = 1, decode_ms = 1, codes = new[] { new { raw } } });
        typeof(MainViewModel).GetMethod("ProcessFrame", Private)!.Invoke(model, [frame]); field.SetValue(model, false);
    }
    private static void Update(MainViewModel model) => Pump((Task)typeof(MainViewModel).GetMethod("UpdateBarcodeReaderAsync", Private)!.Invoke(model, null)!);
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static void Connect(ClientWebSocket socket, int port, CancellationToken token) => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/labels"), token).GetAwaiter().GetResult();
    private static LabelData Receive(ClientWebSocket socket, CancellationToken token)
    { var message = WebSocketMessages.ReceiveAsync(socket, ProtocolJson.MaximumLabelBytes, token).GetAwaiter().GetResult()!; return PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(message.Bytes)); }
    private static void Execute(AsyncCommand command)
    {
        if (!command.CanExecute(null)) throw new InvalidOperationException("Command unexpectedly disabled");
        command.Execute(null); Until(() => !(bool)Field(command, "_running"));
    }
    private static void Pump(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> predicate)
    {
        var end = Environment.TickCount64 + 5000;
        while (!predicate() && Environment.TickCount64 < end) Step();
        if (!predicate()) throw new TimeoutException("Reader test condition did not complete");
    }
    private static void Step()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame); timer.Stop();
    }
    private sealed class FakeReader : IBarcodeReaderConnection
    {
        public event Action<string>? ScanReceived;
        public event Action<bool, string>? StatusChanged;
        public event Action<string>? Diagnostic;
        public BarcodeReaderChoice? Opened { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailOpen { get; set; }
        public bool FastScans { get; private set; }
        public int Opens { get; private set; }
        public int Disconnects { get; private set; }
        public Task ConnectAsync(BarcodeReaderChoice reader, bool fastScans = true)
        {
            if (FailOpen) throw new IOException("Port busy");
            Opened = reader; FastScans = fastScans; Opens++; StatusChanged?.Invoke(true, "Connected"); return Task.CompletedTask;
        }
        public Task DisconnectAsync() { Disconnects++; StatusChanged?.Invoke(false, "Disconnected"); return Task.CompletedTask; }
        public void Emit(string raw) => ScanReceived?.Invoke(raw);
        public void LoseConnection() => StatusChanged?.Invoke(false, "Connection lost");
        public void ReportError(string error) => Diagnostic?.Invoke(error);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
