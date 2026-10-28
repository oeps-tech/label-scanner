using System.IO;
using System.Text;
using Scanner.Contracts;

namespace Scanner.Server.Services;

// Fast mode uses CRLF as an explicit delimiter, including across reads and with
// multiple scans in one read. Embedded CRLF is a boundary in this mode.
// Multiline mode: for manual trigger scans we collect a burst until
// 250 ms of silence, then remove ONLY one scanner-added CR or CRLF suffix.
// Embedded CR/LF, GS, RS, EOT, NUL, whitespace and UTF-8 are preserved.
// This is intentionally not ReadLine: supplier/raw labels can contain newlines.
// Normal/manual mode, UTF-8 output and a CR or CRLF suffix are required. Inventory/bulk
// uploads and scans less than 250 ms apart need a framed device protocol instead.
public sealed class BarcodeScanBuffer(bool fastScans = false)
{
    public const int QuietMilliseconds = 250;
    public const int IncompleteMilliseconds = 5000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly MemoryStream _bytes = new();
    private long _lastByte;
    private bool _overflow;
    private readonly Queue<(string? Payload, string? Error)> _completed = new();
    private bool _discarding, _previousCr;
    public bool HasPending => _overflow || _bytes.Length > 0 || _completed.Count > 0;

    public void Append(ReadOnlySpan<byte> bytes, long now)
    {
        if (bytes.IsEmpty) return;
        _lastByte = now;
        if (fastScans)
        {
            foreach (var value in bytes)
            {
                var ending = _previousCr && value == 10;
                _previousCr = value == 13;
                if (_discarding)
                {
                    if (ending) { _discarding = false; _previousCr = false; }
                    continue;
                }
                if (_bytes.Length >= ProtocolJson.MaximumLabelBytes)
                {
                    _completed.Enqueue((null, "Reader scan is too large; discarded through its CR+LF ending."));
                    _bytes.SetLength(0); _bytes.Position = 0;
                    _discarding = !ending; continue;
                }
                _bytes.WriteByte(value);
                if (!ending) continue;
                try { _completed.Enqueue((Utf8.GetString(_bytes.GetBuffer(), 0, (int)_bytes.Length - 2), null)); }
                catch (DecoderFallbackException) { _completed.Enqueue((null, "Reader data is not UTF-8. Configure UTF-8 output; scan discarded.")); }
                _bytes.SetLength(0); _bytes.Position = 0; _previousCr = false;
            }
            return;
        }
        if (_overflow) return;
        if (_bytes.Length + bytes.Length > ProtocolJson.MaximumLabelBytes)
        { _bytes.SetLength(0); _overflow = true; return; }
        _bytes.Write(bytes);
    }

    public string? TakeCompleted(long now)
    {
        if (fastScans)
        {
            if (_completed.TryDequeue(out var item))
            {
                if (item.Error is not null) throw new InvalidDataException(item.Error);
                return item.Payload;
            }
            if (_bytes.Length > 0 && now - _lastByte >= IncompleteMilliseconds)
            {
                _bytes.SetLength(0); _bytes.Position = 0; _discarding = true;
                throw new InvalidDataException("Reader scan has no CR+LF ending after 5 seconds. Enable Add Enter (CR+LF); discarding through the next ending.");
            }
            return null;
        }
        if (!HasPending || now - _lastByte < QuietMilliseconds) return null;
        var bytes = _bytes.GetBuffer().AsSpan(0, (int)_bytes.Length);
        var suffixLength = bytes.EndsWith("\r\n"u8) ? 2 : bytes.Length > 0 && bytes[^1] == 13 ? 1 : 0;
        // Bluetooth may pause between chunks of a single label. Silence alone
        // never completes a scan: keep the prefix until its ending arrives.
        if (!_overflow && suffixLength == 0 && now - _lastByte < IncompleteMilliseconds) return null;
        try
        {
            if (_overflow) throw new InvalidDataException("Reader scan is too large; scan discarded.");
            // BCST-73's "Add Enter" sends CRLF in SPP mode. Do not TrimEnd:
            // payloads may themselves end in CR/LF or supplier RS/EOT controls.
            if (suffixLength == 0)
                throw new InvalidDataException($"Reader scan has no CR or CR+LF ending. Enable 'Add Enter' on the reader. Last bytes (hex): {Convert.ToHexString(bytes[^Math.Min(8, bytes.Length)..])}; scan discarded.");
            return Utf8.GetString(bytes[..^suffixLength]);
        }
        catch (DecoderFallbackException ex)
        { throw new InvalidDataException("Reader data is not UTF-8. Configure UTF-8 output; scan discarded.", ex); }
        finally { Reset(); }
    }

    public void Reset() { _bytes.SetLength(0); _bytes.Position = 0; _overflow = false; _completed.Clear(); _discarding = _previousCr = false; }
}
