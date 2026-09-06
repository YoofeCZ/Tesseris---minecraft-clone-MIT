namespace Tesseris.Game.Content;

/// <summary>A stable content identifier in <c>namespace:path</c> form.</summary>
public readonly record struct ResourceId
{
    public ResourceId(string @namespace, string path)
    {
        ValidateNamespace(@namespace);
        ValidatePath(path);

        Namespace = @namespace;
        Path = path;
    }

    public string Namespace { get; }

    public string Path { get; }

    public static ResourceId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        int separator = value.IndexOf(':');
        if (separator <= 0 || separator != value.LastIndexOf(':') || separator == value.Length - 1)
        {
            throw new FormatException($"Resource ID '{value}' must use namespace:path form.");
        }

        return new ResourceId(value[..separator], value[(separator + 1)..]);
    }

    public static ResourceId Resolve(string value, string defaultNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Contains(':')
            ? Parse(value)
            : new ResourceId(defaultNamespace, value);
    }

    public override string ToString() => $"{Namespace}:{Path}";

    private static void ValidateNamespace(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Any(c => !IsNamespaceCharacter(c)))
        {
            throw new FormatException(
                $"Resource namespace '{value}' contains an invalid character. " +
                "Only lowercase ASCII letters, digits, '_', '-', and '.' are allowed.");
        }
    }

    private static void ValidatePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value[0] == '/' || value[^1] == '/' || value.Contains('\\')
            || value.Any(c => !IsPathCharacter(c)))
        {
            throw new FormatException(
                $"Resource path '{value}' is invalid. Use lowercase relative paths with '/' separators.");
        }

        foreach (string segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new FormatException($"Resource path '{value}' contains an unsafe path segment.");
            }
        }
    }

    private static bool IsNamespaceCharacter(char value) =>
        value is >= 'a' and <= 'z'
        || value is >= '0' and <= '9'
        || value is '_' or '-' or '.';

    private static bool IsPathCharacter(char value) => IsNamespaceCharacter(value) || value == '/';
}
