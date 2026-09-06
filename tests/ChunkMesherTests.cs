using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy greedy meshingu. Pracují přímo nad odsazeným objemem, takže nepotřebují GL kontext
/// ani svět.
/// </summary>
public sealed class ChunkMesherTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:glass", Texture = "glass", Opaque = false },

        // Druhý průhledný materiál je tu proto, aby šlo odlišit "stejný soused" od
        // "průhledný soused" — mezi dvěma různými průhlednými se stěna kreslit musí.
        new BlockDefinition { Id = "test:water", Texture = "water", Opaque = false },
    ]);

    private static ushort[] EmptyVolume() => new ushort[ChunkMesher.PaddedVolume];

    private static (MeshBuffer Opaque, MeshBuffer Transparent) Mesh(ushort[] volume, BlockRegistry registry)
    {
        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent);
        return (opaque, transparent);
    }

    private static int Quads(MeshBuffer buffer) => buffer.IndexCount / 6;

    /// <summary>Registry se stromem, tedy s bloky, které se sázejí po dílcích.</summary>
    private static BlockRegistry TreeRegistry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:log", Texture = "log", Opaque = true },
        new BlockDefinition { Id = "test:leaves", Texture = "leaves", Opaque = false, Cutout = true },
    ]);

    /// <summary>Odsazený objem masek, kde je všude plný blok.</summary>
    private static byte[] FullPieces()
    {
        byte[] pieces = new byte[ChunkMesher.PaddedVolume];
        Array.Fill(pieces, PieceMask.Full);
        return pieces;
    }

    [Theory]
    [InlineData(PieceMask.PanelAlongX)]
    [InlineData(PieceMask.PanelAlongZ)]
    public void Dvere_sdileji_na_obou_stranach_stejne_lokalni_uv(byte mask)
    {
        BlockRegistry registry = BlockRegistry.Create(
        [
            new BlockDefinition
            {
                Id = "test:door",
                Texture = "door",
                Opaque = true,
                Shape = BlockShape.Door,
                Pieces = PieceMask.PanelAlongX,
            },
        ]);
        ushort door = registry.IndexOf("test:door");
        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();
        int index = ChunkMesher.PaddedIndex(5, 5, 5);
        volume[index] = door;
        pieces[index] = mask;

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        ReadOnlySpan<float> vertices = opaque.Vertices;
        int broadFaces = 0;
        for (int quad = 0; quad * 4 < opaque.VertexCount; quad++)
        {
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                int vertex = ((quad * 4) + corner) * MeshBuffer.FloatsPerVertex;
                minX = MathF.Min(minX, vertices[vertex]);
                maxX = MathF.Max(maxX, vertices[vertex]);
                minZ = MathF.Min(minZ, vertices[vertex + 2]);
                maxZ = MathF.Max(maxZ, vertices[vertex + 2]);
            }

            bool broad = mask == PieceMask.PanelAlongX
                ? maxX - minX > 0.9f && maxZ - minZ < 0.01f
                : maxZ - minZ > 0.9f && maxX - minX < 0.01f;
            if (!broad)
            {
                continue;
            }

            broadFaces++;
            for (int corner = 0; corner < 4; corner++)
            {
                int vertex = ((quad * 4) + corner) * MeshBuffer.FloatsPerVertex;
                float localAxis = mask == PieceMask.PanelAlongX
                    ? vertices[vertex] - 5f
                    : vertices[vertex + 2] - 5f;
                float u = vertices[vertex + 3];
                float expected = PieceMask.DoorUvReversed(mask)
                    ? 1f - localAxis
                    : localAxis;

                // Obě fyzické strany panelu musí používat tutéž lokální souřadnici.
                // Při obejití dveří se pak klika nepřestěhuje na druhý okraj dveří.
                Assert.True(expected < 0.01f ? u < 0.01f : u > 0.99f);
            }
        }

        Assert.Equal(2, broadFaces);
    }

    [Theory]
    [InlineData(BlockShape.Door)]
    [InlineData(BlockShape.Trapdoor)]
    public void Animovany_panel_se_nekresli_pod_dynamickou_kopii(BlockShape shape)
    {
        BlockRegistry registry = BlockRegistry.Create(
        [
            new BlockDefinition
            {
                Id = "test:panel",
                Texture = "panel",
                Opaque = true,
                Shape = shape,
            },
        ]);
        ushort panel = registry.IndexOf("test:panel");
        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();
        int index = ChunkMesher.PaddedIndex(5, 5, 5);
        volume[index] = panel;
        pieces[index] = shape == BlockShape.Door
            ? PieceMask.DoorAnimatingState(PieceMask.DoorState(
                PieceMask.DoorSouth, hingeRight: false, open: true))
            : PieceMask.TrapdoorAnimatingState(PieceMask.TrapdoorOpenBottomAlongX);

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        Assert.True(opaque.IsEmpty);
        Assert.True(transparent.IsEmpty);
    }

    /// <summary>
    /// Blok s jediným dílkem se kreslí jako krychlička o poloviční hraně v tom rohu, kam
    /// dílek patří.
    ///
    /// <para>Je to podmínka toho, aby kolize seděla: <see cref="PieceMask.Colliders"/> vrací
    /// tytéž kvádry a podle nich se do bloku naráží i míří.</para>
    /// </summary>
    [Fact]
    public void Blok_s_jednim_dilkem_je_kryhlicka_o_polovicni_hrane()
    {
        BlockRegistry registry = TreeRegistry();
        ushort log = registry.IndexOf("test:log");

        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();

        int index = ChunkMesher.PaddedIndex(5, 5, 5);
        volume[index] = log;
        pieces[index] = PieceMask.With(PieceMask.Empty, 0, 0, 0);

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        // Uzavřené těleso, takže šest stěn a každá jednou.
        Assert.Equal(6, Quads(opaque));

        ReadOnlySpan<float> vertices = opaque.Vertices;

        for (int i = 0; i < opaque.VertexCount; i++)
        {
            float x = vertices[i * MeshBuffer.FloatsPerVertex];
            float y = vertices[(i * MeshBuffer.FloatsPerVertex) + 1];
            float z = vertices[(i * MeshBuffer.FloatsPerVertex) + 2];

            Assert.InRange(x, 5f, 5.5f);
            Assert.InRange(y, 5f, 5.5f);
            Assert.InRange(z, 5f, 5.5f);
        }
    }

    /// <summary>
    /// Blok rozdělený na dílky nezakrývá stěny sousedů — kolem dílků je vidět skrz.
    /// Kdyby zakrýval, byla by kolem stromu v terénu díra.
    /// </summary>
    [Fact]
    public void Blok_s_dilky_nezakryva_stenu_souseda()
    {
        BlockRegistry registry = TreeRegistry();
        ushort log = registry.IndexOf("test:log");
        ushort stone = registry.IndexOf("test:stone");

        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();

        volume[ChunkMesher.PaddedIndex(5, 5, 5)] = stone;

        int above = ChunkMesher.PaddedIndex(5, 6, 5);
        volume[above] = log;
        pieces[above] = PieceMask.With(PieceMask.Empty, 0, 0, 0);

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        // Kámen má všech šest stěn včetně horní, dílek k tomu přidá svých šest.
        Assert.Equal(12, Quads(opaque));
    }

    /// <summary>
    /// Sousední dílky se dotýkají, takže stěnu mezi sebou nekreslí. Bez toho by koruna
    /// stála na osminásobku trojúhelníků, z nichž většina je schovaná uvnitř.
    /// </summary>
    [Fact]
    public void Sousedni_dilky_nekresli_vnitrni_stenu()
    {
        BlockRegistry registry = TreeRegistry();
        ushort log = registry.IndexOf("test:log");

        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();

        int index = ChunkMesher.PaddedIndex(5, 5, 5);
        volume[index] = log;
        pieces[index] = PieceMask.With(PieceMask.With(PieceMask.Empty, 0, 0, 0), 1, 0, 0);

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        // Dvě krychličky vedle sebe: dvanáct stěn minus dvě, které se dotýkají.
        Assert.Equal(10, Quads(opaque));
    }

    /// <summary>
    /// Dílky navazují i přes hranici bloku. Právě kvůli tomu jemná mřížka vznikla: kmen
    /// a listí se musí dotýkat bez ohledu na to, kudy hranice bloků vedou.
    /// </summary>
    [Fact]
    public void Dilky_navazuji_pres_hranici_bloku()
    {
        BlockRegistry registry = TreeRegistry();
        ushort log = registry.IndexOf("test:log");

        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();

        // Dílek u pravé stěny jednoho bloku a dílek u levé stěny toho vedlejšího.
        int left = ChunkMesher.PaddedIndex(5, 5, 5);
        int right = ChunkMesher.PaddedIndex(6, 5, 5);

        volume[left] = log;
        volume[right] = log;
        pieces[left] = PieceMask.With(PieceMask.Empty, 1, 0, 0);
        pieces[right] = PieceMask.With(PieceMask.Empty, 0, 0, 0);

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        // Kdyby se dílky přes hranici neviděly, bylo by stěn dvanáct.
        Assert.Equal(10, Quads(opaque));
    }

    /// <summary>Plná maska znamená obyčejný blok, který se dál slučuje greedy meshingem.</summary>
    [Fact]
    public void Plna_maska_se_chova_jako_obycejny_blok()
    {
        BlockRegistry registry = TreeRegistry();
        ushort stone = registry.IndexOf("test:stone");

        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();

        for (int x = 0; x < Chunk.Size; x++)
        {
            volume[ChunkMesher.PaddedIndex(x, 0, 0)] = stone;
        }

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        // Řada 32 bloků se slučuje do šesti obdélníků, přesně jako bez masek.
        Assert.Equal(6, Quads(opaque));
    }

    [Fact]
    public void Osamely_blok_ma_sest_sten()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(5, 5, 5)] = stone;

        (MeshBuffer opaque, MeshBuffer transparent) = Mesh(volume, registry);

        Assert.Equal(6, Quads(opaque));
        Assert.True(transparent.IsEmpty);
    }

    [Fact]
    public void Rovna_plocha_se_sloucí_do_jednoho_obdelniku()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        // Jedna celá vrstva 32×32 uvnitř chunku.
        ushort[] volume = EmptyVolume();
        for (int z = 0; z < Chunk.Size; z++)
        {
            for (int x = 0; x < Chunk.Size; x++)
            {
                volume[ChunkMesher.PaddedIndex(x, 0, z)] = stone;
            }
        }

        (MeshBuffer opaque, _) = Mesh(volume, registry);

        // Horní a spodní stěna po jednom velkém obdélníku, plus čtyři boky.
        // Bez slučování by to bylo 32*32*2 + 32*4 = 2176 obdélníků.
        Assert.Equal(6, Quads(opaque));
    }

    [Fact]
    public void Vnitrek_plneho_chunku_se_nekresli()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        ushort[] volume = EmptyVolume();
        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    volume[ChunkMesher.PaddedIndex(x, y, z)] = stone;
                }
            }
        }

        (MeshBuffer opaque, _) = Mesh(volume, registry);

        // Vidět je jen šest vnějších stěn krychle, každá jako jeden obdélník.
        Assert.Equal(6, Quads(opaque));
    }

    [Fact]
    public void Soused_v_lemu_zakryje_stenu_na_hranici()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(0, 0, 0)] = stone;

        (MeshBuffer withoutNeighbour, _) = Mesh(volume, registry);
        Assert.Equal(6, Quads(withoutNeighbour));

        // Blok v lemu na souřadnici -1 patří sousednímu chunku a musí přilehlou stěnu zakrýt.
        volume[ChunkMesher.PaddedIndex(-1, 0, 0)] = stone;

        (MeshBuffer withNeighbour, _) = Mesh(volume, registry);
        Assert.Equal(5, Quads(withNeighbour));
    }

    [Fact]
    public void Pruhledny_blok_jde_do_vlastniho_bufferu()
    {
        BlockRegistry registry = Registry();
        ushort glass = registry.IndexOf("test:glass");

        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(4, 4, 4)] = glass;

        (MeshBuffer opaque, MeshBuffer transparent) = Mesh(volume, registry);

        Assert.True(opaque.IsEmpty);
        Assert.Equal(6, Quads(transparent));
    }

    [Fact]
    public void Dve_sousedni_skla_mezi_sebou_stenu_nekresli()
    {
        BlockRegistry registry = Registry();
        ushort glass = registry.IndexOf("test:glass");

        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(4, 4, 4)] = glass;
        volume[ChunkMesher.PaddedIndex(5, 4, 4)] = glass;

        (_, MeshBuffer transparent) = Mesh(volume, registry);

        // Šest, ne osm: vnitřní přepážka mezi skly odpadá a zbylé stěny se navíc sloučí,
        // takže z dvojice bloků vyjde kvádr 2×1×1 s šesti stěnami. Kdyby se přepážka
        // kreslila, byly by quady dva navíc.
        Assert.Equal(6, Quads(transparent));
    }

    [Fact]
    public void Dva_ruzne_pruhledne_materialy_stenu_mezi_sebou_kresli()
    {
        BlockRegistry registry = Registry();
        ushort glass = registry.IndexOf("test:glass");
        ushort water = registry.IndexOf("test:water");

        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(4, 4, 4)] = glass;
        volume[ChunkMesher.PaddedIndex(5, 4, 4)] = water;

        (_, MeshBuffer transparent) = Mesh(volume, registry);

        // Každý blok si nechá všech šest stěn včetně té na rozhraní — přes průhledný
        // materiál je vidět, takže rozhraní musí být vykreslené.
        Assert.Equal(12, Quads(transparent));
    }

    [Fact]
    public void Pruhledny_soused_nepruhlednemu_stenu_nezakryje()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");
        ushort glass = registry.IndexOf("test:glass");

        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(4, 4, 4)] = stone;
        volume[ChunkMesher.PaddedIndex(5, 4, 4)] = glass;

        (MeshBuffer opaque, MeshBuffer transparent) = Mesh(volume, registry);

        // Kámen si nechá všech šest stěn, protože přes sklo je na něj vidět.
        Assert.Equal(6, Quads(opaque));

        // Sklo jich má jen pět: stěnu přiléhající ke kameni není přes co vidět, takže odpadá.
        Assert.Equal(5, Quads(transparent));
    }

    [Fact]
    public void Stineni_rohu_se_lisi_podle_okoli()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        // Blok s jedním sousedem po straně: dva rohy horní stěny musí být tmavší.
        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(10, 10, 10)] = stone;
        volume[ChunkMesher.PaddedIndex(11, 11, 10)] = stone;

        (MeshBuffer opaque, _) = Mesh(volume, registry);

        // Stínění je poslední složka vrcholu.
        var shades = new HashSet<float>();
        for (int i = 0; i < opaque.VertexCount; i++)
        {
            shades.Add(opaque.Vertices[(i * MeshBuffer.FloatsPerVertex) + 6]);
        }

        Assert.True(shades.Count > 1, "Se sousedem po straně musí vzniknout aspoň dvě různé úrovně stínění.");
    }

    [Fact]
    public void Rozdilne_stineni_zabrani_slouceni()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        // Řada osmi bloků je bez okolí jeden obdélník na každé straně.
        ushort[] plain = EmptyVolume();
        for (int x = 0; x < 8; x++)
        {
            plain[ChunkMesher.PaddedIndex(10 + x, 10, 10)] = stone;
        }

        (MeshBuffer plainMesh, _) = Mesh(plain, registry);
        int plainQuads = Quads(plainMesh);

        // Přidaný blok nad koncem řady zastíní jen část horní stěny, takže se rozpadne.
        ushort[] shaded = (ushort[])plain.Clone();
        shaded[ChunkMesher.PaddedIndex(10, 11, 9)] = stone;

        (MeshBuffer shadedMesh, _) = Mesh(shaded, registry);

        Assert.True(
            Quads(shadedMesh) > plainQuads,
            "Nerovnoměrné stínění musí zabránit sloučení do jednoho obdélníku.");
    }

    [Fact]
    public void Kazdy_obdelnik_ma_ctyri_vrcholy_a_sest_indexu()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        ushort[] volume = EmptyVolume();
        volume[ChunkMesher.PaddedIndex(1, 1, 1)] = stone;

        (MeshBuffer opaque, _) = Mesh(volume, registry);

        Assert.Equal(6 * 4, opaque.VertexCount);
        Assert.Equal(6 * 6, opaque.IndexCount);
        Assert.All(opaque.Indices.ToArray(), index => Assert.True(index < opaque.VertexCount));
    }

    [Fact]
    public void Prilis_maly_objem_je_odmitnut()
    {
        BlockRegistry registry = Registry();
        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();

        Assert.Throws<ArgumentException>(
            () => ChunkMesher.Build(new ushort[10], registry, opaque, transparent));
    }

    [Fact]
    public void Opakovane_meshovani_dava_stejny_vysledek()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("test:stone");

        ushort[] volume = EmptyVolume();
        for (int i = 0; i < 40; i++)
        {
            volume[ChunkMesher.PaddedIndex(i % 20, i % 7, i % 13)] = stone;
        }

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();

        ChunkMesher.Build(volume, registry, opaque, transparent);
        float[] first = opaque.Vertices.ToArray();

        // Buffery se recyklují; druhý průchod nesmí nic zdědit z prvního.
        ChunkMesher.Build(volume, registry, opaque, transparent);
        float[] second = opaque.Vertices.ToArray();

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Plný blok vedle bloku téhož druhu rozděleného na dílky si stěnu mezi sebou NESCHOVÁ.
    ///
    /// <para>Pravidlo „dva stejné bloky mezi sebou stěnu nekreslí" platí jen pro dvě plné
    /// kostky. Když je soused rozdělený na dílky, vyplňuje jen část svého objemu — plný blok
    /// by tedy vynechal stěnu, na jejímž místě není nic. Ve hře z toho byly průhledné čtverce
    /// v koruně, kterými bylo vidět skrz na oblohu.</para>
    /// </summary>
    [Fact]
    public void Plny_blok_vedle_dilkovaneho_tehoz_druhu_stenu_neschova()
    {
        BlockRegistry registry = TreeRegistry();
        ushort leaves = registry.IndexOf("test:leaves");

        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();

        int solid = ChunkMesher.PaddedIndex(5, 5, 5);
        int split = ChunkMesher.PaddedIndex(6, 5, 5);

        volume[solid] = leaves;
        volume[split] = leaves;

        // Soused má jediný dílek, a to na odvrácené straně — mezi bloky tedy nic není.
        pieces[split] = PieceMask.With(PieceMask.Empty, 1, 0, 0);

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        ReadOnlySpan<float> vertices = transparent.Vertices;
        bool wallDrawn = false;

        // Stěna plného bloku směrem k sousedovi leží v rovině x = 6.
        for (int i = 0; i < transparent.VertexCount; i++)
        {
            float x = vertices[i * MeshBuffer.FloatsPerVertex];
            float z = vertices[(i * MeshBuffer.FloatsPerVertex) + 2];

            if (MathF.Abs(x - 6f) < 1e-4f && z < 6f)
            {
                wallDrawn = true;
                break;
            }
        }

        Assert.True(
            wallDrawn,
            "Plný blok vynechal stěnu k sousedovi, který je rozdělený na dílky — vznikne průhledná díra.");
    }

    /// <summary>
    /// Kus bloku dostane jen ten výřez dlaždice, který v bloku zabírá — ne celou texturu.
    ///
    /// <para>Kdyby dostal celou, roztáhla by se přes něj. Přesně tak vypadalo listí kolem
    /// kmene: čtyři pruhy, každý s celou kresbou nataženou na čtvrtinu šířky.</para>
    /// </summary>
    [Fact]
    public void Dilek_nese_jen_svuj_vyrez_dlazdice()
    {
        BlockRegistry registry = TreeRegistry();
        ushort log = registry.IndexOf("test:log");

        ushort[] volume = EmptyVolume();
        byte[] pieces = FullPieces();

        int index = ChunkMesher.PaddedIndex(5, 5, 5);
        volume[index] = log;
        pieces[index] = PieceMask.With(PieceMask.Empty, 0, 0, 0);

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        ChunkMesher.Build(volume, registry, opaque, transparent, default, null, null, pieces);

        ReadOnlySpan<float> vertices = opaque.Vertices;

        // Měří se ROZPĚTÍ UV na každé stěně, ne absolutní hodnota. Stěna viděná z opačné
        // strany má UV zrcadlené, takže dílek u počátku bloku na ní zabírá druhou polovinu
        // dlaždice — a to je správně. Roztažení se pozná na tom, že rozpětí sahá přes celou
        // dlaždici místo přes tu část, kterou dílek v bloku zabírá.
        for (int quad = 0; quad * 4 < opaque.VertexCount; quad++)
        {
            float minU = float.MaxValue;
            float maxU = float.MinValue;

            for (int corner = 0; corner < 4; corner++)
            {
                float u = vertices[(((quad * 4) + corner) * MeshBuffer.FloatsPerVertex) + 3];

                minU = MathF.Min(minU, u);
                maxU = MathF.Max(maxU, u);
            }

            Assert.True(
                maxU - minU <= 0.55f,
                $"Stěna {quad} má UV široké {maxU - minU:F2} dlaždice — textura se přes dílek roztáhla.");
        }
    }
}
