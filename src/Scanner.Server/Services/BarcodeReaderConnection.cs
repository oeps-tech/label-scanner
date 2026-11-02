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

/// <summary>The synchronous serial operations owned by a single reader session.</summary>
public interface IBarcodeSerialPort : IDisposable
{
    void Open();
    int Read(byte[] buffer, int offset, int count);
}

public sealed class BarcodeReaderConnection : IBarcodeReaderConnection
{
    public const int BluetoothCheckIntervalMs = 2000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IBluetoothConnectionProbe _bluetooth;
    private readonly Func<BarcodeReaderChoice, IBarcodeSerialPort> _portFactory;
    private readonly Func<long> _clock;
    private CancellationTokenSource? _stop;
    private Task? _session;
    private bool _disposed;
    public event Action<string>? ScanReceived;
    public event Action<bool, string>? StatusChanged;
    public event Action<string>? Diagnostic;

    public BarcodeReaderConnection(IBluetoothConnectionProbe? bluetooth = null,
        Func<BarcodeReaderChoice, IBarcodeSerialPort>? portFactory = null, Func<long>? clock = null)
    {
        _bluetooth = bluetooth ?? new WindowsBluetoothConnectionProbe();
        _portFactory = portFactory ?? (reader => new ReaderSerialPort(reader.PortName));
        _clock = clock ?? (() => Environment.TickCount64);
    }

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
            using var port = _portFactory(reader);
            port.Open(); // Windows opens exclusively; an occupied port fails visibly.
            token.ThrowIfCancellationRequested();
            var disconnectedChecks = 0;
            string? lastStatus = null;
            void CheckBluetooth()
            {
                BluetoothLinkState state;
                try { state = _bluetooth.Check(reader); }
                catch (Exception) { state = BluetoothLinkState.Unknown; }
                token.ThrowIfCancellationRequested();
                // A failed/unknown query is not evidence of a disconnected reader.
                // Only two consecutive explicit negative results close the port.
                disconnectedChecks = state == BluetoothLinkState.Disconnected ? disconnectedChecks + 1 : 0;
                if (disconnectedChecks >= 2) throw new IOException("Bluetooth connection lost");
                var status = state switch
                {
                    BluetoothLinkState.Connected => $"Connected · {reader.DisplayName} · waiting for a scan",
                    BluetoothLinkState.Disconnected => $"COM open — checking Bluetooth connection · {reader.DisplayName}",
                    _ => $"COM open — reader status unknown · {reader.DisplayName}"
                };
                if (status != lastStatus) { lastStatus = status; StatusChanged?.Invoke(true, status); }
            }
            CheckBluetooth();
            ready.TrySetResult();
            var nextBluetoothCheck = _clock() + BluetoothCheckIntervalMs;
            var buffer = new BarcodeScanBuffer(fastScans);
            var bytes = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                // Query OS connection state on the reader worker, never the GUI thread.
                // Serial read timeouts and time since the last scan do not indicate loss.
                if (_clock() >= nextBluetoothCheck)
                {
                    CheckBluetooth();
                    nextBluetoothCheck = _clock() + BluetoothCheckIntervalMs;
                }
                // Check between reads as well as on timeouts; a chunk may arrive
                // just after a previous scan's quiet interval has completed.
                EmitCompleted(buffer);
                try
                {
                    var count = port.Read(bytes, 0, bytes.Length);
                    if (count == 0) throw new IOException("Bluetooth serial stream closed.");
                    if (token.IsCancellationRequested) break;
                    EmitCompleted(buffer);
                    buffer.Append(bytes.AsSpan(0, count), _clock());
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
            // The using scope above disposes the COM handle before announcing failure.
            // The view model keeps WebSocket clients connected and owns retry policy.
            if (!token.IsCancellationRequested) StatusChanged?.Invoke(false, "Reader disconnected · " + ex.Message);
        }
        finally { ready.TrySetCanceled(); }
    }

    private void EmitCompleted(BarcodeScanBuffer buffer)
    {
        while (true)
        {
            try
            {
                if (buffer.TakeCompleted(_clock()) is not { } raw) return;
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

    private sealed class ReaderSerialPort(string portName) : IBarcodeSerialPort
    {
        private readonly SerialPort _port = new(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, ReadTimeout = 50, WriteTimeout = 1000,
            DiscardNull = false, ReadBufferSize = 65536
        };
        public void Open() => _port.Open();
        public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
        public void Dispose() => _port.Dispose();
    }
}
