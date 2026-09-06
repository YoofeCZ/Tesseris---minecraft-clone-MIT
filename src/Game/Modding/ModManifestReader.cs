using System.Text.Json;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

internal static class ModManifestReader
{
    public const string FileName = "tesseris.mod.json";
    public const string LegacyFileName = "voxelity.mod.json";

    public static ModDescriptor Read(string manifestPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            JsonElement root = document.RootElement;
            string id = RequiredString(root, "id").Trim().ToLowerInvariant();
            ValidateId(id);
            string name = OptionalString(root, "name") ?? id;
            string version = RequiredString(root, "version");
            SemanticVersion.Parse(version);
            string apiVersion = RequiredString(root, "apiVersion");
            VersionConstraint.Parse(apiVersion);
            string? entryAssembly = OptionalString(root, "entryAssembly");
            string? entryType = OptionalString(root, "entryType");
            string? contentRoot = OptionalString(root, "contentRoot");
            bool trustedCode = root.TryGetProperty("trustedCode", out JsonElement trusted) && trusted.ValueKind == JsonValueKind.True;
            IReadOnlyList<string> capabilities = ReadStringArray(root, "capabilities");
            IReadOnlyList<ModDependency> dependencies = ReadDependencies(root);

            if ((entryAssembly is null) != (entryType is null))
            {
                throw new ModHostException("entryAssembly and entryType must either both be present or both be omitted.");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            return new ModDescriptor(
                id, name, SemanticVersion.Parse(version).ToString(), apiVersion, directory,
                entryAssembly, entryType, contentRoot, trustedCode, capabilities, dependencies);
        }
        catch (ModHostException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new ModHostException($"Invalid mod manifest '{manifestPath}': {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<ModDependency> ReadDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return Array.Empty<ModDependency>();
        var result = new List<ModDependency>();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                result.Add(CreateDependency(property.Name, property.Value.GetString() ?? "*"));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                result.Add(CreateDependency(RequiredString(item, "id"), OptionalString(item, "version") ?? "*"));
            }
        }
        else
        {
            throw new ModHostException("dependencies must be an object or array.");
        }

        if (result.Select(dependency => dependency.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
        {
            throw new ModHostException("A dependency is declared more than once.");
        }

        return result.OrderBy(dependency => dependency.Id, StringComparer.Ordinal).ToArray();
    }

    private static ModDependency CreateDependency(string idValue, string version)
    {
        string id = idValue.Trim().ToLowerInvariant();
        ValidateId(id);
        VersionConstraint.Parse(version);
        return new ModDependency(id, version);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)) return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array) throw new ModHostException($"{property} must be an array.");
        return value.EnumerateArray()
            .Select(item => item.GetString() ?? throw new ModHostException($"{property} must contain strings."))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RequiredString(JsonElement root, string property) =>
        OptionalString(root, property) ?? throw new ModHostException($"Required property '{property}' is missing.");

    private static string? OptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ModHostException($"Property '{property}' must be a non-empty string.");
        return value.GetString()!.Trim();
    }

    private static void ValidateId(string id)
    {
        if (id.Length == 0 || id.Any(character =>
                !((character >= 'a' && character <= 'z') || char.IsDigit(character) || character is '_' or '-' or '.')))
        {
            throw new ModHostException($"Invalid mod ID '{id}'. Use lowercase letters, digits, '_', '-' or '.'.");
        }
    }
}
