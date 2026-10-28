using System.Text.Json;
using Scanner.Contracts;

namespace Scanner.Server.Services;

public sealed record LabelFieldRule(string Field, string Rule);

/// <summary>Read-only reference entries for supported versions. Keep each entry aligned with PayloadValidator.</summary>
public sealed record LabelVersionReference(string Version, string Format, IReadOnlyList<LabelFieldRule> Rules,
    string ChecksumRule, string ExamplePayload, string ExampleJson, string JsonRule)
{
    public string Name => Version == "C" ? "Version C — Commands" : $"Version {Version}";

    // When implementing another label version, add its reference here alongside its validator support.
    public static IReadOnlyList<LabelVersionReference> All { get; } = [Version1(), Version2(), Version3(), VersionC(), VersionX()];

    private static LabelVersionReference Version1()
    {
        const string body = "1*OEPSA010123*0926-TRAY-Q5R2*0";
        var payload = body + "*" + PayloadValidator.Crc32(body);
        var example = PayloadValidator.Validate(payload);
        if (!example.Valid) throw new InvalidOperationException("The version 1 reference example no longer matches the label validator.");
        return new("1", "<label_version>*<OEPS_PN>*<Lot>*<quantity>*<checksum>",
        [
            new("label_version", "Must be exactly 1. The version is checked before the other fields; unsupported versions are rejected."),
            new("Structure", "Exactly five fields separated by a single *. Maximum 4,096 characters. The scanner does not trim whitespace or change letter case."),
            new("OEPS_PN", "OEPS followed by six digits, or by one uppercase letter (A–Z) and five or six digits.\nExamples: OEPSA12345, OEPSA010123, OEPS010123."),
            new("Lot", "MMYY, a hyphen, three or four uppercase letters (A–Z), a hyphen, then four uppercase letters or digits.\nMM (month): 00–12. YY (year): 00 or 21–99; years 01–20 are rejected.\nExamples: 0926-TRY-Q5R2, 0926-TRAY-Q5R2. Zero month and zero year are accepted."),
            new("quantity", "Digits with an optional decimal dot followed by digits. Zero is accepted.\nExamples: 0, 1, 12314232, 0.12312, 12312.02312.\nNo comma, sign, exponent or spaces. Leading zeros and trailing decimal zeros are preserved."),
            new("checksum", "Exactly eight uppercase hexadecimal characters (0–9 and A–F), matching the CRC-32/IEEE calculation below.")
        ],
        "Calculate CRC-32/IEEE (compatible with zlib.crc32) over the exact ASCII text of the first four fields, including their single * separators. Exclude the final * separator and checksum. Do not add a newline or normalize the text. Format the result as eight uppercase hexadecimal characters.",
        payload, JsonSerializer.Serialize(example.Data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
        "label_version is a JSON number. oeps_pn, lot and quantity are JSON strings. Quantity keeps its exact precision and formatting. The checksum is verified by the server and omitted from the JSON.");
    }

    private static LabelVersionReference Version2()
    {
        const string body = "2*857*S/2680*1923*WH/OUT/00567";
        var payload = body + "*" + PayloadValidator.Crc32(body);
        var example = PayloadValidator.Validate(payload);
        if (!example.Valid) throw new InvalidOperationException("The version 2 reference example no longer matches the label validator.");
        return new("2", "<label_version>*<sales_order_id>*<document_name>*<out_id>*<out_number>*<checksum>",
        [
            new("label_version", "Must be exactly 2. The version is checked before the other fields; unsupported versions are rejected."),
            new("Structure", "Exactly six fields separated by a single *. Maximum 4,096 characters. The scanner does not trim whitespace or change letter case."),
            new("sales_order_id", "Internal Odoo sale.order ID: digits only, value from 1 to 2147483647. Example: 857. This is not the printed order number."),
            new("document_name", "Printed sales order document_name, for example S/2680. Do not use name (for example S23661). Nonblank printable ASCII text; * and control characters are not allowed. Slashes and spaces are preserved; no fixed prefix is required."),
            new("out_id", "Internal Odoo stock.picking ID: digits only, value from 1 to 2147483647. Example: 1923."),
            new("out_number", "Printed delivery number, for example WH/OUT/00567. Same text rules as document_name; no fixed warehouse prefix is required."),
            new("checksum", "Exactly eight uppercase hexadecimal characters (0–9 and A–F), matching the CRC-32/IEEE calculation below.")
        ],
        "Calculate CRC-32/IEEE (compatible with zlib.crc32) over the exact ASCII text of the first five fields, including their single * separators. Exclude the final * separator and checksum. Do not add a newline or normalize the text. Format the result as eight uppercase hexadecimal characters.",
        payload, JsonSerializer.Serialize(example.Data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
        "label_version, sales_order_id and out_id are JSON numbers. document_name and out_number are strings preserved exactly as printed. Only these five fields are sent; checksum and version 1 fields are omitted. Client applications can use the IDs with their configured Odoo address to open the records. The scanner does not query Odoo or verify that the IDs exist or match the printed numbers.");
    }

    private static LabelVersionReference Version3()
    {
        const string raw = "{pbn:PICK2610150002,on:WM2610150052,pc:C38433,pm:INA826AIDR,qty:100,mc:OEPS080248 STIMJIM FILIPE,cc:1,pdi:242369319,hp:21,wc:ZH}";
        return new("3", "Recognized supplier barcode; no 3* prefix required",
        [
            new("Routing", "Valid OEPS version 1/2/C labels take priority. Worldway and the supplied Farnell/Mouser/DigiKey formats become version 3."),
            new("Fields", "Worldway: mc → OEPS PN, pm → MPN, qty → quantity. ISO 15434 format 06: P → OEPS PN, 1P → MPN, Q → quantity. P, 1P, 3P and 30P are distinct identifiers."),
            new("OEPS PN", "A whole whitespace-delimited token matching OEPS plus six digits, or one uppercase letter plus five or six digits. Missing, invalid or ambiguous matches become null; never use a supplier part number as OEPS PN."),
            new("MPN", "Extracted from pm or 1P, with punctuation such as commas preserved. Missing/blank values become null."),
            new("Required data", "Valid quantity AND (valid OEPS PN OR nonempty MPN). Either identifier may be null, but both cannot be missing."),
            new("Quantity", "Required digits with an optional decimal dot. Kept as a string. Missing/invalid quantity, missing both identifiers, malformed records or duplicate relevant fields cause raw X fallback with the entire original barcode text."),
            new("Separators", "Actual ASCII RS, GS and EOT are recognized. Literal <rs>, <gs>, <eot> notation is also accepted for testing. DigiKey examples without a closing trailer are supported.")
        ], "No OEPS checksum is required or generated for supplier labels.", raw,
        JsonSerializer.Serialize(LabelClassifier.Classify(raw).Data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
        "Exactly label_version (number 3), oeps_pn (string or null), mpn (string or null), quantity (string). At least one identifier must be present. No raw text, supplier metadata or checksum is added.");
    }

    private static LabelVersionReference VersionC()
    {
        const string body = "C*For Odoo OUT";
        var payload = body + "*" + PayloadValidator.Crc32(body);
        var example = PayloadValidator.Validate(payload);
        if (!example.Valid) throw new InvalidOperationException("The version C reference example no longer matches the label validator.");
        return new("C", "C*<command>*<checksum>",
        [
            new("label_version", "Must be exactly uppercase C. Version C carries a command for the receiving application."),
            new("Structure", "Exactly three fields separated by a single *. Maximum 4,096 characters including the version, separators and checksum."),
            new("command", "Nonblank printable ASCII text without * or control characters. Maximum 4,085 characters. Spaces and letter case are preserved exactly; no trimming or normalization."),
            new("Examples", "Add Enter and left; Add Right; For Odoo OUT; For Odoo IN; Disable. These are examples, not a fixed allowed list. New command names work without updating the scanner."),
            new("Handling", "The scanner validates and forwards the command. The client decides which commands to recognize and what they do. Invalid structure, text or checksum falls back to raw X."),
            new("checksum", "Exactly eight uppercase hexadecimal characters (0–9 and A–F), matching CRC-32/IEEE.")
        ],
        "Calculate CRC-32/IEEE (compatible with zlib.crc32) over the exact ASCII text C*<command>, including C and the first single * separator. Exclude the final * and checksum. Do not add a newline or normalize the text. Format as eight uppercase hexadecimal characters.",
        payload, JsonSerializer.Serialize(example.Data, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
        "Exactly label_version (the string C) and command (string). The checksum is verified by the server and omitted from JSON. The scanner and test client display the command; the consuming application implements its behavior.");
    }

    private static LabelVersionReference VersionX()
    {
        const string raw = "OEPS-1234\tsecond field\r\nnext line";
        return new("X", "Any decoded text that fails versions 1, 2, 3 and C; no X* prefix required",
        [
            new("Routing", "Fallback whenever versions 1, 2, 3 and C do not validate. This includes invalid command labels, bad OEPS checksums and supplier labels with invalid/missing quantity, missing identifiers, malformed structure or duplicate fields. No decoded code is discarded because it fails validation."),
            new("Raw text", "No content validation, trimming, case conversion or separator replacement. Preserve tabs, CR/LF, RS/GS/EOT, Unicode, asterisks and empty text. Only the original decoded string is sent."),
            new("JSON", "Escapes such as \\t, \\r, \\n and \\u001d are JSON encoding; parsing restores the actual characters. Clients decide whether to use X messages.")
        ], "No checksum is required or generated for X.", raw,
        JsonSerializer.Serialize(LabelData.FromRaw(raw), new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
        "Exactly label_version (the string X) and raw (string). No additional fields. Camera: 500 ms per-payload duplicate filter. Barcode reader: every scan is sent without suppression.");
    }
}
