using Tesseris.Game.Content;
using Xunit;

namespace Tesseris.Tests;

public sealed class ContentCatalogTests
{
    [Fact]
    public void Files_are_namespaced_recursive_and_deterministic()
    {
        using var temp = new TempDirectory();
        string late = temp.Directory("late", "blocks");
        string early = temp.Directory("early", "blocks", "ores");
        File.WriteAllText(Path.Combine(late, "zinc.json"), "{}");
        File.WriteAllText(Path.Combine(early, "copper.json"), "{}");

        var catalog = new ContentCatalog(
        [
            new ContentSource("late", Path.Combine(temp.Root, "late"), 20),
            new ContentSource("early", Path.Combine(temp.Root, "early"), 10),
        ]);

        ContentFile[] files = [.. catalog.GetFiles("blocks")];

        Assert.Equal(["early:ores/copper", "late:zinc"], files.Select(file => file.Id.ToString()));
    }

    [Fact]
    public void Duplicate_resource_id_fails_with_both_paths()
    {
        using var temp = new TempDirectory();
        string first = temp.Directory("first", "blocks");
        string second = temp.Directory("second", "blocks");
        File.WriteAllText(Path.Combine(first, "ore.json"), "{}");
        File.WriteAllText(Path.Combine(second, "ore.json"), "{}");

        var catalog = new ContentCatalog(
        [
            new ContentSource("same", Path.Combine(temp.Root, "first")),
            new ContentSource("same", Path.Combine(temp.Root, "second")),
        ]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => catalog.GetFiles("blocks"));
        Assert.Contains("same:ore", error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(first, "ore.json"), error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(second, "ore.json"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_relative_paths_cannot_escape_root()
    {
        using var temp = new TempDirectory();
        var source = new ContentSource("safe", temp.Directory("pack"));
        var catalog = new ContentCatalog([source]);

        Assert.Throws<InvalidDataException>(() => catalog.ResolvePath(source, "..", "outside.json"));
        Assert.Throws<InvalidDataException>(() => catalog.ResolvePath(source, Path.GetPathRoot(temp.Root)!));
    }

    [Theory]
    [InlineData("missing-colon")]
    [InlineData("UPPER:path")]
    [InlineData("good:../escape")]
    [InlineData("good:/rooted")]
    [InlineData("good:two//parts")]
    public void Resource_ids_reject_invalid_or_unsafe_values(string value)
    {
        Assert.ThrowsAny<Exception>(() => ResourceId.Parse(value));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "tesseris-content-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
