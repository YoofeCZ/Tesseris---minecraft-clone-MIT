using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class BuildingPartShapeTests
{
    [Fact]
    public void Slab_a_schody_se_v_ruce_nekresli_jako_plna_kostka()
    {
        static (Vector3 Min, Vector3 Max) Bounds(MeshBuffer mesh)
        {
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            ReadOnlySpan<float> vertices = mesh.Vertices;
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                int offset = vertex * MeshBuffer.FloatsPerVertex;
                var point = new Vector3(vertices[offset], vertices[offset + 1], vertices[offset + 2]);
                min = Vector3.ComponentMin(min, point);
                max = Vector3.ComponentMax(max, point);
            }
            return (min, max);
        }

        static Vector3 Identity(Vector3 point) => point;

        var slab = new MeshBuffer();
        ChunkRenderer.AddHeldPieces(slab, Identity, 0f, PieceMask.SlabBottom, 1f);
        (Vector3 slabMin, Vector3 slabMax) = Bounds(slab);
        Assert.Equal(-1f, slabMin.Y, 4);
        Assert.Equal(0f, slabMax.Y, 4);

        var stairs = new MeshBuffer();
        byte stairPieces = PieceMask.StairOccupancy(PieceMask.StairState(
            PieceMask.DoorNorth, upsideDown: false, StairCornerShape.Straight));
        ChunkRenderer.AddHeldPieces(stairs, Identity, 0f, stairPieces, 1f);
        (Vector3 stairMin, Vector3 stairMax) = Bounds(stairs);
        Assert.Equal(-1f, stairMin.Y, 4);
        Assert.Equal(1f, stairMax.Y, 4);
        Assert.True(stairs.VertexCount > 24, "Schody nesmi mit geometrii jedine kostky.");
    }

    [Theory]
    [InlineData(PieceMask.DoorSouth, false, 0xCF)]
    [InlineData(PieceMask.DoorEast, false, 0xAF)]
    [InlineData(PieceMask.DoorNorth, false, 0x3F)]
    [InlineData(PieceMask.DoorWest, false, 0x5F)]
    [InlineData(PieceMask.DoorSouth, true, 0xFC)]
    [InlineData(PieceMask.DoorEast, true, 0xFA)]
    [InlineData(PieceMask.DoorNorth, true, 0xF3)]
    [InlineData(PieceMask.DoorWest, true, 0xF5)]
    public void Schody_maji_ctyri_smery_a_spodni_i_obracenou_polohu(
        int facing, bool upsideDown, byte expectedPieces)
    {
        byte state = PieceMask.StairState(facing, upsideDown, StairCornerShape.Straight);

        Assert.Equal(facing, PieceMask.StairFacing(state));
        Assert.Equal(upsideDown, PieceMask.StairUpsideDown(state));
        Assert.Equal(expectedPieces, PieceMask.StairOccupancy(state));
    }

    [Theory]
    [InlineData(StairCornerShape.Straight, 6)]
    [InlineData(StairCornerShape.InnerLeft, 7)]
    [InlineData(StairCornerShape.InnerRight, 7)]
    [InlineData(StairCornerShape.OuterLeft, 5)]
    [InlineData(StairCornerShape.OuterRight, 5)]
    public void Rohy_schodu_maji_spravny_pocet_pulbloku(
        StairCornerShape corner, int expectedPieces)
    {
        byte state = PieceMask.StairState(PieceMask.DoorNorth, upsideDown: false, corner);

        Assert.Equal(corner, PieceMask.StairCorner(state));
        Assert.Equal(expectedPieces, PieceMask.Colliders(BlockShape.Stairs, state).Count());
    }

    [Fact]
    public void Stare_masky_schodu_zustavaji_citelne_a_nove_bloky_dostanou_tvar_schodu()
    {
        Assert.Equal((byte)0xAF, PieceMask.StairOccupancy(0xAF));
        Assert.Equal(PieceMask.DoorEast, PieceMask.StairFacing(0xAF));

        BlockRegistry blocks = BlockRegistry.Create(
        [
            new BlockDefinition
            {
                Id = "test:marble_stairs",
                Texture = "marble",
                Pieces = 0x3F,
            },
        ]);

        Assert.Equal(BlockShape.Stairs, blocks.ShapeOf(blocks.IndexOf("test:marble_stairs")));
    }

    [Theory]
    [InlineData(PieceMask.PanelAlongX, 1f, PieceMask.PanelThickness)]
    [InlineData(PieceMask.PanelAlongZ, PieceMask.PanelThickness, 1f)]
    public void Dvere_jsou_tenky_panel(byte mask, float widthX, float widthZ)
    {
        Aabb panel = Assert.Single(PieceMask.Colliders(BlockShape.Door, mask));

        Assert.Equal(widthX, panel.Max.X - panel.Min.X, 4);
        Assert.Equal(1f, panel.Max.Y - panel.Min.Y, 4);
        Assert.Equal(widthZ, panel.Max.Z - panel.Min.Z, 4);
    }

    [Theory]
    [InlineData(PieceMask.PanelAlongX, 1f, PieceMask.PanelThickness)]
    [InlineData(PieceMask.PanelAlongZ, PieceMask.PanelThickness, 1f)]
    public void Spolecny_obrys_dveri_nema_hranu_mezi_polovinami(
        byte mask, float widthX, float widthZ)
    {
        Aabb outline = PieceMask.DoorCollider(mask, height: 2f);

        Assert.Equal(widthX, outline.Max.X - outline.Min.X, 4);
        Assert.Equal(2f, outline.Max.Y - outline.Min.Y, 4);
        Assert.Equal(widthZ, outline.Max.Z - outline.Min.Z, 4);
    }

    [Theory]
    [InlineData(1f, 0f, PieceMask.DoorEast)]
    [InlineData(-1f, 0f, PieceMask.DoorWest)]
    [InlineData(0f, 1f, PieceMask.DoorSouth)]
    [InlineData(0f, -1f, PieceMask.DoorNorth)]
    [InlineData(0.8f, 0.2f, PieceMask.DoorEast)]
    [InlineData(0.2f, -0.8f, PieceMask.DoorNorth)]
    public void Smer_dveri_se_bere_z_pohledu_hrace(float x, float z, int expected)
    {
        Assert.Equal(expected, PieceMask.DoorFacingFromDirection(x, z));
    }

    [Theory]
    [InlineData(PieceMask.DoorSouth)]
    [InlineData(PieceMask.DoorEast)]
    [InlineData(PieceMask.DoorNorth)]
    [InlineData(PieceMask.DoorWest)]
    public void Otevreni_dveri_zachova_smer_i_stranu_pantu(int facing)
    {
        foreach (bool hingeRight in new[] { false, true })
        {
            byte closed = PieceMask.DoorState(facing, hingeRight, open: false);
            byte open = PieceMask.ToggleDoor(closed);

            Assert.True(PieceMask.DoorIsOpen(open));
            Assert.Equal(facing, PieceMask.DoorFacing(open));
            Assert.Equal(hingeRight, PieceMask.DoorHingeRight(open));
            Assert.Equal(closed, PieceMask.ToggleDoor(open));
        }
    }

    [Theory]
    [InlineData(PieceMask.DoorSouth, 1f, PieceMask.PanelThickness, 0f, 0f)]
    [InlineData(PieceMask.DoorEast, PieceMask.PanelThickness, 1f, 0f, 0f)]
    [InlineData(PieceMask.DoorNorth, 1f, PieceMask.PanelThickness, 0f, 1f - PieceMask.PanelThickness)]
    [InlineData(PieceMask.DoorWest, PieceMask.PanelThickness, 1f, 1f - PieceMask.PanelThickness, 0f)]
    public void Zavrene_dvere_lezi_na_hrane_proti_hraci(
        int facing, float widthX, float widthZ, float minX, float minZ)
    {
        Aabb panel = PieceMask.DoorCollider(
            PieceMask.DoorState(facing, hingeRight: false, open: false));

        Assert.Equal(widthX, panel.Max.X - panel.Min.X, 4);
        Assert.Equal(widthZ, panel.Max.Z - panel.Min.Z, 4);
        Assert.Equal(minX, panel.Min.X, 4);
        Assert.Equal(minZ, panel.Min.Z, 4);
    }

    [Fact]
    public void Animacni_stav_zachova_finalni_kolizi_dveri_i_trapdooru()
    {
        byte door = PieceMask.DoorState(
            PieceMask.DoorNorth, hingeRight: true, open: true);
        byte animatedDoor = PieceMask.DoorAnimatingState(door);
        Assert.True(PieceMask.DoorIsAnimating(animatedDoor));
        Assert.Equal(door, PieceMask.DoorFinalState(animatedDoor));
        Assert.Equal(PieceMask.DoorCollider(door), PieceMask.DoorCollider(animatedDoor));

        byte trapdoor = PieceMask.TrapdoorOpenTopAlongZ;
        byte animatedTrapdoor = PieceMask.TrapdoorAnimatingState(trapdoor);
        Assert.True(PieceMask.TrapdoorIsAnimating(animatedTrapdoor));
        Assert.Equal(trapdoor, PieceMask.TrapdoorFinalState(animatedTrapdoor));
        Assert.Equal(
            Assert.Single(PieceMask.Colliders(BlockShape.Trapdoor, trapdoor)),
            Assert.Single(PieceMask.Colliders(BlockShape.Trapdoor, animatedTrapdoor)));
    }

    [Fact]
    public void Animovane_dvere_se_v_polovine_opravdu_otoci_kolem_pantu()
    {
        BlockRegistry blocks = BlockRegistry.Create(
        [
            new BlockDefinition { Id = "test:door", Texture = "door", Shape = BlockShape.Door },
            new BlockDefinition { Id = "test:door_top", Texture = "door_top", Shape = BlockShape.Door },
        ]);
        ushort lower = blocks.IndexOf("test:door");
        ushort upper = blocks.IndexOf("test:door_top");
        byte closed = PieceMask.DoorState(
            PieceMask.DoorSouth, hingeRight: false, open: false);
        byte open = PieceMask.DoorState(
            PieceMask.DoorSouth, hingeRight: false, open: true);
        var mesh = new MeshBuffer();

        BuildingPartAnimationMesher.Build(
            mesh,
            blocks,
            [new BuildingPartAnimation(
                Vector3i.Zero, lower, upper, BlockShape.Door, closed, open, 0.5f)]);

        Assert.Equal(48, mesh.VertexCount);
        ReadOnlySpan<float> vertices = mesh.Vertices;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            minX = MathF.Min(minX, vertices[i * MeshBuffer.FloatsPerVertex]);
            maxX = MathF.Max(maxX, vertices[i * MeshBuffer.FloatsPerVertex]);
            minZ = MathF.Min(minZ, vertices[(i * MeshBuffer.FloatsPerVertex) + 2]);
            maxZ = MathF.Max(maxZ, vertices[(i * MeshBuffer.FloatsPerVertex) + 2]);
        }

        // V půlce zabírá panel obě vodorovné osy. Kdyby pouze přeskočil mezi dvěma
        // AABB stavy, jedna z těchto délek by pořád byla jen 1/16 bloku.
        Assert.True(maxX - minX > 0.6f);
        Assert.True(maxZ - minZ > 0.6f);
    }

    [Theory]
    [InlineData(PieceMask.SlabBottom, 0f, PieceMask.PanelThickness)]
    [InlineData(PieceMask.SlabTop, 1f - PieceMask.PanelThickness, 1f)]
    public void Trapdoor_ma_tenkou_vodorovnou_kolizi(byte mask, float minY, float maxY)
    {
        Aabb panel = Assert.Single(PieceMask.Colliders(BlockShape.Trapdoor, mask));

        Assert.Equal(minY, panel.Min.Y, 4);
        Assert.Equal(maxY, panel.Max.Y, 4);
    }

    [Theory]
    [InlineData(PieceMask.TrapdoorBottomAlongX)]
    [InlineData(PieceMask.TrapdoorBottomAlongZ)]
    [InlineData(PieceMask.TrapdoorTopAlongX)]
    [InlineData(PieceMask.TrapdoorTopAlongZ)]
    public void Trapdoor_si_po_otevreni_a_zavreni_pamatuje_orientaci(byte closed)
    {
        byte open = PieceMask.ToggleTrapdoor(closed);

        Assert.True(PieceMask.TrapdoorIsOpen(open));
        Assert.Equal(PieceMask.TrapdoorIsAlongZ(closed), PieceMask.TrapdoorIsAlongZ(open));
        Assert.Equal(PieceMask.TrapdoorIsTop(closed), PieceMask.TrapdoorIsTop(open));
        Assert.Equal(closed, PieceMask.ToggleTrapdoor(open));
    }

    [Fact]
    public void Dvere_a_trapdoor_se_v_ruce_nekresli_jako_kostka()
    {
        BlockRegistry blocks = BlockRegistry.Create(
        [
            new BlockDefinition { Id = "test:door", Texture = "door", Shape = BlockShape.Door },
            new BlockDefinition { Id = "test:trapdoor", Texture = "trapdoor", Shape = BlockShape.Trapdoor },
        ]);
        ItemRegistry items = ItemRegistry.Create(blocks, itemDirectory: null);

        Assert.True(items.IsFlat(items.IndexOf("test:door")));
        Assert.True(items.IsFlat(items.IndexOf("test:trapdoor")));
    }

    [Fact]
    public void Vystup_na_slab_nahlasi_vysku_pro_vyhlazeni_kamery()
    {
        BlockRegistry blocks = BlockRegistry.Create(
        [
            new BlockDefinition { Id = "test:stone", Texture = "stone" },
            new BlockDefinition
            {
                Id = "test:slab",
                Texture = "stone",
                Pieces = PieceMask.SlabBottom,
            },
        ]);
        var world = new VoxelWorld(blocks);
        ushort stone = blocks.IndexOf("test:stone");
        ushort slab = blocks.IndexOf("test:slab");

        for (int x = -2; x <= 8; x++)
        for (int z = -2; z <= 2; z++)
            world.SetBlock(x, 0, z, stone);

        for (int x = 3; x <= 8; x++)
        {
            world.SetBlock(x, 1, 0, slab);
            world.SetPieces(x, 1, 0, PieceMask.SlabBottom);
        }

        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        float reportedRise = 0f;
        for (int frame = 0; frame < 90; frame++)
        {
            player.Update(world, Vector3.UnitX, jump: false, sprint: false, 0f, 1f / 60f);
            reportedRise = MathF.Max(reportedRise, player.StepRiseThisUpdate);
        }

        Assert.InRange(reportedRise, 0.49f, PlayerController.StepHeight);
        Assert.True(player.Position.Y >= 1.49f);
    }
}
