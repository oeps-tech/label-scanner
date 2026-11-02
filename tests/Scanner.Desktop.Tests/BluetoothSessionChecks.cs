using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Scanner.Server.Services;

internal static class BluetoothSessionChecks
{
    private static readonly BarcodeReaderChoice Reader = new("AE3B00256A8D_C00000000", "Test reader", "COM7");

    public static void Run(Action<bool, string> check)
    {
        CadenceLossAndReconnect(check);
        UnknownAndRecovery(check);
        SerialFailures(check);
        CancelPendingOpen(check);
    }

    private static void CadenceLossAndReconnect(Action<bool, string> check)
    {
        using var session = new Session();
        var first = session.Connect();
        check(session.Probe.Calls == 1 && session.Latest.Open && session.Latest.Text.StartsWith("Connected"),
            "Successful COM open immediately probes Bluetooth before claiming connected");
        first.Step(1000, "OLD-PARTIAL");
        session.Probe.State = BluetoothLinkState.Disconnected;
        first.Step(1999);
        check(session.Probe.Calls == 1 && !first.Disposed, "Serial timeouts do not probe Bluetooth before two seconds");
        first.Step(2000);
        check(session.Probe.Calls == 2 && !first.Disposed && session.Latest.Open
            && session.Latest.Text.Contains("checking Bluetooth"), "One negative Bluetooth check keeps the COM session open while confirming");
        first.Step(3999);
        check(session.Probe.Calls == 2 && !first.Disposed, "A first negative does not create a busy polling loop");
        first.Step(4000);
        Until(() => !session.Latest.Open);
        check(session.Probe.Calls == 3 && first.Disposed && session.Latest.PortDisposed
            && session.Latest.Text.Contains("Bluetooth connection lost"), "Second consecutive negative closes COM before reporting Bluetooth loss");
        check(session.Scans.IsEmpty, "Incomplete scan is not flushed when Bluetooth is lost");

        session.Probe.State = BluetoothLinkState.Disconnected;
        var second = session.Connect();
        check(!second.Disposed && session.Latest.Open && session.Probe.Calls == 4,
            "Reconnecting starts a fresh negative-check count instead of immediately closing the new session");
        session.Probe.State = BluetoothLinkState.Connected;
        second.Step(6000, "NEW\r\n");
        check(session.Scans.ToArray().SequenceEqual(new[] { "NEW" }) && session.Latest.Text.StartsWith("Connected"),
            "Reconnect discards old partial bytes and resumes scans with the new Bluetooth state");
        var before = session.Probe.Calls;
        second.Step(7L * 24 * 60 * 60 * 1000);
        check(!second.Disposed && session.Probe.Calls == before + 1 && session.Latest.Open,
            "Long scan inactivity does not disconnect a reader or replay every missed poll");
        session.Disconnect();
        check(second.Disposed && !session.Latest.Open && session.Latest.Text == "Disconnected" && session.Latest.PortDisposed,
            "Manual disconnect closes an idle COM session before reporting disconnected");
    }

    private static void UnknownAndRecovery(Action<bool, string> check)
    {
        using var session = new Session();
        session.Probe.State = BluetoothLinkState.Unknown;
        var port = session.Connect();
        check(session.Latest.Open && session.Latest.Text.Contains("reader status unknown")
            && !session.Latest.Text.StartsWith("Connected"), "Unknown OS status is shown honestly while COM remains usable");
        port.Step(1000, "scan\r\n");
        check(session.Scans.ToArray().SequenceEqual(new[] { "scan" }) && session.Latest.Text.Contains("reader status unknown"),
            "Unknown Bluetooth status still permits scans without claiming a confirmed connection");

        session.Probe.State = BluetoothLinkState.Disconnected;
        port.Step(2000);
        session.Probe.State = BluetoothLinkState.Connected;
        port.Step(4000);
        check(!port.Disposed && session.Latest.Text.StartsWith("Connected"), "A recovered connection cancels the first negative check");
        session.Probe.State = BluetoothLinkState.Disconnected;
        port.Step(6000);
        session.Probe.State = BluetoothLinkState.Unknown;
        port.Step(8000);
        session.Probe.State = BluetoothLinkState.Disconnected;
        port.Step(10000);
        check(!port.Disposed && session.Latest.Open, "Unknown breaks the consecutive-negative sequence");
        session.Probe.Fail = true;
        port.Step(12000);
        check(!port.Disposed && session.Latest.Open && session.Latest.Text.Contains("reader status unknown"),
            "Native probe failure becomes unknown instead of a serial disconnect");
        session.Probe.Fail = false;
        port.Step(14000);
        check(!port.Disposed && session.Latest.Open, "Probe exception resets negative evidence before another disconnected result");
        session.Probe.State = BluetoothLinkState.Connected;
        port.Step(16000);
        check(session.Latest.Text.StartsWith("Connected") && !port.Disposed, "Connection state recovers after unavailable OS status");
        session.Dispose();
        check(port.Disposed, "Disposal releases the serial handle even while waiting for scans");
    }

