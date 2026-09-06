using Tesseris.Game.Content;
using Xunit;

namespace Tesseris.Tests;

public sealed class ContentAssetResolverTests
{
    [Fact]
    public void Namespaced_texture_resolves_inside_owning_source_and_strips_band_suffix()
    {
        using var temp = new TempDirectory();
        string pack = temp.Directory("coppermod");
        string texture = temp.File("coppermod", "textures", "blocks", "ore.png");
        var catalog = new ContentCatalog([new ContentSource("coppermod", pack)]);
        var resolver = new ContentAssetResolver(catalog);

        Assert.Equal(texture, resolver.ResolveTexturePath("coppermod:blocks/ore"));
        Assert.Equal(texture, resolver.ResolveTexturePath("coppermod:blocks/ore#3"));
    }

    [Fact]
    public void Missing_texture_returns_safe_candidate_but_unknown_namespace_returns_null()
    {
        using var temp = new TempDirectory();
        string pack = temp.Directory("pack");
        var resolver = new ContentAssetResolver(
            new ContentCatalog([new ContentSource("pack", pack)]));

        Assert.Equal(
            Path.Combine(pack, "textures", "missing.png"),
            resolver.ResolveTexturePath("pack:missing"));
        Assert.Null(resolver.ResolveTexturePath("unknown:missing"));
    }

    [Fact]
    public void Bare_legacy_texture_uses_legacy_root_and_unsafe_paths_are_rejected()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("vanilla");
        var legacy = new ContentSource("tesseris", root, LegacyFlat: true);
        var resolver = new ContentAssetResolver(new ContentCatalog([legacy]));

        Assert.Equal(
            Path.Combine(root, "textures", "stone.png"),
            resolver.ResolveTexturePath("stone"));
        Assert.Throws<InvalidDataException>(() => resolver.ResolveTexturePath("../outside"));
        Assert.Throws<InvalidDataException>(() => resolver.ResolveTexturePath("tesseris:../outside"));
        Assert.Throws<InvalidDataException>(() => resolver.ResolveTexturePath("tesseris:stone#bad"));
    }

    [Fact]
    public void Explicit_legacy_source_also_resolves_its_namespaced_references()
    {
        using var temp = new TempDirectory();
        string modRoot = temp.Directory("mod");
        string vanillaRoot = temp.Directory("vanilla");
        var catalog = new ContentCatalog([new ContentSource("mod", modRoot)]);
        var resolver = new ContentAssetResolver(
            catalog,
            new ContentSource("tesseris", vanillaRoot, LegacyFlat: true));

        string expected = Path.Combine(vanillaRoot, "textures", "stone.png");
        Assert.Equal(expected, resolver.ResolveTexturePath("stone"));
        Assert.Equal(expected, resolver.ResolveTexturePath("tesseris:stone"));
    }

    [Fact]
    public void Same_namespaced_texture_in_two_roots_is_a_collision()
    {
        using var temp = new TempDirectory();
        string first = temp.Directory("first");
        string second = temp.Directory("second");
        temp.File("first", "textures", "ore.png");
        temp.File("second", "textures", "ore.png");
        var resolver = new ContentAssetResolver(new ContentCatalog(
        [
            new ContentSource("same", first),
            new ContentSource("same", second),
        ]));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            resolver.ResolveTexturePath("same:ore"));

        Assert.Contains(first, error.Message, StringComparison.Ordinal);
        Assert.Contains(second, error.Message, StringComparison.Ordinal);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "tesseris-content-asset-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, [0]);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
