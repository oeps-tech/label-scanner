using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Scanner.Contracts;

namespace Scanner.Server.Services;

/// <summary>Private LE32/UTF-8 IPC. The worker owns capture; no public client traffic enters this channel.</summary>
public sealed class WorkerProcess(AppPaths paths) : IAsyncDisposable
{
    private const int MaximumMessageBytes = 64 * 1024 * 1024;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<JsonElement> _candidates = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private Process? _process;
    private Task? _readTask, _errorTask;
    private JsonElement? _latestPreview;
    private readonly object _previewLock = new();
    public event Action<JsonElement>? Status;
    public event Action<string>? Diagnostic;
    public bool IsRunning => _process is { HasExited: false };
    public IAsyncEnumerable<JsonElement> Candidates(CancellationToken ct) => _candidates.Reader.ReadAllAsync(ct);
    public JsonElement? TakePreview()
    {
        lock (_previewLock) { var result = _latestPreview; _latestPreview = null; return result; }
    }
    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "Scanner.Recognition.exe");
        var packagedPython = Path.Combine(AppContext.BaseDirectory, "worker", "python", "python.exe");
        var python = File.Exists(packagedPython) ? packagedPython : Path.Combine(paths.Repository, ".tools", "python", "python.exe");
        var venv = Path.Combine(paths.Repository, ".venv", "Scripts", "python.exe");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = paths.Repository };
        if (File.Exists(bundled)) start.FileName = bundled;
        else
        {
            if (!File.Exists(packagedPython) && File.Exists(venv)) python = venv;
            if (!File.Exists(python)) throw new FileNotFoundException("Recognition runtime is unavailable. Run scripts/setup-recognition.ps1 or use the complete Windows release.");
            start.FileName = python;
            if (File.Exists(packagedPython)) start.WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "worker");
            start.ArgumentList.Add("-u"); start.ArgumentList.Add("-m"); start.ArgumentList.Add("recognition.worker");
        }
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["OEPS_SCANNER_DATA"] = paths.Root;
        _process?.Dispose();
        _process = Process.Start(start) ?? throw new IOException("Unable to start recognition worker.");
        _readTask = ReadLoopAsync(_process.StandardOutput.BaseStream, _stop.Token);
        _errorTask = ReadErrorsAsync(_process, _stop.Token);
        return Task.CompletedTask;
    }
    public async Task SendAsync(object command, CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited) throw new IOException("Recognition worker is not running.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command, ProtocolJson.Options);
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await _sendLock.WaitAsync(ct);
        try { await _process.StandardInput.BaseStream.WriteAsync(header, ct); await _process.StandardInput.BaseStream.WriteAsync(bytes, ct); await _process.StandardInput.BaseStream.FlushAsync(ct); }
        finally { _sendLock.Release(); }
    }
    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
                var count = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (count is 0 or > MaximumMessageBytes) throw new InvalidDataException("Recognition IPC message exceeds its limit.");
                var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct);
                using var document = JsonDocument.Parse(bytes);
                var item = document.RootElement.Clone();
                var type = item.GetProperty("type").GetString();
                if (type == "preview") { lock (_previewLock) _latestPreview = item; }
                else if (type == "candidate") _candidates.Writer.TryWrite(item);
                else Status?.Invoke(item);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Diagnostic?.Invoke("Worker stopped: " + ex.Message); }
    }
    private async Task ReadErrorsAsync(Process process, CancellationToken ct)
    {
        try { while (await process.StandardError.ReadLineAsync(ct) is { } line) Diagnostic?.Invoke(line); }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (IsRunning) { try { await SendAsync(new { type = "shutdown" }); } catch (IOException) { } }
        _stop.Cancel();
        if (_process is { HasExited: false })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { _process.Kill(true); }
        }
        if (_readTask is not null) { try { await _readTask; } catch (IOException) { } }
        if (_errorTask is not null) { try { await _errorTask; } catch (IOException) { } }
        _process?.Dispose(); _stop.Dispose(); _sendLock.Dispose();
    }
}
