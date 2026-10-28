using System.Text.Json;
using Scanner.Contracts;
using Scanner.Core;

internal static class Version2Checks
{
    public const string Example = "2*857*S/2680*1923*WH/OUT/00567*6B6312A1";
    public static readonly LabelData Data = new(2, 857, "S/2680", 1923, "WH/OUT/00567");
    private static string Encode(string body) => body + "*" + PayloadValidator.Crc32(body);

    public static void Run(Action<bool, string> check)
    {
        check(PayloadValidator.Validate(Example).Data == Data, "Version 2 printer vector agrees with independent Python zlib CRC");
        var json = ProtocolJson.Serialize(Data);
        check(json == "{\"label_version\":2,\"sales_order_id\":857,\"document_name\":\"S/2680\",\"out_id\":1923,\"out_number\":\"WH/OUT/00567\"}", "Version 2 sends exactly five fields with numeric IDs and document_name, no name/CRC/version 1 fields");
        check(PayloadValidator.ReadClientJson(json) == Data && ProtocolJson.Deserialize<LabelData>(json) == Data, "Both version 2 DTO and validated SDK round trips work");
        check(PayloadValidator.Validate(Encode("2*1* S/2680 *2147483647*Other/OUT-001")).Data
            == new LabelData(2, 1, " S/2680 ", int.MaxValue, "Other/OUT-001"), "Record ID boundaries and configurable numbers preserved without trimming");
        check(PayloadValidator.Validate(Encode("2*000857*S/2680*001923*WH/OUT/00567")).Data == Data, "CRC uses original ID digits; JSON carries integer IDs");
        foreach (var id in new[] { "0", "-1", "+1", "1.0", "1e3", " 1", "1 ", "", "2147483648", "9999999999999999999999999999" })
        {
            check(PayloadValidator.Validate(Encode($"2*{id}*S/2680*1923*WH/OUT/00567")).Stage == "sales_order_id", "Invalid sales order ID rejected even with valid CRC");
            check(PayloadValidator.Validate(Encode($"2*857*S/2680*{id}*WH/OUT/00567")).Stage == "out_id", "Invalid OUT ID rejected even with valid CRC");
        }
        foreach (var number in new[] { "", "   ", "S/2680\n", "WH\tOUT", "\0" })
        {
            check(PayloadValidator.Validate(Encode($"2*857*{number}*1923*WH/OUT/00567")).Stage == "document_name", "Invalid document_name rejected");
            check(PayloadValidator.Validate(Encode($"2*857*S/2680*1923*{number}")).Stage == "out_number", "Invalid OUT number rejected");
        }
        check(PayloadValidator.Validate("2*857*S/é*1923*WH/OUT/00567*00000000").Stage == "document_name", "Non-ASCII numbers rejected without CRC throwing");
        foreach (var raw in new[] { "2*857*S/2680*1923", Example + "*extra", Example.Replace("*", "**"), Encode("2*857*S*2680*1923*WH/OUT/00567") })
            check(PayloadValidator.Validate(raw).Stage == "fields", "Version 2 requires six single-separated fields");
        foreach (var raw in new[] { Example.Replace("857", "858"), Example.Replace("S/2680", "S/2681"), Example.Replace("1923", "1924"), Example.Replace("00567", "00568"), Example.ToLowerInvariant(), Example + "\n" })
            check(PayloadValidator.Validate(raw).Stage == "checksum", "Every version 2 field is covered by CRC; strict checksum shape");
        check(PayloadValidator.Validate("02*857*S/2680*1923*WH/OUT/00567*00000000").Stage == "version", "Version must be literal 2");
        check(PayloadValidator.Validate(Encode("1*857*S/2680*1923*WH/OUT/00567")).Stage == "fields", "Version 2 data cannot masquerade as version 1");
        check(PayloadValidator.Validate(Encode("2*OEPSA010123*0926-TRAY-Q5R2*0")).Stage == "fields", "Version 1 data cannot masquerade as version 2");
        check(PayloadValidator.Validate(Example + new string('A', 4096)).Stage == "payload", "Oversized version 2 payload rejected");
        foreach (var invalid in new[] {
            "[]", "{}", json.Replace("\"label_version\":2", "\"label_version\":3"),
            json.Replace("\"label_version\":2", "\"label_version\":\"2\""),
            json.Replace("\"sales_order_id\":857", "\"sales_order_id\":\"857\""),
            json.Replace("\"sales_order_id\":857", "\"sales_order_id\":null"),
            json.Replace("\"sales_order_id\":857", "\"sales_order_id\":0"),
            json.Replace("\"out_id\":1923", "\"out_id\":-1"),
            json.Replace("\"out_id\":1923", "\"out_id\":2147483648"),
            json.Replace("\"out_id\":1923", "\"out_id\":1.5"),
            json.Replace("\"document_name\"", "\"name\""), json.Replace("\"S/2680\"", "null"),
            json.Replace("\"S/2680\"", "\"\""), json.Replace("WH/OUT/00567", "WH*OUT*00567"),
            json.Replace("\"out_id\":1923,", ""), json.Replace("\"label_version\":2,", "\"label_version\":2,\"label_version\":2,"),
            json.Replace("}", ",\"quantity\":\"0\"}"), json.Replace("}", ",\"checksum\":\"6B6312A1\"}") })
        {
            var rejected = false;
            try { PayloadValidator.ReadClientJson(invalid); } catch (JsonException) { rejected = true; }
            check(rejected, "SDK rejects invalid version 2 JSON fields, types, duplicates and unsupported versions");
        }
        var gate = new DuplicateGate();
        var material = "1*OEPSA010123*0926-TRAY-Q5R2*0*AE1BA040";
        gate.MarkSent(material, 100);
        check(gate.CanSend(Example, 100), "Version 1 cooldown does not block version 2");
        gate.MarkSent(Example, 150);
        check(!gate.CanSend(material, 599) && !gate.CanSend(Example, 649), "Both versions suppressed for 499 ms");
        check(gate.CanSend(material, 600) && gate.CanSend(Example, 650), "Both versions repeat at exactly 500 ms");
        gate.MarkSent(Example, 650);
        check(!gate.CanSend(Example, 1149) && gate.CanSend(Example, 1150), "Further observations do not extend version 2 cooldown");
    }
}
