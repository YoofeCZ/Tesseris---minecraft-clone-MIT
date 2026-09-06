using System.Text.Json;

namespace Tesseris.Loader;

/// <summary>Shared launcher/game persistence for pre-load mod enablement.</summary>
public static class ModEnablementSettings
{
    public const string FileName = "mod-manager.json";

    public static string GetPath(string modsRoot) => Path.Combine(
        Path.GetFullPath(modsRoot),
        ModManifestDiscovery.CanonicalCacheDirectoryName,
        FileName);

    public static IReadOnlySet<string> ReadDisabled(string modsRoot)
    {
        string path = GetPath(modsRoot);
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            Settings? settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path));
            if (settings is null || settings.Version != 1 || settings.DisabledMods is null)
                throw new InvalidDataException("Unsupported mod-manager settings version.");
            return settings.DisabledMods
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Invalid mod-manager settings '{path}'.", exception);
        }
    }

    public static void WriteDisabled(string modsRoot, IEnumerable<string> disabledModIds)
    {
        ArgumentNullException.ThrowIfNull(disabledModIds);
        string path = GetPath(modsRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string[] disabled = disabledModIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new Settings(1, disabled),
            new JsonSerializerOptions { WriteIndented = true });
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Settings(int Version, string[] DisabledMods);
}
