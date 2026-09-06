namespace Tesseris.ModApi;

/// <summary>Identifies the public contract implemented by this assembly.</summary>
public static class ModApiInfo
{
    /// <summary>
    /// Version advertised by the legacy in-game host. It remains at 1.x until the standalone loader
    /// negotiates both API generations, so an existing exact-version manifest is not rejected during
    /// the staged v2 rollout.
    /// </summary>
    public const string CurrentVersion = "1.0.0";

    /// <summary>The newest contract generation contained in this assembly.</summary>
    public const string LatestContractVersion = "2.0.0";
}

/// <summary>A stable, namespaced identifier such as <c>example:copper_ore</c>.</summary>
public readonly record struct ResourceId
{
    public ResourceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim().ToLowerInvariant();
        int separator = normalized.IndexOf(':');
        if (separator <= 0 || separator == normalized.Length - 1 ||
            !IsPartValid(normalized.AsSpan(0, separator)) ||
            !IsPartValid(normalized.AsSpan(separator + 1)))
        {
            throw new ArgumentException(
                "A resource ID must have the form 'namespace:path' and use only a-z, 0-9, _, -, . or /.",
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool IsPartValid(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!((character >= 'a' && character <= 'z') ||
                  (character >= '0' && character <= '9') ||
                  character is '_' or '-' or '.' or '/'))
            {
                return false;
            }
        }

        return true;
    }
}
