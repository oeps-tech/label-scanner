using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scanner.Contracts;

public sealed record PayloadValidation(LabelData? Data, string Stage, string Reason, string? ExpectedChecksum = null)
{
    public bool Valid => Data is not null;
}

public static partial class PayloadValidator
{
    [GeneratedRegex(@"\AOEPS(?:[A-Z][0-9]{5,6}|[0-9]{6})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pn();
    [GeneratedRegex(@"\A[0-9]{4}-[A-Z]{3,4}-[A-Z0-9]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Lot();
    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Quantity();
    [GeneratedRegex(@"\A[0-9A-F]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Checksum();

    public static bool IsValidOepsPn(string value) => Pn().IsMatch(value);
    public static bool IsValidQuantity(string value) => Quantity().IsMatch(value);

    private static bool TryRecordId(string text, out int id)
    {
        id = 0;
        return text.Length > 0 && text.All(c => c is >= '0' and <= '9')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
    // Odoo numbering sequences are configurable. Do not require a particular prefix.
    private static bool IsDocumentNumber(string text) => text.Length > 0
        && text.Any(c => c != ' ') && text.All(c => c is >= ' ' and <= '~' && c != '*');
    // Commands are extensible client-defined names, not a fixed list or executable scanner instructions.
    // Leave room for "C*", the final separator and the eight checksum characters in the payload limit.
    private static bool IsCommandText(string text) => text.Length <= 4085 && IsDocumentNumber(text);

    private static string? LotError(string lot)
    {
        if (!Lot().IsMatch(lot)) return "Expected MMYY-YYY-HHHH or MMYY-YYYY-HHHH; H is an uppercase letter or digit";
        var month = (lot[0] - '0') * 10 + lot[1] - '0';
        var year = (lot[2] - '0') * 10 + lot[3] - '0';
        if (month > 12) return "Lot month (MM) must be 00–12";
        if (year is > 0 and <= 20) return "Lot year (YY) must be 00 or 21–99";
        return null;
    }

    public static string Crc32(string payload)
    {
        if (payload.Any(c => c > 127)) throw new ArgumentException("CRC input must be ASCII");
        uint crc = 0xffffffff;
        foreach (var b in Encoding.ASCII.GetBytes(payload))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        return (crc ^ 0xffffffff).ToString("X8");
    }
    public static PayloadValidation Validate(string? raw)
    {
        if (raw is null || raw.Length > 4096) return new(null, "payload", "Missing or oversized payload");
        var separator = raw.IndexOf('*');
        var version = separator < 0 ? raw : raw[..separator];
        if (version is not ("1" or "2" or "C")) return new(null, "version", "Unsupported label version; expected 1, 2 or C");
        var fields = raw.Split('*', StringSplitOptions.None);
        LabelData data;
        if (version == "1")
        {
            if (fields.Length != 5) return new(null, "fields", "Version 1 expects five fields separated by a single *");
            if (!Pn().IsMatch(fields[1])) return new(null, "oeps_pn", "Invalid OEPS part number");
            if (LotError(fields[2]) is { } lotError) return new(null, "lot", lotError);
            if (!Quantity().IsMatch(fields[3])) return new(null, "quantity", "Expected digits with an optional decimal dot; no comma, sign or exponent");
            data = new(1, fields[1], fields[2], fields[3]);
        }
        else if (version == "2")
        {
            if (fields.Length != 6) return new(null, "fields", "Version 2 expects six fields separated by a single *");
            if (!TryRecordId(fields[1], out var salesOrderId)) return new(null, "sales_order_id", "Expected an integer record ID from 1 to 2147483647 (digits only)");
            if (!IsDocumentNumber(fields[2])) return new(null, "document_name", "Expected a nonblank printable ASCII document_name without *; use the printed sales order number, not name");
            if (!TryRecordId(fields[3], out var outId)) return new(null, "out_id", "Expected an integer record ID from 1 to 2147483647 (digits only)");
            if (!IsDocumentNumber(fields[4])) return new(null, "out_number", "Expected a nonblank printable ASCII delivery number without *");
            data = new(2, salesOrderId, fields[2], outId, fields[4]);
        }
        else
        {
            if (fields.Length != 3) return new(null, "fields", "Version C expects three fields separated by a single *");
            if (!IsCommandText(fields[1])) return new(null, "command", "Expected nonblank printable ASCII command text without *; maximum 4,085 characters");
            data = LabelData.FromCommand(fields[1]);
        }
        if (!Checksum().IsMatch(fields[^1])) return new(null, "checksum", "Expected eight uppercase hexadecimal checksum characters");
        var expected = Crc32(raw[..raw.LastIndexOf('*')]);
        if (fields[^1] != expected) return new(null, "checksum", "CRC-32 mismatch", expected);
        return new(data, "valid", "Version, fields and CRC-32 passed", expected);
    }
    public static LabelData ReadClientJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object");
        if (!root.TryGetProperty("label_version", out var versionElement)) throw new JsonException("Missing label version");
        var names = root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        if (versionElement.ValueKind == JsonValueKind.String && versionElement.GetString() == "C")
        {
            if (!names.SequenceEqual(new[] { "command", "label_version" }) || root.GetProperty("command").ValueKind != JsonValueKind.String)
                throw new JsonException("Version C requires exactly label_version and command text");
            var command = root.GetProperty("command").GetString()!;
            if (!IsCommandText(command)) throw new JsonException("Invalid command text");
            return LabelData.FromCommand(command);
        }
        if (versionElement.ValueKind == JsonValueKind.String && versionElement.GetString() == "X")
        {
            if (!names.SequenceEqual(new[] { "label_version", "raw" }) || root.GetProperty("raw").ValueKind != JsonValueKind.String)
                throw new JsonException("Version X requires exactly label_version and raw text");
            return LabelData.FromRaw(root.GetProperty("raw").GetString()!);
        }
        if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version) || version is not (1 or 2 or 3))
            throw new JsonException("Unsupported or invalid label version");
        if (version == 3)
        {
            if (!names.SequenceEqual(new[] { "label_version", "mpn", "oeps_pn", "quantity" })) throw new JsonException("Expected exactly four fields for version 3");
            var pnField = root.GetProperty("oeps_pn");
            var mpnField = root.GetProperty("mpn");
            var quantityField = root.GetProperty("quantity");
            if (pnField.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || mpnField.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || quantityField.ValueKind != JsonValueKind.String)
                throw new JsonException("Invalid supplier field types");
            var pnValue = pnField.GetString(); var mpnValue = mpnField.GetString(); var quantityValue = quantityField.GetString()!;
            if ((pnValue is null && mpnValue is null) || (pnValue is not null && !IsValidOepsPn(pnValue)) || (mpnValue is not null && string.IsNullOrWhiteSpace(mpnValue))
                || !IsValidQuantity(quantityValue)) throw new JsonException("Invalid supplier fields");
            return LabelData.FromSupplier(pnValue, mpnValue, quantityValue);
        }
        if (version == 2)
        {
            if (!names.SequenceEqual(new[] { "document_name", "label_version", "out_id", "out_number", "sales_order_id" })) throw new JsonException("Expected exactly five fields for version 2");
            if (root.GetProperty("sales_order_id").ValueKind != JsonValueKind.Number || !root.GetProperty("sales_order_id").TryGetInt32(out var salesOrderId) || salesOrderId <= 0
                || root.GetProperty("out_id").ValueKind != JsonValueKind.Number || !root.GetProperty("out_id").TryGetInt32(out var outId) || outId <= 0
                || root.GetProperty("document_name").ValueKind != JsonValueKind.String || root.GetProperty("out_number").ValueKind != JsonValueKind.String) throw new JsonException("Invalid version 2 data types or record IDs");
            var documentName = root.GetProperty("document_name").GetString()!;
            var outNumber = root.GetProperty("out_number").GetString()!;
            if (!IsDocumentNumber(documentName) || !IsDocumentNumber(outNumber)) throw new JsonException("Invalid document numbers");
            return new(2, salesOrderId, documentName, outId, outNumber);
        }
        if (!names.SequenceEqual(new[] { "label_version", "lot", "oeps_pn", "quantity" })) throw new JsonException("Expected exactly four data fields");
        if (root.GetProperty("oeps_pn").ValueKind != JsonValueKind.String || root.GetProperty("lot").ValueKind != JsonValueKind.String
            || root.GetProperty("quantity").ValueKind != JsonValueKind.String) throw new JsonException("Invalid data types or label version");
        var pn = root.GetProperty("oeps_pn").GetString()!;
        var lot = root.GetProperty("lot").GetString()!;
        var quantity = root.GetProperty("quantity").GetString()!;
        if (!Pn().IsMatch(pn) || LotError(lot) is not null || !Quantity().IsMatch(quantity)) throw new JsonException("Invalid data fields");
        return new(version, pn, lot, quantity);
    }
}
