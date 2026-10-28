using System.Text.Json;
using Scanner.Contracts;
using Scanner.Core;

internal static class CommandChecks
{
    public const string Example = "C*For Odoo OUT*14C79D4D";
    private static string Encode(string body) => body + "*" + PayloadValidator.Crc32(body);

    public static void Run(Action<bool, string> check)
    {
        // CRC values independently calculated with Python zlib, as used by label printers.
        foreach (var (command, checksum) in new[] {
            ("Add Enter and left", "775087CB"), ("Add Right", "2A6624D3"),
            ("For Odoo OUT", "14C79D4D"), ("For Odoo IN", "15941C04"), ("Disable", "7434FE41") })
        {
            var payload = $"C*{command}*{checksum}";
            var result = PayloadValidator.Validate(payload);
            var data = LabelData.FromCommand(command);
            check(result.Data == data && result.ExpectedChecksum == checksum, "Version C printer checksum vector");
            check(data.IsCommand && !data.IsRaw && data.VersionText == "C" && data.LabelVersion == LabelData.CommandVersion,
                "Command API distinguishes C from raw X and numeric versions");
            check(LabelClassifier.Classify(payload).Data == data, "Valid command routes to C without an X copy");
            var json = ProtocolJson.Serialize(data);
            check(json == $"{{\"label_version\":\"C\",\"command\":\"{command}\"}}", "Command JSON has exactly two string fields and no checksum");
            check(PayloadValidator.ReadClientJson(json) == data && ProtocolJson.Deserialize<LabelData>(json) == data,
                "Command JSON round trip through SDK and DTO converter");
        }
        foreach (var command in new[] { "Future command", " disable ", "Add  Right", "<&>\"'\\", new string('A', 4085) })
        {
            var data = LabelData.FromCommand(command);
            check(LabelClassifier.Classify(Encode("C*" + command)).Data == data,
                "New names, case, repeated/edge spaces, punctuation and maximum-length commands are preserved");
            check(PayloadValidator.ReadClientJson(ProtocolJson.Serialize(data)) == data, "Escaped and maximum-length command JSON preserves text");
        }
        foreach (var raw in new[] {
            Encode("C*"), Encode("C*   "), Encode("C*For\tOdoo"), Encode("C*Disable\r\n"), Encode("C*\0"), Encode("C*\x7f"),
            "C*Caf\u00e9*00000000", "C*\ud83d\ude00*00000000", Encode("C*" + new string('A', 4086)),
            "C", "C*Disable", Encode("C*Disable*extra"), Example.Replace("*", "**"),
            Example.Replace("OUT", "IN"), Example[..^1] + "0", Example[..^8] + "14c79d4d", Example + "\n",
            Example[..^8] + "XYZ79D4D", Example[..^1], Example + "0", Example.Replace("C*", "c*"),
            Example.Replace("C*", " C*"), Example.Replace("C*", "C *"), Example.Replace("C*", "03*") })
        {
            check(!PayloadValidator.Validate(raw).Valid, "Invalid version, shape, text, length or checksum rejects structured command");
            check(LabelClassifier.Classify(raw).Data == LabelData.FromRaw(raw), "Rejected command falls back to unchanged X");
        }
        var expectedJson = "{\"label_version\":\"C\",\"command\":\"For Odoo OUT\"}";
        foreach (var json in new[] {
            "{\"label_version\":\"C\"}", "{\"label_version\":\"C\",\"raw\":\"Disable\"}",
            expectedJson.Replace("\"C\"", "-1"), expectedJson.Replace("\"C\"", "\"c\""),
            expectedJson.Replace("\"C\"", "\"X\""), expectedJson.Replace("\"C\"", "1"),
            expectedJson.Replace("\"For Odoo OUT\"", "null"), expectedJson.Replace("\"For Odoo OUT\"", "123"),
            expectedJson.Replace("\"For Odoo OUT\"", "{}"), expectedJson.Replace("For Odoo OUT", ""),
            expectedJson.Replace("For Odoo OUT", "   "), expectedJson.Replace("For Odoo OUT", "Add*Right"),
            expectedJson.Replace("For Odoo OUT", "Add\\tRight"), expectedJson.Replace("For Odoo OUT", "Caf\\u00e9"),
            expectedJson.Replace("For Odoo OUT", new string('A', 4086)),
            expectedJson.Replace("}", ",\"checksum\":\"14C79D4D\"}"),
            expectedJson.Replace("}", ",\"label_version\":\"C\"}"), expectedJson.Replace("}", ",\"command\":\"Disable\"}") })
        {
            var rejected = false;
            try { PayloadValidator.ReadClientJson(json); } catch (JsonException) { rejected = true; }
            check(rejected, "SDK rejects malformed command envelope, duplicate properties, invalid types and content");
        }
        var gate = new DuplicateGate();
        gate.MarkSent(Example, 100);
        check(!gate.CanSend(Example, 599) && gate.CanSend(Example, 600), "Command camera cooldown is exactly 500 ms");
        check(gate.CanSend("C*Disable*7434FE41", 101), "Different command is immediately eligible");
    }
}
