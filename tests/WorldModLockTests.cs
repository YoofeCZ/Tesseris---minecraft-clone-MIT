using System.Text.Json;
using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class WorldModLockTests
{
    [Fact]
    public void Capture_sorts_mods_and_hashes_their_directories()
    {
        using var temporary = new TempDirectory();
        LoadedModInfo zulu = MakeMod(temporary.CreateMod("zulu", "payload-z"), "zulu", "2.0.0");
        LoadedModInfo alpha = MakeMod(temporary.CreateMod("alpha", "payload-a"), "alpha", "1.0.0");

        WorldModLockSnapshot snapshot = WorldModLock.Capture([zulu, alpha]);

        Assert.Equal(WorldModLock.CurrentFormatVersion, snapshot.FormatVersion);
        Assert.Equal(ModApiInfo.CurrentVersion, snapshot.ModApiVersion);
        Assert.Equal(WorldModLock.CurrentWorldGenerationPipelineVersion, snapshot.WorldGenerationPipelineVersion);
        Assert.Equal(new[] { "alpha", "zulu" }, snapshot.Mods.Select(mod => mod.Id));
        Assert.All(snapshot.Mods, mod => Assert.Matches("^[0-9a-f]{64}$", mod.Sha256));
    }

    [Fact]
    public void Directory_hash_is_stable_and_ignores_build_and_cache_directories()
    {
        using var temporary = new TempDirectory();
        string mod = temporary.CreateMod("sample", "content");
        string first = WorldModLock.ComputeDirectoryHash(mod);

        Write(Path.Combine(mod, "bin", "debug.dll"), "volatile");
        Write(Path.Combine(mod, "obj", "project.assets.json"), "volatile");
        Write(Path.Combine(mod, "cache", "download.bin"), "volatile");
        Write(Path.Combine(mod, ".cache", "index"), "volatile");

        Assert.Equal(first, WorldModLock.ComputeDirectoryHash(mod));

        Write(Path.Combine(mod, "content.txt"), "changed");
        Assert.NotEqual(first, WorldModLock.ComputeDirectoryHash(mod));
    }

    [Fact]
    public void Write_atomically_replaces_and_read_validates_the_lock()
    {
        using var temporary = new TempDirectory();
        WorldModLockSnapshot first = Snapshot("alpha", "1.0.0", Hash('a'));
        WorldModLockSnapshot second = Snapshot("alpha", "2.0.0", Hash('b'));

        WorldModLock.Write(temporary.Path, first);
        WorldModLock.Write(temporary.Path, second);
        WorldModLockReadResult result = WorldModLock.Read(temporary.Path);

        Assert.Equal(WorldModLockReadStatus.Valid, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(second.FormatVersion, result.Snapshot.FormatVersion);
        Assert.Equal(second.ModApiVersion, result.Snapshot.ModApiVersion);
        Assert.Equal(second.WorldGenerationPipelineVersion, result.Snapshot.WorldGenerationPipelineVersion);
        Assert.Equal(second.Mods, result.Snapshot.Mods);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public void Read_reports_missing_lock_explicitly()
    {
        using var temporary = new TempDirectory();

        WorldModLockReadResult read = WorldModLock.Read(temporary.Path);
        WorldModLockComparison comparison = WorldModLock.Compare(read, EmptySnapshot());

        Assert.Equal(WorldModLockReadStatus.Missing, read.Status);
        Assert.Equal(WorldModLockComparisonStatus.MissingLock, comparison.Status);
        Assert.False(comparison.IsMatch);
    }

    [Fact]
    public void Read_rejects_invalid_or_unsorted_snapshots()
    {
        using var temporary = new TempDirectory();
        string path = Path.Combine(temporary.Path, WorldModLock.FileName);
        var invalid = new
        {
            formatVersion = WorldModLock.CurrentFormatVersion,
            modApiVersion = ModApiInfo.CurrentVersion,
            worldGenerationPipelineVersion = 1,
            mods = new[]
            {
                new { id = "zulu", version = "1.0.0", sha256 = Hash('a') },
                new { id = "alpha", version = "1.0.0", sha256 = Hash('b') },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(invalid));

        WorldModLockReadResult result = WorldModLock.Read(temporary.Path);

        Assert.Equal(WorldModLockReadStatus.Invalid, result.Status);
        Assert.Contains("sorted", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_reports_added_removed_changed_api_and_worldgen()
    {
        WorldModLockSnapshot saved = new(
            WorldModLock.CurrentFormatVersion,
            "1.0.0",
            1,
            [
                new WorldModLockEntry("changed", "1.0.0", Hash('a')),
                new WorldModLockEntry("removed", "1.0.0", Hash('b')),
            ]);
        WorldModLockSnapshot current = new(
            WorldModLock.CurrentFormatVersion,
            "2.0.0",
            2,
            [
                new WorldModLockEntry("added", "1.0.0", Hash('c')),
                new WorldModLockEntry("changed", "1.1.0", Hash('d')),
            ]);

        WorldModLockComparison comparison = WorldModLock.Compare(saved, current);

        Assert.Equal(WorldModLockComparisonStatus.Different, comparison.Status);
        Assert.True(comparison.ModApiChanged);
        Assert.True(comparison.WorldGenerationPipelineChanged);
        Assert.Equal("added", Assert.Single(comparison.Added).Id);
        Assert.Equal("removed", Assert.Single(comparison.Removed).Id);
        ChangedWorldMod changed = Assert.Single(comparison.Changed);
        Assert.Equal("1.0.0", changed.Saved.Version);
        Assert.Equal("1.1.0", changed.Current.Version);
    }

    [Fact]
    public void Compare_matches_identical_snapshots()
    {
        WorldModLockSnapshot snapshot = Snapshot("alpha", "1.0.0", Hash('a'));

        WorldModLockComparison comparison = WorldModLock.Compare(snapshot, snapshot);

        Assert.Equal(WorldModLockComparisonStatus.Match, comparison.Status);
        Assert.True(comparison.IsMatch);
        Assert.Empty(comparison.Added);
        Assert.Empty(comparison.Removed);
        Assert.Empty(comparison.Changed);
    }

    [Fact]
    public void Compare_rejects_changed_world_definition_fingerprint()
    {
        WorldModLockSnapshot saved = EmptySnapshot() with
        {
            WorldPresetId = "total:skylands",
            WorldDimensionId = "total:overworld",
            WorldDefinitionFingerprint = Hash('a'),
        };
        WorldModLockSnapshot current = saved with { WorldDefinitionFingerprint = Hash('b') };

        WorldModLockComparison comparison = WorldModLock.Compare(saved, current);

        Assert.Equal(WorldModLockComparisonStatus.Different, comparison.Status);
        Assert.True(comparison.WorldDefinitionChanged);
    }

    [Fact]
    public void Compare_allows_one_time_upgrade_of_legacy_lock_without_world_definition()
    {
        WorldModLockSnapshot saved = EmptySnapshot();
        WorldModLockSnapshot current = saved with
        {
            WorldPresetId = "tesseris:default",
            WorldDimensionId = "tesseris:overworld",
            WorldDefinitionFingerprint = Hash('c'),
        };

        WorldModLockComparison comparison = WorldModLock.Compare(saved, current);

        Assert.True(comparison.IsMatch);
        Assert.False(comparison.WorldDefinitionChanged);
    }

    [Fact]
    public void Write_and_read_preserve_world_selection_and_definition_fingerprint()
    {
        using var temporary = new TempDirectory();
        WorldModLockSnapshot snapshot = EmptySnapshot() with
        {
            WorldPresetId = "total:skylands",
            WorldDimensionId = "total:void",
            WorldDefinitionFingerprint = Hash('d'),
        };

        WorldModLock.Write(temporary.Path, snapshot);
        WorldModLockSnapshot restored = Assert.IsType<WorldModLockSnapshot>(
            WorldModLock.Read(temporary.Path).Snapshot);

        Assert.Equal(snapshot.WorldPresetId, restored.WorldPresetId);
        Assert.Equal(snapshot.WorldDimensionId, restored.WorldDimensionId);
        Assert.Equal(snapshot.WorldDefinitionFingerprint, restored.WorldDefinitionFingerprint);
    }

    private static LoadedModInfo MakeMod(string directory, string id, string version)
    {
        var descriptor = new ModDescriptor(
            id,
            id,
            version,
            "^1.0.0",
            directory,
            null,
            null,
            "content",
            false,
            [],
            []);
        return new LoadedModInfo(descriptor, "content", false);
    }

    private static WorldModLockSnapshot Snapshot(string id, string version, string hash) =>
        new(
            WorldModLock.CurrentFormatVersion,
            ModApiInfo.CurrentVersion,
            WorldModLock.CurrentWorldGenerationPipelineVersion,
            [new WorldModLockEntry(id, version, hash)]);

    private static WorldModLockSnapshot EmptySnapshot() =>
        new(
            WorldModLock.CurrentFormatVersion,
            ModApiInfo.CurrentVersion,
            WorldModLock.CurrentWorldGenerationPipelineVersion,
            []);

    private static string Hash(char character) => new(character, 64);

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tesseris-world-mod-lock-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateMod(string name, string content)
        {
            string directory = System.IO.Path.Combine(Path, name);
            Write(System.IO.Path.Combine(directory, "content.txt"), content);
            return directory;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
