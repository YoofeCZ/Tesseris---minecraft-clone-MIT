using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy správy dlaždic vzdáleného terénu.
///
/// <para>Vzniklo z chyby nahlášené z běhu hry: „při načítání lodu tam vznikají takové díry,
/// problikne, zmizí ten kousek, vidím skrz". Příčinou bylo, že odložené dlaždice se
/// hromadily přes víc přepočtů, takže se týž klíč mohl octnout zároveň mezi odloženými
/// i mezi chtěnými — a úklid odložených ho pak vyhodil z rendereru, i když byl potřeba.
/// Ve slovníku přitom zůstal ve stavu <c>Ready</c>, takže ho už nikdo nepostavil znovu
/// a díra byla trvalá.</para>
///
/// <para>Test žádné GL ani Vulkan nepotřebuje: renderer se nahradí množinou klíčů, což
/// je přesně to, co se v něm pro tenhle účel sleduje.</para>
/// </summary>
public sealed class FarTerrainTests
{
    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    /// <summary>
    /// Náhrada rendereru: drží klíče dlaždic, které by se kreslily. Zároveň hraje roli
    /// smyčky ve <c>TesserisWindow.UpdateFarTerrain</c>, aby se testovalo totéž pořadí
    /// kroků jako ve hře.
    /// </summary>
    private sealed class FakeRenderer
    {
        public HashSet<FarTerrain.TileKey> Tiles { get; } = [];

        /// <summary>Jeden „frame": přepočet, zahození zastaralých a nahrání hotových.</summary>
        public void Frame(FarTerrain far, Vector3 camera)
        {
            foreach (FarTerrain.TileKey stale in far.Update(camera))
            {
                Tiles.Remove(stale);
            }

            while (far.TryTakeFinished(out FarTerrain.FinishedTile tile))
            {
                // Přesná kopie pořadí ze hry: dlaždice, která už není chtěná, se musí
                // uvolnit z rozpracovaných, jinak se už nikdy nezadá znovu.
                if (!far.IsWanted(tile.Key))
                {
                    far.Discard(tile.Key);
                    far.Recycle(tile);
                    continue;
                }

                Tiles.Add(tile.Key);
                far.MarkUploaded(tile.Key);
                far.Recycle(tile);
            }
        }

        /// <summary>Nechá systém doběhnout do klidu na jednom místě.</summary>
        public void Settle(FarTerrain far, Vector3 camera, int frames = 400)
        {
            for (int i = 0; i < frames; i++)
            {
                Frame(far, camera);

                if (far.PendingTiles == 0 && far.RetiredTiles == 0 && Tiles.Count == far.TileCount)
                {
                    return;
                }

                Thread.Sleep(1);
            }
        }
    }

    /// <summary>
    /// Jedno pásmo a úzký prstenec, aby test nestavěl stovky dlaždic. Vnitřní poloměr
    /// leží těsně pod vnějším, takže chtěných dlaždic je pár desítek.
    /// </summary>
    private static FarTerrain Build(BlockRegistry registry, JobSystem jobs) =>
        new(new TerrainGenerator(registry, seed: 20260728), registry, jobs)
        {
            ActiveLevels = 1,
            NearRadius = 600f,
            BuildsPerFrame = 64,
        };

    [Fact]
    public void Dlazdice_ktera_se_vratila_do_dosahu_zustane_v_rendereru()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();
        FarTerrain far = Build(registry, jobs);
        var renderer = new FakeRenderer();

        var home = new Vector3(0f, 200f, 0f);
        renderer.Settle(far, home);

        Assert.NotEmpty(renderer.Tiles);
        Assert.Equal(far.TileCount, renderer.Tiles.Count);

        // Odejít za hranici přepočtu (ta je 64 bloků) a hned se vrátit, aniž by se nová
        // sada stihla dostavět. Přesně tohle dělá hráč, když se ve hře otočí a jde zpátky.
        renderer.Frame(far, new Vector3(200f, 200f, 0f));
        renderer.Frame(far, home);

        renderer.Settle(far, home);

