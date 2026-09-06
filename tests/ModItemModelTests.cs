using Tesseris.Game.Blocks;
using Tesseris.Game.Content;
using Tesseris.Game.Items;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModItemModelTests
{
    private const float Half = 0.35f;

    [Fact]
    public void Namespaced_models_and_textures_from_different_packs_coexist()
    {
        using var temp = new TempDirectory();
        string alpha = temp.Directory("alpha");
        string beta = temp.Directory("beta");

        temp.Model("alpha", "models", "tools", "hammer.json", "items/hammer");
        temp.Png(16, 32, "alpha", "textures", "items", "hammer.png");
        temp.Model("beta", "models", "hammer.json", "items/hammer");
        temp.Png(32, 32, "beta", "textures", "items", "hammer.png");

        var catalog = new ContentCatalog(
        [
            new ContentSource("beta", beta, 20),
            new ContentSource("alpha", alpha, 10),
        ]);

        List<ItemModelFile> models = ItemModelFile.LoadAll(
            catalog,
            new ContentAssetResolver(catalog),
            Half);

        Assert.Equal(["alpha:tools/hammer", "beta:hammer"], models.Select(model => model.Name));
        Assert.Equal(["alpha:items/hammer#0", "alpha:items/hammer#1"], models[0].LayerNames());
        Assert.Equal(["beta:items/hammer#0"], models[1].LayerNames());
    }

    [Fact]
    public void Texture_with_explicit_namespace_is_preserved()
    {
        using var temp = new TempDirectory();
        string modelsRoot = temp.Directory("models-pack");
        string sharedRoot = temp.Directory("shared-pack");
        temp.Model("models-pack", "models", "wand.json", "shared:textures/wand");
        temp.Png(8, 16, "shared-pack", "textures", "textures", "wand.png");

        var catalog = new ContentCatalog(
        [
            new ContentSource("magic", modelsRoot),
            new ContentSource("shared", sharedRoot),
        ]);

        ItemModelFile model = Assert.Single(ItemModelFile.LoadAll(
            catalog,
            new ContentAssetResolver(catalog),
            Half));

        Assert.Equal("magic:wand", model.Name);
        Assert.Equal(["shared:textures/wand#0", "shared:textures/wand#1"], model.LayerNames());
    }

    [Fact]
    public void Model_without_texture_list_uses_its_namespaced_resource_id()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("pack");
        temp.ModelWithoutTextures("pack", "models", "tools", "mallet.json");
        temp.Png(16, 32, "pack", "textures", "tools", "mallet.png");
        var catalog = new ContentCatalog([new ContentSource("carpentry", root)]);

        ItemModelFile model = Assert.Single(ItemModelFile.LoadAll(
            catalog,
            new ContentAssetResolver(catalog),
            Half));

        Assert.Equal(["carpentry:tools/mallet#0", "carpentry:tools/mallet#1"], model.LayerNames());
    }

    [Fact]
    public void Unsafe_texture_reference_is_rejected()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("pack");
        temp.Model("pack", "models", "unsafe.json", "../outside");
        var catalog = new ContentCatalog([new ContentSource("safe", root)]);

        Assert.Throws<InvalidDataException>(() => ItemModelFile.LoadAll(
            catalog,
            new ContentAssetResolver(catalog),
            Half));
    }

    [Fact]
    public void Duplicate_model_id_across_roots_is_rejected()
    {
        using var temp = new TempDirectory();
        string first = temp.Directory("first");
        string second = temp.Directory("second");
        temp.Model("first", "models", "hammer.json", "items/first");
        temp.Model("second", "models", "hammer.json", "items/second");
        var catalog = new ContentCatalog(
        [
            new ContentSource("same", first),
            new ContentSource("same", second),
        ]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ItemModelFile.LoadAll(catalog, new ContentAssetResolver(catalog), Half));

        Assert.Contains("same:hammer", error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(first, "models", "hammer.json"), error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(second, "models", "hammer.json"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Modern_item_model_reference_receives_own_namespace()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("pack");
        temp.File(
            """
            {
              "name": "Hammer",
              "kind": "Tool",
              "model": "tools/hammer"
            }
            """,
            "pack", "items", "hammer.json");
        var catalog = new ContentCatalog([new ContentSource("smithing", root)]);
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

        ItemRegistry items = ItemRegistry.Create(blocks, catalog);
        ItemDefinition hammer = items.Definition(items.IndexOf("smithing:hammer"));

        Assert.Equal("smithing:tools/hammer", hammer.Model);
    }

    [Fact]
    public void Unsafe_modern_item_model_reference_is_rejected()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("pack");
        temp.File(
            """{ "name": "Bad", "kind": "Tool", "model": "../outside" }""",
            "pack", "items", "bad.json");
        var catalog = new ContentCatalog([new ContentSource("safe", root)]);
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

        Assert.Throws<InvalidDataException>(() => ItemRegistry.Create(blocks, catalog));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "tesseris-mod-item-model-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void File(string contents, params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, contents);
        }

        public void Model(string first, params string[] segments)
        {
            string texture = segments[^1];
            File(ModelJson(texture), new[] { first }.Concat(segments[..^1]).ToArray());
        }

        public void ModelWithoutTextures(params string[] segments) =>
            File(ModelJson(texture: null), segments);

        public void Png(int width, int height, params string[] segments)
        {
            byte[] header = new byte[24];
            header[16] = (byte)(width >> 24);
            header[17] = (byte)(width >> 16);
            header[18] = (byte)(width >> 8);
            header[19] = (byte)width;
            header[20] = (byte)(height >> 24);
            header[21] = (byte)(height >> 16);
            header[22] = (byte)(height >> 8);
            header[23] = (byte)height;

            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, header);
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);

        private static string ModelJson(string? texture) => $$"""
            {
              {{(texture is null ? "" : $"\"textures\": {{ \"0\": \"{texture}\" }},")}}
              "elements": [
                {
                  "from": [0, 0, 0],
                  "to": [16, 16, 16],
                  "faces": {
                    "north": { "uv": [0, 0, 16, 8], "texture": "#0" }
                  }
                }
              ]
            }
            """;
    }
}