    private static void SerialFailures(Action<bool, string> check)
    {
        foreach (var error in new Exception?[] { new IOException("Radio handle failed"), new InvalidOperationException("COM port closed"), null })
        {
            using var session = new Session();
            var port = session.Connect();
            port.Step(1, error: error, eof: error is null);
            Until(() => !session.Latest.Open);
            check(port.Disposed && session.Latest.PortDisposed && session.Probe.Calls == 1,
                "Read error or end-of-stream closes immediately without waiting for Bluetooth polling");
            check(session.Latest.Text.StartsWith("Reader disconnected"), "Serial failure produces a disconnected status");
        }
        using var failed = new Session();
        var failure = new ScriptedPort(failed.Clock) { OpenError = new IOException("COM occupied") };
        failed.Ports.Enqueue(failure);
        var rejected = false;
        try { Await(failed.Connection.ConnectAsync(Reader)); }
        catch (IOException) { rejected = true; }
        Until(() => !failed.Events.IsEmpty);
        check(rejected && failure.Disposed && !failed.Latest.Open && failed.Probe.Calls == 0,
            "Failed COM open disposes the port and never probes or announces Bluetooth connected");
    }

    private static void CancelPendingOpen(Action<bool, string> check)
    {
        using var session = new Session();
        using var gate = new ManualResetEventSlim();
        var port = new ScriptedPort(session.Clock) { OpenGate = gate };
        session.Ports.Enqueue(port);
        var connect = session.Connection.ConnectAsync(Reader);
        Until(() => port.OpenEntered);
        var disconnect = session.Connection.DisconnectAsync();
        gate.Set();
        var cancelled = false;
        try { Await(connect); } catch (OperationCanceledException) { cancelled = true; }
        Await(disconnect);
        check(cancelled && port.Disposed && session.Probe.Calls == 0 && !session.Events.Any(e => e.Open),
            "Disconnect requested during a pending open prevents late connected status and Bluetooth polling");
    }

    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static void Until(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5))) throw new TimeoutException("Bluetooth session test did not reach its expected state.");
    }

    private sealed class ClockValue
    {
        private long _now;
        public long Read() => Interlocked.Read(ref _now);
        public void Set(long value) => Interlocked.Exchange(ref _now, value);
    }
    private sealed class Probe : IBluetoothConnectionProbe
    {
        private int _state = (int)BluetoothLinkState.Connected, _calls, _fail;
        public BluetoothLinkState State { set => Volatile.Write(ref _state, (int)value); }
        public bool Fail { set => Volatile.Write(ref _fail, value ? 1 : 0); }
        public int Calls => Volatile.Read(ref _calls);
        public BluetoothLinkState Check(BarcodeReaderChoice reader)
        {
            Interlocked.Increment(ref _calls);
            if (Volatile.Read(ref _fail) != 0) throw new IOException("OS query unavailable");
            return (BluetoothLinkState)Volatile.Read(ref _state);
        }
    }
    private sealed record Status(bool Open, string Text, bool PortDisposed);
    private sealed class Session : IDisposable
    {
        public readonly ClockValue Clock = new();
        public readonly Probe Probe = new();
        public readonly ConcurrentQueue<ScriptedPort> Ports = new();
        public readonly ConcurrentQueue<Status> Events = new();
        public readonly ConcurrentQueue<string> Scans = new();
        public readonly BarcodeReaderConnection Connection;
        private ScriptedPort? _current;
        private bool _disposed;
        public Status Latest => Events.Last();
        public Session()
        {
            Connection = new(Probe, _ => Ports.TryDequeue(out _current) ? _current : throw new InvalidOperationException("No scripted port"), Clock.Read);
            Connection.StatusChanged += (open, text) => Events.Enqueue(new(open, text, _current?.Disposed == true));
            Connection.ScanReceived += Scans.Enqueue;
        }
        public ScriptedPort Connect()
        {
            var port = new ScriptedPort(Clock); Ports.Enqueue(port);
            Await(Connection.ConnectAsync(Reader));
            Until(() => port.ReadCount > 0);
            return port;
        }
        public void Disconnect() => Await(Connection.DisconnectAsync());
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Await(Connection.DisposeAsync().AsTask());
        }
    }
    private sealed class ReadStep(long time, byte[]? bytes, Exception? error, bool eof)
    {
        public readonly long Time = time;
        public readonly byte[]? Bytes = bytes;
        public readonly Exception? Error = error;
        public readonly bool Eof = eof;
        public int ReadNumber;
        public int Taken;
    }
    private sealed class ScriptedPort(ClockValue clock) : IBarcodeSerialPort
    {
        private readonly BlockingCollection<ReadStep> _steps = new();
        private int _reads, _disposed, _openEntered;
        public Exception? OpenError { get; init; }
        public ManualResetEventSlim? OpenGate { get; init; }
        public bool OpenEntered => Volatile.Read(ref _openEntered) != 0;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public int ReadCount => Volatile.Read(ref _reads);
        public void Open()
        {
            Volatile.Write(ref _openEntered, 1);
            if (OpenGate is not null && !OpenGate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Scripted open gate");
            if (OpenError is not null) throw OpenError;
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            var number = Interlocked.Increment(ref _reads);
            // Match the production read timeout, so cancellation never depends on a test sending more data.
            if (!_steps.TryTake(out var step, 50)) throw new TimeoutException();
            clock.Set(step.Time); step.ReadNumber = number; Volatile.Write(ref step.Taken, 1);
            if (step.Error is not null) throw step.Error;
            if (step.Eof) return 0;
            if (step.Bytes is null) throw new TimeoutException();
            if (step.Bytes.Length > count) throw new InvalidOperationException("Test input exceeds serial read buffer");
            step.Bytes.CopyTo(buffer, offset); return step.Bytes.Length;
        }
        public void Step(long time, string? text = null, Exception? error = null, bool eof = false)
        {
            var step = new ReadStep(time, text is null ? null : Encoding.UTF8.GetBytes(text), error, eof);
            _steps.Add(step);
            Until(() => Volatile.Read(ref step.Taken) != 0 && (ReadCount > step.ReadNumber || Disposed));
        }
        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
}
