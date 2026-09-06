using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>Frozen semantic output and worker-order checks for vanilla generation.</summary>
public sealed class DeterminismTests
{
    private const int Seed = 20260727;

    [Fact]
    public void Representative_vanilla_chunks_match_the_frozen_semantic_hash()
    {
        BlockRegistry blocks = Blocks();
        TerrainGenerator generator = Generator(blocks);
        Vector3i[] positions = RepresentativePositions(generator);

        string actual = CanonicalWorldHash(generator, blocks, positions);

        Assert.Equal("260bbdeaefa8d255454eb12e4a212560502034f1a863b5365d5872512f2b8527", actual);
    }

    [Fact]
    public void Parallel_generation_matches_sequential_generation_per_chunk()
    {
        BlockRegistry blocks = Blocks();
        TerrainGenerator reference = Generator(blocks);
        Vector3i[] positions = RepresentativePositions(reference);
        Dictionary<Vector3i, string> sequential = positions.ToDictionary(
            position => position,
            position => CanonicalChunkHash(reference, blocks, position));
        var parallel = new Dictionary<Vector3i, string>();
        object gate = new();

        Parallel.ForEach(positions, position =>
        {
            TerrainGenerator generator = Generator(blocks);
            string hash = CanonicalChunkHash(generator, blocks, position);
            lock (gate)
            {
                parallel.Add(position, hash);
            }
        });

        Assert.Equal(sequential.Count, parallel.Count);
        foreach ((Vector3i position, string expected) in sequential)
        {
            Assert.Equal(expected, parallel[position]);
        }
    }

    private static TerrainGenerator Generator(BlockRegistry blocks)
    {
        var generator = new TerrainGenerator(blocks, Seed);
        generator.EnableTrees(blocks);
        return generator;
    }

    private static Vector3i[] RepresentativePositions(TerrainGenerator generator) =>
    [
        new Vector3i(0, generator.SurfaceHeight(0, 0) >> Chunk.SizeShift, 0),
        new Vector3i(3, generator.SurfaceHeight(100, -140) >> Chunk.SizeShift, -5),
        new Vector3i(-8, generator.SurfaceHeight(-255, 411) >> Chunk.SizeShift, 12),
        new Vector3i(2, 0, 1),
        new Vector3i(7, 15, -9),
    ];

    private static string CanonicalWorldHash(
        TerrainGenerator generator,
        BlockRegistry blocks,
        IEnumerable<Vector3i> positions)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Tesseris.VanillaGeneration.Semantic.v1\0"u8);
        Span<byte> coordinates = stackalloc byte[sizeof(int) * 3];
        foreach (Vector3i position in positions)
        {
            BinaryPrimitives.WriteInt32LittleEndian(coordinates, position.X);
            BinaryPrimitives.WriteInt32LittleEndian(coordinates[sizeof(int)..], position.Y);
            BinaryPrimitives.WriteInt32LittleEndian(coordinates[(sizeof(int) * 2)..], position.Z);
            hash.AppendData(coordinates);
            AppendChunk(generator, blocks, position, hash);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string CanonicalChunkHash(
        TerrainGenerator generator,
        BlockRegistry blocks,
        Vector3i position)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendChunk(generator, blocks, position, hash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendChunk(
        TerrainGenerator generator,
        BlockRegistry blocks,
        Vector3i position,
        IncrementalHash hash)
    {
        var chunk = new Chunk();
        generator.Generate(chunk, position);
        Span<byte> length = stackalloc byte[sizeof(ushort)];
        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    string id = blocks.Definition(chunk.GetBlock(x, y, z)).Id;
                    byte[] encoded = Encoding.UTF8.GetBytes(id);
                    BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)encoded.Length));
                    hash.AppendData(length);
                    hash.AppendData(encoded);
                }
            }
        }
    }

    private static BlockRegistry Blocks() => BlockRegistry.LoadFromDirectory(
        Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));
}
