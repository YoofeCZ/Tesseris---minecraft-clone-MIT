using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy tvaru vodní hladiny.
///
/// Hlídá se hlavně spojitost: sousední bloky musí mít ve sdíleném rohu STEJNOU výšku,
/// jinak z klesajícího doběhu vzniknou schody místo rampy. Je to vlastnost, kterou od
/// pohledu snadno přehlédneš, když je rozdíl jen osminu bloku.
/// </summary>
public sealed class WaterSurfaceTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true, Solid = true },
        new BlockDefinition { Id = "tesseris:water", Texture = "water", Opaque = false, Solid = false, Liquid = true },
    ]);

    /// <summary>Objem s kamenným dnem a řadou vody s klesající úrovní.</summary>
    private static (ushort[] Volume, byte[] Fluid) Ramp(BlockRegistry registry, int length)
    {
        var volume = new ushort[ChunkMesher.PaddedVolume];
        var fluid = new byte[ChunkMesher.PaddedVolume];

        ushort stone = registry.IndexOf("test:stone");
        ushort water = registry.IndexOf("tesseris:water");

        Array.Fill(volume, BlockRegistry.Air);
        Array.Fill(fluid, FluidCell.Source);

        for (int x = 0; x < length; x++)
        {
            volume[ChunkMesher.PaddedIndex(x, 0, 0)] = stone;
            volume[ChunkMesher.PaddedIndex(x, 1, 0)] = water;

            // Úroveň klesá s vzdáleností: zdroj, pak doběh 7, 6, 5...
            fluid[ChunkMesher.PaddedIndex(x, 1, 0)] = (byte)Math.Max(1, FluidCell.Source - x);
        }

        return (volume, fluid);
    }

    /// <summary>
    /// Výšky vrcholů vodního meshe na zadané svislici, počítají se jen ty u hladiny.
    /// </summary>
    /// <remarks>
    /// Voda leží v patře y = 1, takže vrcholy na výšce přesně 1 jsou její DNO — spodní hrany
    /// bočních stěn. Ty s hladinou nemají co dělat a do porovnání nepatří; kdyby se braly,
    /// test by hlásil zlom pokaždé.
    /// </remarks>
    private static List<float> HeightsAt(MeshBuffer water, float x, float z)
    {
        const float FloorY = 1f;

        var found = new List<float>();
        ReadOnlySpan<float> vertices = water.Vertices;

        for (int i = 0; i < water.VertexCount; i++)
        {
            int at = i * MeshBuffer.FloatsPerVertex;

            if (Math.Abs(vertices[at] - x) < 0.001f
                && Math.Abs(vertices[at + 2] - z) < 0.001f
                && vertices[at + 1] > FloorY + 0.001f)
            {
                found.Add(vertices[at + 1]);
            }
        }

        return found;
    }

    private static MeshBuffer MeshWater(ushort[] volume, byte[] fluid, BlockRegistry registry)
    {
        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var cutout = new MeshBuffer();
        var water = new MeshBuffer();

        ChunkMesher.Build(
            volume, registry, opaque, transparent, default, cutout, water,
            default, default, default, fluid);

        return water;
    }

    [Fact]
    public void Hladina_klesa_s_urovni()
    {
        BlockRegistry registry = Registry();
        (ushort[] volume, byte[] fluid) = Ramp(registry, 6);

        MeshBuffer water = MeshWater(volume, fluid, registry);

        Assert.True(water.VertexCount > 0, "vodní buffer je prázdný");

        // Vrchol nad zdrojem musí ležet výš než vrchol na konci doběhu. Kdyby se úroveň
        // do meshe nepromítla, byly by obě výšky stejné.
        List<float> nearSource = HeightsAt(water, 0f, 0f);
        List<float> farEnd = HeightsAt(water, 6f, 0f);

        Assert.NotEmpty(nearSource);
        Assert.NotEmpty(farEnd);
        Assert.True(
            nearSource.Max() > farEnd.Max(),
            $"hladina neklesá: u zdroje {nearSource.Max()}, na konci {farEnd.Max()}");
    }

    [Fact]
    public void Sousedni_bloky_maji_ve_sdilenem_rohu_stejnou_vysku()
    {
        BlockRegistry registry = Registry();
        (ushort[] volume, byte[] fluid) = Ramp(registry, 6);

        MeshBuffer water = MeshWater(volume, fluid, registry);

        // Na každé svislé hraně mezi dvěma bloky musí všechny vrcholy ležet ve stejné výšce.
        // Právě tím se z hladiny stane rampa místo schodů: kdyby měl každý blok vlastní
        // rovinu, potkaly by se v rohu dvě různé výšky a mezi nimi by zůstal svislý zlom.
        for (int x = 1; x <= 5; x++)
        {
            List<float> heights = HeightsAt(water, x, 0f);

            if (heights.Count < 2)
            {
                continue;
            }

            float spread = heights.Max() - heights.Min();

            Assert.True(
                spread < 0.001f,
                $"na hraně x={x} se potkaly různé výšky (rozptyl {spread:F4}): {string.Join(", ", heights)}");
        }
    }

    [Fact]
    public void Bez_urovni_je_hladina_rovna()
    {
        BlockRegistry registry = Registry();
        (ushort[] volume, _) = Ramp(registry, 6);

        // Bez pole hladin se mesher chová jako dřív — všechna voda je plný blok. Na tom
        // stojí zpětná kompatibilita: Build se volá i z benchmarku a z testů bez hladin.
        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var cutout = new MeshBuffer();
        var water = new MeshBuffer();

        ChunkMesher.Build(volume, registry, opaque, transparent, default, cutout, water);

        List<float> nearSource = HeightsAt(water, 0f, 0f);
        List<float> farEnd = HeightsAt(water, 6f, 0f);

        Assert.NotEmpty(nearSource);
        Assert.NotEmpty(farEnd);
        Assert.Equal(nearSource.Max(), farEnd.Max(), precision: 3);
    }

    [Fact]
    public void Vodni_rostlina_ma_tentyz_vodni_mesh_a_navrch_vlastni_cutout()
    {
        BlockRegistry registry = BlockRegistry.Create(
        [
            new BlockDefinition
            {
                Id = "tesseris:water", Texture = "water", Opaque = false,
                Solid = false, Liquid = true,
            },
            new BlockDefinition
            {
                Id = "test:seagrass", Texture = "seagrass", Opaque = false,
                Solid = false, Aquatic = true, Shape = BlockShape.Cross, Cutout = true,
            },
        ]);
        ushort water = registry.IndexOf("tesseris:water");
        ushort seagrass = registry.IndexOf("test:seagrass");
        int index = ChunkMesher.PaddedIndex(4, 5, 6);

        var plain = new ushort[ChunkMesher.PaddedVolume];
        plain[index] = water;
        var planted = (ushort[])plain.Clone();
        planted[index] = seagrass;

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var plainCutout = new MeshBuffer();
        var plainWater = new MeshBuffer();
        ChunkMesher.Build(plain, registry, opaque, transparent, default, plainCutout, plainWater);

        var plantedCutout = new MeshBuffer();
        var plantedWater = new MeshBuffer();
        ChunkMesher.Build(
            planted, registry, opaque, transparent, default, plantedCutout, plantedWater);

        Assert.Equal(plainWater.Vertices.ToArray(), plantedWater.Vertices.ToArray());
        Assert.Equal(plainWater.Indices.ToArray(), plantedWater.Indices.ToArray());
        Assert.True(plantedCutout.IndexCount > 0);
        Assert.True(plainCutout.IsEmpty);
    }

    [Fact]
    public void Sloupec_vody_nad_sebou_nema_mezery()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");
        ushort water = registry.IndexOf("tesseris:water");

        var volume = new ushort[ChunkMesher.PaddedVolume];
        var fluid = new byte[ChunkMesher.PaddedVolume];

        Array.Fill(volume, BlockRegistry.Air);
        Array.Fill(fluid, FluidCell.Source);

        volume[ChunkMesher.PaddedIndex(0, 0, 0)] = stone;

        // Padající proud: tři patra doběhu nad sebou, každé s nízkou úrovní.
        for (int y = 1; y <= 3; y++)
        {
            volume[ChunkMesher.PaddedIndex(0, y, 0)] = water;
            fluid[ChunkMesher.PaddedIndex(0, y, 0)] = 3;
        }

        MeshBuffer water_ = MeshWater(volume, fluid, registry);

        // PATRO, NAD KTERÝM JE VODA, MUSÍ SAHAT KE STROPU. Spodní dvě patra mají nad sebou
        // další vodu, takže jejich vrcholy smí ležet jen na celých číslech — 1, 2, 3. Kdyby
        // se u nich uplatnila úroveň 3, skončily by ve 3/8 bloku (tedy na 1,375 a 2,375)
        // a mezi patry proudu by prosvítal vzduch. Přesně to hráč viděl.
        //
        // Nejvyšší patro vodu nad sebou nemá, takže tam hladina být SMÍ a bude na 3,375.
        var broken = new List<float>();
        ReadOnlySpan<float> vertices = water_.Vertices;

        for (int i = 0; i < water_.VertexCount; i++)
        {
            float y = vertices[(i * MeshBuffer.FloatsPerVertex) + 1];

            if (y < 3f && Math.Abs(y - MathF.Round(y)) > 0.001f)
            {
                broken.Add(y);
            }
        }

        Assert.True(
            broken.Count == 0,
            $"proud je přerušený, patra končí pod stropem: {string.Join(", ", broken.Distinct().Order())}");

        // Nahoře naopak hladina být má - tam voda opravdu končí.
        bool hasSurface = false;

        for (int i = 0; i < water_.VertexCount; i++)
        {
            float y = vertices[(i * MeshBuffer.FloatsPerVertex) + 1];

            if (y > 3f && y < 4f)
            {
                hasSurface = true;
            }
        }

        Assert.True(hasSurface, "nejvyšší patro nemá hladinu - kreslí se jako plný blok");
    }


    [Fact]
    public void Sucha_jama_pod_urovni_more_nedostane_podvodni_efekt()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        var volume = new ushort[ChunkMesher.PaddedVolume];
        Array.Fill(volume, BlockRegistry.Air);

        // Chunk, ve kterém leží hladina moře - přesně situace jámy vykopané ze břehu.
        int chunkY = TerrainGenerator.SeaLevel / Chunk.Size;
        int baseY = chunkY * Chunk.Size;

        // Dno jámy pár bloků POD hladinou, nad ním otevřený vzduch až ke stropu chunku.
        int floorY = TerrainGenerator.SeaLevel - baseY - 5;

        Assert.InRange(floorY, 0, Chunk.Size - 1);

        for (int z = 0; z < 4; z++)
        {
            for (int x = 0; x < 4; x++)
            {
                volume[ChunkMesher.PaddedIndex(x, floorY, z)] = stone;
            }
        }

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();

        ChunkMesher.Build(volume, registry, opaque, transparent, new Vector3i(0, chunkY, 0));

        // Příznak „nad tímhle je voda" se veze ve stínění jako přičtená dvojka. Suchý blok
        // ho mít nesmí, jinak dostane modrou clonu a kaustiky, přestože kolem není ani kapka.
        var wet = new List<float>();

        for (int i = 0; i < opaque.VertexCount; i++)
        {
            float shade = opaque.Vertices[(i * MeshBuffer.FloatsPerVertex) + 6];

            if (shade >= 1.5f)
            {
                wet.Add(shade);
            }
        }

        Assert.True(wet.Count == 0, $"{wet.Count} vrcholů suché jámy je označeno jako pod vodou");
    }


    [Fact]
    public void Prvni_prelivani_je_videt()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");
        ushort water = registry.IndexOf("tesseris:water");

        var volume = new ushort[ChunkMesher.PaddedVolume];
        var fluid = new byte[ChunkMesher.PaddedVolume];

        Array.Fill(volume, BlockRegistry.Air);
        Array.Fill(fluid, FluidCell.Source);

        // Zdroj a vedle něj první stupeň doběhu.
        for (int x = 0; x < 2; x++)
        {
            volume[ChunkMesher.PaddedIndex(x, 0, 0)] = stone;
            volume[ChunkMesher.PaddedIndex(x, 1, 0)] = water;
        }

        fluid[ChunkMesher.PaddedIndex(0, 1, 0)] = FluidCell.Source;
        fluid[ChunkMesher.PaddedIndex(1, 1, 0)] = FluidCell.MaxFlowing;

        MeshBuffer surface = MeshWater(volume, fluid, registry);

        // Vršek nad zdrojem proti vršku nad prvním doběhem. Dřív vycházely oba na 0,875
        // a mezi plnou vodou a prvním přelitím nebyl vidět žádný rozdíl.
        float atSource = HeightsAt(surface, 0f, 0f).Max();
        float atFlowing = HeightsAt(surface, 2f, 0f).Max();

        float step = atSource - atFlowing;

        Assert.True(
            step > 0.1f,
            $"první přelití není poznat: zdroj {atSource:F3}, doběh {atFlowing:F3}, rozdíl {step:F3}");
    }


    [Fact]
    public void Zdroj_neklesne_kvuli_sousednimu_dobehu()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");
        ushort water = registry.IndexOf("tesseris:water");

        var volume = new ushort[ChunkMesher.PaddedVolume];
        var fluid = new byte[ChunkMesher.PaddedVolume];

        Array.Fill(volume, BlockRegistry.Air);
        Array.Fill(fluid, FluidCell.Source);

        // Zdroj a za ním klesající doběh - jako když hráč vylije kbelík a voda se rozteče.
        for (int x = 0; x < 5; x++)
        {
            volume[ChunkMesher.PaddedIndex(x, 0, 0)] = stone;
            volume[ChunkMesher.PaddedIndex(x, 1, 0)] = water;
            fluid[ChunkMesher.PaddedIndex(x, 1, 0)] = (byte)Math.Max(1, FluidCell.Source - x);
        }

        MeshBuffer surface = MeshWater(volume, fluid, registry);

        // Vršek nad zdrojem musí zůstat v plné výšce. Průměrování rohů ho dřív stáhlo
        // dolů spolu se sousedním doběhem - hráč vylil vodu a propadla se mu i ta,
        // kterou právě položil.
        float atSource = HeightsAt(surface, 0f, 0f).Max();

        // Zdroj sahá ke stropu bloku. Snížení hladiny je dnes nulové, protože jakékoli
        // dělalo díru proti vodě pod vodou - viz ChunkMesher.WaterSurfaceDrop.
        Assert.Equal(2f - ChunkMesher.WaterSurfaceDrop, atSource, precision: 3);
    }


    [Fact]
    public void Hladina_navazuje_na_vodu_pod_vodou()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");
        ushort water = registry.IndexOf("tesseris:water");

        var volume = new ushort[ChunkMesher.PaddedVolume];
        var fluid = new byte[ChunkMesher.PaddedVolume];

        Array.Fill(volume, BlockRegistry.Air);
        Array.Fill(fluid, FluidCell.Source);

        // Hladina vedle sloupce vody. Voda pod vodou sahá ke stropu bloku, takže kdyby se
        // hladina kreslila níž, zůstala by mezi nimi svislá díra - a stěna se tam nekreslí,
        // protože z obou stran je voda. Přesně tím se hladina rozpadala na desky.
        for (int x = 0; x < 3; x++)
        {
            volume[ChunkMesher.PaddedIndex(x, 0, 0)] = stone;
            volume[ChunkMesher.PaddedIndex(x, 1, 0)] = water;
        }

        volume[ChunkMesher.PaddedIndex(1, 2, 0)] = water;

        MeshBuffer surface = MeshWater(volume, fluid, registry);

        // ROH U HRANICE MUSÍ DOSÁHNOUT KE STROPU.
        //
        // Prostřední blok má nad sebou vodu, takže sahá do 2. Sousedi jsou hladina, ta leží
        // níž. Mezi nimi se ale stěna NEKRESLÍ - z obou stran je voda - takže jediné, co
        // díře brání, je roh: v místě dotyku musí i hladina vystoupat do 2.
        //
        // Měří se svislice na hranici mezi levým sousedem a prostředním blokem, tedy x = 1.
        var atSeam = new List<float>();
        ReadOnlySpan<float> vertices = surface.Vertices;

        for (int i = 0; i < surface.VertexCount; i++)
        {
            int at = i * MeshBuffer.FloatsPerVertex;

            if (Math.Abs(vertices[at] - 1f) < 0.001f && vertices[at + 1] > 1.001f)
            {
                atSeam.Add(MathF.Round(vertices[at + 1], 4));
            }
        }

        Assert.True(atSeam.Count > 0, "na hranici nejsou žádné vrcholy hladiny");

        Assert.True(
            atSeam.Min() >= 1.999f,
            $"hladina na hranici nedosáhla ke stropu, výšky: {string.Join(", ", atSeam.Distinct().Order())}");
    }

}
