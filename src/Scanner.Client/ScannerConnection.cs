using System.Net.WebSockets;
using System.Text;
using Scanner.Contracts;

namespace Scanner.Client;

/// <summary>Connects to the OEPS scanner server and receives its validated label messages.</summary>
public sealed class ScannerConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _stop;
    private Task? _read;
    private bool _disposed;
    public bool Connected => _socket?.State == WebSocketState.Open;
    /// <summary>
    /// Raised once per valid server message with the original JSON, including quantity as a string.
    /// Subscribe before connecting. Handlers run on the receive loop; keep them short and marshal UI updates.
    /// </summary>
    public event Action<string>? JsonReceived;
    public event Action<LabelData>? LabelReceived;
    public event Action<string>? StateChanged;
    /// <summary>Connects to the server at the given IP/host and port, or to this computer.</summary>
    /// <param name="ip">Server IP address or host name. Ignored (and may be null) when thisComputer is true.</param>
    /// <param name="port">The scanner server's listening port, normally 8765.</param>
    /// <param name="thisComputer">Use 127.0.0.1 instead of ip.</param>
    /// <param name="cancellationToken">Cancels connection establishment, not the subsequent receive loop.</param>
    public Task ConnectAsync(string? ip, int port, bool thisComputer, CancellationToken cancellationToken = default)
        => ConnectAsync(thisComputer ? "127.0.0.1" : ip ?? "", port, cancellationToken);

    public async Task ConnectAsync(string host, int port = 8765, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) throw new ArgumentException("Enter a host and port 1–65535");
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await DisconnectCoreAsync().ConfigureAwait(false);
            StateChanged?.Invoke("Connecting");
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await socket.ConnectAsync(new UriBuilder("ws", host, port, "/labels").Uri, timeout.Token).ConfigureAwait(false); }
            catch { socket.Dispose(); throw; }
            _socket = socket; _stop = new();
            StateChanged?.Invoke("Connected");
            _read = ReadAsync(socket, _stop.Token);
        }
        catch (Exception ex) { StateChanged?.Invoke("Connection failed: " + ex.Message); throw; }
        finally { _lifecycle.Release(); }
    }
    private async Task ReadAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (await WebSocketMessages.ReceiveAsync(socket, 16 * 1024, ct).ConfigureAwait(false) is { } message)
            {
                if (message.Type != WebSocketMessageType.Text) throw new InvalidDataException("Expected data JSON text");
                var json = Encoding.UTF8.GetString(message.Bytes);
                var data = PayloadValidator.ReadClientJson(json);
                JsonReceived?.Invoke(json);
                LabelReceived?.Invoke(data);
            }
            StateChanged?.Invoke("Disconnected");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { StateChanged?.Invoke("Disconnected: " + ex.Message); }
        finally { socket.Abort(); }
    }
    /// <summary>Closes this client's connection. The same instance can be connected again.</summary>
    public Task CloseAsync() => DisconnectAsync();

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
    private async Task DisconnectCoreAsync()
    {
        _stop?.Cancel(); _socket?.Abort();
        if (_read is not null) await _read.ConfigureAwait(false);
        _socket?.Dispose(); _stop?.Dispose(); _socket = null; _stop = null; _read = null;
        StateChanged?.Invoke("Disconnected");
    }
    public async ValueTask DisposeAsync()
    { if (_disposed) return; _disposed = true; await DisconnectAsync().ConfigureAwait(false); }
}
