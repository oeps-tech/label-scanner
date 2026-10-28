using System.IO;
using System.IO.Ports;

namespace Scanner.Server.Services;

public interface IBarcodeReaderConnection : IAsyncDisposable
{
    event Action<string>? ScanReceived;
    event Action<bool, string>? StatusChanged;
    event Action<string>? Diagnostic;
    Task ConnectAsync(BarcodeReaderChoice reader, bool fastScans = true);
    Task DisconnectAsync();
}

public sealed class BarcodeReaderConnection : IBarcodeReaderConnection
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _session;
    private bool _disposed;
    public event Action<string>? ScanReceived;
    public event Action<bool, string>? StatusChanged;
    public event Action<string>? Diagnostic;

    public async Task ConnectAsync(BarcodeReaderChoice reader, bool fastScans = true)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopAsync();
            _stop = new();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = _stop.Token;
            // Open/read/close all run off the WPF thread. SerialPort.Open can wait
            // on Bluetooth paging; cancel marks this session stale until it returns.
            _session = Task.Run(() => Run(reader, fastScans, token, ready));
            await ready.Task;
        }
        finally { _gate.Release(); }
    }

    private void Run(BarcodeReaderChoice reader, bool fastScans, CancellationToken token, TaskCompletionSource ready)
    {
        try
        {
            using var port = new SerialPort(reader.PortName, 9600, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None, ReadTimeout = 50, WriteTimeout = 1000,
                DiscardNull = false, ReadBufferSize = 65536
            };
            port.Open(); // Windows opens exclusively; an occupied port fails visibly.
            token.ThrowIfCancellationRequested();
            StatusChanged?.Invoke(true, $"Connected · {reader.DisplayName} · waiting for a scan");
            ready.TrySetResult();
            var buffer = new BarcodeScanBuffer(fastScans);
            var bytes = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                // Check between reads as well as on timeouts; a chunk may arrive
                // just after a previous scan's quiet interval has completed.
                EmitCompleted(buffer);
                try
                {
                    var count = port.Read(bytes, 0, bytes.Length);
                    if (count == 0) throw new IOException("Bluetooth serial stream closed.");
                    if (token.IsCancellationRequested) break;
                    EmitCompleted(buffer);
                    buffer.Append(bytes.AsSpan(0, count), Environment.TickCount64);
                    EmitCompleted(buffer);
                }
                catch (TimeoutException) { }
            }
            // Never flush partial data on disconnect or replay it on reconnect.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            if (!token.IsCancellationRequested) StatusChanged?.Invoke(false, "Disconnected · " + ex.Message + " · wake the reader and reconnect");
        }
        finally { ready.TrySetCanceled(); }
    }

    private void EmitCompleted(BarcodeScanBuffer buffer)
    {
        while (true)
        {
            try
            {
                if (buffer.TakeCompleted(Environment.TickCount64) is not { } raw) return;
                ScanReceived?.Invoke(raw);
            }
            catch (InvalidDataException ex) { Diagnostic?.Invoke(ex.Message); }
        }
    }

    public async Task DisconnectAsync()
    {
        // Signal before waiting for ConnectAsync's gate: a pending Bluetooth open
        // must not become an active reader after the user has disconnected.
        _stop?.Cancel();
        await _gate.WaitAsync();
        try { await StopAsync(); StatusChanged?.Invoke(false, "Disconnected"); }
        finally { _gate.Release(); }
    }

    private async Task StopAsync()
    {
        _stop?.Cancel();
        if (_session is not null) await _session;
        _session = null; _stop?.Dispose(); _stop = null;
    }

    public async ValueTask DisposeAsync()
    {
        _stop?.Cancel();
        await _gate.WaitAsync();
        try { _disposed = true; await StopAsync(); }
        finally { _gate.Release(); }
    }
}