        // Nic chtěného nesmí v rendereru chybět. Před opravou tady zůstávaly dlaždice,
        // které systém považoval za nahrané, ale v rendereru nebyly — a byla to díra.
        Assert.Equal(far.TileCount, renderer.Tiles.Count);
        Assert.Equal(0, far.RetiredTiles);
    }

    [Fact]
    public void Chuze_tam_a_zpet_nenechava_odlozene_dlazdice()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();
        FarTerrain far = Build(registry, jobs);
        var renderer = new FakeRenderer();

        var home = new Vector3(0f, 200f, 0f);
        renderer.Settle(far, home);

        // Deset přechodů tam a zpět bez čekání na dostavění. Bez opravy se odložené
        // klíče hromadí a každý z nich pořád stojí draw call.
        for (int i = 0; i < 10; i++)
        {
            renderer.Frame(far, new Vector3(0f, 200f, 300f));
            renderer.Frame(far, home);
        }

        renderer.Settle(far, home);

        Assert.Equal(0, far.RetiredTiles);
        Assert.Equal(far.TileCount, renderer.Tiles.Count);
    }

    /// <summary>
    /// Pokrytí musí být rozdělení, ne hromada: každý bod v dosahu LOD a mimo blízké okolí
    /// leží pod <b>právě jednou</b> dlaždicí.
    ///
    /// <para>Vzniklo z chyby, kvůli které se z dálky terén „kombinoval": úrovně se
    /// vybíraly nezávisle na sobě, takže dlaždice přesahující hranici pásma se dostala
    /// do obou úrovní naráz a dvě různě jemné plochy se praly o hloubku.</para>
    /// </summary>
    [Fact]
    public void Kazdy_bod_pokryva_prave_jedna_dlazdice()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();
        FarTerrain far = new(new TerrainGenerator(registry, seed: 20260728), registry, jobs)
        {
            NearRadius = 336f,
            BuildsPerFrame = 0,
        };

        // Stavba se nezadává (BuildsPerFrame = 0), zajímá jen výběr dlaždic.
        far.Update(Vector3.Zero);

        List<FarTerrain.TileKey> tiles = [.. far.Tiles];
        Assert.NotEmpty(tiles);

        // Vzorky po celém dosahu, schválně i těsně u hranic pásem (768, 1536, 3072).
        int[] samples = [400, 500, 700, 760, 780, 900, 1500, 1600, 2000, 3000, 3200, 4000, 5000, 6000];

        foreach (int distance in samples)
        {
            foreach ((int dirX, int dirZ) in (ReadOnlySpan<(int, int)>)[(1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1)])
            {
                // Úhlopříčné směry se zkrátí, aby vzorek zůstal na dané vzdálenosti.
                float scale = dirX != 0 && dirZ != 0 ? 0.7071f : 1f;
                int x = (int)(distance * dirX * scale);
                int z = (int)(distance * dirZ * scale);

                int covering = 0;
                foreach (FarTerrain.TileKey key in tiles)
                {
                    int size = FarTerrain.TileSize(key.Level);
                    if (x >= key.TileX * size && x < (key.TileX + 1) * size
                        && z >= key.TileZ * size && z < (key.TileZ + 1) * size)
                    {
                        covering++;
                    }
                }

                Assert.True(
                    covering == 1,
                    $"Bod ({x}, {z}) ve vzdálenosti {distance} pokrývá {covering} dlaždic, má právě 1.");
            }
        }
    }

    /// <summary>
    /// Zmenšení vnitřního poloměru musí zabrat <b>hned</b>, ne až po ujetí 64 bloků.
    ///
    /// <para>Poloměr se řídí podle toho, kam až je svět hotový, a to se při rychlém letu
    /// mění bez ohledu na to, kolik hráč zrovna ujel. Kdyby se čekalo na pohyb, LOD by
    /// zaskočil dovnitř pozdě — a přesně v té mezeře byly vidět díry.</para>
    /// </summary>
    [Fact]
    public void Zmensení_vnitrniho_polomeru_zabere_bez_pohybu()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();
        FarTerrain far = Build(registry, jobs);
        var renderer = new FakeRenderer();

        var home = new Vector3(0f, 200f, 0f);
        renderer.Settle(far, home);

        int before = far.TileCount;

        // Streaming přestal stíhat: svět je hotový jen do 200 bloků.
        far.NearRadius = 200f;
        renderer.Frame(far, home);

        Assert.True(
            far.TileCount > before,
            $"Po zmenšení poloměru má být dlaždic víc, ale je {far.TileCount} proti {before}.");

        renderer.Settle(far, home);

        // A pokrytí musí i pak zůstat rozdělením — bod ve 300 blocích leží pod právě
        // jednou dlaždicí, ne pod dvěma.
        int covering = 0;
        foreach (FarTerrain.TileKey key in far.Tiles)
        {
            int size = FarTerrain.TileSize(key.Level);
            if (300 >= key.TileX * size && 300 < (key.TileX + 1) * size
                && 0 >= key.TileZ * size && 0 < (key.TileZ + 1) * size)
            {
                covering++;
            }
        }

        Assert.Equal(1, covering);
    }

    /// <summary>
    /// Povrch dlaždice nesmí nikde ležet <b>nad</b> skutečným terénem.
    ///
    /// <para>Vzniklo z toho, co bylo ve hře vidět po zapnutí LOD blízko hráče: krajina
    /// vypadala jako schodiště z dvoublokových teras. Buňka totiž dostávala výšku podle
    /// jednoho svého rohu, takže na svahu vylezla až o blok nad terén a koukala skrz
    /// plnou geometrii ven. Teď bere minimum ze čtyř rohů.</para>
    ///
    /// <para>Kontroluje se přímo geometrie dlaždice proti <c>ColumnAt</c>, tedy proti
    /// témuž zdroji, ze kterého staví chunky.</para>
    /// </summary>
    [Fact]
    public void Povrch_dlazdice_nikde_neprevysuje_teren()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();
        var generator = new TerrainGenerator(registry, seed: 20260728);
        FarTerrain far = new(generator, registry, jobs)
        {
            ActiveLevels = 1,
            NearRadius = 192f,
            BuildsPerFrame = 64,
        };
        var renderer = new FakeRenderer();

        // Kus světa se svahy: startovní okolí má i hory.
        renderer.Settle(far, new Vector3(0f, 200f, 0f));

        // Nejjemnější úroveň má krok 2, takže buňka je 2x2 bloky. Projdeme hustou mřížku
        // bodů kolem hráče a u každého porovnáme výšku dlaždice s výškou terénu.
        int checkedPoints = 0;

        for (int z = -600; z <= 600; z += 7)
        {
            for (int x = -600; x <= 600; x += 7)
            {
                if (!TryTileHeight(far, generator, x, z, out int tileTop))
                {
                    continue;
                }

                int terrain = generator.ColumnAt(x, z).Surface;
                checkedPoints++;

                Assert.True(
                    tileTop <= terrain,
                    $"Dlaždice na ({x}, {z}) má povrch {tileTop}, terén je {terrain} — kouká nad krajinu.");
            }
        }

        Assert.True(checkedPoints > 1000, $"Zkontrolováno jen {checkedPoints} bodů, to je málo na důkaz.");
    }

    /// <summary>
    /// Výška buňky dlaždice pod daným bodem. Počítá se stejně jako ve stavbě: minimum
    /// ze čtyř rohů buňky.
    /// </summary>
    private static bool TryTileHeight(FarTerrain far, TerrainGenerator generator, int x, int z, out int height)
    {
        height = 0;

        foreach (FarTerrain.TileKey key in far.Tiles)
        {
            int size = FarTerrain.TileSize(key.Level);
            if (x < key.TileX * size || x >= (key.TileX + 1) * size
                || z < key.TileZ * size || z >= (key.TileZ + 1) * size)
            {
                continue;
            }

            int step = size / FarTerrain.CellsPerTile;
            int cellX = ((x - (key.TileX * size)) / step * step) + (key.TileX * size);
            int cellZ = ((z - (key.TileZ * size)) / step * step) + (key.TileZ * size);

            // Stejné pravidlo jako ve stavbě: u nejjemnější úrovně minimum přes bloky,
            // u hrubších přes rohy buňky.
            int sub = step <= 2 ? step : 1;
            int sampleStep = step / sub;

            height = int.MaxValue;
            for (int sz = 0; sz <= sub; sz++)
            {
                for (int sx = 0; sx <= sub; sx++)
                {
                    height = Math.Min(
                        height,
                        generator.ColumnAt(cellX + (sx * sampleStep), cellZ + (sz * sampleStep)).Surface);
                }
            }

            return true;
        }

        return false;
    }

    [Fact]
    public void Vypnuty_LOD_zahodi_vsechny_dlazdice()
    {
        BlockRegistry registry = Registry();
        using var jobs = new JobSystem();
        FarTerrain far = Build(registry, jobs);
        var renderer = new FakeRenderer();

        var home = new Vector3(0f, 200f, 0f);
        renderer.Settle(far, home);
        Assert.NotEmpty(renderer.Tiles);

        far.Enabled = false;
        renderer.Frame(far, home);

        Assert.Empty(renderer.Tiles);
        Assert.Equal(0, far.TileCount);
    }
}
