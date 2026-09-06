using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Entities;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class MobCatalogTests
{
    [Fact]
    public void Catalog_contains_complete_audited_packs_without_creeper_or_exploding_attack()
    {
        string[] expected = [
            "mobs_animal:bee", "mobs_animal:bunny", "mobs_animal:chicken", "mobs_animal:cow",
            "mobs_animal:kitten", "mobs_animal:panda", "mobs_animal:penguin", "mobs_animal:rat",
            "mobs_animal:pumba", "mobs_animal:sheep_black", "mobs_animal:sheep_blue",
            "mobs_animal:sheep_brown", "mobs_animal:sheep_cyan", "mobs_animal:sheep_dark_green",
            "mobs_animal:sheep_dark_grey", "mobs_animal:sheep_green", "mobs_animal:sheep_grey",
            "mobs_animal:sheep_magenta", "mobs_animal:sheep_orange", "mobs_animal:sheep_pink",
            "mobs_animal:sheep_red", "mobs_animal:sheep_violet", "mobs_animal:sheep_yellow",
            "mobs_monster:dirt_monster", "mobs_monster:dungeon_master", "mobs_monster:fire_spirit",
            "mobs_monster:land_guard", "mobs_monster:lava_flan", "mobs_monster:obsidian_flan",
            "mobs_monster:mese_monster", "mobs_monster:oerkki", "mobs_monster:sand_monster",
            "mobs_monster:spider", "mobs_monster:stone_monster", "mobs_monster:tree_monster"];

        foreach (string id in expected)
            Assert.True(MobDefinitions.TryFor(id, out _), id);
        Assert.DoesNotContain(MobDefinitions.All, value =>
            value.Id.Contains("creeper", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MobDefinitions.All.Count, MobDefinitions.All.Select(value => value.Kind).Distinct().Count());
        Assert.All(MobDefinitions.All, value => Assert.False(string.IsNullOrWhiteSpace(value.VisualId)));
    }

    [Fact]
    public void Legacy_byte_assignments_are_stable()
    {
        Assert.Equal(1, (byte)AnimalKind.Sheep);
        Assert.Equal(2, (byte)AnimalKind.Deer);
        Assert.Equal(3, (byte)AnimalKind.Wolf);
    }

    [Fact]
    public void Anm4_roundtrip_preserves_canonical_definition_id()
    {
        BlockRegistry registry = BlockRegistry.LoadFromDirectory(Path.Combine(AssetsRoot(), "blocks"));
        var terrain = new TerrainGenerator(registry, 81);
        string directory = Path.Combine(Path.GetTempPath(), $"tesseris-mob-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, AnimalPopulation.FileName);
            var original = new AnimalPopulation(registry, terrain, 81);
            original.AddForTest("mobs_monster:spider", new Vector3(4.5f, 9f, 2.5f), 19);
            original.Save(path);

            var restored = new AnimalPopulation(registry, terrain, 81);
            Assert.Equal(1, restored.Load(path));
            AnimalEntity spider = Assert.Single(restored.Animals);
            Assert.Equal(AnimalKind.Spider, spider.Kind);
            Assert.Equal("mobs_monster:spider", spider.DefinitionId);
            Assert.Equal(MobDefinitions.For(spider).MaximumHealth, spider.Health);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string AssetsRoot()
    {
        string current = AppContext.BaseDirectory;
        while (current is not null && !Directory.Exists(Path.Combine(current, "assets")))
            current = Directory.GetParent(current)?.FullName!;
        return Path.Combine(current!, "assets");
    }
}
