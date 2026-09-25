using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scanner.Contracts;

namespace Scanner.Core;

public sealed class ScannerSocketHost() : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Peer> _labels = new();
    private WebApplication? _app;
    public int Port { get; private set; }
    public int MaximumLabelBytes { get; init; } = 16 * 1024;
    public int MaximumClientMessageBytes { get; init; } = 64 * 1024;
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int LabelClientCount => _labels.Count;
    public event Action? ClientsChanged;
    public event Action<string>? Diagnostic;
    public async Task StartAsync(int port = 8765, CancellationToken cancellationToken = default)
    {
        if (_app is not null) throw new InvalidOperationException("Listener is already running.");
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, port));
        var app = builder.Build();
        app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
        app.Map("/labels", AcceptAsync);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            Port = new Uri(address).Port; _app = app;
        }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public int PublishLabel(LabelData observation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(observation, ProtocolJson.Options);
        if (bytes.Length > MaximumLabelBytes) throw new InvalidDataException("Label event exceeds configured network size.");
        var recipients = 0;
        foreach (var peer in _labels.Values)
            if (peer.Pending.Writer.TryWrite(new(bytes, WebSocketMessageType.Text))) recipients++;
            else { Diagnostic?.Invoke("Disconnected a slow label client whose queue was full."); peer.Stop(); }
        return recipients;
    }
    private async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var peer = new Peer(socket);
        var peers = _labels;
        var id = Guid.NewGuid(); peers[id] = peer; ClientsChanged?.Invoke();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, peer.Cancellation.Token);
        var sending = SendLoopAsync(peer, linked.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var message = await WebSocketMessages.ReceiveAsync(socket, MaximumClientMessageBytes, linked.Token).ConfigureAwait(false);
                if (message is null) break;
                // Clients receive data only. No feedback, acknowledgements or replay.
                throw new InvalidDataException("The label endpoint is receive-only for clients.");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or InvalidDataException)
        { if (ex is not OperationCanceledException) Diagnostic?.Invoke($"Label client: {ex.Message}"); }
        finally
        {
            peers.TryRemove(id, out _); peer.Stop();
            try { await sending.ConfigureAwait(false); } catch (OperationCanceledException) { }
            ClientsChanged?.Invoke();
        }
    }
    private async Task SendLoopAsync(Peer peer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in peer.Pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SendTimeout);
                await peer.Socket.SendAsync(packet.Bytes.AsMemory(), packet.Type, true, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        { peer.Stop(); }
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var peer in _labels.Values) peer.Stop();
        if (_app is not null) { await _app.StopAsync(cancellationToken).ConfigureAwait(false); await _app.DisposeAsync().ConfigureAwait(false); _app = null; }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    private sealed record Packet(byte[] Bytes, WebSocketMessageType Type);
    private sealed class Peer : IDisposable
    {
        public WebSocket Socket { get; }
        public Channel<Packet> Pending { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Peer(WebSocket socket)
        {
            Socket = socket;
            Pending = Channel.CreateBounded<Packet>(new BoundedChannelOptions(8)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        }
        public void Stop()
        {
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            Pending.Writer.TryComplete();
            try { Socket.Abort(); } catch (ObjectDisposedException) { }
        }
        public void Dispose() { Cancellation.Dispose(); }
    }
}
