using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Engine.Rendering;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy ořezání podle pohledového jehlanu a kvádrů.
///
/// Matice se skládá stejně jako v rendereru (<c>view * projection</c>). Kdyby se roviny
/// vytáhly z řádků místo ze sloupců, culling by zahazoval to, co je vidět — a naopak.
/// </summary>
public sealed class FrustumTests
{
    private static Frustum LookingDownNegativeZ()
    {
        var camera = new Camera(Vector3.Zero)
        {
            AspectRatio = 16f / 9f,
            FieldOfViewDegrees = 70f,
            NearPlane = 0.1f,
            FarPlane = 100f,
        };

        var frustum = new Frustum();
        frustum.Update(camera.ViewMatrix * camera.ProjectionMatrix);
        return frustum;
    }

    [Fact]
    public void Kvadr_primo_pred_kamerou_je_videt()
    {
        Frustum frustum = LookingDownNegativeZ();

        Assert.True(frustum.Intersects(new Aabb(new Vector3(-1, -1, -12), new Vector3(1, 1, -10))));
    }

    [Fact]
    public void Kvadr_za_kamerou_videt_neni()
    {
        Frustum frustum = LookingDownNegativeZ();

        Assert.False(frustum.Intersects(new Aabb(new Vector3(-1, -1, 10), new Vector3(1, 1, 12))));
    }

    [Fact]
    public void Kvadr_za_vzdalenou_rovinou_videt_neni()
    {
        Frustum frustum = LookingDownNegativeZ();

        Assert.False(frustum.Intersects(new Aabb(new Vector3(-1, -1, -400), new Vector3(1, 1, -390))));
    }

    [Fact]
    public void Kvadr_daleko_stranou_videt_neni()
    {
        Frustum frustum = LookingDownNegativeZ();

        Assert.False(frustum.Intersects(new Aabb(new Vector3(500, -1, -12), new Vector3(520, 1, -10))));
        Assert.False(frustum.Intersects(new Aabb(new Vector3(-1, 500, -12), new Vector3(1, 520, -10))));
    }

    [Fact]
    public void Velky_kvadr_obklopujici_kameru_je_videt()
    {
        Frustum frustum = LookingDownNegativeZ();

        Assert.True(frustum.Intersects(new Aabb(new Vector3(-50, -50, -50), new Vector3(50, 50, 50))));
    }

    [Fact]
    public void Otoceni_kamery_zmeni_co_je_videt()
    {
        var camera = new Camera(Vector3.Zero) { AspectRatio = 1f, FarPlane = 100f };
        var frustum = new Frustum();

        var behind = new Aabb(new Vector3(-1, -1, 10), new Vector3(1, 1, 12));

        frustum.Update(camera.ViewMatrix * camera.ProjectionMatrix);
        Assert.False(frustum.Intersects(behind));

        // Otočka o 180 stupňů musí ten samý kvádr dostat do záběru.
        camera.ApplyLook(180f, 0f);
        frustum.Update(camera.ViewMatrix * camera.ProjectionMatrix);
        Assert.True(frustum.Intersects(behind));
    }

    [Fact]
    public void Kvadr_chunku_odpovida_jeho_souradnicim()
    {
        Aabb bounds = Tesseris.Game.World.VoxelWorld.ChunkBounds(new Vector3i(2, -1, 3));

        Assert.Equal(new Vector3(64, -32, 96), bounds.Min);
        Assert.Equal(new Vector3(96, 0, 128), bounds.Max);
    }

    [Fact]
    public void Aabb_prunik_je_symetricky()
    {
        var a = new Aabb(Vector3.Zero, new Vector3(2));
        var b = new Aabb(new Vector3(1), new Vector3(3));
        var c = new Aabb(new Vector3(5), new Vector3(6));

        Assert.True(a.Intersects(b));
        Assert.True(b.Intersects(a));
        Assert.False(a.Intersects(c));
        Assert.False(c.Intersects(a));
    }

    [Fact]
    public void Aabb_zna_svuj_stred_a_rozmer()
    {
        var box = Aabb.Block(3, 4, 5);

        Assert.Equal(new Vector3(3.5f, 4.5f, 5.5f), box.Center);
        Assert.Equal(new Vector3(1, 1, 1), box.Size);
        Assert.True(box.Contains(new Vector3(3.5f, 4.5f, 5.5f)));
        Assert.False(box.Contains(new Vector3(2.5f, 4.5f, 5.5f)));
    }
}
