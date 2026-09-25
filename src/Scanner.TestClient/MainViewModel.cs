using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using Scanner.Client;
using Scanner.Contracts;
using Scanner.Desktop;

namespace Scanner.TestClient;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ScannerConnection _client = new();
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private readonly string _settingsPath;
    private readonly Channel<LabelData> _incoming = Channel.CreateBounded<LabelData>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _connectCancellation;
    private bool _wanted, _busy, _disposed;
    private long _retryAt, _received;
    private string _host = "127.0.0.1", _status = "Disconnected", _json = "No data received", _pn = "—", _lot = "—", _quantity = "—", _version = "—";
    private int _port = 8765;
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Json { get => _json; private set => Set(ref _json, value); }
    public string OepsPn { get => _pn; private set => Set(ref _pn, value); }
    public string Lot { get => _lot; private set => Set(ref _lot, value); }
    public string Quantity { get => _quantity; private set => Set(ref _quantity, value); }
    public string LabelVersion { get => _version; private set => Set(ref _version, value); }
    public string Received => $"Messages received this session: {Interlocked.Read(ref _received)}";
    public string ConnectLabel => _wanted ? "Disconnect" : "Connect";
    public AsyncCommand ConnectCommand { get; }
    public MainViewModel(Dispatcher dispatcher, string? settingsPath = null)
    {
        _ui = dispatcher;
        _settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OEPS", "Scanner", "client-settings.json");
        try
        {
            if (File.Exists(_settingsPath)) { var settings = ProtocolJson.Deserialize<ClientSettings>(File.ReadAllText(_settingsPath)); _host = settings.Host; _port = settings.Port; }
        }
        catch (Exception ex) { Status = "Could not load connection settings: " + ex.Message; }
        _client.LabelReceived += data => { Interlocked.Increment(ref _received); _incoming.Writer.TryWrite(data); };
        _client.StateChanged += state => _ui.BeginInvoke(() => { if (!_disposed) Status = state; });
        ConnectCommand = new(async () =>
        {
            _wanted = !_wanted; Raise(nameof(ConnectLabel));
            if (!_wanted) { _connectCancellation?.Cancel(); await _client.DisconnectAsync(); }
            else { _retryAt = 0; await ConnectAsync(); }
        });
        ConnectCommand.Failed += ex => Status = ex.Message;
        _timer = new(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, async (_, _) =>
        {
            if (_disposed) return;
            if (_incoming.Reader.TryRead(out var data)) Display(data);
            Raise(nameof(Received));
            if (_wanted && !_client.Connected && !_busy && Environment.TickCount64 >= _retryAt) await ConnectAsync();
        }, dispatcher);
    }
    public void Display(LabelData data)
    {
        Json = JsonSerializer.Serialize(data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true });
        LabelVersion = data.LabelVersion.ToString(); OepsPn = data.OepsPn; Lot = data.Lot; Quantity = data.Quantity;
    }
    private async Task ConnectAsync()
    {
        if (_busy || _disposed) return; _busy = true;
        try
        {
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _connectCancellation = connectCancellation;
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            await File.WriteAllTextAsync(_settingsPath, ProtocolJson.Serialize(new ClientSettings(Host, Port)));
            await _client.ConnectAsync(Host, Port, connectCancellation.Token);
        }
        catch (Exception ex) { if (!_disposed) Status = "Retrying in 3 seconds: " + ex.Message; }
        finally { _connectCancellation = null; _busy = false; _retryAt = Environment.TickCount64 + 3000; }
    }
    public async ValueTask DisposeAsync()
    { if (_disposed) return; _disposed = true; _wanted = false; _timer.Stop(); _stop.Cancel(); await _client.DisposeAsync(); }
    private sealed record ClientSettings(string Host, int Port);
}
