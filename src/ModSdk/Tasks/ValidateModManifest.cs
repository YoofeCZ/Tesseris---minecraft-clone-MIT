using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Tesseris.ModSdk.Tasks;

public sealed class ValidateModManifest : Microsoft.Build.Utilities.Task
{
    [Required] public string ManifestPath { get; set; } = string.Empty;
    [Required] public string ModId { get; set; } = string.Empty;
    [Required] public string ModName { get; set; } = string.Empty;
    [Required] public string ModVersion { get; set; } = string.Empty;
    [Required] public string ModApiVersion { get; set; } = string.Empty;
    [Required] public string EntryAssembly { get; set; } = string.Empty;
    [Required] public string EntryType { get; set; } = string.Empty;
    [Required] public string ContentRoot { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            ValidateIdentifier(ModId);
            ValidateRelativeFileName(EntryAssembly, nameof(EntryAssembly));
            ValidateRelativeDirectory(ContentRoot, nameof(ContentRoot));

            using FileStream stream = File.OpenRead(ManifestPath);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The manifest root must be a JSON object.");
            }

            RequireEqual(root, "id", ModId);
            RequireEqual(root, "name", ModName);
            RequireEqual(root, "version", ModVersion);
            RequireEqual(root, "contentRoot", ContentRoot);

            int schemaVersion = root.TryGetProperty("schemaVersion", out JsonElement schema)
                && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out int parsedSchema)
                    ? parsedSchema
                    : 1;
            if (schemaVersion == 1)
            {
                RequireEqual(root, "apiVersion", ModApiVersion);
                RequireEqual(root, "entryAssembly", EntryAssembly);
                RequireEqual(root, "entryType", EntryType);
            }
            else if (schemaVersion == 2)
            {
                RequireEqual(root, "modApiVersion", ModApiVersion);
                ValidateRuntimeEntrypoint(root, EntryAssembly, EntryType);
            }
            else
            {
                throw new InvalidDataException($"Unsupported schemaVersion '{schemaVersion}'.");
            }

            if (!root.TryGetProperty("trustedCode", out JsonElement trustedCode) ||
                trustedCode.ValueKind != JsonValueKind.True)
            {
                throw new InvalidDataException("Property 'trustedCode' must explicitly be true for a managed mod.");
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.LogError($"Invalid Tesseris mod manifest '{ManifestPath}': {exception.Message}");
            return false;
        }
    }

    private static void ValidateRuntimeEntrypoint(
        JsonElement root,
        string expectedAssembly,
        string expectedType)
    {
        if (!root.TryGetProperty("entrypoints", out JsonElement entrypoints)
            || entrypoints.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Property 'entrypoints' must be an array for schemaVersion 2.");
        }

        foreach (JsonElement entrypoint in entrypoints.EnumerateArray())
        {
            if (entrypoint.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool runtime = entrypoint.TryGetProperty("phase", out JsonElement phase)
                && phase.ValueKind == JsonValueKind.String
                && string.Equals(phase.GetString(), "runtime", StringComparison.OrdinalIgnoreCase);
            bool assembly = entrypoint.TryGetProperty("assembly", out JsonElement assemblyName)
                && assemblyName.ValueKind == JsonValueKind.String
                && string.Equals(assemblyName.GetString(), expectedAssembly, StringComparison.Ordinal);
            bool type = entrypoint.TryGetProperty("type", out JsonElement typeName)
                && typeName.ValueKind == JsonValueKind.String
                && string.Equals(typeName.GetString(), expectedType, StringComparison.Ordinal);
            if (runtime && assembly && type)
            {
                return;
            }
        }

        throw new InvalidDataException(
            $"SchemaVersion 2 must declare runtime entrypoint '{expectedType}' in '{expectedAssembly}'.");
    }

    private static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim().ToLowerInvariant() ||
            value.Any(character => !((character >= 'a' && character <= 'z') ||
                                     char.IsDigit(character) || character is '_' or '-' or '.')))
        {
            throw new InvalidDataException(
                $"Invalid mod ID '{value}'. Use lowercase letters, digits, '_', '-' or '.'.");
        }
    }

    private static void ValidateRelativeFileName(string value, string property)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value != Path.GetFileName(value) || value is "." or "..")
        {
            throw new InvalidDataException($"{property} must be a safe file name, not a path.");
        }
    }

    private static void ValidateRelativeDirectory(string value, string property)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"{property} must be a safe relative directory.");
        }
    }

    private static void RequireEqual(JsonElement root, string propertyName, string expected)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            !string.Equals(property.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Property '{propertyName}' must equal '{expected}'.");
        }
    }
}
