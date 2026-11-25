using System.Text.Json;
using Scanner.Contracts;
using Scanner.Core;

internal static class OdooDocumentChecks
{
    // Independently calculated with Python zlib.crc32, matching the label-printer algorithm.
    public const string SalesOrderExample = "4*857*S/2680*71878073";
    public const string OutExample = "5*1923*WH/OUT/00567*D3BF0810";
    private static string Encode(string body) => body + "*" + PayloadValidator.Crc32(body);
    private static LabelData Data(int version, int id, string number) => version == 4
        ? LabelData.FromSalesOrder(id, number) : LabelData.FromOut(id, number);

    public static void Run(Action<bool, string> check)
    {
        foreach (var (version, example, id, number, idField, numberField) in new[] {
            (4, SalesOrderExample, 857, "S/2680", "sales_order_id", "document_name"),
            (5, OutExample, 1923, "WH/OUT/00567", "out_id", "out_number") })
        {
            var expected = Data(version, id, number);
            check(PayloadValidator.Validate(example).Data == expected, "Single-document printer vector has independent matching CRC");
            check(LabelClassifier.Classify(example).Data == expected, "Valid Odoo document routes to its specific version");
            var json = ProtocolJson.Serialize(expected);
            check(json == $"{{\"label_version\":{version},\"{idField}\":{id},\"{numberField}\":\"{number}\"}}",
                "Single-document JSON has exactly its version, numeric ID and printed number, without unrelated fields or checksum");
            check(PayloadValidator.ReadClientJson(json) == expected && ProtocolJson.Deserialize<LabelData>(json) == expected,
                "Single-document client and DTO JSON round trips");
            check(PayloadValidator.Validate(Encode($"{version}*000{id}*{number}")).Data == expected,
                "Leading zero IDs are checked as printed and delivered as integers");
            foreach (var boundaryId in new[] { 1, int.MaxValue })
            foreach (var printed in new[] { number, " Custom/Document-1 ", "<&>\"'\\" })
            {
                var valid = Data(version, boundaryId, printed);
                check(PayloadValidator.Validate(Encode($"{version}*{boundaryId}*{printed}")).Data == valid
                    && PayloadValidator.ReadClientJson(ProtocolJson.Serialize(valid)) == valid,
                    "Same version 2 rules accept positive-ID boundaries, custom sequences, punctuation and exact surrounding spaces");
            }
            foreach (var badId in new[] { "", "0", "000", "-1", "+1", "1.0", "1e3", " 1", "1 ", "2147483648", "9999999999999999999999", "١" })
            {
                var body = $"{version}*{badId}*{number}";
                var raw = badId == "١" ? body + "*00000000" : Encode(body);
                check(PayloadValidator.Validate(raw).Stage == idField, "ID validation matches version 2 and reports the correct field");
                check(LabelClassifier.Classify(raw).Data == LabelData.FromRaw(raw), "Invalid document ID falls back to exact raw X");
            }
            foreach (var badNumber in new[] { "", "   ", "S/2680\n", "WH\tOUT", "\r", "\0", "\x7f", "Café" })
            {
                var body = $"{version}*{id}*{badNumber}";
                var raw = badNumber == "Café" ? body + "*00000000" : Encode(body);
                check(PayloadValidator.Validate(raw).Stage == numberField, "Printed number validation matches version 2 without throwing for non-ASCII input");
                check(LabelClassifier.Classify(raw).Data == LabelData.FromRaw(raw), "Invalid document number falls back to exact raw X");
            }
            foreach (var raw in new[] { version.ToString(), $"{version}*{id}*{number}", example + "*extra", example.Replace("*", "**"),
                Encode($"{version}*{id}*part*number"), Encode($"{version}*857*S/2680*1923*WH/OUT/00567"),
                example.Replace(id.ToString(), (id + 1).ToString()), example.Replace(number, number + "A"),
                example[..^8] + "FFFFFFFF", example[..^8] + "abcdef12", example[..^8] + "0123456G", example + "\n", example[..^1],
                "0" + example, " " + example, example.Replace(version + "*", (version == 4 ? "5*" : "4*")),
                Encode($"{version}*1*" + new string('A', 4084)) })
            {
                check(!PayloadValidator.Validate(raw).Valid, "Malformed structure, unsupported version spelling, corruption and oversized payloads reject structured data");
                check(LabelClassifier.Classify(raw).Data == LabelData.FromRaw(raw), "Every rejected single-document label is sent as original X");
            }
            var maximum = Encode($"{version}*1*" + new string('<', 4083));
            var maximumData = Data(version, 1, new string('<', 4083));
            check(maximum.Length == 4096 && PayloadValidator.Validate(maximum).Data == maximumData
                && PayloadValidator.ReadClientJson(ProtocolJson.Serialize(maximumData)) == maximumData,
                "Exactly 4096 printed characters are accepted, including JSON escaping expansion");

            foreach (var badJson in new[] { "{}", "[]", json.Replace($"\"label_version\":{version}", $"\"label_version\":\"{version}\""),
                json.Replace($"\"label_version\":{version}", $"\"label_version\":{(version == 4 ? 5 : 4)}"),
                json.Replace($"\"{idField}\":{id}", $"\"{idField}\":\"{id}\""),
                json.Replace($"\"{idField}\":{id}", $"\"{idField}\":null"),
                json.Replace($"\"{idField}\":{id}", $"\"{idField}\":0"),
                json.Replace($"\"{idField}\":{id}", $"\"{idField}\":-1"),
                json.Replace($"\"{idField}\":{id}", $"\"{idField}\":2147483648"),
                json.Replace($"\"{idField}\":{id}", $"\"{idField}\":1.5"),
                json.Replace($"\"{number}\"", "null"), json.Replace($"\"{number}\"", "123"),
                json.Replace($"\"{numberField}\"", "\"name\""), json.Replace(number, ""), json.Replace(number, "   "),
                json.Replace(number, "number*with*separator"), json.Replace(number, "number\\n"), json.Replace(number, "Caf\\u00e9"),
                json.Replace($",\"{idField}\":{id}", ""),
                json.Replace("}", $",\"label_version\":{version}}}"), json.Replace("}", $",\"{idField}\":{id}}}"),
                json.Replace("}", $",\"{numberField}\":\"other\"}}"), json.Replace("}", ",\"checksum\":\"00000000\"}"),
                json.Replace("}", ",\"quantity\":\"1\"}"),
                json.Replace("}", version == 4 ? ",\"out_id\":null}" : ",\"sales_order_id\":null}") })
            {
                var rejected = false;
                try { PayloadValidator.ReadClientJson(badJson); } catch (JsonException) { rejected = true; }
                check(rejected, "SDK rejects wrong field sets/types, missing/duplicate/extra fields, invalid IDs and document numbers");
            }
        }
        var gate = new DuplicateGate();
        gate.MarkSent(SalesOrderExample, 100);
        check(gate.CanSend(OutExample, 100), "Different document versions do not suppress each other");
        gate.MarkSent(OutExample, 150);
        check(!gate.CanSend(SalesOrderExample, 599) && gate.CanSend(SalesOrderExample, 600)
            && !gate.CanSend(OutExample, 649) && gate.CanSend(OutExample, 650), "Both document versions retain exact independent 500 ms camera boundaries");
    }
}
