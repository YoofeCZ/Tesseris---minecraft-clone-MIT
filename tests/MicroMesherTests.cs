using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy mikro meshingu, kolizních kvádrů a deduplikační zásoby.
/// </summary>
public sealed class MicroMesherTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:wood", Texture = "planks", Opaque = true },
    ]);

    private static ushort Stone(BlockRegistry registry) => registry.IndexOf("test:stone");

    [Fact]
    public void Prazdny_tvar_nema_geometrii_ani_kolize()
    {
        MicroShape shape = MicroMesher.Build(MicroBlock.Empty(), Registry());

        Assert.True(shape.IsEmpty);
        Assert.Empty(shape.Colliders);
    }

    [Fact]
    public void Plna_mrizka_da_sest_sten_a_jeden_kvadr()
    {
        BlockRegistry registry = Registry();
        MicroShape shape = MicroMesher.Build(MicroBlock.FromSolid(Stone(registry)), registry);

        // Plná krychle: šest velkých obdélníků, tedy dvanáct trojúhelníků.
        Assert.Equal(12, shape.TriangleCount);

        // A jediný kolizní kvádr přes celý blok.
        Assert.Single(shape.Colliders);
        Assert.Equal(0f, shape.Colliders[0].Min.X, 4);
        Assert.Equal(1f, shape.Colliders[0].Max.X, 4);
    }

    [Fact]
    public void Jediny_mikrovoxel_da_sest_malych_sten()
    {
        BlockRegistry registry = Registry();
        MicroBlock block = MicroBlock.Empty();
        block.SetMaterial(5, 5, 5, Stone(registry));

        MicroShape shape = MicroMesher.Build(block, registry);

        Assert.Equal(12, shape.TriangleCount);
        Assert.Single(shape.Colliders);

        // Šestnáctina bloku na správném místě.
        Assert.Equal(5f / 16f, shape.Colliders[0].Min.X, 4);
        Assert.Equal(6f / 16f, shape.Colliders[0].Max.X, 4);
    }

    [Fact]
    public void Rovna_deska_se_slouci_do_jednoho_kvadru()
    {
        BlockRegistry registry = Registry();
        ushort stone = Stone(registry);

        // Spodní vrstva celého bloku.
        MicroBlock block = MicroBlock.Empty();
        for (int z = 0; z < MicroBlock.Size; z++)
        {
            for (int x = 0; x < MicroBlock.Size; x++)
            {
                block.SetMaterial(x, 0, z, stone);
            }
        }

        MicroShape shape = MicroMesher.Build(block, registry);

        // Bez slučování by kvádrů bylo 256.
        Assert.Single(shape.Colliders);
        Assert.Equal(1f / 16f, shape.Colliders[0].Max.Y, 4);
        Assert.Equal(1f, shape.Colliders[0].Max.X, 4);
        Assert.Equal(1f, shape.Colliders[0].Max.Z, 4);
    }

    [Fact]
    public void Stena_na_okraji_bloku_se_kresli_vzdycky()
    {
        BlockRegistry registry = Registry();

        // Bezkontextové meshování je záměr: díky němu jde tvar sdílet mezi bloky.
        // Cenou je, že se kreslí i stěny přiléhající k sousedům.
        MicroShape full = MicroMesher.Build(MicroBlock.FromSolid(Stone(registry)), registry);

        Assert.Equal(12, full.TriangleCount);
    }

    [Fact]
    public void Odebrany_roh_prida_stenu()
    {
        BlockRegistry registry = Registry();
        MicroBlock block = MicroBlock.FromSolid(Stone(registry));

        MicroShape before = MicroMesher.Build(block.Clone(), registry);

        block.SetMaterial(0, 0, 0, 0);
        MicroShape after = MicroMesher.Build(block, registry);

        Assert.True(after.TriangleCount > before.TriangleCount,
            $"Po odebrání rohu má tvar {after.TriangleCount} trojúhelníků, předtím {before.TriangleCount}.");
    }

    [Fact]
    public void Kolizni_kvadry_pokryvaji_vsechny_mikrovoxely()
    {
        BlockRegistry registry = Registry();
        ushort stone = Stone(registry);

        MicroBlock block = MicroBlock.Empty();
        for (int i = 0; i < 300; i++)
        {
            block.SetMaterial(i % 16, (i / 64) % 16, (i / 8) % 16, stone);
        }

        MicroShape shape = MicroMesher.Build(block, registry);

        // Každý vyplněný mikrovoxel musí ležet právě v jednom kvádru.
        for (int y = 0; y < MicroBlock.Size; y++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int x = 0; x < MicroBlock.Size; x++)
                {
                    int covering = 0;
                    var center = new OpenTK.Mathematics.Vector3(
                        (x + 0.5f) / 16f, (y + 0.5f) / 16f, (z + 0.5f) / 16f);

                    foreach (Aabb box in shape.Colliders)
                    {
                        if (box.Contains(center))
                        {
                            covering++;
                        }
                    }

                    Assert.Equal(block.IsSolid(x, y, z) ? 1 : 0, covering);
                }
            }
        }
    }

    [Fact]
    public void Zasoba_vrati_pro_stejny_tvar_tentyz_objekt()
    {
        BlockRegistry registry = Registry();
        var cache = new MicroShapeCache(registry);

        MicroBlock a = MicroBlock.FromSolid(Stone(registry));
        MicroBlock b = MicroBlock.FromSolid(Stone(registry));
        a.SetMaterial(2, 2, 2, 0);
        b.SetMaterial(2, 2, 2, 0);

        MicroShape first = cache.Get(a);
        MicroShape second = cache.Get(b);

        Assert.Same(first, second);
        Assert.Equal(1, cache.UniqueShapes);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void Tisic_stejnych_tvaru_se_spocita_jednou()
    {
        BlockRegistry registry = Registry();
        var cache = new MicroShapeCache(registry);
        ushort stone = Stone(registry);

        for (int i = 0; i < 1000; i++)
        {
            MicroBlock block = MicroBlock.FromSolid(stone);
            block.SetMaterial(8, 8, 8, 0);
            cache.Get(block);
        }

        // Tohle je celý smysl deduplikace: tisíc bloků, jeden výpočet.
        Assert.Equal(1, cache.UniqueShapes);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(999, cache.Hits);
    }

    [Fact]
    public void Ruzne_tvary_zustanou_oddelene()
    {
        BlockRegistry registry = Registry();
        var cache = new MicroShapeCache(registry);
        ushort stone = Stone(registry);

        for (int i = 0; i < 20; i++)
        {
            MicroBlock block = MicroBlock.FromSolid(stone);
            block.SetMaterial(i, 0, 0, 0);
            cache.Get(block);
        }

        Assert.Equal(20, cache.UniqueShapes);
        Assert.Equal(20, cache.Misses);
    }

    [Fact]
    public void Zasoba_je_bezpecna_pri_soubeznem_pouziti()
    {
        BlockRegistry registry = Registry();
        var cache = new MicroShapeCache(registry);
        ushort stone = Stone(registry);

        // Meshing běží na worker vláknech, takže do zásoby sahá víc vláken najednou.
        Parallel.For(0, 2000, i =>
        {
            MicroBlock block = MicroBlock.FromSolid(stone);
            block.SetMaterial(i % 16, 0, 0, 0);
            MicroShape shape = cache.Get(block);
            Assert.False(shape.IsEmpty);
        });

        Assert.Equal(16, cache.UniqueShapes);
    }

    [Fact]
    public void Zasoba_se_da_vyprazdnit()
    {
        BlockRegistry registry = Registry();
        var cache = new MicroShapeCache(registry);

        cache.Get(MicroBlock.FromSolid(Stone(registry)));
        Assert.Equal(1, cache.UniqueShapes);

        cache.Clear();

        Assert.Equal(0, cache.UniqueShapes);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }
}
