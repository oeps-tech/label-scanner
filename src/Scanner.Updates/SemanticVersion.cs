using System.Globalization;
using System.Text.RegularExpressions;

namespace Scanner.Updates;

public sealed partial class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(ulong major, ulong minor, ulong patch, string prerelease, string build)
        => (Major, Minor, Patch, Prerelease, Build) = (major, minor, patch, prerelease, build);

    public ulong Major { get; }
    public ulong Minor { get; }
    public ulong Patch { get; }
    public string Prerelease { get; }
    public string Build { get; }
    public bool IsPrerelease => Prerelease.Length != 0;

    [GeneratedRegex(@"^[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static SemanticVersion Parse(string value) => TryParse(value, out var version)
        ? version! : throw new FormatException($"Invalid semantic version: {value}");

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) return false;
        var match = VersionPattern().Match(value);
        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, out var major)
            || !ulong.TryParse(match.Groups[2].Value, out var minor)
            || !ulong.TryParse(match.Groups[3].Value, out var patch)) return false;
        var prerelease = match.Groups[4].Value;
        if (prerelease.Split('.').Any(x => x.Length > 1 && x[0] == '0' && x.All(char.IsAsciiDigit))) return false;
        version = new(major, minor, patch, prerelease, match.Groups[5].Value);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = left[i].All(char.IsAsciiDigit);
            var rightNumeric = right[i].All(char.IsAsciiDigit);
            result = leftNumeric && rightNumeric
                ? left[i].Length.CompareTo(right[i].Length)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : 0;
            if (result == 0) result = string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Major}.{Minor}.{Patch}{(IsPrerelease ? "-" + Prerelease : "")}{(Build.Length > 0 ? "+" + Build : "")}");
}
