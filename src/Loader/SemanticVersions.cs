using System.Globalization;

namespace Tesseris.Loader;

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        int build = normalized.IndexOf('+');
        if (build >= 0) normalized = normalized[..build];
        string? preRelease = null;
        int dash = normalized.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = normalized[(dash + 1)..];
            normalized = normalized[..dash];
            if (preRelease.Length == 0) throw new FormatException($"Invalid semantic version '{value}'.");
        }

        string[] parts = normalized.Split('.');
        if (parts.Length is < 1 or > 3
            || !TryPart(parts, 0, out int major)
            || !TryPart(parts, 1, out int minor)
            || !TryPart(parts, 2, out int patch))
        {
            throw new FormatException($"Invalid semantic version '{value}'.");
        }

        return new SemanticVersion(major, minor, patch, preRelease);
    }

    public int CompareTo(SemanticVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease is null ? "" : "-" + PreRelease)}";

    private static bool TryPart(string[] parts, int index, out int value)
    {
        if (index >= parts.Length)
        {
            value = 0;
            return true;
        }

        string part = parts[index];
        value = 0;
        return part.Length > 0
            && (part.Length == 1 || part[0] != '0')
            && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }
}

internal sealed class VersionConstraint
{
    private readonly Func<SemanticVersion, bool> predicate;
    private readonly string source;

    private VersionConstraint(string source, Func<SemanticVersion, bool> predicate)
    {
        this.source = source;
        this.predicate = predicate;
    }

    public static VersionConstraint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string source = value.Trim();
        if (source is "*" or "x" or "X") return new VersionConstraint(source, _ => true);

        if (source.StartsWith('^'))
        {
            SemanticVersion minimum = SemanticVersion.Parse(source[1..]);
            SemanticVersion maximum = minimum.Major > 0
                ? new SemanticVersion(minimum.Major + 1, 0, 0)
                : minimum.Minor > 0
                    ? new SemanticVersion(0, minimum.Minor + 1, 0)
                    : new SemanticVersion(0, 0, minimum.Patch + 1);
            return new VersionConstraint(source, candidate => candidate.CompareTo(minimum) >= 0 && candidate.CompareTo(maximum) < 0);
        }

        if (source.StartsWith('~'))
        {
            SemanticVersion minimum = SemanticVersion.Parse(source[1..]);
            SemanticVersion maximum = new(minimum.Major, minimum.Minor + 1, 0);
            return new VersionConstraint(source, candidate => candidate.CompareTo(minimum) >= 0 && candidate.CompareTo(maximum) < 0);
        }

        if (source.Contains(' ', StringComparison.Ordinal))
        {
            VersionConstraint[] parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(Parse)
                .ToArray();
            return new VersionConstraint(source, candidate => parts.All(part => part.Allows(candidate)));
        }

        foreach ((string prefix, Func<int, bool> compare) in new (string, Func<int, bool>)[]
                 {
                     (">=", result => result >= 0),
                     ("<=", result => result <= 0),
                     (">", result => result > 0),
                     ("<", result => result < 0),
                     ("=", result => result == 0),
                 })
        {
            if (!source.StartsWith(prefix, StringComparison.Ordinal)) continue;
            SemanticVersion expected = SemanticVersion.Parse(source[prefix.Length..]);
            return new VersionConstraint(source, candidate => compare(candidate.CompareTo(expected)));
        }

        string[] wildcard = source.Split('.');
        if (wildcard.Any(part => part is "x" or "X" or "*"))
        {
            int wildcardIndex = Array.FindIndex(wildcard, part => part is "x" or "X" or "*");
            if (wildcard.Skip(wildcardIndex).Any(part => part is not ("x" or "X" or "*")))
                throw new FormatException($"Invalid version constraint '{source}'.");
            int? major = wildcardIndex > 0 ? int.Parse(wildcard[0], CultureInfo.InvariantCulture) : null;
            int? minor = wildcardIndex > 1 ? int.Parse(wildcard[1], CultureInfo.InvariantCulture) : null;
            return new VersionConstraint(source, candidate =>
                (major is null || candidate.Major == major) && (minor is null || candidate.Minor == minor));
        }

        SemanticVersion exact = SemanticVersion.Parse(source);
        return new VersionConstraint(source, candidate => candidate.CompareTo(exact) == 0);
    }

    public bool Allows(string version) => Allows(SemanticVersion.Parse(version));

    private bool Allows(SemanticVersion version) => predicate(version);

    public override string ToString() => source;
}
