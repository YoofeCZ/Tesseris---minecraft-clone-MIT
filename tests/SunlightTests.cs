using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Engine.Rendering;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Kdo zastaví přímé sluneční světlo. V Luanti je to <c>sunlight_propagates</c> a je to
/// jediný zdroj stínu pod stromem — dynamické stíny má vypnuté.
/// </summary>
/// <remarks>
/// Vzniklo z konkrétní chyby: strop slunečního sloupce se hledal podle
/// <see cref="BlockRegistry.IsOpaque"/>, jenže listí neprůhledné není. Slunce proto
/// korunou projelo, jako by tam nebyla, a les byl stejně jasný jako louka.
/// </remarks>
public sealed class SunlightTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition
        {
            Id = "test:leaves", Texture = "leaves", Opaque = false, Cutout = true,
            Shape = BlockShape.Foliage,
        },
        new BlockDefinition { Id = "test:glass", Texture = "glass", Opaque = false },
        new BlockDefinition
        {
            Id = "test:grass_tuft", Texture = "tuft", Opaque = false, Cutout = true,
            Shape = BlockShape.Cross,
        },
        new BlockDefinition { Id = "test:water", Texture = "water", Opaque = false, Liquid = true },
    ]);

    private static ushort Id(BlockRegistry registry, string id)
    {
        Assert.True(registry.TryIndexOf(id, out ushort index), $"Blok {id} v registru není.");
        return index;
    }

    [Fact]
    public void Listi_zastavi_slunce_i_kdyz_neni_nepruhledne()
    {
        BlockRegistry registry = Registry();
        ushort leaves = Id(registry, "test:leaves");

        Assert.False(registry.IsOpaque(leaves));
        Assert.True(registry.BlocksSunlight(leaves));
    }

    [Fact]
    public void Kamen_zastavi_slunce()
    {
        BlockRegistry registry = Registry();

        Assert.True(registry.BlocksSunlight(Id(registry, "test:stone")));
    }

    [Fact]
    public void Voda_zastavi_slunce_aby_hloubka_tmavla()
    {
        BlockRegistry registry = Registry();

        Assert.True(registry.BlocksSunlight(Id(registry, "test:water")));
    }

    [Fact]
    public void Sklo_a_kytka_slunce_pousti()
    {
        BlockRegistry registry = Registry();

        // Kytka, která si sama pod sebou dělá stín, vypadá jako chyba — v Minetest Game
        // si proto porost `sunlight_propagates = true` nastavuje výslovně.
        Assert.False(registry.BlocksSunlight(Id(registry, "test:glass")));
        Assert.False(registry.BlocksSunlight(Id(registry, "test:grass_tuft")));
    }

    [Fact]
    public void Vzduch_slunce_pousti()
    {
        Assert.False(Registry().BlocksSunlight(BlockRegistry.Air));
    }

    /// <summary>
    /// Konec konců jde o tohle: vrchní stěna země pod korunou musí být tmavší než tatáž
    /// stěna na volném prostranství. Tomu se v Luanti říká stín.
    /// </summary>
    [Fact]
    public void Zeme_pod_korunou_je_tmavsi_nez_na_louce()
    {
        BlockRegistry registry = Registry();
        ushort stone = Id(registry, "test:stone");
        ushort leaves = Id(registry, "test:leaves");

        const int size = ChunkMesher.PaddedSize;
        var padded = new ushort[ChunkMesher.PaddedVolume];
        var skyEntry = new int[size * size];

        // Země přes celý chunk ve výšce 0 a koruna ve výšce 6 nad LEVOU polovinou.
        for (int z = 0; z < size; z++)
        for (int x = 0; x < size; x++)
        {
            padded[ChunkMesher.PaddedIndex(x - ChunkMesher.Pad, -ChunkMesher.Pad, z - ChunkMesher.Pad)] = stone;

            bool covered = x < size / 2;
            if (covered)
            {
                for (int y = 6; y <= 8; y++)
                {
                    padded[ChunkMesher.PaddedIndex(x - ChunkMesher.Pad, y, z - ChunkMesher.Pad)] = leaves;
                }
            }

            // Nad celým chunkem je otevřená obloha; tlumit má až koruna uvnitř.
            skyEntry[x + (z * size)] = LuantiLight.LightSun;
        }

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var cutout = new MeshBuffer();

        ChunkMesher.Build(
            padded, registry, opaque, transparent,
            chunkPosition: Vector3i.Zero, cutout: cutout,
            skyEntry: skyEntry);

        float shaded = TopFaceShade(opaque, coveredHalf: true);
        float lit = TopFaceShade(opaque, coveredHalf: false);

        Assert.True(lit > 0.9f, $"Louka má být plně osvětlená, ale má jas {lit:F3}.");
        // NAMĚŘENO 0,471. Tři vrstvy listí po třech stupních srazí slunce z 15 na 6,
        // průměrování rohů to zvedne na 7, a to je po převodní křivce 47 % jasu louky.
        // Rozsah hlídá obě strany: kdyby spadl, je ze stínu zase černá díra (přesně to
        // se stalo, když listí slunce zastavovalo natvrdo); kdyby stoupl, stín mizí.
        Assert.InRange(shaded / lit, 0.40f, 0.55f);
    }

    /// <summary>Nejvyšší jas vodorovné stěny v dané polovině chunku.</summary>
    private static float TopFaceShade(MeshBuffer mesh, bool coveredHalf)
    {
        ReadOnlySpan<float> vertices = mesh.Vertices;
        float best = 0f;

        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            int at = vertex * MeshBuffer.FloatsPerVertex;
            float x = vertices[at];
            float y = vertices[at + 1];

            // Vrchní stěna země leží na y = 0; svislé stěny mají oba rohy jinde.
            if (MathF.Abs(y) > 0.01f)
            {
                continue;
            }

            // Vzorkuje se hluboko uvnitř každé poloviny, ne u hranice: na ní leží
            // vrcholy, které patří oběma.
            bool inCovered = x <= 8f;
            bool inOpen = x >= 24f;
            if (coveredHalf ? !inCovered : !inOpen)
            {
                continue;
            }

            // PŘÍZNAK VODY SE MUSÍ ODEČÍST. Mesher přičítá ke stínění dvojku, když neumí
            // dokázat, že nad stěnou není voda — a bez stropu nad chunkem to dokázat nejde.
            // Bez odečtení vyjde jas 2,x a test by nezměřil nic.
            float shade = vertices[at + 6];
            best = MathF.Max(best, shade > 1f ? shade - 2f : shade);
        }

        return best;
    }
}
