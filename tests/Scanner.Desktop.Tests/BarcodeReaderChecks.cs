using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Scanner.Contracts;
using Scanner.Core;
using Scanner.Desktop;
using Scanner.Server.Services;
using Scanner.Server.ViewModels;

internal static class BarcodeReaderChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string spp = "{00001101-0000-1000-8000-00805f9b34fb}_VID&000105d6_PID&000a";
        check(BluetoothReaderDiscovery.OutgoingAddress(spp, "7&15f8e43c&0&AE3B00256A8D_C00000000") == "AE3B00256A8D", "Outgoing SPP remote address identified");
        check(BluetoothReaderDiscovery.OutgoingAddress(spp, "7&15f8e43c&0&000000000000_00000000") is null, "Incoming Bluetooth port excluded");
        check(BluetoothReaderDiscovery.OutgoingAddress(spp.Replace("1101", "110b"), "7&0&AE3B00256A8D_C00000000") is null, "Non-SPP Bluetooth service excluded");
        check(BluetoothReaderDiscovery.OutgoingAddress("USB", "7&0&AE3B00256A8D_C00000000") is null, "Non-Bluetooth serial port excluded");
        check(BluetoothReaderDiscovery.OutgoingAddress(spp, "unknown") is null, "Unknown direction is not presented as compatible");

        var buffer = new BarcodeScanBuffer();
        const string raw = " \t[)>\x1e" + "06\x1dPé😀\0\r\nnext\x04\r";
        var bytes = Encoding.UTF8.GetBytes(raw + "\r");
        for (var i = 0; i < bytes.Length; i++) buffer.Append(bytes.AsSpan(i, 1), i);
        check(buffer.TakeCompleted(bytes.Length + 248) is null, "No scan before quiet boundary, including fragmented UTF-8");
        check(buffer.TakeCompleted(bytes.Length + 249) == raw, "Only scanner CR suffix removed; all payload controls and Unicode preserved");
        check(buffer.TakeCompleted(9999) is null, "A scan cannot be delivered twice by buffer");
        buffer.Append("second\r"u8, 10000);
        check(buffer.TakeCompleted(10250) == "second", "Separate manual scans remain separate");
        buffer.Append("incomplete"u8, 11000);
        check(buffer.TakeCompleted(11250) is null && buffer.TakeCompleted(15999) is null, "Incomplete scan survives Bluetooth gaps while waiting for ending");
        Throws<InvalidDataException>(() => buffer.TakeCompleted(16000), check, "Incomplete scan expires after five idle seconds without emitting partial raw data");
        buffer.Append(new byte[] { 0xff, 13 }, 12000);
        Throws<InvalidDataException>(() => buffer.TakeCompleted(12250), check, "Invalid UTF-8 is not silently replaced");
        buffer.Append(new byte[ProtocolJson.MaximumLabelBytes + 1], 13000);
        Throws<InvalidDataException>(() => buffer.TakeCompleted(13250), check, "Oversized scan dropped atomically");
        buffer.Append("after-error\r"u8, 14000);
        check(buffer.TakeCompleted(14250) == "after-error", "Next scan recovers after buffer errors");
        buffer.Append("partial"u8, 15000); buffer.Reset();
        check(buffer.TakeCompleted(16000) is null, "Partial scan discarded on session reset");

        // Both supported reader endings must preserve every payload character,
        // including original trailing newlines and supplier RS/EOT controls.
        foreach (var suffix in new[] { "\r", "\r\n" })
        foreach (var payload in new[] { "", "plain", "\tvalue\r\nnext", "trailing\r", "trailing\n", "trailing\r\n", "supplier\x1e\x04", "é😀\0\t " })
        {
            buffer.Append(Encoding.UTF8.GetBytes(payload + suffix), 17000);
            check(buffer.TakeCompleted(17250) == payload, "CR/CRLF suffix removal preserves exact payload and trailing controls");
        }
        buffer.Append("split\r"u8, 18000);
        check(buffer.TakeCompleted(18100) is null, "Fragmented CRLF is not emitted before its LF arrives");
        buffer.Append("\n"u8, 18100);
        check(buffer.TakeCompleted(18349) is null && buffer.TakeCompleted(18350) == "split", "Fragmented CRLF removed as one suffix");
        buffer.Append("wrong\n"u8, 19000);
        Throws<InvalidDataException>(() => buffer.TakeCompleted(24000), check, "Bare LF is not mistaken for configured CR/CRLF framing");
        const string supplierScan = "[)>\x1e" + "06\x1dPOEPS080126 P-831 IA-1247\x1d" + "1PSN74LVC4245APWR\x1dQ3\x1e\x04";
        buffer.Append(Encoding.UTF8.GetBytes(supplierScan + "\r\n"), 20000);
        var completed = buffer.TakeCompleted(20250)!;
        check(completed == supplierScan && LabelClassifier.Classify(completed).Data == LabelData.FromSupplier("OEPS080126", "SN74LVC4245APWR", "3"),
            "BCST-73 supplier scan ending RS EOT CR LF preserves payload and validates as version 3");
        var supplierBytes = Encoding.UTF8.GetBytes(supplierScan + "\r\n");
        buffer.Append(supplierBytes.AsSpan(0, 20), 25000);
        check(buffer.TakeCompleted(25250) is null && buffer.TakeCompleted(26000) is null, "First supplier chunk retained across one-second Bluetooth pause");
        buffer.Append(supplierBytes.AsSpan(20), 26500);
        check(buffer.TakeCompleted(26750) == supplierScan, "Delayed supplier continuation is reunited with original prefix");

        FastFramingChecks(check);
        ReaderRoutingChecks.Run(check);
    }

    private static void FastFramingChecks(Action<bool, string> check)
    {
        var buffer = new BarcodeScanBuffer(fastScans: true);
        var input = string.Concat(Enumerable.Repeat("same\r\n", 100));
        buffer.Append(Encoding.UTF8.GetBytes(input), 0);
        for (var i = 0; i < 100; i++) check(buffer.TakeCompleted(0) == "same", "Fast mode emits every repeated scan immediately from one read");
        check(buffer.TakeCompleted(0) is null, "Fast burst has no merged payload or extra scan");
        const string raw = "\t[)>\x1e" + "06\x1dPé😀\0\x04";
        var bytes = Encoding.UTF8.GetBytes(raw + "\r\n");
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            buffer.Append(bytes.AsSpan(i, 1), i * 300);
            check(buffer.TakeCompleted(i * 300) is null, "UTF-8 and CRLF may span delayed reads without premature delivery");
        }
        buffer.Append(bytes.AsSpan(bytes.Length - 1), 10000);
        check(buffer.TakeCompleted(10000) == raw, "Fast framing preserves non-delimiter controls and UTF-8");
        buffer.Append("a\r\nb\r\n"u8, 10001);
        check(buffer.TakeCompleted(10001) == "a" && buffer.TakeCompleted(10001) == "b", "Embedded CRLF is explicitly a boundary in fast mode");
        buffer.Append(new byte[] { 255, 13, 10, 65, 13, 10 }, 10002);
        Throws<InvalidDataException>(() => buffer.TakeCompleted(10002), check, "Invalid UTF-8 frame is diagnosed");
        check(buffer.TakeCompleted(10002) == "A", "Valid frame after invalid frame in same read is retained");
        buffer.Append(new byte[ProtocolJson.MaximumLabelBytes + 1], 11000);
        Throws<InvalidDataException>(() => buffer.TakeCompleted(11000), check, "Oversized fast frame is diagnosed");
        buffer.Append("tail\r\nnext\r\n"u8, 11001);
        check(buffer.TakeCompleted(11001) == "next" && buffer.TakeCompleted(11001) is null, "Overflow discards remainder until delimiter without leaking a partial X payload");
        buffer.Append("partial"u8, 12000);
        Throws<InvalidDataException>(() => buffer.TakeCompleted(17000), check, "Unterminated fast frame times out");
        buffer.Append("tail\r\nnext\r\n"u8, 18000);
        check(buffer.TakeCompleted(18000) == "next", "Timeout resynchronizes at next delimiter");
        buffer.Append("queued\r\nunfinished"u8, 19000); buffer.Reset();
        check(buffer.TakeCompleted(19000) is null, "Disconnect reset clears complete and partial queued data");
    }
    private static void Throws<T>(Action action, Action<bool, string> check, string reason) where T : Exception
    {
        try { action(); check(false, reason); } catch (T) { check(true, reason); }
    }
}
