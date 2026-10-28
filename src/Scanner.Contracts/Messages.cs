using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scanner.Contracts;

public static class ProtocolJson
{
    // Includes JSON escaping for raw text from large QR/Data Matrix symbols. Never truncate a reading.
    public const int MaximumLabelBytes = 128 * 1024;
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.Strict, MaxDepth = 12
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty JSON");
}

/// <summary>Versioned data. Use IsCommand, IsRaw or LabelVersion to select fields. Missing supplier fields are explicit JSON nulls.</summary>
/// <param name="LabelVersion">1 for materials, 2 for Odoo, 3 for suppliers; -1 represents commands C and 0 represents raw X in the C# API.</param>
/// <param name="OepsPn">Version 1 part number.</param>
/// <param name="Lot">Version 1 lot.</param>
/// <param name="Quantity">Version 1 exact quantity text, without conversion or rounding.</param>
[method: JsonConstructor]
[JsonConverter(typeof(LabelDataJsonConverter))]
public sealed record LabelData(int LabelVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OepsPn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Lot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quantity)
{
    /// <summary>C# value for the JSON string label_version "X". Existing numeric versions keep their integer API.</summary>
    public const int RawVersion = 0;
    /// <summary>C# value for the JSON string label_version "C". Never sent as a numeric version.</summary>
    public const int CommandVersion = -1;
    [JsonIgnore] public bool IsRaw => LabelVersion == RawVersion;
    [JsonIgnore] public bool IsCommand => LabelVersion == CommandVersion;
    [JsonIgnore] public string VersionText => LabelVersion switch
    {
        RawVersion => "X", CommandVersion => "C",
        _ => LabelVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
    /// <summary>Version C exact command text. The receiving application decides which commands to support and execute.</summary>
    public string? Command { get; init; }
    /// <summary>Version 3 supplier manufacturer part number; null when unavailable.</summary>
    public string? Mpn { get; init; }
    /// <summary>Version X exact decoded text, including control characters. No checksum or content validation.</summary>
    public string? Raw { get; init; }
    public static LabelData FromRaw(string raw) => new(RawVersion, null, null, null) { Raw = raw };
    public static LabelData FromCommand(string command) => new(CommandVersion, null, null, null) { Command = command };
    public static LabelData FromSupplier(string? oepsPn, string? mpn, string quantity) => new(3, oepsPn, null, quantity) { Mpn = mpn };
    /// <summary>Version 2 internal sale.order record ID.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SalesOrderId { get; init; }
    /// <summary>Version 2 printed sales order document_name, for example S/2680; not Odoo's name field.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentName { get; init; }
    /// <summary>Version 2 internal stock.picking record ID.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutId { get; init; }
    /// <summary>Version 2 printed delivery number, for example WH/OUT/00567.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutNumber { get; init; }

    /// <summary>Creates version 2 sales order/delivery data.</summary>
    public LabelData(int labelVersion, int salesOrderId, string documentName, int outId, string outNumber)
        : this(labelVersion, null, null, null)
        => (SalesOrderId, DocumentName, OutId, OutNumber) = (salesOrderId, documentName, outId, outNumber);
}

/// <summary>Writes only the fields belonging to the message version, including explicit missing supplier values.</summary>
public sealed class LabelDataJsonConverter : JsonConverter<LabelData>
{
    public override LabelData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return PayloadValidator.ReadClientJson(document.RootElement.GetRawText());
    }
    public override void Write(Utf8JsonWriter writer, LabelData value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.IsRaw)
        {
            writer.WriteString("label_version", "X");
            writer.WriteString("raw", value.Raw);
        }
        else if (value.IsCommand)
        {
            writer.WriteString("label_version", "C");
            writer.WriteString("command", value.Command);
        }
        else
        {
            writer.WriteNumber("label_version", value.LabelVersion);
            switch (value.LabelVersion)
            {
                case 1:
                    writer.WriteString("oeps_pn", value.OepsPn);
                    writer.WriteString("lot", value.Lot);
                    writer.WriteString("quantity", value.Quantity);
                    break;
                case 2:
                    writer.WritePropertyName("sales_order_id"); JsonSerializer.Serialize(writer, value.SalesOrderId, options);
                    writer.WriteString("document_name", value.DocumentName);
                    writer.WritePropertyName("out_id"); JsonSerializer.Serialize(writer, value.OutId, options);
                    writer.WriteString("out_number", value.OutNumber);
                    break;
                case 3:
                    writer.WriteString("oeps_pn", value.OepsPn);
                    writer.WriteString("mpn", value.Mpn);
                    writer.WriteString("quantity", value.Quantity);
                    break;
                default: throw new JsonException("Unsupported label version");
            }
        }
        writer.WriteEndObject();
    }
}
