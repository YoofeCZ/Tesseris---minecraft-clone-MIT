using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Příznak, kterým mesher odlišuje drobný porost od listí.
///
/// <para>Vznikl proto, že tráva a kytky nemají vrhat stín, kdežto koruna stromu ano —
/// a v datech byly do té doby <b>k nerozeznání</b>. Jednoblokový trs má článek i sloupec
/// nulový, takže mu vyšlo přesně totéž stínění jako chomáči listí.</para>
///
/// <para>Testuje se to takhle podrobně, protože příznak jede ve stínění spolu se třemi
/// dalšími poli a rozbaluje se v <b>trojím</b> vertex shaderu. Kdyby se pořadí rozbalování
/// rozešlo, neprojeví se to chybou — jen se z trávy stane osmiblokový sloup, který se ve
/// větru ohýbá jako chaluha.</para>
/// </summary>
public sealed class PlantShadowTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:grass", Texture = "grass", Opaque = false, Shape = BlockShape.Cross },
        new BlockDefinition { Id = "test:leaves", Texture = "leaves", Opaque = false, Shape = BlockShape.Foliage },
    ]);

    /// <summary>Postaví jeden blok daného typu a vrátí stínění všech jeho vrcholů.</summary>
    private static float[] ShadesOf(string id)
    {
        BlockRegistry registry = Registry();
        ushort block = registry.IndexOf(id);

        ushort[] volume = new ushort[ChunkMesher.PaddedVolume];
        volume[ChunkMesher.PaddedIndex(5, 5, 5)] = block;

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var cutout = new MeshBuffer();

        ChunkMesher.Build(volume, registry, opaque, transparent, default, cutout);

        Assert.True(cutout.VertexCount > 0, $"{id} nevytvořil žádnou geometrii ve výřezu.");

        float[] shades = new float[cutout.VertexCount];

        for (int i = 0; i < cutout.VertexCount; i++)
        {
            // Stínění je poslední ze sedmi floatů na vrchol: pozice, UV, vrstva, stínění.
            shades[i] = cutout.Vertices[(i * MeshBuffer.FloatsPerVertex) + 6];
        }

        return shades;
    }

    /// <summary>Přesná kopie toho, co dělá UnpackShade ve vertex shaderech.</summary>
    private static (float Shade, bool Submerged, int Segment, int Total, bool Plant) Unpack(float packed)
    {
        float plant = MathF.Floor(packed / 256f);
        packed -= plant * 256f;

        float total = MathF.Floor(packed / 32f);
        packed -= total * 32f;

        float segment = MathF.Floor(packed / 4f);
        packed -= segment * 4f;

        float submerged = packed >= 1.5f ? 1f : 0f;
        float shade = packed - (submerged * 2f);

        return (shade, submerged > 0.5f, (int)segment, (int)total + 1, plant > 0.5f);
    }

    [Fact]
    public void Trava_nese_priznak_porostu()
    {
        foreach (float packed in ShadesOf("test:grass"))
        {
            Assert.True(Unpack(packed).Plant, $"Vrchol trávy nemá příznak porostu, stínění {packed}.");
        }
    }

    [Fact]
    public void Listi_priznak_porostu_nenese()
    {
        foreach (float packed in ShadesOf("test:leaves"))
        {
            Assert.False(Unpack(packed).Plant, $"Vrchol listí má příznak porostu, stínění {packed}.");
        }
    }

    /// <summary>
    /// Příznak nesmí ukrást bity ostatním polím. Kdyby se odloupával až po výšce sloupce,
    /// vyšla by z trávy osmiblokovka.
    /// </summary>
    [Fact]
    public void Priznak_neposkodi_ostatni_pole()
    {
        foreach (float packed in ShadesOf("test:grass"))
        {
            (float shade, _, int segment, int total, bool plant) = Unpack(packed);

            // Příznak vody se nekontroluje: v prázdném testovacím objemu není strop, takže
            // sonda vzhůru skončí na „nevíme" a mesher dá jedničku. Ve světě to nenastane.
            Assert.True(plant);
            Assert.InRange(shade, 0f, 1f);
            Assert.Equal(0, segment);
            Assert.Equal(1, total);
        }
    }

    /// <summary>
    /// Rozbalení musí projít pro každou kombinaci polí, ne jen pro tu, kterou zrovna
    /// vyrobí mesher. Přetečení do vyššího pole by se jinak našlo až u vodní rostliny
    /// o pěti blocích.
    /// </summary>
    [Fact]
    public void Rozbaleni_projde_pro_vsechny_kombinace()
    {
        foreach (bool plant in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (bool submerged in (ReadOnlySpan<bool>)[false, true])
            {
                for (int segment = 0; segment < 8; segment++)
                {
                    for (int total = 0; total < 8; total++)
                    {
                        float packed = 1f
                            + (submerged ? 2f : 0f)
                            + (segment * 4f)
                            + (total * 32f)
                            + (plant ? ChunkMesher.PlantFlag : 0f);

                        (float shade, bool gotSubmerged, int gotSegment, int gotTotal, bool gotPlant) =
                            Unpack(packed);

                        Assert.Equal(plant, gotPlant);
                        Assert.Equal(submerged, gotSubmerged);
                        Assert.Equal(segment, gotSegment);
                        Assert.Equal(total + 1, gotTotal);
                        Assert.Equal(1f, shade, 4);
                    }
                }
            }
        }
    }
}
