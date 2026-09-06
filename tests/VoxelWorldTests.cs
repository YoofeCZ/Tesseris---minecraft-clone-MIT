using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy světa jako kontejneru chunků. Nejcennější je příprava odsazeného objemu —
/// když se lem nenaplní ze sousedů, objeví se na hranicích chunků stěny, které tam nepatří.
/// </summary>
public sealed class VoxelWorldTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(31, 0)]
    [InlineData(32, 1)]
    [InlineData(-1, -1)]
    [InlineData(-32, -1)]
    [InlineData(-33, -2)]
    public void Prevod_na_souradnici_chunku_zaokrouhluje_dolu(int worldX, int expectedChunkX)
    {
        Assert.Equal(expectedChunkX, VoxelWorld.ToChunkPosition(worldX, 0, 0).X);
    }

    [Fact]
    public void Zapis_a_cteni_funguje_i_v_zapornych_souradnicich()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        world.SetBlock(-1, -1, -1, stone);
        world.SetBlock(-33, 5, 70, stone);

        Assert.Equal(stone, world.GetBlock(-1, -1, -1));
        Assert.Equal(stone, world.GetBlock(-33, 5, 70));
        Assert.Equal(BlockRegistry.Air, world.GetBlock(0, 0, 0));
    }

    [Fact]
    public void Neexistujici_chunk_se_chova_jako_vzduch()
    {
        var world = new VoxelWorld(Registry());

        Assert.Equal(BlockRegistry.Air, world.GetBlock(1000, 1000, 1000));
        Assert.False(world.IsSolid(1000, 1000, 1000));
        Assert.Null(world.GetChunk(new Vector3i(31, 31, 31)));
    }

    [Fact]
    public void Odsazeny_objem_obsahuje_vlastni_chunk()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        world.SetBlock(0, 0, 0, stone);
        world.SetBlock(31, 31, 31, stone);

        ushort[] padded = new ushort[ChunkMesher.PaddedVolume];
        world.CopyPadded(Vector3i.Zero, padded);

        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(0, 0, 0)]);
        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(31, 31, 31)]);
        Assert.Equal(BlockRegistry.Air, padded[ChunkMesher.PaddedIndex(15, 15, 15)]);
    }

    [Fact]
    public void Lem_odsazeneho_objemu_se_naplni_ze_sousednich_chunku()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Bloky těsně za hranicí chunku (0,0,0) na všech šesti stranách.
        world.SetBlock(-1, 0, 0, stone);
        world.SetBlock(32, 0, 0, stone);
        world.SetBlock(0, -1, 0, stone);
        world.SetBlock(0, 32, 0, stone);
        world.SetBlock(0, 0, -1, stone);
        world.SetBlock(0, 0, 32, stone);

        ushort[] padded = new ushort[ChunkMesher.PaddedVolume];
        world.CopyPadded(Vector3i.Zero, padded);

        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(-1, 0, 0)]);
        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(32, 0, 0)]);
        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(0, -1, 0)]);
        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(0, 32, 0)]);
        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(0, 0, -1)]);
        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(0, 0, 32)]);
    }

    [Fact]
    public void Lem_pokryva_i_rohy_uhloprickou()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Rohový soused přes tři osy najednou — bez správného výběru sousedního chunku
        // by se sem dostal vzduch a stínění rohů by vyšlo špatně.
        world.SetBlock(-1, -1, -1, stone);
        world.SetBlock(32, 32, 32, stone);

        ushort[] padded = new ushort[ChunkMesher.PaddedVolume];
        world.CopyPadded(Vector3i.Zero, padded);

        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(-1, -1, -1)]);
        Assert.Equal(stone, padded[ChunkMesher.PaddedIndex(32, 32, 32)]);
    }

    [Fact]
    public void Odsazeny_objem_odpovida_ctení_po_jednom_bloku()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Nepravidelný vzorek přes hranice chunků.
        for (int i = -40; i < 70; i += 3)
        {
            world.SetBlock(i, (i * 7) % 40, (i * 13) % 50, stone);
        }

        ushort[] padded = new ushort[ChunkMesher.PaddedVolume];
        var chunkPosition = new Vector3i(0, 0, 0);
        world.CopyPadded(chunkPosition, padded);

        for (int y = -1; y <= Chunk.Size; y++)
        {
            for (int z = -1; z <= Chunk.Size; z++)
            {
                for (int x = -1; x <= Chunk.Size; x++)
                {
                    Assert.Equal(world.GetBlock(x, y, z), padded[ChunkMesher.PaddedIndex(x, y, z)]);
                }
            }
        }
    }

    [Fact]
    public void Prilis_maly_cil_je_odmitnut()
    {
        var world = new VoxelWorld(Registry());

        Assert.Throws<ArgumentException>(() => world.CopyPadded(Vector3i.Zero, new ushort[10]));
    }

    [Fact]
    public void Odebrany_chunk_uz_ve_svete_neni()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        world.SetBlock(5, 5, 5, stone);
        Assert.True(world.HasChunk(Vector3i.Zero));

        Assert.True(world.RemoveChunk(Vector3i.Zero));
        Assert.False(world.HasChunk(Vector3i.Zero));
        Assert.Equal(BlockRegistry.Air, world.GetBlock(5, 5, 5));
        Assert.False(world.RemoveChunk(Vector3i.Zero));
    }

    [Fact]
    public void Hotovy_chunk_jde_zverejnit_jen_jednou()
    {
        var world = new VoxelWorld(Registry());

        Assert.True(world.TryAddChunk(Vector3i.Zero, new Chunk()));
        Assert.False(world.TryAddChunk(Vector3i.Zero, new Chunk()));
        Assert.Equal(1, world.ChunkCount);
    }
}
