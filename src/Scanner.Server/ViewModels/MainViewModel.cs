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
    private readonly ScannerSocketHost _sockets = new();
    private readonly DuplicateGate _duplicates = new();
    private readonly CameraDemand _demand = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _timer;
    private ServerSettings _settings;
    private Task? _pump;
    private bool _connected, _cameraPending, _previewUpdating, _disposed, _initialized;
    private long _generation, _retryAt, _fpsStarted = Stopwatch.GetTimestamp(), _lastPreview;
    private int _fpsFrames, _port;
    private long _valid, _invalid, _sent, _suppressed;
    private double _captureFps;
    private JsonElement? _device;
    private CameraChoice? _selectedCamera;
    private BitmapSource? _previewImage;
    private string _notice = "", _cameraStatus = "Waiting for a client", _previewFps = "Capture: 0 FPS · Preview: 0 FPS",
        _frameStatus = "No frame", _timing = "No timing sample", _lastJson = "No valid label yet", _listenerStatus = "Starting";
    private static long Now => Environment.TickCount64;
    public ObservableCollection<CameraChoice> Cameras { get; } = [];
    public ObservableCollection<string> Addresses { get; } = [];
    public ObservableCollection<CodeDiagnostic> Diagnostics { get; } = [];
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string CameraStatus { get => _cameraStatus; private set => Set(ref _cameraStatus, value); }
    public string PreviewFps { get => _previewFps; private set => Set(ref _previewFps, value); }
    public string FrameStatus { get => _frameStatus; private set => Set(ref _frameStatus, value); }
    public string Timing { get => _timing; private set => Set(ref _timing, value); }
    public string LastJson { get => _lastJson; private set => Set(ref _lastJson, value); }
    public BitmapSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string ClientStatus => $"Data clients: {_sockets.LabelClientCount} · {_listenerStatus}";
    public string Counters => $"Valid: {_valid} · Invalid: {_invalid} · Suppressed: {_suppressed} · Broadcasts: {_sent}";
    public bool CanChooseCamera => !_connected && !_cameraPending;
    private bool PreviewActive => ShowDetails && PreviewEnabled;
    public string PreviewPlaceholder => PreviewEnabled ? "Camera opens when a client connects.\nUse ‘Keep camera connected’ to inspect labels without a client." : "Camera preview disabled";
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
    public AsyncCommand RefreshCamerasCommand { get; }
    public AsyncCommand CameraSettingsCommand { get; }
    public AsyncCommand ApplyNetworkCommand { get; }
    public MainViewModel(AppPaths paths)
    {
        _paths = paths;
        try { _settings = File.Exists(paths.SettingsFile) ? ProtocolJson.Deserialize<ServerSettings>(File.ReadAllText(paths.SettingsFile)) : new(); _settings.Validate(); }
        catch (Exception ex) { _settings = new(); Notice = "Settings reset: " + ex.Message; }
        _port = _settings.Port;
        _worker = new(paths);
        Cameras.Add(new(_settings.Camera.Index, $"Camera {_settings.Camera.Index}")); SelectedCamera = Cameras[0];
        RefreshCamerasCommand = Command(async () => { await EnsureWorkerAsync(); await _worker.SendAsync(new { type = "enumerate_cameras" }, _stop.Token); }, () => CanChooseCamera);
        CameraSettingsCommand = Command(EditCameraAsync);
        ApplyNetworkCommand = Command(async () =>
        {
            var next = _settings with { Port = Port }; next.Validate();
            if (Port != _sockets.Port) { await _sockets.StopAsync(); await _sockets.StartAsync(Port, _stop.Token); }
            _settings = next; await SaveAsync(); _listenerStatus = $"ws://<server>:{Port}/labels"; Raise(nameof(ClientStatus));
        });
        _worker.Status += message => _ui.BeginInvoke(() => { if (!_disposed) HandleStatus(message); });
        _worker.Diagnostic += text => _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            Notice = text;
            if (text.StartsWith("Worker stopped:")) { _connected = _cameraPending = false; _generation++; _retryAt = Now + 5000; CameraStatus = "Worker stopped; waiting to retry"; UpdateCameraControls(); }
        });
        _sockets.ClientsChanged += () => _ui.BeginInvoke(() => { if (!_disposed) { Raise(nameof(ClientStatus)); _ = GuardAsync(UpdateCameraAsync); } });
        _sockets.Diagnostic += text => _ui.BeginInvoke(() => { if (!_disposed) Notice = text; });
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
    public async Task InitializeAsync(bool smoke = false)
    {
        try
        {
            await _sockets.StartAsync(Port, _stop.Token);
            _listenerStatus = $"ws://<server>:{Port}/labels"; Raise(nameof(ClientStatus));
            if (smoke) return;
            _initialized = true;
            await EnsureWorkerAsync();
            await UpdateCameraAsync();
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
            var wanted = _demand.ShouldConnect(_sockets.LabelClientCount, KeepCameraConnected, _connected || _cameraPending, Now);
            if (wanted && !_connected && !_cameraPending && Now >= _retryAt)
            {
                await EnsureWorkerAsync();
                _generation++; _cameraPending = true; CameraStatus = "Connecting…"; UpdateCameraControls();
                await _worker.SendAsync(new { type = "connect", generation = _generation, camera_index = _settings.Camera.Index, settings = _settings.Camera }, _stop.Token);
            }
            else if (!wanted && (_connected || _cameraPending))
            {
                _generation++; _connected = _cameraPending = false; _duplicates.Clear(); Diagnostics.Clear(); PreviewImage = null;
                CameraStatus = "Camera off · no clients for one minute"; UpdateCameraControls();
                if (_worker.IsRunning) await _worker.SendAsync(new { type = "disconnect", generation = _generation }, _stop.Token);
            }
            else if (_connected)
                CameraStatus = _sockets.LabelClientCount > 0 ? "Connected · scanning for Data Matrix" : KeepCameraConnected ? "Connected · debug override" : $"No clients · camera closes in {_demand.IdleSecondsRemaining(Now)} s";
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
        Timing = $"Camera read: {frame.GetProperty("read_ms").GetDouble():F1} ms · Data Matrix decoding: {frame.GetProperty("decode_ms").GetDouble():F1} ms";
        Diagnostics.Clear();
        foreach (var code in frame.GetProperty("codes").EnumerateArray())
        {
            var raw = code.GetProperty("raw").GetString()!;
            var result = PayloadValidator.Validate(raw);
            var status = result.Valid ? "Valid" : "Rejected: " + result.Stage;
            if (!result.Valid) _invalid++;
            else
            {
                _valid++; LastJson = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true });
                if (_sockets.LabelClientCount == 0) status = "Valid · no clients";
                else if (!_duplicates.CanSend(raw, Now)) { status = "Valid · suppressed (<500 ms)"; _suppressed++; }
                else
                {
                    var recipients = _sockets.PublishLabel(result.Data!);
                    if (recipients > 0) { _duplicates.MarkSent(raw, Now); _sent++; status = $"Sent to {recipients} client(s)"; }
                    else status = "Valid · no delivery queued";
                }
            }
            Diagnostics.Add(new(raw, status, result.Reason, result.ExpectedChecksum ?? "—"));
        }
        Raise(nameof(Counters));
    }
    private void Tick()
    {
        if (_disposed) return;
        _ = GuardAsync(UpdateCameraAsync);
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
        _stop.Cancel(); await _sockets.StopAsync(); await _worker.DisposeAsync();
        if (_pump is not null) { try { await _pump; } catch (OperationCanceledException) { } }
    }
}
