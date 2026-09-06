using OpenTK.Mathematics;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy toho, které chunky se musí po úpravě bloku přemeshovat.
///
/// Vzniklo z chyby nahlášené z běhu hry: po vytěžení bloku na hranici chunku zůstala
/// v sousedním chunku stěna, která se měla odkrýt. Příčinou byly prohozené směry — kód
/// přemešovával vždycky souseda na opačné straně, než bylo potřeba.
///
/// Výpočet je čistá aritmetika bez GL, takže se dá otestovat přímo.
/// </summary>
public sealed class ChunkInvalidationTests
{
    private static List<Vector3i> Affected(int x, int y, int z)
    {
        var result = new List<Vector3i>();
        ChunkStreamer.CollectChunksContaining(x, y, z, result);
        return result;
    }

    [Fact]
    public void Blok_uprostred_chunku_zasahuje_jen_vlastni_chunk()
    {
        List<Vector3i> affected = Affected(16, 16, 16);

        Assert.Single(affected);
        Assert.Equal(Vector3i.Zero, affected[0]);
    }

    [Fact]
    public void Blok_na_kladnem_okraji_zasahuje_souseda_v_kladnem_smeru()
    {
        // Blok na x = 31 leží v lemu chunku vpravo, protože ten lem sahá na x = 31.
        List<Vector3i> affected = Affected(31, 16, 16);

        Assert.Equal(2, affected.Count);
        Assert.Contains(new Vector3i(0, 0, 0), affected);
        Assert.Contains(new Vector3i(1, 0, 0), affected);
        Assert.DoesNotContain(new Vector3i(-1, 0, 0), affected);
    }

    [Fact]
    public void Blok_na_zapornem_okraji_zasahuje_souseda_v_zapornem_smeru()
    {
        // Blok na x = 0 leží v lemu chunku vlevo, protože ten lem sahá na x = 0.
        List<Vector3i> affected = Affected(0, 16, 16);

        Assert.Equal(2, affected.Count);
        Assert.Contains(new Vector3i(0, 0, 0), affected);
        Assert.Contains(new Vector3i(-1, 0, 0), affected);
        Assert.DoesNotContain(new Vector3i(1, 0, 0), affected);
    }

    [Theory]
    [InlineData(31, 16, 16, 1, 0, 0)]
    [InlineData(0, 16, 16, -1, 0, 0)]
    [InlineData(16, 31, 16, 0, 1, 0)]
    [InlineData(16, 0, 16, 0, -1, 0)]
    [InlineData(16, 16, 31, 0, 0, 1)]
    [InlineData(16, 16, 0, 0, 0, -1)]
    public void Kazda_stena_zasahne_souseda_na_spravne_strane(
        int x, int y, int z, int expectedX, int expectedY, int expectedZ)
    {
        List<Vector3i> affected = Affected(x, y, z);
        var expected = new Vector3i(expectedX, expectedY, expectedZ);

        Assert.Contains(expected, affected);

        // A rozhodně ne souseda na opačné straně — to byla ta chyba.
        Assert.DoesNotContain(-expected, affected);
    }

    [Fact]
    public void Blok_v_rohu_zasahuje_osm_chunku()
    {
        List<Vector3i> affected = Affected(31, 31, 31);

        Assert.Equal(8, affected.Count);

        for (int dy = 0; dy <= 1; dy++)
        {
            for (int dz = 0; dz <= 1; dz++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    Assert.Contains(new Vector3i(dx, dy, dz), affected);
                }
            }
        }
    }

    [Fact]
    public void Vlastni_chunk_je_i_na_rohu_prvni()
    {
        List<Vector3i> affected = Affected(31, 31, 31);

        Assert.Equal(Vector3i.Zero, affected[0]);
    }

    [Fact]
    public void Blok_v_protejsim_rohu_zasahuje_taky_osm_chunku()
    {
        List<Vector3i> affected = Affected(0, 0, 0);

        Assert.Equal(8, affected.Count);

        for (int dy = -1; dy <= 0; dy++)
        {
            for (int dz = -1; dz <= 0; dz++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    Assert.Contains(new Vector3i(dx, dy, dz), affected);
                }
            }
        }
    }

    [Fact]
    public void Blok_na_hrane_zasahuje_ctyri_chunky()
    {
        // Dvě krajní souřadnice ze tří: hrana, ne roh.
        List<Vector3i> affected = Affected(31, 16, 0);

        Assert.Equal(4, affected.Count);
        Assert.Contains(new Vector3i(0, 0, 0), affected);
        Assert.Contains(new Vector3i(1, 0, 0), affected);
        Assert.Contains(new Vector3i(0, 0, -1), affected);
        Assert.Contains(new Vector3i(1, 0, -1), affected);
    }

    [Fact]
    public void Funguje_i_v_zapornych_souradnicich()
    {
        // Světový blok -1 leží v chunku -1 na místní souřadnici 31.
        List<Vector3i> affected = Affected(-1, 16, 16);

        Assert.Equal(2, affected.Count);
        Assert.Contains(new Vector3i(-1, 0, 0), affected);
        Assert.Contains(new Vector3i(0, 0, 0), affected);
    }

    [Fact]
    public void Vypsane_chunky_opravdu_maji_blok_ve_svem_odsazenem_objemu()
    {
        // Nezávislá kontrola: pro každý vypsaný chunk se ověří, že blok padne
        // do rozsahu, který jeho odsazený objem pokrývá.
        foreach (int x in new[] { -33, -32, -1, 0, 1, 15, 31, 32, 63, 64 })
        {
            foreach (int y in new[] { 0, 5, 31, 32 })
            {
                foreach (int z in new[] { -1, 0, 16, 31 })
                {
                    foreach (Vector3i chunk in Affected(x, y, z))
                    {
                        AssertInsidePadded(chunk.X, x);
                        AssertInsidePadded(chunk.Y, y);
                        AssertInsidePadded(chunk.Z, z);
                    }
                }
            }
        }
    }

    [Fact]
    public void Zadny_dalsi_chunk_uz_blok_v_lemu_nema()
    {
        // Opačný směr kontroly: projde se okolí 5×5×5 a ověří, že vypsané chunky
        // jsou přesně ty, jejichž lem blok obsahuje — ani víc, ani míň.
        foreach (int x in new[] { 0, 7, 31 })
        {
            foreach (int y in new[] { 0, 20, 31 })
            {
                foreach (int z in new[] { 0, 12, 31 })
                {
                    List<Vector3i> affected = Affected(x, y, z);

                    for (int cx = -2; cx <= 2; cx++)
                    {
                        for (int cy = -2; cy <= 2; cy++)
                        {
                            for (int cz = -2; cz <= 2; cz++)
                            {
                                var chunk = new Vector3i(cx, cy, cz);
                                bool covers = Covers(cx, x) && Covers(cy, y) && Covers(cz, z);

                                Assert.Equal(covers, affected.Contains(chunk));
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>Pokrývá odsazený objem chunku danou světovou souřadnici?</summary>
    private static bool Covers(int chunkCoordinate, int worldCoordinate)
    {
        int min = (chunkCoordinate * Chunk.Size) - ChunkMesher.Pad;
        int max = (chunkCoordinate * Chunk.Size) + Chunk.Size - 1 + ChunkMesher.Pad;
        return worldCoordinate >= min && worldCoordinate <= max;
    }

    private static void AssertInsidePadded(int chunkCoordinate, int worldCoordinate) =>
        Assert.True(
            Covers(chunkCoordinate, worldCoordinate),
            $"Chunk {chunkCoordinate} nemá souřadnici {worldCoordinate} ve svém lemu.");
}
