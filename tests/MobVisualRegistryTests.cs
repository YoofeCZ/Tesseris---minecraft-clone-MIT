using Tesseris.Game.World;
using Tesseris.Game.Entities;
using OpenTK.Mathematics;
using Xunit;

namespace Tesseris.Tests;

public sealed class MobVisualRegistryTests
{
    [Fact]
    public void Manifest_exposes_every_texture_without_forbidden_creeper_assets()
    {
        string assets = AssetsRoot();
        MobVisualRegistry registry = MobVisualRegistry.ReadManifest(
            Path.Combine(assets, "mobs", "mobs-redo-test-only.json"));

        Assert.True(registry.TextureNames.Count >= 70);
        Assert.DoesNotContain(registry.TextureNames, name =>
            name.Contains("creeper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("tree_monster6", StringComparison.OrdinalIgnoreCase));
        Assert.All(registry.TextureNames, name =>
            Assert.True(File.Exists(Path.Combine(assets, "textures", name + ".png")), name));
    }

    [Fact]
    public void Every_catalog_mob_builds_an_imported_visual_mesh()
    {
        string assets = AssetsRoot();
        MobVisualRegistry registry = MobVisualRegistry.ReadManifest(
            Path.Combine(assets, "mobs", "mobs-redo-test-only.json"));
        registry.LoadModels(assets, _ => 1);

        foreach (MobDefinition definition in MobDefinitions.All)
        {
            var entity = new AnimalEntity
            {
                Id = (long)definition.Kind,
                Kind = definition.Kind,
                DefinitionId = definition.Id,
                Position = Vector3.Zero,
                RenderPosition = Vector3.Zero,
                RenderYaw = 0f,
                VisualInitialized = true,
            };
            var mesh = new MeshBuffer();

            Assert.True(registry.Append(mesh, entity), definition.Id);
            Assert.False(mesh.IsEmpty, definition.Id);
        }
    }

    private static string AssetsRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !Directory.Exists(Path.Combine(current, "assets")))
            current = Directory.GetParent(current)?.FullName;
        return Path.Combine(current!, "assets");
    }
}
