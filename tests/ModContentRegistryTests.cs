using Tesseris.Game.Blocks;
using Tesseris.Game.Content;
using Tesseris.Game.Items;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModContentRegistryTests
{
    [Fact]
    public void Unqualified_mod_content_uses_source_namespace_and_recipes_resolve_locally()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("coppermod");
        temp.Write("coppermod", "blocks", "copper.json", """
            { "texture": "copper", "hardness": 3 }
            """);
        temp.Write("coppermod", "items", "copper.json", """
            { "name": "Polished copper", "kind": "Block", "block": "copper", "burnSeconds": 2 }
            """);
        temp.Write("coppermod", "items", "hammer.json", """
            {
              "name": "Hammer",
              "kind": "Tool",
              "maxStack": 64,
              "texture": "tools/hammer",
              "iconTexture": "icons/hammer"
            }
            """);
        temp.Write("coppermod", "recipes", "hammer.json", """
            {
              "output": "hammer",
              "inputs": [{ "item": "copper", "count": 2 }]
            }
            """);

        var catalog = new ContentCatalog([new ContentSource("coppermod", root)]);
        BlockRegistry blocks = BlockRegistry.Load(catalog);
        ItemRegistry items = ItemRegistry.Create(blocks, catalog);
        RecipeBook recipes = RecipeBook.Load(items, catalog);

        ushort copperBlock = blocks.IndexOf("coppermod:copper");
        int copperItem = items.IndexOf("coppermod:copper");
        int hammer = items.IndexOf("coppermod:hammer");

        Assert.NotEqual(ItemRegistry.Nothing, hammer);
        Assert.Equal("Polished copper", items.Definition(copperItem).Name);
        Assert.Equal(copperBlock, items.BlockForItem(copperItem));
        Assert.Equal(1, items.Definition(hammer).MaxStack);
        Assert.Contains("coppermod:copper", blocks.TextureNames);
        Assert.Equal("coppermod:tools/hammer", items.Definition(hammer).Texture);
        Assert.Equal("coppermod:icons/hammer", items.Definition(hammer).IconTexture);
        Assert.Contains("coppermod:icons/hammer", items.IconTextures());

        RecipeBook.Entry recipe = Assert.Single(recipes.Crafting);
        Assert.Equal(hammer, recipe.Output);
        Assert.Equal((copperItem, 2), Assert.Single(recipe.Inputs));
    }

    [Fact]
    public void Explicit_items_override_auto_block_items_but_duplicate_explicit_items_fail()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("mod");
        temp.Write("mod", "blocks", "ore.json", "{ \"texture\": \"ore\" }");
        temp.Write("mod", "items", "override.json", """
            { "id": "ore", "name": "Ore override", "kind": "Block", "block": "ore" }
            """);

        var catalog = new ContentCatalog([new ContentSource("mod", root)]);
        BlockRegistry blocks = BlockRegistry.Load(catalog);
        ItemRegistry items = ItemRegistry.Create(blocks, catalog);
        Assert.Equal("Ore override", items.Definition(items.IndexOf("mod:ore")).Name);

        temp.Write("mod", "items", "duplicate.json", """
            { "id": "ore", "name": "Duplicate", "kind": "Material" }
            """);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ItemRegistry.Create(blocks, new ContentCatalog([new ContentSource("mod", root)])));
        Assert.Contains("mod:ore", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Modern_source_cannot_define_another_namespace_but_legacy_overload_preserves_it()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("modern");
        temp.Write("modern", "blocks", "stone.json", """
            { "id": "other:stone", "texture": "stone" }
            """);

        var catalog = new ContentCatalog([new ContentSource("modern", root)]);
        Assert.Throws<InvalidDataException>(() => BlockRegistry.Load(catalog));

        string legacy = temp.Directory("legacy-blocks");
        File.WriteAllText(Path.Combine(legacy, "stone.json"), """
            { "id": "other:stone", "texture": "stone" }
            """);

        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(legacy);
        Assert.NotEqual(BlockRegistry.Air, blocks.IndexOf("other:stone"));
    }

    [Fact]
    public void Block_model_path_cannot_escape_its_content_source()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("unsafe");
        temp.Write("unsafe", "blocks", "clutter.json", """
            {
              "texture": "clutter",
              "shape": "GroundClutter",
              "solid": false,
              "model": "../../../outside"
            }
            """);

        var catalog = new ContentCatalog([new ContentSource("unsafe", root)]);
        Assert.Throws<InvalidDataException>(() => BlockRegistry.Load(catalog));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "tesseris-mod-content-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Write(string pack, string category, string name, string json)
        {
            string directory = Directory(pack, category);
            File.WriteAllText(Path.Combine(directory, name), json);
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
