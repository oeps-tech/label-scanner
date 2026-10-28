namespace Scanner.Contracts;

public sealed record LabelClassification(LabelData Data, string Reason, string? ExpectedChecksum = null);

/// <summary>Routes one decoded string to one message: validated OEPS, recognized supplier, or unmodified raw fallback.</summary>
public static class LabelClassifier
{
    public static LabelClassification Classify(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var own = PayloadValidator.Validate(raw);
        if (own.Valid) return new(own.Data!, own.Reason, own.ExpectedChecksum);
        var supplier = SupplierLabelParser.Parse(raw);
        if (supplier?.Data is { } data) return new(data, supplier.Reason);
        return new(LabelData.FromRaw(raw), supplier?.Reason ?? $"Raw fallback; OEPS validation: {own.Stage} — {own.Reason}", own.ExpectedChecksum);
    }
}
