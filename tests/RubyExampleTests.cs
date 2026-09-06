using Tesseris.Engine.Rendering;
using Tesseris.Game.Blocks;
using Tesseris.Game.Content;
using Tesseris.Game.Items;
using Tesseris.Game.UI;
using Xunit;

namespace Tesseris.Tests;

public sealed class RubyExampleTests
{
    [Fact]
    public void Ruby_content_is_present_in_game_registries_with_distinct_icons()
    {
        string root = FindRepositoryRoot();
        var catalog = new ContentCatalog(
        [
            new ContentSource("tesseris", Path.Combine(root, "assets"), LoadOrder: 0, LegacyFlat: true),
            new ContentSource("ruby", Path.Combine(root, "examples", "RubyWorldgenMod", "content"), LoadOrder: 1),
        ]);

        BlockRegistry blocks = BlockRegistry.Load(catalog);
        ItemRegistry items = ItemRegistry.Create(blocks, catalog);

        ushort ore = blocks.IndexOf("ruby:ruby_ore");
        int oreItem = items.IndexOf("ruby:ruby_ore");
        int ruby = items.IndexOf("ruby:ruby");
        int pickaxe = items.IndexOf("ruby:ruby_pickaxe");

        Assert.NotEqual(BlockRegistry.Air, ore);
        Assert.NotEqual(ItemRegistry.Nothing, oreItem);
        Assert.NotEqual(ItemRegistry.Nothing, ruby);
        Assert.NotEqual(ItemRegistry.Nothing, pickaxe);
        Assert.Equal("ruby:ruby", items.Definition(ruby).IconTexture);
        Assert.Equal("ruby:ruby_pickaxe", items.Definition(pickaxe).IconTexture);

        var creative = new InventoryScreen(items);
        Assert.Equal(
            ["ruby:ruby", "ruby:ruby_ore", "ruby:ruby_pickaxe"],
            Enumerable.Range(0, 3)
                .Select(position => items.Definition(creative.CreativeItemAt(position)).Id)
                .ToArray());

        AssertRedTexture("ruby:ruby_ore");
        AssertRedTexture("ruby:ruby");
        AssertRedTexture("ruby:ruby_pickaxe");
    }

    private static void AssertRedTexture(string name)
    {
        var pixels = new byte[TextureArray.ArtSize * TextureArray.ArtSize * 4];
        TextureArray.GenerateTile(name, pixels);
        Assert.Contains(
            Enumerable.Range(0, pixels.Length / 4),
            index => pixels[(index * 4) + 3] > 0
                     && pixels[index * 4] > pixels[(index * 4) + 1] * 2
                     && pixels[index * 4] > pixels[(index * 4) + 2]);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesseris.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Tesseris.sln was not found.");
    }
}
