using System.Text;
using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModDataStoreTests
{
    [Fact]
    public void Missing_data_directory_opens_empty_and_mod_view_stays_stable()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ModDataStore();
        IModData first = store.ForMod("alpha");
        IModData second = store.ForMod("alpha");

        Assert.Same(first, second);
        Assert.False(first.IsWorldOpen);
        Assert.Null(first.World);

        store.AttachWorld(temporary.Path);

        Assert.True(first.IsWorldOpen);
        Assert.NotNull(first.World);
        Assert.Empty(first.World!.Keys);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, ModDataStore.DirectoryName)));

        store.DetachWorld();

        Assert.False(first.IsWorldOpen);
        Assert.Null(first.World);
    }

    [Fact]
    public void Values_are_owner_namespaced_sorted_and_defensively_copied()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ModDataStore();
        IModData data = store.ForMod("alpha");
        store.AttachWorld(temporary.Path);
        IModDataContainer world = Assert.IsAssignableFrom<IModDataContainer>(data.World);
        byte[] source = [1, 2, 3];

        world.Set(new ResourceId("alpha:zulu"), source);
        world.Set(new ResourceId("alpha:beta"), [4, 5]);
        source[0] = 99;

        Assert.Equal(
            new[] { "alpha:beta", "alpha:zulu" },
            world.Keys.Select(key => key.Value));
        Assert.True(world.TryGet(new ResourceId("alpha:zulu"), out ReadOnlyMemory<byte> firstRead));
        Assert.Equal(new byte[] { 1, 2, 3 }, firstRead.ToArray());

        byte[] callerCopy = firstRead.ToArray();
        callerCopy[1] = 88;
        Assert.True(world.TryGet(new ResourceId("alpha:zulu"), out ReadOnlyMemory<byte> secondRead));
        Assert.Equal(new byte[] { 1, 2, 3 }, secondRead.ToArray());

        Assert.Throws<ArgumentException>(() =>
            world.Set(new ResourceId("other:key"), [7]));
        Assert.Throws<ArgumentException>(() =>
            world.TryGet(new ResourceId("other:key"), out _));
    }

    [Fact]
    public void World_and_position_values_round_trip_and_position_can_be_removed_explicitly()
    {
        using var temporary = new TemporaryDirectory();
        var position = new ModBlockPosition(-12, 45, 7);
        var store = new ModDataStore();
        IModData data = store.ForMod("alpha");
        store.AttachWorld(temporary.Path);
        data.World!.Set(new ResourceId("alpha:world"), Encoding.UTF8.GetBytes("global"));
        data.At(position)!.Set(new ResourceId("alpha:position"), [9, 8, 7]);
        store.SaveWorld();
        store.DetachWorld();

        store.AttachWorld(temporary.Path);

        Assert.True(data.World!.TryGet(new ResourceId("alpha:world"), out ReadOnlyMemory<byte> world));
        Assert.Equal("global", Encoding.UTF8.GetString(world.Span));
        Assert.True(data.At(position)!.TryGet(
            new ResourceId("alpha:position"),
            out ReadOnlyMemory<byte> local));
        Assert.Equal(new byte[] { 9, 8, 7 }, local.ToArray());
        Assert.True(store.RemovePosition("alpha", position));
        Assert.False(store.RemovePosition("alpha", position));
        store.SaveWorld();
        store.DetachWorld();

        store.AttachWorld(temporary.Path);
        Assert.Empty(data.At(position)!.Keys);
    }

    [Fact]
    public void Serialization_is_deterministic_independent_of_insertion_order()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();

        WriteInOrder(firstDirectory.Path, reverse: false);
        WriteInOrder(secondDirectory.Path, reverse: true);

        Assert.Equal(
            File.ReadAllBytes(DataPath(firstDirectory.Path, "alpha")),
            File.ReadAllBytes(DataPath(secondDirectory.Path, "alpha")));
    }

    [Fact]
    public void Corrupt_owner_file_fails_closed_and_is_not_overwritten()
    {
        using var temporary = new TemporaryDirectory();
        string path = DataPath(temporary.Path, "alpha");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] corrupt = Encoding.UTF8.GetBytes("not valid mod data");
        File.WriteAllBytes(path, corrupt);
        var store = new ModDataStore();
        IModData data = store.ForMod("alpha");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.AttachWorld(temporary.Path));

        Assert.Contains("corrupt", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.IsWorldOpen);
        Assert.False(data.IsWorldOpen);
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.Throws<InvalidOperationException>(store.SaveWorld);
    }

    [Fact]
    public void Files_for_missing_mods_are_untouched()
    {
        using var temporary = new TemporaryDirectory();
        string absentPath = DataPath(temporary.Path, "absent");
        Directory.CreateDirectory(Path.GetDirectoryName(absentPath)!);
        byte[] absentBytes = Encoding.UTF8.GetBytes("opaque data owned by an absent mod");
        File.WriteAllBytes(absentPath, absentBytes);
        var store = new ModDataStore();
        IModData loaded = store.ForMod("loaded");

        store.AttachWorld(temporary.Path);
        loaded.World!.Set(new ResourceId("loaded:value"), [1]);
        store.SaveWorld();

        Assert.Equal(absentBytes, File.ReadAllBytes(absentPath));
        Assert.True(File.Exists(DataPath(temporary.Path, "loaded")));
    }

    [Fact]
    public void Containers_are_rejected_after_detach_or_a_new_world_attach()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();
        var store = new ModDataStore();
        IModData data = store.ForMod("alpha");
        store.AttachWorld(firstDirectory.Path);
        IModDataContainer staleWorld = data.World!;
        IModDataContainer stalePosition = data.At(new ModBlockPosition(1, 2, 3))!;

        store.DetachWorld();

        Assert.Throws<InvalidOperationException>(() => _ = staleWorld.Keys);
        Assert.Throws<InvalidOperationException>(() => stalePosition.Set(new ResourceId("alpha:x"), [1]));

        store.AttachWorld(secondDirectory.Path);

        Assert.Throws<InvalidOperationException>(() => staleWorld.TryGet(new ResourceId("alpha:x"), out _));
        Assert.NotSame(staleWorld, data.World);
    }

    [Fact]
    public void Worker_thread_access_is_rejected()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ModDataStore();
        IModData data = store.ForMod("alpha");
        store.AttachWorld(temporary.Path);
        IModDataContainer world = data.World!;
        Exception? actual = null;
        var worker = new Thread(() =>
        {
            actual = Record.Exception(() => world.Set(new ResourceId("alpha:value"), [1]));
        });

        worker.Start();
        worker.Join();

        InvalidOperationException error = Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("main game thread", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saving_replaces_the_previous_file_and_leaves_no_temporary_files()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ModDataStore();
        IModData data = store.ForMod("alpha");
        store.AttachWorld(temporary.Path);
        data.World!.Set(new ResourceId("alpha:value"), [1]);
        store.SaveWorld();
        data.World.Set(new ResourceId("alpha:value"), [2]);
        store.SaveWorld();
        store.DetachWorld();

        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(temporary.Path, ModDataStore.DirectoryName),
            "*.tmp"));

        store.AttachWorld(temporary.Path);
        Assert.True(data.World!.TryGet(new ResourceId("alpha:value"), out ReadOnlyMemory<byte> restored));
        Assert.Equal(new byte[] { 2 }, restored.ToArray());
    }

    private static void WriteInOrder(string directory, bool reverse)
    {
        var store = new ModDataStore();
        IModData data = store.ForMod("alpha");
        store.AttachWorld(directory);
        IModDataContainer world = data.World!;
        IModDataContainer first = data.At(new ModBlockPosition(5, 4, 3))!;
        IModDataContainer second = data.At(new ModBlockPosition(-1, 2, 9))!;

        if (reverse)
        {
            second.Set(new ResourceId("alpha:c"), [3]);
            first.Set(new ResourceId("alpha:b"), [2]);
            world.Set(new ResourceId("alpha:z"), [26]);
            world.Set(new ResourceId("alpha:a"), [1]);
        }
        else
        {
            world.Set(new ResourceId("alpha:a"), [1]);
            world.Set(new ResourceId("alpha:z"), [26]);
            first.Set(new ResourceId("alpha:b"), [2]);
            second.Set(new ResourceId("alpha:c"), [3]);
        }

        store.SaveWorld();
    }

    private static string DataPath(string worldDirectory, string modId) => Path.Combine(
        worldDirectory,
        ModDataStore.DirectoryName,
        ModDataStore.FileNameForMod(modId));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Tesseris.ModDataStoreTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
