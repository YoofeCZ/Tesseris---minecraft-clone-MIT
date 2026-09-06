using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class HeldArmTests
{
    [Fact]
    public void Arm_is_closed_four_by_twelve_by_four_prism()
    {
        var mesh = new MeshBuffer();

        ChunkRenderer.AddHeldArm(
            mesh, static position => position, layer: 7f,
            uvMin: Vector2.Zero, uvSize: Vector2.One);

        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(36, mesh.IndexCount);

        var corners = new HashSet<(float X, float Y, float Z)>();
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        ReadOnlySpan<float> vertices = mesh.Vertices;

        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            int at = vertex * MeshBuffer.FloatsPerVertex;
            float x = vertices[at];
            float y = vertices[at + 1];
            float z = vertices[at + 2];

            corners.Add((x, y, z));
            Assert.Equal(7f, vertices[at + 5]);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        Assert.Equal(8, corners.Count);
        Assert.Equal(0.16f, maxX - minX, 5);
        Assert.Equal(0.48f, maxY - minY, 5);
        Assert.Equal(0.16f, maxZ - minZ, 5);
        Assert.Equal(3f, (maxY - minY) / (maxX - minX), 5);
    }

    [Fact]
    public void Arm_uses_four_long_panels_and_two_square_caps()
    {
        var mesh = new MeshBuffer();

        ChunkRenderer.AddHeldArm(
            mesh, static position => position, layer: 3f,
            uvMin: Vector2.Zero, uvSize: Vector2.One);

        (float MinU, float MinV, float MaxU, float MaxV)[] expected =
        [
            Rect(4, 4, 4, 12),
            Rect(12, 4, 4, 12),
            Rect(0, 4, 4, 12),
            Rect(8, 4, 4, 12),
            Rect(4, 0, 4, 4),
            Rect(8, 0, 4, 4),
        ];

        ReadOnlySpan<float> vertices = mesh.Vertices;

        for (int quad = 0; quad < expected.Length; quad++)
        {
            float minU = float.PositiveInfinity;
            float minV = float.PositiveInfinity;
            float maxU = float.NegativeInfinity;
            float maxV = float.NegativeInfinity;

            for (int corner = 0; corner < 4; corner++)
            {
                int at = ((quad * 4) + corner) * MeshBuffer.FloatsPerVertex;
                float u = vertices[at + 3];
                float v = vertices[at + 4];

                (float U, float V) expectedCorner = corner switch
                {
                    0 => (expected[quad].MinU, expected[quad].MaxV),
                    1 => (expected[quad].MaxU, expected[quad].MaxV),
                    2 => (expected[quad].MaxU, expected[quad].MinV),
                    _ => (expected[quad].MinU, expected[quad].MinV),
                };

                Assert.Equal(expectedCorner.U, u, 6);
                Assert.Equal(expectedCorner.V, v, 6);
                minU = Math.Min(minU, u);
                minV = Math.Min(minV, v);
                maxU = Math.Max(maxU, u);
                maxV = Math.Max(maxV, v);
            }

            Assert.Equal(expected[quad].MinU, minU, 6);
            Assert.Equal(expected[quad].MinV, minV, 6);
            Assert.Equal(expected[quad].MaxU, maxU, 6);
            Assert.Equal(expected[quad].MaxV, maxV, 6);

            float uvWidth = maxU - minU;
            float uvHeight = maxV - minV;
            Assert.Equal(quad < 4 ? 3f : 1f, uvHeight / uvWidth, 6);
        }
    }

    [Fact]
    public void Arm_applies_view_transform_to_all_vertices()
    {
        var mesh = new MeshBuffer();
        int transformed = 0;

        ChunkRenderer.AddHeldArm(
            mesh,
            position =>
            {
                transformed++;
                return position + new OpenTK.Mathematics.Vector3(10f, 20f, 30f);
            },
            layer: 2f,
            uvMin: Vector2.Zero,
            uvSize: Vector2.One);

        Assert.Equal(24, transformed);

        ReadOnlySpan<float> vertices = mesh.Vertices;
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            int at = vertex * MeshBuffer.FloatsPerVertex;
            Assert.InRange(vertices[at], 9.92f, 10.08f);
            Assert.InRange(vertices[at + 1], 19.76f, 20.24f);
            Assert.InRange(vertices[at + 2], 29.92f, 30.08f);
        }
    }

    [Fact]
    public void Player_arm_texture_is_fully_opaque_and_has_pixel_detail()
    {
        Span<byte> pixels = stackalloc byte[TextureArray.ArtSize * TextureArray.ArtSize * 4];

        TextureArray.GenerateTile("player_arm_v2", pixels);

        var colours = new HashSet<(byte R, byte G, byte B)>();

        for (int pixel = 0; pixel < TextureArray.ArtSize * TextureArray.ArtSize; pixel++)
        {
            int at = pixel * 4;
            Assert.Equal(255, pixels[at + 3]);
            colours.Add((pixels[at], pixels[at + 1], pixels[at + 2]));
        }

        Assert.True(colours.Count >= 12, $"Arm texture has only {colours.Count} colours.");

        foreach ((int x, int y, int width, int height) in new[]
        {
            (4, 4, 4, 12), (12, 4, 4, 12), (0, 4, 4, 12),
            (8, 4, 4, 12), (4, 0, 4, 4), (8, 0, 4, 4),
        })
        {
            var panelColours = new HashSet<(byte R, byte G, byte B)>();

            for (int py = y; py < y + height; py++)
            {
                for (int px = x; px < x + width; px++)
                {
                    int at = ((py * TextureArray.ArtSize) + px) * 4;
                    panelColours.Add((pixels[at], pixels[at + 1], pixels[at + 2]));
                }
            }

            Assert.True(panelColours.Count > 1,
                $"Arm panel {x},{y} has no pixel detail.");
        }
    }

    private static (float MinU, float MinV, float MaxU, float MaxV) Rect(
        int x, int y, int width, int height) =>
        (
            x / (float)TextureArray.ArtSize,
            y / (float)TextureArray.ArtSize,
            (x + width) / (float)TextureArray.ArtSize,
            (y + height) / (float)TextureArray.ArtSize
        );
}
