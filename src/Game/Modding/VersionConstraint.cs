using System.Globalization;

namespace Tesseris.Game.Modding;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out SemanticVersion version))
        {
            throw new FormatException($"'{value}' is not a semantic version.");
        }

        return version;
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string withoutBuild = value.Trim().Split('+', 2)[0];
        string[] releaseAndPre = withoutBuild.Split('-', 2);
        string[] numbers = releaseAndPre[0].Split('.');
        int minor = 0;
        int patch = 0;
        if (numbers.Length is < 1 or > 3 ||
            !TryPart(numbers[0], out int major) ||
            (numbers.Length >= 2 && !TryPart(numbers[1], out minor)) ||
            (numbers.Length >= 3 && !TryPart(numbers[2], out patch)))
        {
            return false;
        }

        string? preRelease = releaseAndPre.Length == 2 ? releaseAndPre[1] : null;
        if (preRelease is not null && (preRelease.Length == 0 || preRelease.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))))
        {
            return false;
        }

        version = new SemanticVersion(major, numbers.Length >= 2 ? minor : 0, numbers.Length >= 3 ? patch : 0, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        int comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}{(PreRelease is null ? string.Empty : $"-{PreRelease}")}");

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static bool TryPart(string value, out int part) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out part) && part >= 0;

    private static int ComparePreRelease(string left, string right)
    {
        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');
        for (int index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            bool leftNumber = int.TryParse(leftParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int leftValue);
            bool rightNumber = int.TryParse(rightParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int rightValue);
            int comparison = leftNumber && rightNumber
                ? leftValue.CompareTo(rightValue)
                : leftNumber ? -1 : rightNumber ? 1 : StringComparer.Ordinal.Compare(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }
}

public sealed class VersionConstraint
{
    private readonly IReadOnlyList<IReadOnlyList<Func<SemanticVersion, bool>>> alternatives;

    private VersionConstraint(IReadOnlyList<IReadOnlyList<Func<SemanticVersion, bool>>> alternatives)
    {
        this.alternatives = alternatives;
    }

    public static VersionConstraint Parse(string? expression)
    {
        string normalized = string.IsNullOrWhiteSpace(expression) ? "*" : expression.Trim();
        var alternatives = new List<IReadOnlyList<Func<SemanticVersion, bool>>>();
        foreach (string alternative in normalized.Split("||", StringSplitOptions.TrimEntries))
        {
            if (alternative.Length == 0)
            {
                throw new FormatException($"Invalid version constraint '{expression}'.");
            }

            alternatives.Add(ParseAlternative(alternative));
        }

        return new VersionConstraint(alternatives);
    }

    public bool Allows(SemanticVersion version) => alternatives.Any(group => group.All(predicate => predicate(version)));

    public bool Allows(string version) => Allows(SemanticVersion.Parse(version));

    private static IReadOnlyList<Func<SemanticVersion, bool>> ParseAlternative(string expression)
    {
        if (expression is "*" or "x" or "X") return Array.Empty<Func<SemanticVersion, bool>>();

        string[] tokens = expression.Replace(",", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var predicates = new List<Func<SemanticVersion, bool>>();
        foreach (string token in tokens)
        {
            if (token.StartsWith('^'))
            {
                SemanticVersion minimum = SemanticVersion.Parse(token[1..]);
                SemanticVersion maximum = minimum.Major > 0
                    ? new SemanticVersion(minimum.Major + 1, 0, 0)
                    : minimum.Minor > 0
                        ? new SemanticVersion(0, minimum.Minor + 1, 0)
                        : new SemanticVersion(0, 0, minimum.Patch + 1);
                predicates.Add(version => version >= minimum && version < maximum);
                continue;
            }

            if (token.StartsWith('~'))
            {
                SemanticVersion minimum = SemanticVersion.Parse(token[1..]);
                SemanticVersion maximum = new(minimum.Major, minimum.Minor + 1, 0);
                predicates.Add(version => version >= minimum && version < maximum);
                continue;
            }

            if (token.Contains('x', StringComparison.OrdinalIgnoreCase) || token.Contains('*'))
            {
                AddWildcard(token, predicates);
                continue;
            }

            string operation = token.StartsWith(">=", StringComparison.Ordinal) || token.StartsWith("<=", StringComparison.Ordinal)
                ? token[..2]
                : token.Length > 0 && token[0] is '>' or '<' or '=' ? token[..1] : "=";
            int versionStart = operation == "=" && !token.StartsWith('=') ? 0 : operation.Length;
            SemanticVersion expected = SemanticVersion.Parse(token[versionStart..]);
            predicates.Add(operation switch
            {
                ">=" => version => version >= expected,
                "<=" => version => version <= expected,
                ">" => version => version > expected,
                "<" => version => version < expected,
                "=" => version => version.CompareTo(expected) == 0,
                _ => throw new FormatException($"Unknown version operator '{operation}'.")
            });
        }

        return predicates;
    }

    private static void AddWildcard(string token, ICollection<Func<SemanticVersion, bool>> predicates)
    {
        string[] parts = token.Split('.');
        int wildcardIndex = Array.FindIndex(parts, part => part is "*" or "x" or "X");
        if (wildcardIndex == 0) return;
        if (wildcardIndex < 0 || wildcardIndex > 2)
        {
            throw new FormatException($"Invalid version wildcard '{token}'.");
        }

        string prefix = string.Join('.', parts.Take(wildcardIndex));
        SemanticVersion minimum = SemanticVersion.Parse(prefix);
        SemanticVersion maximum = wildcardIndex == 1
            ? new SemanticVersion(minimum.Major + 1, 0, 0)
            : new SemanticVersion(minimum.Major, minimum.Minor + 1, 0);
        predicates.Add(version => version >= minimum && version < maximum);
    }
}
