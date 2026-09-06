using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class StructureGeneratorTests
{
    private const int Seed = 20260811;

    private static BlockRegistry Blocks() => BlockRegistry.LoadFromDirectory(
        Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    [Fact]
    public void Underground_structure_contains_loot_chest_and_is_deterministic()
    {
        BlockRegistry blocks = Blocks();
        var generator = new TerrainGenerator(blocks, Seed);
        ushort lootChest = blocks.IndexOf("tesseris:loot_chest");

        (int rx, int rz, ulong hash) = FindUndergroundRegion();
        const int regionBlocks = 8 * Chunk.Size;
        int originX = (rx * regionBlocks) + 48 + (int)((hash >> 8) % (ulong)(regionBlocks - 96));
        int originZ = (rz * regionBlocks) + 48 + (int)((hash >> 24) % (ulong)(regionBlocks - 96));
        int surface = generator.SurfaceHeight(originX, originZ);
        int roomY = Math.Clamp(surface - 20 - (int)((hash >> 52) & 15UL), 18, TerrainGenerator.WorldHeight - 10);
        Vector3i chunkPosition = new(FloorDiv(originX + 3, Chunk.Size), FloorDiv(roomY, Chunk.Size), FloorDiv(originZ + 3, Chunk.Size));

        var first = new Chunk();
        var second = new Chunk();
        generator.Generate(first, chunkPosition);
        generator.Generate(second, chunkPosition);

        Assert.True(Contains(first, lootChest));
        for (int y = 0; y < Chunk.Size; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            Assert.Equal(first.GetBlock(x, y, z), second.GetBlock(x, y, z));
    }

    [Theory]
    [InlineData(StructureGenerator.StructureKind.Cabin)]
    [InlineData(StructureGenerator.StructureKind.Watchtower)]
    public void Surface_buildings_use_full_glass_and_a_real_building_palette(
        StructureGenerator.StructureKind expectedKind)
    {
        BlockRegistry blocks = Blocks();
        var generator = new TerrainGenerator(blocks, Seed);
        (int rx, int rz, ulong hash) = FindSurfaceRegion(generator, expectedKind);

        const int regionBlocks = 8 * Chunk.Size;
        int originX = (rx * regionBlocks) + 48
            + (int)((hash >> 8) % (ulong)(regionBlocks - 96));
        int originZ = (rz * regionBlocks) + 48
            + (int)((hash >> 24) % (ulong)(regionBlocks - 96));
        int originY = generator.SurfaceHeight(originX, originZ) + 1;
        int centreChunkX = FloorDiv(originX, Chunk.Size);
        int centreChunkZ = FloorDiv(originZ, Chunk.Size);
        int minChunkY = FloorDiv(originY - 2, Chunk.Size);
        int maxChunkY = FloorDiv(originY + 14, Chunk.Size);
        HashSet<ushort> found = [];
        int ladderCount = 0;
        ushort ladder = blocks.IndexOf("tesseris:ladder");

        for (int cy = minChunkY; cy <= maxChunkY; cy++)
        for (int cz = centreChunkZ - 1; cz <= centreChunkZ + 1; cz++)
        for (int cx = centreChunkX - 1; cx <= centreChunkX + 1; cx++)
        {
            var chunk = new Chunk();
            generator.Generate(chunk, new Vector3i(cx, cy, cz));
            for (int y = 0; y < Chunk.Size; y++)
            for (int z = 0; z < Chunk.Size; z++)
            for (int x = 0; x < Chunk.Size; x++)
            {
                ushort block = chunk.GetBlock(x, y, z);
                found.Add(block);
                if (block == ladder) ladderCount++;
            }
        }

        Assert.Contains(blocks.IndexOf("tesseris:glass"), found);
        Assert.DoesNotContain(blocks.IndexOf("tesseris:window_pane"), found);
        Assert.Contains(blocks.IndexOf("tesseris:wooden_door"), found);
        Assert.Contains(blocks.IndexOf("tesseris:loot_chest"), found);
        Assert.Contains(found, block => blocks.Definition(block).Id.EndsWith("_stairs", StringComparison.Ordinal));
        Assert.Contains(found, block => blocks.Definition(block).Id.EndsWith("_planks", StringComparison.Ordinal));
        Assert.DoesNotContain(found, block =>
            blocks.Definition(block).Id.EndsWith("_log", StringComparison.Ordinal));
        if (expectedKind == StructureGenerator.StructureKind.Watchtower)
        {
            Assert.Contains(ladder, found);
            Assert.True(ladderCount >= 8, $"Věž má jen {ladderCount} souvislých dílů žebříku.");
        }
    }

    private static (int X, int Z, ulong Hash) FindUndergroundRegion()
    {
        for (int z = -12; z <= 12; z++)
        for (int x = -12; x <= 12; x++)
        {
            ulong hash = StructureGenerator.Hash(Seed, x, z, 0);
            if ((hash & 7UL) < 5UL && ((hash >> 40) % 3UL) == 2UL) return (x, z, hash);
        }
        throw new InvalidOperationException("Test seed has no nearby underground structure.");
    }

    private static (int X, int Z, ulong Hash) FindSurfaceRegion(
        TerrainGenerator terrain, StructureGenerator.StructureKind expectedKind)
    {
        for (int z = -40; z <= 40; z++)
        for (int x = -40; x <= 40; x++)
        {
            ulong hash = StructureGenerator.Hash(Seed, x, z, 0);
            if ((hash & 7UL) >= 5UL || StructureGenerator.SelectKind(hash) != expectedKind) continue;

            const int regionBlocks = 8 * Chunk.Size;
            int originX = (x * regionBlocks) + 48
                + (int)((hash >> 8) % (ulong)(regionBlocks - 96));
            int originZ = (z * regionBlocks) + 48
                + (int)((hash >> 24) % (ulong)(regionBlocks - 96));
            int centre = terrain.SurfaceHeight(originX, originZ);
            if (centre <= TerrainGenerator.SeaLevel + 2) continue;

            int min = centre;
            int max = centre;
            for (int dz = -6; dz <= 6; dz += 6)
            for (int dx = -6; dx <= 6; dx += 6)
            {
                int height = terrain.SurfaceHeight(originX + dx, originZ + dz);
                min = Math.Min(min, height);
                max = Math.Max(max, height);
            }
            int maximumRelief = expectedKind switch
            {
                StructureGenerator.StructureKind.Cabin
                    or StructureGenerator.StructureKind.Watchtower => 2,
                StructureGenerator.StructureKind.Camp => 3,
                _ => 4,
            };
            if (max - min <= maximumRelief) return (x, z, hash);
        }
        throw new InvalidOperationException($"Test seed has no suitable {expectedKind} structure.");
    }

    private static bool Contains(Chunk chunk, ushort block)
    {
        for (int y = 0; y < Chunk.Size; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            if (chunk.GetBlock(x, y, z) == block) return true;
        return false;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : ((value + 1) / divisor) - 1;
}
