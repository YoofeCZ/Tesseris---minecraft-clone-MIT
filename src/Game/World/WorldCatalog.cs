using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tesseris.Game.World;

/// <summary>Svět nalezený na disku nebo právě založený hráčem.</summary>
public sealed record WorldInfo(
    string DisplayName,
    string FolderId,
    string Directory,
    int Seed,
    string? WorldPresetId = null);

/// <summary>
/// Bezpečně vytváří a objevuje světy pod jedním kořenem. Veřejný kód nikdy neskládá cestu
/// přímo ze jména zadaného hráčem.
/// </summary>
public sealed class WorldCatalog
{
    public const string MetadataFileName = "world.json";
    public const int CurrentMetadataVersion = 2;
    public const int LegacySeed = 20260727;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    public WorldCatalog(string savesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savesRoot);
        SavesRoot = Path.GetFullPath(savesRoot);
    }

    public string SavesRoot { get; }

    /// <summary>
    /// Vytvoří vždy novou složku. Existující svět se stejným názvem dostane číselnou příponu
    /// a jeho metadata se nikdy nepřepíšou.
    /// </summary>
    public WorldInfo Create(string displayName, int seed, string? worldPresetId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Trim().Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(displayName), "Název je příliš dlouhý.");
        }

        lock (_gate)
        {
            if (worldPresetId is not null)
            {
                _ = new Tesseris.ModApi.ResourceId(worldPresetId);
            }

            System.IO.Directory.CreateDirectory(SavesRoot);

            string baseId = SanitizeFolderId(displayName);
            string folderId = FindAvailableFolderId(baseId);
            string directory = ChildDirectory(folderId);
            System.IO.Directory.CreateDirectory(directory);

            var metadata = new WorldMetadata(
                CurrentMetadataVersion,
                displayName.Trim(),
                seed,
                worldPresetId);

            try
            {
                WriteNewMetadata(directory, metadata);
            }
            catch
            {
                // Složku vytvořil tento pokus a metadata se nezapsala. Maže se pouze tehdy,
                // když je pořád prázdná; cizích dat se katalog nikdy nedotkne.
                if (!System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    System.IO.Directory.Delete(directory);
                }

                throw;
            }

            return ToInfo(folderId, directory, metadata);
        }
    }

    /// <summary>
    /// Načte platné světy. Rozbitá metadata přeskočí; původní složka <c>svet</c> zůstane
    /// dostupná i bez metadat se starým pevným seedem.
    /// </summary>
    public IReadOnlyList<WorldInfo> Discover()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SavesRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var worlds = new List<WorldInfo>();
        IEnumerable<string> directories;

        try
        {
            directories = System.IO.Directory.EnumerateDirectories(SavesRoot).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return worlds;
        }

        foreach (string directory in directories)
        {
            string folderId = Path.GetFileName(directory);
            if (TryReadMetadata(directory, out WorldMetadata? metadata))
            {
                worlds.Add(ToInfo(folderId, Path.GetFullPath(directory), metadata));
                continue;
            }

            if (!string.Equals(folderId, "svet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var legacy = new WorldMetadata(CurrentMetadataVersion, "svet", LegacySeed, null);
            worlds.Add(ToInfo(folderId, Path.GetFullPath(directory), legacy));

            string metadataPath = Path.Combine(directory, MetadataFileName);
            if (!File.Exists(metadataPath))
            {
                try
                {
                    WriteNewMetadata(directory, legacy);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Nabídnutí starého světa nesmí záviset na tom, zda jsou metadata zapisovatelná.
                }
            }
        }

        return worlds
            .OrderBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Převede zobrazovaný název na přenositelné ASCII id jedné složky.</summary>
    public static string SanitizeFolderId(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        string decomposed = displayName.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(Math.Min(decomposed.Length, 48));
        bool pendingSeparator = false;

        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            char lower = char.ToLowerInvariant(character);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingSeparator && result.Length > 0 && result.Length < 48)
                {
                    result.Append('-');
                }

                if (result.Length < 48)
                {
                    result.Append(lower);
                }

                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        string folderId = result.ToString().Trim('-');
        if (folderId.Length == 0)
        {
            folderId = "svet";
        }

        if (ReservedWindowsNames.Contains(folderId))
        {
            folderId = $"svet-{folderId}";
        }

        return folderId;
    }

    private string FindAvailableFolderId(string baseId)
    {
        string candidate = baseId;
        int suffix = 2;

        while (System.IO.Directory.Exists(ChildDirectory(candidate))
            || File.Exists(ChildDirectory(candidate)))
        {
            string ending = $"-{suffix}";
            int availableBaseLength = Math.Max(1, 48 - ending.Length);
            string shortenedBase = baseId[..Math.Min(baseId.Length, availableBaseLength)].TrimEnd('-');
            candidate = shortenedBase + ending;
            suffix++;
        }

        return candidate;
    }

    private string ChildDirectory(string folderId)
    {
        string path = Path.GetFullPath(Path.Combine(SavesRoot, folderId));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(SavesRoot)
            + Path.DirectorySeparatorChar;

        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cesta světa opustila složku saves.");
        }

        return path;
    }

    private static bool TryReadMetadata(string directory, out WorldMetadata metadata)
    {
        metadata = null!;
        try
        {
            string json = File.ReadAllText(Path.Combine(directory, MetadataFileName));
            WorldMetadata? parsed = JsonSerializer.Deserialize<WorldMetadata>(json, JsonOptions);
            if (parsed is null
                || parsed.Version is not (1 or CurrentMetadataVersion)
                || string.IsNullOrWhiteSpace(parsed.Name))
            {
                return false;
            }

            if (parsed.WorldPresetId is not null)
            {
                _ = new Tesseris.ModApi.ResourceId(parsed.WorldPresetId);
            }

            metadata = parsed;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or FormatException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static void WriteNewMetadata(string directory, WorldMetadata metadata)
    {
        string json = JsonSerializer.Serialize(metadata, JsonOptions);
        using var stream = new FileStream(
            Path.Combine(directory, MetadataFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(json);
    }

    private static WorldInfo ToInfo(string folderId, string directory, WorldMetadata metadata) =>
        new(metadata.Name, folderId, directory, metadata.Seed, metadata.WorldPresetId);

    private sealed record WorldMetadata(
        int Version,
        string Name,
        int Seed,
        string? WorldPresetId = null);
}
