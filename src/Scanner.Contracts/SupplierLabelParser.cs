using System.Text.RegularExpressions;

namespace Scanner.Contracts;

internal sealed record SupplierParseResult(LabelData? Data, string Reason);

/// <summary>Supplier formats are separate parsers. Add another parser here without changing OEPS or raw routing.</summary>
internal static partial class SupplierLabelParser
{
    private static readonly Func<string, SupplierParseResult?>[] Parsers = [ParseWorldway, ParseIso15434];
    public static SupplierParseResult? Parse(string raw)
    {
        foreach (var parse in Parsers) if (parse(raw) is { } result) return result;
        return null;
    }

    // Split on field boundaries, not every comma: an MPN may itself contain a comma.
    [GeneratedRegex(@",\s*(?=[A-Za-z][A-Za-z0-9_]*\s*:)", RegexOptions.CultureInvariant)]
    private static partial Regex WorldwaySeparator();
    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorldwayKey();
    [GeneratedRegex(@"\A([0-9]*[A-Z])(.*)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex DataIdentifier();

    private static SupplierParseResult? ParseWorldway(string raw)
    {
        if (!raw.StartsWith('{') || !raw.EndsWith('}')) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var malformed = false;
        foreach (var part in WorldwaySeparator().Split(raw[1..^1]))
        {
            var colon = part.IndexOf(':');
            if (colon < 1) { malformed = true; continue; }
            var key = part[..colon].Trim();
            if (!WorldwayKey().IsMatch(key) || !fields.TryAdd(key, part[(colon + 1)..].Trim())) malformed = true;
        }
        // Recognize the supplied Worldway envelope even when optional customer/MPN fields are absent.
        if (!fields.ContainsKey("pbn") || !fields.ContainsKey("on") || !fields.ContainsKey("pc")) return null;
        if (malformed) return new(null, "Raw fallback: malformed or duplicate Worldway fields");
        return Extract(fields.GetValueOrDefault("mc"), fields.GetValueOrDefault("pm"), fields.GetValueOrDefault("qty"), "Worldway");
    }

    private static SupplierParseResult? ParseIso15434(string raw)
    {
        // Accept documentation-style separators as well as actual ASCII RS/GS/EOT.
        // Normalize only this local parsing copy; X always carries the untouched input.
        var text = raw.Replace("<rs>", "\x1e", StringComparison.OrdinalIgnoreCase)
            .Replace("<gs>", "\x1d", StringComparison.OrdinalIgnoreCase)
            .Replace("<eot>", "\x04", StringComparison.OrdinalIgnoreCase);
        const string header = "[)>\x1e" + "06\x1d";
        if (!text.StartsWith(header, StringComparison.Ordinal)) return null;
        var body = text[header.Length..];
        if (body.EndsWith("\x1e\x04", StringComparison.Ordinal)) body = body[..^2];
        // Some supplied DigiKey examples have no trailer. Never guess past an embedded frame boundary.
        if (body.IndexOfAny(['\x1e', '\x04']) >= 0) return new(null, "Raw fallback: malformed supplier trailer or multiple records");
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in body.Split('\x1d'))
        {
            var match = DataIdentifier().Match(field);
            if (!match.Success) return new(null, "Raw fallback: malformed supplier data identifier");
            var key = match.Groups[1].Value;
            if (key is not ("P" or "1P" or "Q")) continue;
            if (!selected.TryAdd(key, match.Groups[2].Value)) return new(null, "Raw fallback: duplicate supplier " + key + " fields");
        }
        return Extract(selected.GetValueOrDefault("P"), selected.GetValueOrDefault("1P"), selected.GetValueOrDefault("Q"), "Supplier ISO 15434 (Farnell/Mouser/DigiKey)");
    }

    private static SupplierParseResult Extract(string? customerPart, string? mpn, string? quantity, string source)
    {
        quantity = quantity?.Trim();
        if (quantity is null || !PayloadValidator.IsValidQuantity(quantity)) return new(null, $"Raw fallback: {source} quantity is missing or invalid");
        // Inspect whole whitespace-delimited tokens only; never truncate an invalid PN into a valid one.
        var matches = (customerPart ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(PayloadValidator.IsValidOepsPn).Distinct(StringComparer.Ordinal).ToArray();
        var pn = matches.Length == 1 ? matches[0] : null;
        mpn = string.IsNullOrWhiteSpace(mpn) ? null : mpn.Trim();
        if (pn is null && mpn is null) return new(null, $"Raw fallback: {source} requires a valid OEPS PN or a nonempty MPN");
        return new(LabelData.FromSupplier(pn, mpn, quantity), $"{source}: extracted quantity; OEPS PN {(pn is null ? "unavailable or invalid" : "validated")}; MPN {(mpn is null ? "unavailable" : "extracted")}");
    }
}
