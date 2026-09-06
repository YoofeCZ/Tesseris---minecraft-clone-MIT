using OpenTK.Mathematics;
using Tesseris.Game.Entities;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class B3dAnimatedModelTests
{
    private static string SheepPath => Path.Combine(
        AppContext.BaseDirectory, "assets", "models", "animalia_sheep.b3d");

    private static string ClassicSheepPath => Path.Combine(
        AppContext.BaseDirectory, "assets", "models", "mobs_sheep_test_nc.b3d");

    private static string ReindeerPath => Path.Combine(
        AppContext.BaseDirectory, "assets", "models", "animalia_reindeer.b3d");

    [Fact]
    public void Animalia_sheep_loads_complete_skeleton_and_animation()
    {
        B3dAnimatedModel model = B3dAnimatedModel.Load(SheepPath);

        Assert.Equal(408, model.VertexCount);
        Assert.Equal(612, model.IndexCount);
        Assert.Equal(14, model.NodeCount);
        Assert.Equal(149, model.AnimationFrames);
        Assert.Equal(60f, model.AnimationFps);

        (Vector3 minimum, Vector3 maximum) = model.BoundsAt(1f);
        Assert.InRange(maximum.X - minimum.X, 0.4f, 1.5f);
        Assert.InRange(maximum.Y - minimum.Y, 0.6f, 1.8f);
        Assert.InRange(maximum.Z - minimum.Z, 0.8f, 2f);
    }

    [Fact]
    public void Animalia_sheep_appends_animated_textured_triangles_at_entity_position()
    {
        B3dAnimatedModel model = B3dAnimatedModel.Load(SheepPath);
        var mesh = new MeshBuffer();
        var sheep = new AnimalEntity
        {
            Kind = AnimalKind.Sheep,
            Position = new Vector3(10f, 2f, 20f),
            RenderPosition = new Vector3(10f, 2f, 20f),
            VisualInitialized = true,
            Activity = AnimalActivity.Wander,
            Moving = true,
            WalkPhase = 1.5f,
        };

        model.Append(mesh, sheep, textureLayer: 7f);

        Assert.Equal(model.IndexCount, mesh.IndexCount);
        Assert.Equal(model.IndexCount, mesh.VertexCount);
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            Assert.Equal(7f, mesh.RawVertices[(vertex * MeshBuffer.FloatsPerVertex) + 5]);
    }

    [Fact]
    public void Current_mobs_animal_sheep_uses_original_timeline_and_world_scale()
    {
        B3dAnimatedModel model = B3dAnimatedModel.Load(
            ClassicSheepPath,
            B3dAnimationProfile.ClassicMobsSheep,
            worldScale: 0.1f,
            worldOffset: new Vector3(0f, 1f, 0f));
        var mesh = new MeshBuffer();
        var sheep = new AnimalEntity
        {
            Kind = AnimalKind.Sheep,
            Position = Vector3.Zero,
            RenderPosition = Vector3.Zero,
            VisualInitialized = true,
            Activity = AnimalActivity.Wander,
            Moving = true,
            WalkPhase = 1f,
        };

        model.Append(mesh, sheep, textureLayer: 3f);

        Assert.Equal(model.IndexCount, mesh.IndexCount);
        float minimumY = float.PositiveInfinity;
        float maximumY = float.NegativeInfinity;
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            int offset = vertex * MeshBuffer.FloatsPerVertex;
            minimumY = Math.Min(minimumY, mesh.RawVertices[offset + 1]);
            maximumY = Math.Max(maximumY, mesh.RawVertices[offset + 1]);
            Assert.Equal(3f, mesh.RawVertices[offset + 5]);
        }
        Assert.InRange(minimumY, -0.1f, 0.25f);
        Assert.InRange(maximumY, 0.9f, 1.8f);
    }

    [Fact]
    public void Animalia_reindeer_preserves_the_upstream_brush_and_uv_domain()
    {
        B3dAnimatedModel model = B3dAnimatedModel.Load(ReindeerPath);

        B3dMaterialInfo material = Assert.Single(model.Materials);
        Assert.Equal("Reindeer", material.Name);
        Assert.Equal([ -1 ], material.TextureIndices);

        B3dSubmeshInfo submesh = Assert.Single(model.Submeshes);
        Assert.Equal(0, submesh.FirstIndex);
        Assert.Equal(510, submesh.IndexCount);
        Assert.Equal(0, submesh.MaterialIndex);
        Assert.Equal(0f, submesh.MinimumUv.X);
        Assert.Equal(-0.010416627f, submesh.MinimumUv.Y, 6);
        Assert.Equal(0.9791667f, submesh.MaximumUv.X, 6);
        Assert.Equal(0.9479167f, submesh.MaximumUv.Y, 6);

        // A negative V value is present in the upstream file. Clamping or normalising UVs in
        // the importer moves a seam onto the animal's upper body and appears as a missing patch.
        Assert.True(submesh.MinimumUv.Y < 0f);
    }

    [Fact]
    public void AppendSubmesh_emits_only_the_selected_triangle_group()
    {
        string path = CreateTwoMaterialModel();
        try
        {
            B3dAnimatedModel model = B3dAnimatedModel.Load(path);
            Assert.Equal(2, model.Submeshes.Count);
            Assert.Equal(0, model.Submeshes[0].MaterialIndex);
            Assert.Equal(1, model.Submeshes[1].MaterialIndex);

            var mesh = new MeshBuffer();
            model.AppendSubmesh(mesh, new AnimalEntity { Position = Vector3.Zero }, 1, 17f);

            Assert.Equal(3, mesh.IndexCount);
            Assert.Equal(3, mesh.VertexCount);
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
                Assert.Equal(17f, mesh.RawVertices[(vertex * MeshBuffer.FloatsPerVertex) + 5]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Invalid_submesh_material_reference_is_rejected()
    {
        string path = CreateTwoMaterialModel(secondMaterial: 2);
        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => B3dAnimatedModel.Load(path));
            Assert.Contains("invalid material", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("animalia_sheep.b3d", 408, 612, 14, 149)]
    [InlineData("animalia_reindeer.b3d", 340, 510, 14, 149)]
    [InlineData("animalia_wolf.b3d", 264, 396, 15, 139)]
    public void Every_vendored_Animalia_mob_has_its_exact_mesh_skeleton_and_timeline(
        string file,
        int vertices,
        int indices,
        int nodes,
        int frames)
    {
        B3dAnimatedModel model = B3dAnimatedModel.Load(Path.Combine(
            AppContext.BaseDirectory, "assets", "models", file));

        Assert.Equal(vertices, model.VertexCount);
        Assert.Equal(indices, model.IndexCount);
        Assert.Equal(nodes, model.NodeCount);
        Assert.Equal(frames, model.AnimationFrames);
    }

    private static string CreateTwoMaterialModel(int secondMaterial = 1)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tesseris-b3d-{Guid.NewGuid():N}.b3d");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        WriteChunk(writer, "BB3D", root =>
        {
            root.Write(1);
            WriteChunk(root, "BRUS", brushes =>
            {
                brushes.Write(0);
                WriteMaterial(brushes, "first");
                WriteMaterial(brushes, "second");
            });
            WriteChunk(root, "NODE", node =>
            {
                WriteNullString(node, "root");
                WriteVector(node, 0f, 0f, 0f);
                WriteVector(node, 1f, 1f, 1f);
                node.Write(1f); node.Write(0f); node.Write(0f); node.Write(0f);
                WriteChunk(node, "MESH", mesh =>
                {
                    mesh.Write(-1);
                    WriteChunk(mesh, "VRTS", vertices =>
                    {
                        vertices.Write(0);
                        vertices.Write(1);
                        vertices.Write(2);
                        WriteVertex(vertices, 0f, 0f, 0f, 0f, 0f);
                        WriteVertex(vertices, 1f, 0f, 0f, 1f, 0f);
                        WriteVertex(vertices, 0f, 1f, 0f, 0f, 1f);
                        WriteVertex(vertices, 1f, 1f, 0f, 1f, 1f);
                    });
                    WriteChunk(mesh, "TRIS", triangles =>
                    {
                        triangles.Write(0);
                        triangles.Write(0); triangles.Write(1); triangles.Write(2);
                    });
                    WriteChunk(mesh, "TRIS", triangles =>
                    {
                        triangles.Write(secondMaterial);
                        triangles.Write(1); triangles.Write(3); triangles.Write(2);
                    });
                });
            });
        });
        return path;
    }

    private static void WriteChunk(BinaryWriter writer, string name, Action<BinaryWriter> content)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes(name));
        long sizePosition = writer.BaseStream.Position;
        writer.Write(0);
        long contentStart = writer.BaseStream.Position;
        content(writer);
        long end = writer.BaseStream.Position;
        writer.BaseStream.Position = sizePosition;
        writer.Write(checked((int)(end - contentStart)));
        writer.BaseStream.Position = end;
    }

    private static void WriteMaterial(BinaryWriter writer, string name)
    {
        WriteNullString(writer, name);
        writer.Write(1f); writer.Write(1f); writer.Write(1f); writer.Write(1f);
        writer.Write(0f);
        writer.Write(1);
        writer.Write(0);
    }

    private static void WriteNullString(BinaryWriter writer, string value)
    {
        writer.Write(System.Text.Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void WriteVector(BinaryWriter writer, float x, float y, float z)
    {
        writer.Write(x); writer.Write(y); writer.Write(z);
    }

    private static void WriteVertex(
        BinaryWriter writer,
        float x,
        float y,
        float z,
        float u,
        float v)
    {
        WriteVector(writer, x, y, z);
        writer.Write(u); writer.Write(v);
    }
}
