using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Scanner.Client;
using Scanner.Contracts;
using Scanner.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); _checks++; }
    private static string Encode(string body) => body + "*" + PayloadValidator.Crc32(body);
    private static async Task Main()
    {
        Check(PayloadValidator.Crc32("123456789") == "CBF43926", "Standard CRC-32/IEEE check vector");
        Check(PayloadValidator.Crc32("1*OEPS010123*0926-TRAY-Q5R2*1") == "455A8C51", "Single-separator printer test vector");
        Check(PayloadValidator.Validate("1*OEPSA010123*0926-TRAY-Q5R2*1*D91C90D6").Data == new LabelData(1, "OEPSA010123", "0926-TRAY-Q5R2", "1"), "Ready-to-print example with independently calculated CRC");
        var valid = new[] { "OEPSA12345", "OEPSA010123", "OEPS010123" };
        foreach (var pn in valid)
        foreach (var lot in new[] { "0926-TRY-Q5R2", "0926-TRAY-0123" })
        foreach (var quantity in new[] { "0", "1", "12314232", "0.12312", "12312.02312", "000.100", "123456789012345678901234567890.123456789012345678901" })
        {
            var result = PayloadValidator.Validate(Encode($"1*{pn}*{lot}*{quantity}"));
            Check(result.Valid && result.Data!.Quantity == quantity, "All allowed formats preserve exact quantity");
            var json = ProtocolJson.Serialize(result.Data!);
            using var doc = JsonDocument.Parse(json);
            Check(doc.RootElement.EnumerateObject().Count() == 4 && doc.RootElement.GetProperty("quantity").ValueKind == JsonValueKind.String, "Exactly four fields, quantity string");
            Check(PayloadValidator.ReadClientJson(json) == result.Data, "SDK round trip");
        }
        foreach (var pn in new[] { "OEPSa010123", "OEPS12345", "OEPSA1234", "OEPSAA010123", " OEPS010123", "OEPS010123\n" })
            Check(!PayloadValidator.Validate(Encode($"1*{pn}*0926-TRAY-Q5R2*0")).Valid, "Reject invalid PN");
        foreach (var lot in new[] { "0926_TRAY_Q5R2", "0926-Tray-Q5R2", "0926-TR-Q5R2", "926-TRAY-Q5R2", "0926-TRAY-q5R2", "0926-TRAY-Q5R22" })
            Check(!PayloadValidator.Validate(Encode($"1*OEPSA010123*{lot}*0")).Valid, "Reject invalid lot");
        foreach (var prefix in new[] { "0000", "0021", "0026", "0100", "0121", "0926", "1200", "1221", "1299" })
        foreach (var suffix in new[] { "TRY-Q5R2", "TRAY-Q5R2" })
        {
            var data = new LabelData(1, "OEPSA010123", $"{prefix}-{suffix}", "0");
            Check(PayloadValidator.Validate(Encode($"1*{data.OepsPn}*{data.Lot}*0")).Valid, "Accept allowed MMYY boundaries including independent zero values");
            Check(PayloadValidator.ReadClientJson(ProtocolJson.Serialize(data)) == data, "Client accepts allowed MMYY without changing the Lot");
        }
        foreach (var prefix in new[] { "1300", "1321", "9926", "0001", "0019", "0020", "0901", "0919", "0920", "1201", "1220", "9900" })
        {
            var data = new LabelData(1, "OEPSA010123", $"{prefix}-TRAY-Q5R2", "0");
            var result = PayloadValidator.Validate(Encode($"1*{data.OepsPn}*{data.Lot}*0"));
            Check(!result.Valid && result.Stage == "lot", "Reject out-of-range MMYY even with a valid checksum");
            var rejected = false;
            try { PayloadValidator.ReadClientJson(ProtocolJson.Serialize(data)); }
            catch (JsonException) { rejected = true; }
            Check(rejected, "Client rejects the same invalid MMYY ranges");
        }
        foreach (var q in new[] { "", "-1", "+1", "1,2", "1.", ".2", "1e3", "1 2", " 0" })
            Check(!PayloadValidator.Validate(Encode($"1*OEPSA010123*0926-TRAY-Q5R2*{q}")).Valid, "Reject invalid quantity");
        Check(PayloadValidator.Validate("2*bad").Stage == "version", "Version gates all other validation");
        Check(!PayloadValidator.Validate("1*OEPSA010123*0926-TRAY-Q5R2*0*01234567").Valid, "User placeholder is not a valid CRC");
        var body = "1*OEPSA010123*0926-TRAY-Q5R2*0";
        var encoded = Encode(body);
        Check(!PayloadValidator.Validate(encoded.Replace("Q5R2", "Q5R3")).Valid, "Corruption fails CRC");
        Check(!PayloadValidator.Validate(encoded + "\n").Valid, "No trailing newline");
        Check(!PayloadValidator.Validate(encoded + "*extra").Valid, "Exactly five encoded fields");
        Check(!PayloadValidator.Validate("1*OEPSA010123*0926-TRAY-Q5R2*0*abcdef12").Valid, "Uppercase checksum required");
        Check(encoded == "1*OEPSA010123*0926-TRAY-Q5R2*0*AE1BA040", "Updated zero-quantity reference checksum");
        Check(PayloadValidator.Validate("1**OEPSA010123**0926-TRAY-Q5R2**0**A924880D").Stage == "fields", "Reject old double separators even with their original valid CRC");
        Check(PayloadValidator.Validate(Encode("1*OEPSA010123**0926-TRAY-Q5R2*0")).Stage == "fields", "Reject mixed single/double separators even with recalculated CRC");
        Check(PayloadValidator.Validate("1*OEPSA010123*0926-TRAY-Q5R2*0*A924880D").Stage == "checksum", "Replacing separators requires recalculating the checksum");
        var gate = new DuplicateGate();
        Check(gate.CanSend("A", 0), "First observation"); gate.MarkSent("A", 0);
        Check(!gate.CanSend("A", 499), "Suppress for 499 ms");
        Check(gate.CanSend("B", 10), "Different payload independent"); gate.MarkSent("B", 10);
        Check(gate.CanSend("A", 500) && !gate.CanSend("B", 500), "Exact 500 ms boundary is per payload");
        gate.MarkSent("A", 500); Check(!gate.CanSend("A", 999), "Interval anchored to last broadcast");
        var demand = new CameraDemand();
        Check(!demand.ShouldConnect(0, false, false, 0), "Idle startup does not open camera");
        Check(demand.ShouldConnect(1, false, false, 10), "Client connects camera");
        Check(demand.ShouldConnect(0, false, true, 20), "Last client leaves: grace starts");
        Check(demand.ShouldConnect(0, false, true, 60_019), "59.999 seconds stays on");
        Check(!demand.ShouldConnect(0, false, true, 60_020), "60 seconds disconnects");
        Check(demand.ShouldConnect(1, false, false, 70_000), "Reconnect after idle");
        Check(demand.ShouldConnect(0, false, true, 70_010), "New idle period");
        Check(demand.ShouldConnect(1, false, true, 70_500), "New client cancels idle timer");
        Check(demand.ShouldConnect(0, true, true, 900_000), "Always-on debug override");
        Check(demand.ShouldConnect(0, false, true, 900_010), "Turning override off starts fresh minute");
        await NetworkAsync(PayloadValidator.Validate(encoded).Data!);
        await UpdateChecks.RunAsync(Check);
        Console.WriteLine($"PASS: {_checks} validation, CRC, cooldown, camera lifecycle, network and update assertions.");
        Console.WriteLine("Valid example: " + encoded);
    }
    private static async Task Until(Func<bool> check)
    { using var timeout = new CancellationTokenSource(5000); while (!check()) await Task.Delay(10, timeout.Token); }
    private static async Task NetworkAsync(LabelData data)
    {
        await using var host = new ScannerSocketHost(); await host.StartAsync(0);
        using var timeout = new CancellationTokenSource(5000);
        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/labels"), timeout.Token);
        var message = WebSocketMessages.ReceiveAsync(raw, 16384, timeout.Token);
        await Task.Delay(60);
        Check(!message.IsCompleted, "No greeting, metadata or history sent to new client");
        await using var sdk = new ScannerConnection();
        var delivered = new TaskCompletionSource<LabelData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jsonMessages = Channel.CreateUnbounded<string>();
        sdk.JsonReceived += json => jsonMessages.Writer.TryWrite(json);
        sdk.LabelReceived += data => delivered.TrySetResult(data);
        await sdk.ConnectAsync("ignored.invalid", host.Port, thisComputer: true, cancellationToken: timeout.Token);
        await Until(() => host.LabelClientCount == 2);
        Check(sdk.Connected, "This-computer option ignores the supplied IP and connects to loopback");
        Check(host.PublishLabel(data) == 2, "Broadcast to both clients");
        var packet = await message;
        Check(packet?.Type == WebSocketMessageType.Text, "Plain JSON text message");
        var parsed = PayloadValidator.ReadClientJson(Encoding.UTF8.GetString(packet!.Bytes));
        Check(parsed == data, "Raw websocket has exactly the requested data");
        Check(await delivered.Task.WaitAsync(timeout.Token) == data, "Shipping SDK reads new contract");
        Check(await jsonMessages.Reader.ReadAsync(timeout.Token) == Encoding.UTF8.GetString(packet.Bytes), "JSON event exposes the exact server message");
        await sdk.CloseAsync();
        await Until(() => host.LabelClientCount == 1);
        Check(!sdk.Connected, "Close releases this client's connection");
        await sdk.CloseAsync();
        await sdk.ConnectAsync("127.0.0.1", host.Port, thisComputer: false, cancellationToken: timeout.Token);
        await Until(() => host.LabelClientCount == 2);
        var precise = data with { Quantity = "12345678901234567890.0012300" };
        host.PublishLabel(precise);
        Check(await jsonMessages.Reader.ReadAsync(timeout.Token) == ProtocolJson.Serialize(precise), "Reconnect by IP preserves string quantity exactly");
        var invalidPortRejected = false;
        try { await sdk.ConnectAsync(null, 0, thisComputer: true, cancellationToken: timeout.Token); }
        catch (ArgumentException) { invalidPortRejected = true; }
        Check(invalidPortRejected && sdk.Connected, "Invalid port is rejected before replacing the working connection");
        host.PublishLabel(data with { Quantity = "1,2" });
        await Until(() => !sdk.Connected);
        Check(!jsonMessages.Reader.TryRead(out _), "Invalid server data is never raised as received JSON");
        await sdk.CloseAsync(); raw.Abort();
        await Until(() => host.LabelClientCount == 0);
        Check(host.LabelClientCount == 0, "Client count falls on disconnect");
        Check(host.PublishLabel(data) == 0, "No backlog when nobody is connected");
        using var noPreview = new ClientWebSocket();
        var rejected = false;
        try { await noPreview.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/preview"), timeout.Token); }
        catch (WebSocketException) { rejected = true; }
        Check(rejected, "No image endpoint on data-only server");
    }
}
