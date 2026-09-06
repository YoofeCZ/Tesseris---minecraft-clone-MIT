using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class StructureCreatorTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone" },
        new BlockDefinition
        {
            Id = "tesseris:chest", Texture = "chest_side", Shape = BlockShape.Chest,
            Pieces = PieceMask.ChestClosedState, Opaque = false, Cutout = true,
        },
    ]);

    [Fact]
    public void Capture_save_load_and_place_preserve_blocks_pieces_and_loot_markers()
    {
        BlockRegistry registry = Registry();
        var source = new VoxelWorld(registry);
        ushort stone = registry.IndexOf("test:stone");
        ushort chest = registry.IndexOf("tesseris:chest");
        source.SetBlock(10, 20, 30, stone);
        source.SetBlock(11, 20, 30, chest);
        source.SetPieces(11, 20, 30, PieceMask.ChestClosedState);

        StructureTemplate template = StructureTemplate.Capture(
            source, new Vector3i(10, 20, 30), new Vector3i(11, 21, 31), "test_house");
        string directory = Path.Combine(Path.GetTempPath(), "tesseris-structures-" + Guid.NewGuid().ToString("N"));
        try
        {
            StructureTemplateStore.Save(template, directory);
            StructureTemplate loaded = StructureTemplateStore.Load("test_house", directory);
            var target = new VoxelWorld(registry);
            int count = loaded.Place(target, new Vector3i(100, 40, 100));

            Assert.Equal(2, count);
            Assert.Single(loaded.Loot);
            Assert.Equal(stone, target.GetBlock(99, 40, 99));
            Assert.Equal(chest, target.GetBlock(100, 40, 99));
            Assert.Equal(PieceMask.ChestClosedState, target.GetPieces(100, 40, 99));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Chest_has_reduced_collision_and_animation_states()
    {
        var colliders = PieceMask.Colliders(BlockShape.Chest, PieceMask.ChestClosedState).ToArray();
        Assert.Single(colliders);
        Assert.Equal(14f / 16f, colliders[0].Max.Y);
        Assert.True(PieceMask.ChestIsAnimating(PieceMask.ChestAnimatingState(open: true)));
        Assert.True(PieceMask.ChestIsOpen(PieceMask.ChestAnimatingOpenState));
    }

    [Fact]
    public void Torch_is_rigid_cutout_geometry_not_a_wind_plant()
    {
        BlockRegistry registry = BlockRegistry.Create(
        [
            new BlockDefinition
            {
                Id = "test:torch", Texture = "torch", Shape = BlockShape.Torch,
                Opaque = false, Cutout = true, Solid = false,
            },
        ]);
        ushort[] volume = new ushort[ChunkMesher.PaddedVolume];
        byte[] pieces = new byte[ChunkMesher.PaddedVolume];
        int index = ChunkMesher.PaddedIndex(4, 4, 4);
        volume[index] = registry.IndexOf("test:torch");
        pieces[index] = PieceMask.Full;
        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var cutout = new MeshBuffer();

        ChunkMesher.Build(volume, registry, opaque, transparent, default, cutout, pieces: pieces);

        Assert.True(cutout.VertexCount > 0);
        for (int vertex = 0; vertex < cutout.VertexCount; vertex++)
        {
            float shade = cutout.Vertices[(vertex * MeshBuffer.FloatsPerVertex) + 6];
            Assert.Equal(3f, MathF.Floor(shade / 256f));
        }
    }
}
