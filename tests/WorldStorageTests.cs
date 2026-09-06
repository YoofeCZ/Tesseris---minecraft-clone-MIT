using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy trvalého ukládání světa.
///
/// <para>Do 28. 7. 2026 se svět neukládal vůbec: <c>RemoveChunk</c> chunk prostě zahodil
/// a při návratu se vygeneroval znovu ze šumu. Hráč tedy přišel o všechno, co postavil
/// nebo vytesal, jakmile odešel za hranici uvolňování — při dohledu 12 zhruba 480 bloků.
/// U hry, jejíž hlavní nápad je tesání, to je zásadní.</para>
///
/// <para>Formát je čistá funkce bez I/O, takže se dá otestovat voxel po voxelu. Testy
/// se souborem si píšou do dočasného adresáře a po sobě uklidí.</para>
/// </summary>
public sealed class WorldStorageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tesseris-test-" + Guid.NewGuid().ToString("N"));

    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Chunk s pestrým obsahem, ať se otestuje i paleta a širší index.</summary>
    private static Chunk BusyChunk(BlockRegistry registry)
    {
        ushort[] blocks =
        [
            registry.IndexOf("tesseris:stone"),
            registry.IndexOf("tesseris:dirt"),
            registry.IndexOf("tesseris:grass"),
            registry.IndexOf("tesseris:sand"),
            registry.IndexOf("tesseris:gravel"),
            registry.IndexOf("tesseris:planks"),
            registry.IndexOf("tesseris:glass"),
        ];

        var chunk = new Chunk(blocks[0]);

        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    // Sedm různých bloků vynutí čtyřbitový index.
                    chunk.SetBlock(x, y, z, blocks[(x + y + z) % blocks.Length]);
                }
            }
        }

        return chunk;
    }

    private static void AssertSameBlocks(Chunk expected, Chunk actual)
    {
        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    Assert.True(
                        expected.GetBlock(x, y, z) == actual.GetBlock(x, y, z),
                        $"Blok na ({x}, {y}, {z}) se liší: {expected.GetBlock(x, y, z)} proti {actual.GetBlock(x, y, z)}.");
                }
            }
        }
    }

    [Fact]
    public void Chunk_prezije_zakodovani_a_dekodovani()
    {
        BlockRegistry registry = Registry();
        Chunk original = BusyChunk(registry);

        Chunk restored = ChunkSerializer.Decode(ChunkSerializer.Encode(original, registry), registry);

        AssertSameBlocks(original, restored);
        Assert.Equal(original.BitsPerIndex, restored.BitsPerIndex);
        Assert.Equal(original.PaletteCount, restored.PaletteCount);
    }

    [Fact]
    public void Homogenni_chunk_prezije_zakodovani()
    {
        BlockRegistry registry = Registry();
        ushort stone = registry.IndexOf("tesseris:stone");
        var original = new Chunk(stone);

        Chunk restored = ChunkSerializer.Decode(ChunkSerializer.Encode(original, registry), registry);

        Assert.True(restored.IsHomogeneous);
        Assert.Equal(stone, restored.HomogeneousBlock);
        AssertSameBlocks(original, restored);
    }

    /// <summary>
    /// Mikrovoxely jsou to hlavní, co se ukládat musí — je to jediná věc, kterou hráč
    /// vyrobí a generátor ji nikdy nespočítá znovu.
    /// </summary>
    [Fact]
    public void Otesany_blok_prezije_zakodovani_vcetne_tvaru()
    {
        BlockRegistry registry = Registry();
        Chunk original = BusyChunk(registry);

        ushort stone = registry.IndexOf("tesseris:stone");
        ushort planks = registry.IndexOf("tesseris:planks");

        MicroBlock micro = MicroBlock.FromSolid(stone).Clone();
        micro.SetMaterial(0, 0, 0, 0);
        micro.SetMaterial(1, 2, 3, 0);
        micro.SetMaterial(15, 15, 15, planks);
        original.SetMicro(4, 5, 6, micro);

        Chunk restored = ChunkSerializer.Decode(ChunkSerializer.Encode(original, registry), registry);

        Assert.Equal(1, restored.MicroCount);

        MicroBlock? loaded = restored.GetMicro(4, 5, 6);
        Assert.NotNull(loaded);

        Assert.Equal(micro.SolidCount, loaded.SolidCount);
        Assert.Equal(0, loaded.GetMaterial(0, 0, 0));
        Assert.Equal(0, loaded.GetMaterial(1, 2, 3));
        Assert.Equal(planks, loaded.GetMaterial(15, 15, 15));
        Assert.Equal(stone, loaded.GetMaterial(8, 8, 8));

        // Deduplikace stojí na obsahovém hashi, takže se musí shodovat i ten.
        Assert.True(micro.HasSameContent(loaded));
    }

    /// <summary>
    /// V souboru jsou JMÉNA bloků, ne indexy. Registry se řadí abecedně, takže přidání
    /// jediného typu bloku posune indexy všech za ním — a uložený svět by se tím tiše
    /// proměnil v nesmysl.
    /// </summary>
    [Fact]
    public void Ulozeny_svet_prezije_pridani_noveho_bloku_do_registry()
    {
        BlockRegistry before = Registry();
        Chunk original = BusyChunk(before);
        byte[] payload = ChunkSerializer.Encode(original, before);

        // Registry, do které přibyl blok na začátku abecedy — tím se posunou všechny
        // ostatní indexy.
        var extra = new BlockDefinition
        {
            Id = "tesseris:aaa_novy_blok",
            Texture = "stone",
            Material = BlockMaterial.Stone,
            Opaque = true,
        };

        BlockRegistry after = BlockRegistry.Create([.. Definitions(before), extra]);

        Assert.NotEqual(before.IndexOf("tesseris:stone"), after.IndexOf("tesseris:stone"));

        Chunk restored = ChunkSerializer.Decode(payload, after);

        // Bloky musí sedět podle JMÉNA, ne podle původního čísla.
        for (int y = 0; y < Chunk.Size; y += 7)
        {
            for (int z = 0; z < Chunk.Size; z += 5)
            {
                for (int x = 0; x < Chunk.Size; x += 3)
                {
                    string expected = before.Definition(original.GetBlock(x, y, z)).Id;
                    string actual = after.Definition(restored.GetBlock(x, y, z)).Id;

                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    private static IEnumerable<BlockDefinition> Definitions(BlockRegistry registry)
    {
        // Vzduch se přeskakuje: registry si ho doplňuje sama.
        for (ushort i = 1; i < registry.Count; i++)
        {
            yield return registry.Definition(i);
        }
    }

    [Fact]
    public void Cizi_data_se_odmitnou_misto_tichého_nesmyslu()
    {
        BlockRegistry registry = Registry();
        byte[] junk = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Throws<InvalidDataException>(() => ChunkSerializer.Decode(junk, registry));
    }

    /// <summary>
    /// Sloty a regiony musí vycházet i pro záporné souřadnice. Operátor % v C# vrací
    /// u záporných čísel záporný zbytek, na což se dá snadno naběhnout.
    /// </summary>
    [Fact]
    public void Sloty_vychazeji_i_pro_zaporne_souradnice()
    {
        var seen = new HashSet<(Vector2i Region, int Slot)>();

        for (int x = -40; x <= 40; x += 3)
        {
            for (int z = -40; z <= 40; z += 3)
            {
                foreach (int y in (ReadOnlySpan<int>)[0, 7, 31])
                {
                    var position = new Vector3i(x, y, z);
                    int slot = RegionFile.SlotOf(position);

                    Assert.InRange(slot, 0, RegionFile.SlotCount - 1);

                    // Dvě různé pozice nesmí padnout do téhož slotu téhož regionu.
                    Assert.True(
                        seen.Add((RegionFile.RegionOf(position), slot)),
                        $"Pozice {position} se sráží s jinou ve slotu {slot}.");
                }
            }
        }
    }

    [Fact]
    public void Region_prezije_zavreni_a_znovuotevreni()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "r.0.0.vxr");

        byte[] payload = ChunkSerializer.Encode(BusyChunk(Registry()), Registry());

        using (RegionFile region = RegionFile.Open(path))
        {
            region.Write(17, payload);
            region.Write(4000, payload);
        }

        using (RegionFile reopened = RegionFile.Open(path))
        {
            Assert.Null(reopened.Read(18));

            byte[]? first = reopened.Read(17);
            byte[]? second = reopened.Read(4000);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(payload, first);
            Assert.Equal(payload, second);
        }
    }

    /// <summary>
    /// Celá cesta: uložit, zapomenout, načíst. Přesně to, co se ve hře stane, když hráč
    /// odejde z dohledu a vrátí se.
    /// </summary>
    [Fact]
    public void Upraveny_chunk_se_vrati_i_po_uvolneni_z_pameti()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();

        var position = new Vector3i(-3, 11, 7);
        Chunk original = BusyChunk(registry);
        original.MarkModified();

        using (var storage = new WorldStorage(_directory, jobs, registry))
        {
            storage.Save(position, original);
            storage.FlushAll();
        }

        using (var reopened = new WorldStorage(_directory, jobs, registry))
        {
            Chunk? loaded = reopened.TryLoad(position);

            Assert.NotNull(loaded);
            AssertSameBlocks(original, loaded);
            Assert.Equal(1, reopened.LoadedChunks);
        }
    }

    [Fact]
    public void Neupraveny_chunk_se_neuklada()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();
        using var storage = new WorldStorage(_directory, jobs, registry);

        var position = new Vector3i(2, 3, 4);

        // Chunk bez MarkModified je výsledek generátoru — ten je spočitatelný ze seedu
        // a ukládat ho by znamenalo psát na disk celý svět.
        storage.Save(position, BusyChunk(registry));
        storage.FlushAll();

        Assert.Equal(0, storage.SavedChunks);
        Assert.Null(storage.TryLoad(position));
    }
}
