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
        if (version != "1") return new(null, "version", "Unsupported label version; expected 1");
        var fields = raw.Split('*', StringSplitOptions.None);
        if (fields.Length != 5) return new(null, "fields", "Expected five fields separated by a single *");
        if (!Pn().IsMatch(fields[1])) return new(null, "oeps_pn", "Invalid OEPS part number");
        if (LotError(fields[2]) is { } lotError) return new(null, "lot", lotError);
        if (!Quantity().IsMatch(fields[3])) return new(null, "quantity", "Expected digits with an optional decimal dot; no comma, sign or exponent");
        if (!Checksum().IsMatch(fields[4])) return new(null, "checksum", "Expected eight uppercase hexadecimal checksum characters");
        var expected = Crc32(raw[..raw.LastIndexOf('*')]);
        if (fields[4] != expected) return new(null, "checksum", "CRC-32 mismatch", expected);
        return new(new(1, fields[1], fields[2], fields[3]), "valid", "Version, fields and CRC-32 passed", expected);
    }
    public static LabelData ReadClientJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object");
        var names = root.EnumerateObject().Select(p => p.Name).Order().ToArray();
        if (!names.SequenceEqual(new[] { "label_version", "lot", "oeps_pn", "quantity" })) throw new JsonException("Expected exactly four data fields");
        if (!root.GetProperty("label_version").TryGetInt32(out var version) || version != 1
            || root.GetProperty("oeps_pn").ValueKind != JsonValueKind.String || root.GetProperty("lot").ValueKind != JsonValueKind.String
            || root.GetProperty("quantity").ValueKind != JsonValueKind.String) throw new JsonException("Invalid data types or label version");
        var pn = root.GetProperty("oeps_pn").GetString()!;
        var lot = root.GetProperty("lot").GetString()!;
        var quantity = root.GetProperty("quantity").GetString()!;
        if (!Pn().IsMatch(pn) || LotError(lot) is not null || !Quantity().IsMatch(quantity)) throw new JsonException("Invalid data fields");
        return new(version, pn, lot, quantity);
    }
}
