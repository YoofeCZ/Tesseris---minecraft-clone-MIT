using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class LadderTests
{
    [Theory]
    [InlineData(PieceMask.DoorSouth, 0f, 0f, 0f, 1f, 1f, 0.0625f)]
    [InlineData(PieceMask.DoorNorth, 0f, 0f, 0.9375f, 1f, 1f, 1f)]
    [InlineData(PieceMask.DoorEast, 0f, 0f, 0f, 0.0625f, 1f, 1f)]
    [InlineData(PieceMask.DoorWest, 0.9375f, 0f, 0f, 1f, 1f, 1f)]
    public void Ladder_is_a_thin_wall_panel(
        int facing, float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        Aabb box = PieceMask.LadderCollider(PieceMask.LadderState(facing));
        Assert.Equal(new Vector3(minX, minY, minZ), box.Min);
        Assert.Equal(new Vector3(maxX, maxY, maxZ), box.Max);
    }

    [Theory]
    [InlineData(1, 0, 0, PieceMask.DoorEast)]
    [InlineData(-1, 0, 0, PieceMask.DoorWest)]
    [InlineData(0, 0, 1, PieceMask.DoorSouth)]
    [InlineData(0, 0, -1, PieceMask.DoorNorth)]
    public void Placement_normal_selects_the_supporting_wall(
        int x, int y, int z, int expectedFacing)
    {
        byte state = PieceMask.LadderStateFromNormal(new Vector3i(x, y, z));
        Assert.Equal(expectedFacing, PieceMask.LadderFacing(state));
    }

    [Theory]
    [InlineData(BlockShape.Ladder, 192)]
    [InlineData(BlockShape.Cube, PieceMask.PanelAlongX)]
    public void Rigid_cutout_panels_carry_the_static_wind_flag(BlockShape shape, byte state)
    {
        BlockRegistry registry = BlockRegistry.Create(
        [
            new BlockDefinition
            {
                Id = "test:panel",
                Texture = "panel",
                Shape = shape,
                Pieces = state,
                Opaque = false,
                Cutout = true,
            },
        ]);
        ushort[] volume = new ushort[ChunkMesher.PaddedVolume];
        byte[] pieces = new byte[ChunkMesher.PaddedVolume];
        int index = ChunkMesher.PaddedIndex(5, 5, 5);
        volume[index] = registry.IndexOf("test:panel");
        pieces[index] = state;
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
