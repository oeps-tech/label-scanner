using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scanner.Contracts;
using Scanner.Core;
using Scanner.Desktop;
using Scanner.Server.Services;

namespace Scanner.Server.ViewModels;

public sealed record CodeDiagnostic(string Raw, string Status, string Reason, string ExpectedChecksum);

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly WorkerProcess _worker;
    private readonly IBarcodeReaderConnection _reader;
    private readonly Func<IReadOnlyList<BarcodeReaderChoice>> _discoverReaders;
    private readonly ScannerSocketHost _sockets = new();
    private readonly ScannerSocketHost _readerSockets = new() { ClientQueueCapacity = 256 };
    private readonly DuplicateGate _duplicates = new();
    private readonly CameraDemand _demand = new();
    private readonly CameraDemand _readerDemand = new();
    private readonly Func<long> _readerClock;
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _timer;
    private ServerSettings _settings;
    private Task? _pump;
    private bool _connected, _cameraPending, _previewUpdating, _disposed, _initialized;
    private bool _readerConnected, _readerPending, _refreshingReaders;
    private BarcodeReaderChoice? _selectedReader;
    private string _readerStatus = "Disconnected";
    private long _readerRetryAt;
    private bool _readerLastHadClients;
    private long _generation, _retryAt, _fpsStarted = Stopwatch.GetTimestamp(), _lastPreview;
    private int _fpsFrames, _port, _readerPort;
    private long _valid, _raw, _sent, _suppressed;
    private double _captureFps;
    private JsonElement? _device;
    private CameraChoice? _selectedCamera;
    private BitmapSource? _previewImage;
    private string _notice = "", _cameraStatus = "Waiting for a client", _previewFps = "Capture: 0 FPS · Preview: 0 FPS",
        _frameStatus = "No frame", _timing = "No timing sample", _lastJson = "No label yet", _listenerStatus = "Starting";
    private static long Now => Environment.TickCount64;
    public ObservableCollection<CameraChoice> Cameras { get; } = [];
    public ObservableCollection<BarcodeReaderChoice> BarcodeReaders { get; } = [];
    public ObservableCollection<string> Addresses { get; } = [];
    public ObservableCollection<CodeDiagnostic> Diagnostics { get; } = [];
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string CameraStatus { get => _cameraStatus; private set => Set(ref _cameraStatus, value); }
    public string BarcodeReaderStatus { get => _readerStatus; private set => Set(ref _readerStatus, value); }
    public bool CanChooseBarcodeReader => !_readerConnected && !_readerPending && !_refreshingReaders;
    public string BarcodeReaderButtonText => _readerPending ? "Connecting…" : _readerConnected ? "Disconnect" : "Connect";
    public bool BarcodeReaderFastScans
    {
        get => _settings.BarcodeReaderFastScans;
        set
        {
            if (value == BarcodeReaderFastScans || !CanChooseBarcodeReader) return;
            _settings = _settings with { BarcodeReaderFastScans = value }; Raise(); Raise(nameof(BarcodeReaderSetup));
            _ = GuardAsync(SaveAsync);
        }
    }
    public string BarcodeReaderSetup => BarcodeReaderFastScans
        ? "UTF-8 · CR+LF ends each scan immediately; embedded CR+LF also separates scans · no duplicate suppression"
        : "Multiline mode · UTF-8 · CR or CR+LF ending · leave 250 ms between scans · no duplicate suppression";
    public BarcodeReaderChoice? SelectedBarcodeReader
    {
        get => _selectedReader;
        set
        {
            if (!Set(ref _selectedReader, value)) return;
            ConnectBarcodeReaderCommand?.NotifyCanExecuteChanged();
            if (_refreshingReaders || value is null) return;
            _settings = _settings with { BarcodeReaderDeviceId = value.DeviceId };
            _readerRetryAt = 0;
            _ = GuardAsync(async () => { await SaveAsync(); await UpdateBarcodeReaderAsync(); });
        }
    }
    public string PreviewFps { get => _previewFps; private set => Set(ref _previewFps, value); }
    public string FrameStatus { get => _frameStatus; private set => Set(ref _frameStatus, value); }
    public string Timing { get => _timing; private set => Set(ref _timing, value); }
    public string LastJson { get => _lastJson; private set => Set(ref _lastJson, value); }
    public BitmapSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public int BarcodeReaderPort { get => _readerPort; set => Set(ref _readerPort, value); }
    public string BarcodeReaderClientStatus => $"Reader clients: {_readerSockets.LabelClientCount} · {(_readerSockets.IsRunning ? $"ws://<server>:{_readerSockets.Port}/labels" : "Not listening")}";
    public string ClientStatus => $"Camera clients: {_sockets.LabelClientCount} · {_listenerStatus}";
    public string Counters => $"Structured: {_valid} · Raw (X): {_raw} · Camera suppressed: {_suppressed} · Broadcasts: {_sent}";
    public bool CanChooseCamera => !_connected && !_cameraPending;
    private bool PreviewActive => ShowDetails && PreviewEnabled;
    public string PreviewPlaceholder => !CameraEnabled ? "Camera disabled" : PreviewEnabled ? "Camera opens when a client connects.\nUse ‘Keep camera connected’ to inspect labels without a client." : "Camera preview disabled";
    public bool CameraEnabled
    {
        get => _settings.CameraEnabled;
        set
        {
            if (value == CameraEnabled) return;
            _settings = _settings with { CameraEnabled = value }; Raise(); Raise(nameof(PreviewPlaceholder));
            _ = GuardAsync(async () => { await SaveAsync(); await UpdateCameraAsync(); });
        }
    }
    public CameraChoice? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (!Set(ref _selectedCamera, value) || value is null) return;
            _settings = _settings with { Camera = _settings.Camera with { Index = value.Index } };
            _retryAt = 0;
            if (_initialized) _ = GuardAsync(async () => { await SaveAsync(); await UpdateCameraAsync(); });
        }
    }
    public bool PreviewEnabled
    {
        get => _settings.LocalPreviewEnabled;
        set
        {
            if (value == PreviewEnabled) return;
            _settings = _settings with { LocalPreviewEnabled = value }; Raise(); Raise(nameof(PreviewPlaceholder));
            if (!value) PreviewImage = null;
            _fpsFrames = 0;
            _ = GuardAsync(SavePreviewPreferencesAsync);
        }
    }
    public bool ShowDetails
    {
        get => _settings.ShowDetails;
        set
        {
            if (value == ShowDetails) return;
            _settings = _settings with { ShowDetails = value }; Raise();
            if (!value) PreviewImage = null;
            _fpsFrames = 0;
            _ = GuardAsync(SavePreviewPreferencesAsync);
        }
    }
    private async Task SavePreviewPreferencesAsync()
    {
        if (_worker.IsRunning) await _worker.SendAsync(new { type = "local_preview", enabled = PreviewActive }, _stop.Token);
        await SaveAsync();
    }
    public bool KeepCameraConnected
    {
        get => _settings.KeepCameraConnected;
        set
        {
            if (value == KeepCameraConnected) return;
            _settings = _settings with { KeepCameraConnected = value }; Raise();
            _ = GuardAsync(async () => { await SaveAsync(); await UpdateCameraAsync(); });
        }
    }
    public bool KeepBarcodeReaderConnected
    {
        get => _settings.KeepBarcodeReaderConnected;
        set
        {
            if (value == KeepBarcodeReaderConnected) return;
            _settings = _settings with { KeepBarcodeReaderConnected = value }; Raise();
            _readerRetryAt = 0;
            _ = GuardAsync(async () => { await SaveAsync(); await UpdateBarcodeReaderAsync(); });
        }
    }
    public AsyncCommand RefreshCamerasCommand { get; }
    public AsyncCommand CameraSettingsCommand { get; }
    public AsyncCommand ApplyNetworkCommand { get; }
    public AsyncCommand RefreshBarcodeReadersCommand { get; }
    public AsyncCommand ConnectBarcodeReaderCommand { get; }
    public MainViewModel(AppPaths paths, IBarcodeReaderConnection? reader = null,
        Func<IReadOnlyList<BarcodeReaderChoice>>? discoverReaders = null, Func<long>? readerClock = null)
    {
        _paths = paths;
        _reader = reader ?? new BarcodeReaderConnection();
        _discoverReaders = discoverReaders ?? BluetoothReaderDiscovery.Discover;
        _readerClock = readerClock ?? (() => Now);
        try
        {
            var saved = File.Exists(paths.SettingsFile) ? File.ReadAllText(paths.SettingsFile) : "{}";
            _settings = ProtocolJson.Deserialize<ServerSettings>(saved);
            using var document = JsonDocument.Parse(saved);
            // Existing installations may already use 8766 for the camera.
            if (!document.RootElement.TryGetProperty("barcode_reader_port", out _)
                && _settings.Port == _settings.BarcodeReaderPort) _settings = _settings with { BarcodeReaderPort = 8767 };
            _settings.Validate();
        }
        catch (Exception ex) { _settings = new(); Notice = "Settings reset: " + ex.Message; }
        _port = _settings.Port;
        _readerPort = _settings.BarcodeReaderPort;
        if (!_settings.CameraEnabled) _cameraStatus = "Camera disabled";
        _worker = new(paths);
        Cameras.Add(new(_settings.Camera.Index, $"Camera {_settings.Camera.Index}")); SelectedCamera = Cameras[0];
        RefreshCamerasCommand = Command(async () => { await EnsureWorkerAsync(); await _worker.SendAsync(new { type = "enumerate_cameras" }, _stop.Token); }, () => CanChooseCamera);
        CameraSettingsCommand = Command(EditCameraAsync);
        RefreshBarcodeReadersCommand = Command(RefreshBarcodeReadersAsync, () => CanChooseBarcodeReader);
        ConnectBarcodeReaderCommand = Command(ToggleBarcodeReaderAsync,
            () => !_readerPending && !_refreshingReaders && (_readerConnected || SelectedBarcodeReader is not null)
                && !(_readerConnected && _readerSockets.LabelClientCount > 0));
        _reader.ScanReceived += ReaderScanReceived;
        _reader.StatusChanged += ReaderStatusChanged;
        _reader.Diagnostic += ReaderDiagnostic;
        ApplyNetworkCommand = Command(ApplyNetworkAsync);
        _worker.Status += message => _ui.BeginInvoke(() => { if (!_disposed) HandleStatus(message); });
        _worker.Diagnostic += text => _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            Notice = text;
            if (text.StartsWith("Worker stopped:")) { _connected = _cameraPending = false; _generation++; _retryAt = Now + 5000; CameraStatus = "Worker stopped; waiting to retry"; UpdateCameraControls(); }
        });
        _sockets.ClientsChanged += () => _ui.BeginInvoke(() => { if (!_disposed) { Raise(nameof(ClientStatus)); _ = GuardAsync(UpdateCameraAsync); } });
        _sockets.Diagnostic += text => _ui.BeginInvoke(() => { if (!_disposed) Notice = text; });
        _readerSockets.ClientsChanged += () => _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            var hasClients = _readerSockets.LabelClientCount > 0;
            if (hasClients && !_readerLastHadClients) _readerRetryAt = 0;
            _readerLastHadClients = hasClients;
            Raise(nameof(BarcodeReaderClientStatus)); UpdateReaderControls();
            _ = GuardAsync(UpdateBarcodeReaderAsync);
        });
        _readerSockets.Diagnostic += text => _ui.BeginInvoke(() => { if (!_disposed) Notice = "Reader clients: " + text; });
        _timer = new(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), _ui);
        CompositionTarget.Rendering += OnRendering;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        RefreshAddresses();
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? can = null)
    { var command = new AsyncCommand(action, can); command.Failed += ex => Notice = ex.Message; return command; }
    private async Task GuardAsync(Func<Task> action)
    { try { if (!_disposed) await action(); } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { } catch (Exception ex) { if (!_disposed) Notice = ex.Message; } }
    private Task SaveAsync() => AppPaths.AtomicJsonAsync(_paths.SettingsFile, _settings);
    private void UpdateListenerStatuses()
    {
        _listenerStatus = _sockets.IsRunning ? $"ws://<server>:{_sockets.Port}/labels" : "Not listening";
        Raise(nameof(ClientStatus)); Raise(nameof(BarcodeReaderClientStatus));
    }
    private async Task ApplyNetworkAsync()
    {
        var next = _settings with { Port = Port, BarcodeReaderPort = BarcodeReaderPort }; next.Validate();
        var cameraWasRunning = _sockets.IsRunning; var readerWasRunning = _readerSockets.IsRunning;
        var oldCameraPort = _sockets.Port; var oldReaderPort = _readerSockets.Port;
        var cameraChanged = !cameraWasRunning || next.Port != oldCameraPort;
        var readerChanged = !readerWasRunning || next.BarcodeReaderPort != oldReaderPort;
        if (cameraChanged) await _sockets.StopAsync();
        if (readerChanged) await _readerSockets.StopAsync();
        try
        {
            if (cameraChanged) await _sockets.StartAsync(next.Port, _stop.Token);
            if (readerChanged) await _readerSockets.StartAsync(next.BarcodeReaderPort, _stop.Token);
        }
        catch
        {
            if (cameraChanged) await _sockets.StopAsync();
            if (readerChanged) await _readerSockets.StopAsync();
            // Preserve the last working listeners/settings when a new port is busy.
            try { if (cameraChanged && cameraWasRunning) await _sockets.StartAsync(oldCameraPort, _stop.Token); }
            finally
            {
                try { if (readerChanged && readerWasRunning) await _readerSockets.StartAsync(oldReaderPort, _stop.Token); }
                finally { UpdateListenerStatuses(); }
            }
            throw;
        }
        _settings = next; await SaveAsync(); UpdateListenerStatuses();
    }
    private void UpdateReaderControls()
    {
        Raise(nameof(CanChooseBarcodeReader)); Raise(nameof(BarcodeReaderButtonText));
        RefreshBarcodeReadersCommand.NotifyCanExecuteChanged(); ConnectBarcodeReaderCommand.NotifyCanExecuteChanged();
    }
    private async Task RefreshBarcodeReadersAsync()
    {
        _refreshingReaders = true; UpdateReaderControls();
        try
        {
            var readers = await Task.Run(_discoverReaders);
            if (_disposed) return;
            BarcodeReaders.Clear(); foreach (var reader in readers) BarcodeReaders.Add(reader);
            SelectedBarcodeReader = readers.FirstOrDefault(r => r.DeviceId == _settings.BarcodeReaderDeviceId)
                ?? (_settings.BarcodeReaderDeviceId is null && readers.Count == 1 ? readers[0] : null);
            BarcodeReaderStatus = readers.Count == 0 ? "No outgoing Bluetooth SPP ports found. Pair the reader in Windows, then refresh."
                : SelectedBarcodeReader is null ? "Select a reader · the saved reader may no longer be paired" : "Disconnected · ready to connect";
        }
        catch (Exception ex) { BarcodeReaderStatus = "Could not list readers · " + ex.Message; }
        finally { _refreshingReaders = false; UpdateReaderControls(); }
        _readerRetryAt = 0; await UpdateBarcodeReaderAsync();
    }
    private async Task ToggleBarcodeReaderAsync()
    {
        if (_readerConnected)
        {
            if (_readerSockets.LabelClientCount > 0 || !await _readerGate.WaitAsync(0)) return;
            try
            {
                _settings = _settings with { KeepBarcodeReaderConnected = false }; Raise(nameof(KeepBarcodeReaderConnected));
                await SaveAsync(); await DisconnectReaderAsync();
            }
            finally { _readerGate.Release(); }
            return;
        }
        _settings = _settings with { KeepBarcodeReaderConnected = _readerSockets.LabelClientCount == 0 || KeepBarcodeReaderConnected };
        Raise(nameof(KeepBarcodeReaderConnected)); await SaveAsync(); _readerRetryAt = 0;
        await UpdateBarcodeReaderAsync();
    }
    private async Task UpdateBarcodeReaderAsync()
    {
        if (!_initialized || _disposed || _refreshingReaders || !await _readerGate.WaitAsync(0)) return;
        try
        {
            var now = _readerClock();
            var wanted = _readerDemand.ShouldConnect(_readerSockets.LabelClientCount, KeepBarcodeReaderConnected,
                _readerConnected || _readerPending, now);
            if (wanted && !_readerConnected && !_readerPending && now >= _readerRetryAt)
            {
                if (SelectedBarcodeReader is null) { BarcodeReaderStatus = "A reader is required · select a paired Bluetooth reader"; return; }
                try { await ConnectReaderAsync(); }
                catch (Exception ex) { _readerConnected = false; BarcodeReaderStatus = "Could not connect · " + ex.Message + " · retrying in 5 s"; }
                finally { _readerRetryAt = _readerClock() + 5000; }
            }
            else if (!wanted && _readerConnected) await DisconnectReaderAsync();
            else if (_readerConnected && _readerSockets.LabelClientCount == 0 && !KeepBarcodeReaderConnected)
                BarcodeReaderStatus = $"No clients · reader closes in {_readerDemand.IdleSecondsRemaining(now)} s";
            else if (_readerConnected && BarcodeReaderStatus.StartsWith("No clients")) BarcodeReaderStatus = "Connected · waiting for a scan";
        }
        finally { _readerGate.Release(); }
    }
    private async Task DisconnectReaderAsync()
    {
        _readerConnected = false; _readerPending = true; UpdateReaderControls();
        BarcodeReaderStatus = "Disconnecting…";
        try { await _reader.DisconnectAsync(); }
        finally { _readerPending = false; UpdateReaderControls(); }
    }
    private async Task ConnectReaderAsync()
    {
        if (SelectedBarcodeReader is not { } selected) return;
        _readerPending = true; UpdateReaderControls(); BarcodeReaderStatus = "Connecting… · wake the reader if it is asleep";
        try
        {
            // Resolve the saved device again instead of opening a stale COM number
            // that Windows might now have assigned to another device.
            var readers = await Task.Run(_discoverReaders);
            if (_disposed) return;
            var current = readers.FirstOrDefault(r => r.DeviceId == selected.DeviceId)
                ?? throw new InvalidOperationException("Reader is no longer available. Refresh the list and pair it in Windows if needed.");
            if (current != selected)
            {
                var index = BarcodeReaders.IndexOf(selected);
                if (index >= 0) BarcodeReaders[index] = current;
                _selectedReader = current; Raise(nameof(SelectedBarcodeReader));
            }
            _settings = _settings with { BarcodeReaderDeviceId = current.DeviceId }; await SaveAsync();
            await _reader.ConnectAsync(current, BarcodeReaderFastScans);
        }
        finally { _readerPending = false; UpdateReaderControls(); }
    }
    private void ReaderStatusChanged(bool connected, string status) => _ui.Invoke(() =>
    {
        if (_disposed) return;
        _readerConnected = connected; BarcodeReaderStatus = status;
        if (!connected) _readerRetryAt = _readerClock() + 5000;
        UpdateReaderControls();
    });
    private void ReaderDiagnostic(string message) => _ui.Invoke(() => { if (!_disposed) BarcodeReaderStatus = message; });
    private void ReaderScanReceived(string raw) => _ui.Invoke(() =>
    {
        if (_disposed || !_readerConnected) return;
        try
        {
            Diagnostics.Clear(); ProcessDecodedPayload(raw, fromReader: true); Raise(nameof(Counters));
            BarcodeReaderStatus = $"Connected · last scan {DateTime.Now:HH:mm:ss} · {raw.Length} characters";
        }
        catch (Exception ex) { BarcodeReaderStatus = "Reader scan could not be sent · " + ex.Message; }
    });
    public async Task InitializeAsync(bool smoke = false)
    {
        try
        {
            try { await _sockets.StartAsync(Port, _stop.Token); }
            catch (Exception ex) { Notice = "Camera listener: " + ex.Message; }
            try { await _readerSockets.StartAsync(BarcodeReaderPort, _stop.Token); }
            catch (Exception ex) { Notice += " Reader listener: " + ex.Message; }
            UpdateListenerStatuses();
            if (smoke) { await RefreshBarcodeReadersAsync(); return; }
            _initialized = true;
            await RefreshBarcodeReadersAsync();
            if (CameraEnabled) await EnsureWorkerAsync();
            await UpdateCameraAsync();
            await UpdateBarcodeReaderAsync();
        }
        catch (Exception ex) { Notice = ex.Message; }
    }
    private async Task EnsureWorkerAsync()
    {
        if (_worker.IsRunning) return;
        await _worker.StartAsync();
        await _worker.SendAsync(new { type = "local_preview", enabled = PreviewActive }, _stop.Token);
        _pump ??= Task.Run(async () =>
        {
            try { await foreach (var message in _worker.Candidates(_stop.Token)) await _ui.InvokeAsync(() => { if (!_disposed) ProcessFrame(message); }); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    private async Task UpdateCameraAsync()
    {
        if (!_initialized || _disposed || !await _cameraGate.WaitAsync(0)) return;
        try
        {
            var wanted = CameraEnabled && _demand.ShouldConnect(_sockets.LabelClientCount, KeepCameraConnected, _connected || _cameraPending, Now);
            if (wanted && !_connected && !_cameraPending && Now >= _retryAt)
            {
                await EnsureWorkerAsync();
                _generation++; _cameraPending = true; CameraStatus = "Connecting…"; UpdateCameraControls();
                await _worker.SendAsync(new { type = "connect", generation = _generation, camera_index = _settings.Camera.Index, settings = _settings.Camera }, _stop.Token);
            }
            else if (!wanted && (_connected || _cameraPending))
            {
                _generation++; _connected = _cameraPending = false; PreviewImage = null;
                CameraStatus = CameraEnabled ? "Camera off · no clients for one minute" : "Camera disabled"; UpdateCameraControls();
                if (_worker.IsRunning) await _worker.SendAsync(new { type = "disconnect", generation = _generation }, _stop.Token);
            }
            else if (_connected)
                CameraStatus = _sockets.LabelClientCount > 0 ? "Connected · scanning barcodes" : KeepCameraConnected ? "Connected · debug override" : $"No clients · camera closes in {_demand.IdleSecondsRemaining(Now)} s";
            else if (!CameraEnabled) CameraStatus = "Camera disabled";
            else if (!_cameraPending && Now >= _retryAt) CameraStatus = "Waiting for a client";
        }
        catch { _cameraPending = false; _retryAt = Now + 5000; UpdateCameraControls(); throw; }
        finally { _cameraGate.Release(); }
    }
    private void UpdateCameraControls() { Raise(nameof(CanChooseCamera)); RefreshCamerasCommand.NotifyCanExecuteChanged(); }
    private void HandleStatus(JsonElement message)
    {
        if (message.GetProperty("generation").GetInt64() != _generation) return;
        var state = message.GetProperty("state").GetString();
        if (state == "connected") { _connected = true; _cameraPending = false; _fpsFrames = 0; _lastPreview = 0; CameraStatus = "Connected"; UpdateCameraControls(); }
        if (state is "camera_error" or "disconnected") { _connected = _cameraPending = false; _captureFps = 0; _retryAt = Now + 5000; CameraStatus = message.GetProperty("message").GetString()!; UpdateCameraControls(); }
        if (state == "cameras" && message.TryGetProperty("cameras", out var cameras))
        {
            var index = _settings.Camera.Index; Cameras.Clear();
            foreach (var camera in cameras.EnumerateArray()) Cameras.Add(new(camera.GetProperty("index").GetInt32(), camera.GetProperty("name").GetString()!));
            if (!Cameras.Any(c => c.Index == index)) Cameras.Add(new(index, $"Camera {index} (saved)"));
            SelectedCamera = Cameras.First(c => c.Index == index);
        }
        if (message.TryGetProperty("camera", out var device))
        {
            var fields = device.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            if (message.TryGetProperty("capabilities", out var capabilities)) fields["capabilities"] = capabilities.Clone();
            _device = JsonSerializer.SerializeToElement(fields);
        }
        if (state is "error" or "decode_error" or "preview_error") Notice = message.GetProperty("message").GetString()!;
    }
    private void ProcessFrame(JsonElement frame)
    {
        if (!_connected || frame.GetProperty("generation").GetInt64() != _generation) return;
        _captureFps = frame.GetProperty("capture_fps").GetDouble();
        var age = Math.Max(0, (DateTimeOffset.UtcNow - frame.GetProperty("captured_at").GetDateTimeOffset()).TotalMilliseconds);
        FrameStatus = $"Frame {frame.GetProperty("frame_sequence").GetInt64()} · {age:F0} ms old";
        Timing = $"Camera read: {frame.GetProperty("read_ms").GetDouble():F1} ms · Barcode decoding: {frame.GetProperty("decode_ms").GetDouble():F1} ms";
        Diagnostics.Clear();
        foreach (var code in frame.GetProperty("codes").EnumerateArray())
            ProcessDecodedPayload(code.GetProperty("raw").GetString()!);
        Raise(nameof(Counters));
    }
    private void ProcessDecodedPayload(string raw, bool fromReader = false)
    {
        var sockets = fromReader ? _readerSockets : _sockets;
        var result = LabelClassifier.Classify(raw);
        var data = result.Data;
        var kind = data.IsRaw ? "Raw X" : $"Version {data.VersionText}";
        string status;
        if (data.IsRaw) _raw++; else _valid++;
        LastJson = JsonSerializer.Serialize(data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true });
        if (sockets.LabelClientCount == 0) status = kind + " · no clients";
        else if (!fromReader && !_duplicates.CanSend(raw, Now)) { status = kind + " · suppressed (<500 ms)"; _suppressed++; }
        else
        {
            var recipients = sockets.PublishLabel(data);
            if (recipients > 0) { if (!fromReader) _duplicates.MarkSent(raw, Now); _sent++; status = $"{kind} · sent to {recipients} client(s)"; }
            else status = kind + " · no delivery queued";
        }
        Diagnostics.Add(new(raw, status, result.Reason, result.ExpectedChecksum ?? "—"));
    }
    private void Tick()
    {
        if (_disposed) return;
        _ = GuardAsync(UpdateCameraAsync);
        _ = GuardAsync(UpdateBarcodeReaderAsync);
        var seconds = Stopwatch.GetElapsedTime(_fpsStarted).TotalSeconds;
        if (seconds >= 1)
        {
            var preview = PreviewActive ? $"{_fpsFrames / seconds:F1} FPS" : "disabled";
            PreviewFps = $"Capture: {(_connected ? _captureFps : 0):F1} FPS · Preview: {preview}";
            _fpsFrames = 0; _fpsStarted = Stopwatch.GetTimestamp();
        }
    }
    private async void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || _previewUpdating || !PreviewActive || !_connected) return;
        _previewUpdating = true;
        try
        {
            if (_worker.TakePreview() is not { } frame || frame.GetProperty("generation").GetInt64() != _generation) return;
            var generation = _generation;
            var image = await Task.Run(() => ImageLoader.FromBase64(frame.GetProperty("jpeg").GetString()));
            if (_disposed || !_connected || !PreviewActive || generation != _generation) return;
            PreviewImage = image; _fpsFrames++; _lastPreview = Now;
            _captureFps = frame.GetProperty("capture_fps").GetDouble();
        }
        catch (Exception ex) { if (!_disposed) Notice = "Preview: " + ex.Message; }
        finally { _previewUpdating = false; }
    }
    private async Task EditCameraAsync()
    {
        var previous = _settings.Camera;
        async Task Apply(CameraSettings camera)
        {
            new ServerSettings { Camera = camera }.Validate();
            _settings = _settings with { Camera = camera };
            if (_connected) { _generation++; await _worker.SendAsync(new { type = "apply_camera_settings", generation = _generation, settings = camera }, _stop.Token); }
        }
        var dialog = new CameraSettingsWindow(previous, _device, _worker, Apply) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true) { await Apply(dialog.Result); await SaveAsync(); }
        else await Apply(previous);
    }
    private void NetworkChanged(object? sender, EventArgs e) => _ui.BeginInvoke(() => { if (!_disposed) RefreshAddresses(); });
    private void RefreshAddresses()
    {
        Addresses.Clear(); Addresses.Add("127.0.0.1 · Same computer");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
            Addresses.Add($"{address.Address} · {nic.Name}");
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _timer.Stop();
        CompositionTarget.Rendering -= OnRendering; NetworkChange.NetworkAddressChanged -= NetworkChanged;
        _stop.Cancel(); await _sockets.StopAsync(); await _readerSockets.StopAsync(); await _worker.DisposeAsync();
        await _reader.DisposeAsync();
        _reader.ScanReceived -= ReaderScanReceived; _reader.StatusChanged -= ReaderStatusChanged; _reader.Diagnostic -= ReaderDiagnostic;
        if (_pump is not null) { try { await _pump; } catch (OperationCanceledException) { } }
    }
}
