using System.Text.Json;
using Scanner.Contracts;

namespace Scanner.Server.Services;

public sealed record LabelFieldRule(string Field, string Rule);

/// <summary>Read-only reference entries for supported versions. Keep each entry aligned with PayloadValidator.</summary>
public sealed record LabelVersionReference(int Version, string Format, IReadOnlyList<LabelFieldRule> Rules,
    string ChecksumRule, string ExamplePayload, string ExampleJson)
{
    public string Name => $"Version {Version}";

    // When implementing another label version, add its reference here alongside its validator support.
    public static IReadOnlyList<LabelVersionReference> All { get; } = [Version1()];

    private static LabelVersionReference Version1()
    {
        const string body = "1*OEPSA010123*0926-TRAY-Q5R2*0";
        var payload = body + "*" + PayloadValidator.Crc32(body);
        var example = PayloadValidator.Validate(payload);
        if (!example.Valid) throw new InvalidOperationException("The version 1 reference example no longer matches the label validator.");
        return new(1, "<label_version>*<OEPS_PN>*<Lot>*<quantity>*<checksum>",
        [
            new("label_version", "Must be exactly 1. The version is checked before the other fields; unsupported versions are rejected."),
            new("Structure", "Exactly five fields separated by a single *. Maximum 4,096 characters. The scanner does not trim whitespace or change letter case."),
            new("OEPS_PN", "OEPSynnnnn, OEPSynnnnnn or OEPSnnnnnn.\nExamples: OEPSA12345, OEPSA010123, OEPS010123."),
            new("Lot", "MMYY-yyy-hhhh or MMYY-yyyy-hhhh.\nMM (month): 00–12. YY (year): 00 or 21–99; years 01–20 are rejected.\nExamples: 0926-TRY-Q5R2, 0926-TRAY-Q5R2. Zero month and zero year are accepted."),
            new("quantity", "Digits with an optional decimal dot followed by digits. Zero is accepted.\nExamples: 0, 1, 12314232, 0.12312, 12312.02312.\nNo comma, sign, exponent or spaces. Leading zeros and trailing decimal zeros are preserved."),
            new("checksum", "Exactly eight uppercase hexadecimal characters (0–9 and A–F), matching the CRC-32/IEEE calculation below.")
        ],
        "Calculate CRC-32/IEEE (compatible with zlib.crc32) over the exact ASCII text of the first four fields, including their single * separators. Exclude the final * separator and checksum. Do not add a newline or normalize the text. Format the result as eight uppercase hexadecimal characters.",
        payload, JsonSerializer.Serialize(example.Data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }));
    }
}
