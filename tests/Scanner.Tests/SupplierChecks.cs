using System.Text.Json;
using Scanner.Contracts;
using Scanner.Core;

internal static class SupplierChecks
{
    public const string RawText = "\t OEPS-1234\r\nnext\nline\r\0\x1d\x1e\x04 * <gs> café € 漢字 😀 ";
    public static string Controls(string value) => value.Replace("<rs>", "\x1e").Replace("<gs>", "\x1d").Replace("<eot>", "\x04");
    public static void Run(Action<bool, string> check)
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "supplier-labels.json")));
        foreach (var sample in fixtures.RootElement.EnumerateArray())
        {
            var expected = LabelData.FromSupplier(sample.GetProperty("oeps_pn").GetString(), sample.GetProperty("mpn").GetString(), sample.GetProperty("quantity").GetString()!);
            var original = sample.GetProperty("raw").GetString()!;
            foreach (var raw in new[] { original, Controls(original) })
            {
                var result = LabelClassifier.Classify(raw);
                check(result.Data == expected, sample.GetProperty("supplier").GetString() + " extracts the exact supplied values");
                var json = ProtocolJson.Serialize(result.Data);
                using var doc = JsonDocument.Parse(json);
                check(doc.RootElement.EnumerateObject().Count() == 4 && doc.RootElement.GetProperty("quantity").ValueKind == JsonValueKind.String, "Supplier JSON has exactly four fields and string quantity");
                check(doc.RootElement.GetProperty("oeps_pn").ValueKind == (expected.OepsPn is null ? JsonValueKind.Null : JsonValueKind.String)
                    && doc.RootElement.GetProperty("mpn").ValueKind == (expected.Mpn is null ? JsonValueKind.Null : JsonValueKind.String), "Missing supplier identifiers are explicit JSON nulls");
                check(PayloadValidator.ReadClientJson(json) == expected && ProtocolJson.Deserialize<LabelData>(json) == expected, "Supplier SDK and DTO round trip");
            }
            var quantity = sample.GetProperty("quantity").GetString()!;
            var missing = original.StartsWith('{') ? original.Replace("qty:" + quantity + ",", "") : original.Replace("<gs>Q" + quantity, "");
            check(LabelClassifier.Classify(missing).Data == LabelData.FromRaw(missing)
                && LabelClassifier.Classify(Controls(missing)).Data == LabelData.FromRaw(Controls(missing)), "Every supplier without quantity falls back to X with its exact original separators");
        }
        foreach (var pn in new[] { "OEPSA12345", "OEPSA010123", "OEPS010123" })
            check(LabelClassifier.Classify(Controls($"[)><rs>06<gs>P{pn} customer note<gs>Q0<rs><eot>")).Data == LabelData.FromSupplier(pn, null, "0"), "All existing OEPS PN forms and zero quantity accepted");
        foreach (var pn in new[] { "OEPSa010123", "OEPS01012", "OEPS0101234", "XOEPS010123", "OEPSA010123X", "1727-6369-6-ND", "OEPS010123 OEPS010124", "" })
            check(LabelClassifier.Classify(Controls($"[)><rs>06<gs>P{pn}<gs>1P74LVC4245AD,118<gs>Q200<rs><eot>")).Data == LabelData.FromSupplier(null, "74LVC4245AD,118", "200"), "Invalid or ambiguous OEPS PN becomes null without truncating or confusing it with MPN");
        check(LabelClassifier.Classify(Controls("[)><rs>06<gs>1POEPS010123<gs>30POEPS010124<gs>3POEPS010125<gs>Q000.1200<rs><eot>")).Data
            == LabelData.FromSupplier(null, "OEPS010123", "000.1200"), "Exact P/1P/30P/3P identifiers; quantity precision and formatting preserved");
        check(LabelClassifier.Classify("{pbn:pick,on:order,pc:stock,pm:74LVC4245AD,118,qty:10}").Data
            == LabelData.FromSupplier(null, "74LVC4245AD,118", "10"), "Worldway MPN comma is not treated as a field boundary");
        const string noIdentifiers = "{pbn:pick,on:order,pc:stock,qty:10}";
        check(LabelClassifier.Classify(noIdentifiers).Data == LabelData.FromRaw(noIdentifiers), "Valid quantity with both identifiers missing falls back to X");
        // Exercise the requested AND/OR rule for both supplier syntaxes, including blank MPNs and zero quantity.
        foreach (var pn in new[] { "OEPS010123", "OEPS0101234", "" })
        foreach (var mpn in new[] { "INA826AIDR", "", " \t " })
        foreach (var quantity in new[] { "0", "0.1200", "", "1,2" })
        {
            var expectedVersion3 = (quantity is "0" or "0.1200") && (pn == "OEPS010123" || mpn == "INA826AIDR");
            foreach (var original in new[] { $"{{pbn:pick,on:order,pc:stock,mc:{pn},pm:{mpn},qty:{quantity}}}",
                Controls($"[)><rs>06<gs>P{pn}<gs>1P{mpn}<gs>Q{quantity}<rs><eot>") })
            {
                var actual = LabelClassifier.Classify(original).Data;
                var expected = expectedVersion3
                    ? LabelData.FromSupplier(pn == "OEPS010123" ? pn : null, mpn == "INA826AIDR" ? mpn : null, quantity)
                    : LabelData.FromRaw(original);
                check(actual == expected, "Version 3 requires valid quantity AND (valid OEPS PN OR nonempty MPN); every failure yields original raw X");
                check(PayloadValidator.ReadClientJson(ProtocolJson.Serialize(actual)) == expected, "SDK agrees with routing for every version 3 AND/OR combination");
            }
        }
        foreach (var q in new[] { "", "-1", "1,2", "1e3", ".2", "1.", "ABC" })
        {
            var iso = Controls($"[)><rs>06<gs>POEPS010123<gs>Q{q}<rs><eot>");
            var worldway = $"{{pbn:pick,on:order,pc:stock,pm:INA826AIDR,qty:{q}}}";
            check(LabelClassifier.Classify(iso).Data == LabelData.FromRaw(iso), "Invalid ISO quantity falls back to X");
            check(LabelClassifier.Classify(worldway).Data == LabelData.FromRaw(worldway), "Invalid Worldway quantity falls back to X");
        }
        foreach (var raw in new[] { "[)><rs>06<gs>Q2<gs>Q3<rs><eot>", "[)><rs>06<gs>Q2<gs>POEPS010123<gs>POEPS010124<rs><eot>",
            "[)><rs>06<gs>Q2<rs><eot>garbage", "{pbn:pick,on:order,pc:stock,qty:1,qty:2}" })
            check(LabelClassifier.Classify(Controls(raw)).Data == LabelData.FromRaw(Controls(raw)), "Malformed or duplicate supplier fields fall back to untouched X");
        foreach (var raw in new[] { RawText, "", "X*already-prefixed*anything", "{qty:2,pm:random}", "1*bad", "3*unknown", new string('\x1d', 10000) })
        {
            var data = LabelClassifier.Classify(raw).Data!;
            check(data.IsRaw && data.Raw == raw && data.VersionText == "X", "Unrecognized data falls back to X byte-for-character without normalization");
            var json = ProtocolJson.Serialize(data);
            using var doc = JsonDocument.Parse(json);
            check(doc.RootElement.EnumerateObject().Count() == 2 && doc.RootElement.GetProperty("label_version").GetString() == "X", "Raw JSON contains only string X and raw");
            check(PayloadValidator.ReadClientJson(json) == data && ProtocolJson.Deserialize<LabelData>(json) == data, "All raw controls, whitespace, Unicode and long text survive JSON round trip");
        }
        check(LabelClassifier.Classify(Version2Checks.Example).Data == Version2Checks.Data, "Valid OEPS label takes priority, not X or supplier");
        var badCrc = Version2Checks.Example.Replace("6B6312A1", "6B6312A0");
        check(LabelClassifier.Classify(badCrc).Data?.Raw == badCrc && LabelClassifier.Classify(badCrc).ExpectedChecksum == "6B6312A1", "Invalid OEPS CRC is sent only as X with diagnostic expected CRC retained");
        foreach (var json in new[] { "{\"label_version\":\"X\",\"raw\":null}", "{\"label_version\":\"X\",\"raw\":5}",
            "{\"label_version\":0,\"raw\":\"text\"}", "{\"label_version\":\"X\",\"raw\":\"text\",\"checksum\":\"test\"}",
            "{\"label_version\":\"X\",\"raw\":\"a\",\"raw\":\"b\"}",
            "{\"label_version\":3,\"oeps_pn\":null,\"mpn\":null,\"quantity\":null}",
            "{\"label_version\":3,\"oeps_pn\":null,\"mpn\":null,\"quantity\":3}",
            "{\"label_version\":3,\"oeps_pn\":null,\"mpn\":null,\"quantity\":\"3\"}",
            "{\"label_version\":3,\"oeps_pn\":null,\"mpn\":\"   \",\"quantity\":\"3\"}",
            "{\"label_version\":3,\"oeps_pn\":\"OEPS0101234\",\"mpn\":null,\"quantity\":\"3\"}" })
        {
            var rejected = false;
            try { PayloadValidator.ReadClientJson(json); } catch (JsonException) { rejected = true; }
            check(rejected, "SDK still enforces JSON envelope and structured supplier types");
        }
        var gate = new DuplicateGate();
        const string supplier = "{pbn:pick,on:order,pc:stock,pm:INA826AIDR,qty:10}";
        gate.MarkSent(RawText, 0); gate.MarkSent(supplier, 10);
        check(!gate.CanSend(RawText, 499) && gate.CanSend(RawText, 500), "X respects exact 500 ms boundary");
        check(!gate.CanSend(supplier, 509) && gate.CanSend(supplier, 510), "Supplier respects its independent 500 ms boundary");
        check(gate.CanSend(RawText + "\n", 100), "Raw text differing only in newline is a distinct payload");
        gate.MarkSent(noIdentifiers, 20);
        check(!gate.CanSend(noIdentifiers, 519) && gate.CanSend(noIdentifiers, 520), "Failed version 3 still uses the exact same 500 ms filter as X");
    }
}
