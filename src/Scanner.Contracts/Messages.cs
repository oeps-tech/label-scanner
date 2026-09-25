using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scanner.Contracts;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.Strict, MaxDepth = 12
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty JSON");
}

// Preserve quantity exactly, without numeric conversion or rounding.
public sealed record LabelData(int LabelVersion, string OepsPn, string Lot, string Quantity);
