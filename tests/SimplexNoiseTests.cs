using Tesseris.Engine.MathLib;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy simplexového šumu. Klíčová vlastnost je opakovatelnost — na ní stojí to, že se svět
/// dá znovu vygenerovat místo ukládání.
/// </summary>
public sealed class SimplexNoiseTests
{
    [Fact]
    public void Stejny_seed_a_stejne_souradnice_daji_stejne_cislo()
    {
        var first = new SimplexNoise(1234);
        var second = new SimplexNoise(1234);

        for (int i = 0; i < 200; i++)
        {
            float x = i * 0.37f;
            float y = i * -0.11f;
            float z = i * 0.53f;

            Assert.Equal(first.Sample(x, y), second.Sample(x, y));
            Assert.Equal(first.Sample(x, y, z), second.Sample(x, y, z));
        }
    }

    [Fact]
    public void Ruzne_seedy_daji_ruzny_sum()
    {
        var a = new SimplexNoise(1);
        var b = new SimplexNoise(2);

        int different = 0;
        for (int i = 0; i < 200; i++)
        {
            if (Math.Abs(a.Sample(i * 0.13f, i * 0.29f) - b.Sample(i * 0.13f, i * 0.29f)) > 1e-6f)
            {
                different++;
            }
        }

        Assert.True(different > 150, $"Šumy se lišily jen v {different} z 200 bodů.");
    }

    [Fact]
    public void Dvojrozmerny_sum_zustava_v_rozsahu()
    {
        var noise = new SimplexNoise(42);

        float min = float.MaxValue;
        float max = float.MinValue;

        for (int i = 0; i < 20000; i++)
        {
            float value = noise.Sample(i * 0.017f, i * 0.031f);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        Assert.InRange(min, -1.05f, 0f);
        Assert.InRange(max, 0f, 1.05f);
    }

    [Fact]
    public void Trojrozmerny_sum_zustava_v_rozsahu()
    {
        var noise = new SimplexNoise(42);

        float min = float.MaxValue;
        float max = float.MinValue;

        for (int i = 0; i < 20000; i++)
        {
            float value = noise.Sample(i * 0.013f, i * 0.019f, i * 0.023f);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        Assert.InRange(min, -1.05f, 0f);
        Assert.InRange(max, 0f, 1.05f);
    }

    [Fact]
    public void Sum_neni_konstantni()
    {
        var noise = new SimplexNoise(7);
        var values = new HashSet<float>();

        for (int i = 0; i < 500; i++)
        {
            values.Add(noise.Sample(i * 0.21f, i * 0.34f));
        }

        Assert.True(values.Count > 400, $"Jen {values.Count} různých hodnot z 500 vzorků.");
    }

    [Fact]
    public void Sum_je_spojity()
    {
        var noise = new SimplexNoise(99);

        // Malá změna vstupu nesmí dát velký skok — jinak by v terénu vznikaly srázy z ničeho.
        for (int i = 0; i < 500; i++)
        {
            float x = i * 0.07f;
            float a = noise.Sample(x, 3.5f);
            float b = noise.Sample(x + 0.001f, 3.5f);

            Assert.True(Math.Abs(a - b) < 0.05f, $"Skok {Math.Abs(a - b)} u x = {x}.");
        }
    }

    [Fact]
    public void Zaporne_souradnice_fungují_stejne_dobre()
    {
        var noise = new SimplexNoise(5);
        var values = new HashSet<float>();

        for (int i = -500; i < 0; i++)
        {
            float value = noise.Sample(i * 0.11f, i * 0.23f);
            Assert.InRange(value, -1.05f, 1.05f);
            values.Add(value);
        }

        Assert.True(values.Count > 400, "V záporných souřadnicích šum degeneroval.");
    }

    [Fact]
    public void Fraktal_kombinuje_oktavy_a_drzi_rozsah()
    {
        var noise = new SimplexNoise(11);

        float min = float.MaxValue;
        float max = float.MinValue;

        for (int i = 0; i < 5000; i++)
        {
            float value = noise.Fractal(i * 0.005f, i * 0.007f, octaves: 5);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        Assert.InRange(min, -1.05f, 0f);
        Assert.InRange(max, 0f, 1.05f);
    }

    [Fact]
    public void Fraktal_s_jednou_oktavou_odpovida_prostemu_sumu()
    {
        var noise = new SimplexNoise(3);

        Assert.Equal(noise.Sample(1.5f, 2.5f), noise.Fractal(1.5f, 2.5f, octaves: 1), 5);
    }

    [Fact]
    public void Vice_oktav_prida_detail()
    {
        var noise = new SimplexNoise(13);

        // Součet oktáv musí být členitější než jediná vrstva: měříme průměrnou změnu
        // mezi sousedními vzorky.
        double coarse = 0;
        double detailed = 0;

        for (int i = 1; i < 2000; i++)
        {
            float x = i * 0.01f;
            coarse += Math.Abs(noise.Fractal(x, 0f, 1) - noise.Fractal(x - 0.01f, 0f, 1));
            detailed += Math.Abs(noise.Fractal(x, 0f, 5) - noise.Fractal(x - 0.01f, 0f, 5));
        }

        Assert.True(detailed > coarse, $"Pět oktáv ({detailed:F3}) není členitějších než jedna ({coarse:F3}).");
    }

    [Fact]
    public void Seed_je_dostupny()
    {
        Assert.Equal(-17, new SimplexNoise(-17).Seed);
    }

    [Fact]
    public void Hrebenovy_sum_zustava_v_rozsahu_nula_az_jedna()
    {
        var noise = new SimplexNoise(4242);

        for (int i = 0; i < 4000; i++)
        {
            float value = noise.Ridged(i * 0.031f, i * -0.017f, octaves: 5);
            Assert.InRange(value, 0f, 1f);
        }
    }

    /// <summary>
    /// Vrcholy hřebenů musí být vzácné.
    ///
    /// <para><b>Proč zrovna takhle.</b> Terén do 28. 7. 2026 bral hřebeny z jediné oktávy
    /// <c>(1 − |šum|)²</c>. Tam je podmínka „nad 0,9" totéž co <c>|šum| &lt; 0,051</c>,
    /// což u simplexového šumu splňuje řádově desetina plochy — a protože jde o okolí
    /// nulové vrstevnice, tvoří to souvislou síť stejně vysokých valů. Přes celý svět
    /// pak vede pravidelné „kopec, dolina, kopec, dolina".</para>
    ///
    /// <para>Multifraktál násobí každou oktávu předchozí, takže vrcholu dosáhne jen místo,
    /// kde se trefí všechny vrstvy naráz. Naměřeno: nad 0,5 leží 27 % plochy, ale
    /// <b>nad 0,9 jen 0,1 %</b>. Práh 2 % leží mezi oběma případy s velkou rezervou.</para>
    /// </summary>
    [Fact]
    public void Vrcholy_hrebenu_jsou_vzacne()
    {
        var noise = new SimplexNoise(20260728);

        int high = 0;
        const int Samples = 40000;

        for (int i = 0; i < Samples; i++)
        {
            float x = (i % 200) * 0.05f;
            float y = (i / 200) * 0.05f;

            if (noise.Ridged(x, y, octaves: 5) > 0.9f)
            {
                high++;
            }
        }

        double fraction = high / (double)Samples;

        Assert.True(
            fraction < 0.02,
            $"Vrcholy zabírají {fraction:P1} plochy. Hřebeny nejsou hřebeny, ale rovnoměrná síť valů.");
    }
}
