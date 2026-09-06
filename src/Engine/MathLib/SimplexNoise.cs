namespace Tesseris.Engine.MathLib;

/// <summary>
/// Simplexový šum ve dvou a třech rozměrech.
///
/// Proti klasickému Perlinovu šumu dělí prostor na simplexy (trojúhelníky, resp. čtyřstěny)
/// místo na krychle. Vychází z toho méně artefaktů podél os a v trojrozměrné variantě se
/// sčítá jen pět příspěvků místo osmi.
///
/// Výstup je zhruba v rozsahu −1 až 1 a je plně určený seedem — stejný seed a stejné
/// souřadnice dají vždycky stejné číslo, což je podmínka pro to, aby se svět dal
/// znovu vygenerovat místo ukládání.
/// </summary>
public sealed class SimplexNoise
{
    /// <summary>Hrany krychle. Pro dvojrozměrný šum se třetí složka ignoruje.</summary>
    private static readonly int[][] Gradients =
    [
        [1, 1, 0], [-1, 1, 0], [1, -1, 0], [-1, -1, 0],
        [1, 0, 1], [-1, 0, 1], [1, 0, -1], [-1, 0, -1],
        [0, 1, 1], [0, -1, 1], [0, 1, -1], [0, -1, -1],
    ];

    private static readonly float SkewFactor2D = 0.5f * (MathF.Sqrt(3f) - 1f);
    private static readonly float UnskewFactor2D = (3f - MathF.Sqrt(3f)) / 6f;

    private const float SkewFactor3D = 1f / 3f;
    private const float UnskewFactor3D = 1f / 6f;

    private readonly byte[] _permutation = new byte[512];
    private readonly byte[] _gradientIndex = new byte[512];

    public SimplexNoise(int seed)
    {
        Seed = seed;

        byte[] source = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            source[i] = (byte)i;
        }

        // Vlastní generátor místo Random: chceme, aby stejný seed dal stejný svět
        // i po změně verze .NET. Random takovou záruku nedává.
        uint state = (uint)seed | 1u;
        for (int i = 255; i > 0; i--)
        {
            state = (state * 1664525u) + 1013904223u;
            int j = (int)(state % (uint)(i + 1));
            (source[i], source[j]) = (source[j], source[i]);
        }

        for (int i = 0; i < 512; i++)
        {
            _permutation[i] = source[i & 255];
            _gradientIndex[i] = (byte)(_permutation[i] % 12);
        }
    }

    public int Seed { get; }

    /// <summary>Dvojrozměrný šum, zhruba v rozsahu −1 až 1.</summary>
    public float Sample(float x, float y)
    {
        float skew = (x + y) * SkewFactor2D;
        int i = FastFloor(x + skew);
        int j = FastFloor(y + skew);

        float unskew = (i + j) * UnskewFactor2D;
        float x0 = x - (i - unskew);
        float y0 = y - (j - unskew);

        // Ve kterém z obou trojúhelníků buňky bod leží.
        int i1 = x0 > y0 ? 1 : 0;
        int j1 = x0 > y0 ? 0 : 1;

        float x1 = x0 - i1 + UnskewFactor2D;
        float y1 = y0 - j1 + UnskewFactor2D;
        float x2 = x0 - 1f + (2f * UnskewFactor2D);
        float y2 = y0 - 1f + (2f * UnskewFactor2D);

        int ii = i & 255;
        int jj = j & 255;

        float total =
            Corner2D(x0, y0, _gradientIndex[ii + _permutation[jj]]) +
            Corner2D(x1, y1, _gradientIndex[ii + i1 + _permutation[jj + j1]]) +
            Corner2D(x2, y2, _gradientIndex[ii + 1 + _permutation[jj + 1]]);

        // Konstanta srovnává výsledek do rozsahu zhruba −1 až 1.
        return 70f * total;
    }

    /// <summary>Trojrozměrný šum, zhruba v rozsahu −1 až 1.</summary>
    public float Sample(float x, float y, float z)
    {
        float skew = (x + y + z) * SkewFactor3D;
        int i = FastFloor(x + skew);
        int j = FastFloor(y + skew);
        int k = FastFloor(z + skew);

        float unskew = (i + j + k) * UnskewFactor3D;
        float x0 = x - (i - unskew);
        float y0 = y - (j - unskew);
        float z0 = z - (k - unskew);

        // Pořadí, ve kterém se procházejí rohy čtyřstěnu, plyne ze seřazení složek.
        int i1, j1, k1;
        int i2, j2, k2;

        if (x0 >= y0)
        {
            if (y0 >= z0)
            {
                (i1, j1, k1) = (1, 0, 0);
                (i2, j2, k2) = (1, 1, 0);
            }
            else if (x0 >= z0)
            {
                (i1, j1, k1) = (1, 0, 0);
                (i2, j2, k2) = (1, 0, 1);
            }
            else
            {
                (i1, j1, k1) = (0, 0, 1);
                (i2, j2, k2) = (1, 0, 1);
            }
        }
        else
        {
            if (y0 < z0)
            {
                (i1, j1, k1) = (0, 0, 1);
                (i2, j2, k2) = (0, 1, 1);
            }
            else if (x0 < z0)
            {
                (i1, j1, k1) = (0, 1, 0);
                (i2, j2, k2) = (0, 1, 1);
            }
            else
            {
                (i1, j1, k1) = (0, 1, 0);
                (i2, j2, k2) = (1, 1, 0);
            }
        }

        float x1 = x0 - i1 + UnskewFactor3D;
        float y1 = y0 - j1 + UnskewFactor3D;
        float z1 = z0 - k1 + UnskewFactor3D;

        float x2 = x0 - i2 + (2f * UnskewFactor3D);
        float y2 = y0 - j2 + (2f * UnskewFactor3D);
        float z2 = z0 - k2 + (2f * UnskewFactor3D);

        float x3 = x0 - 1f + (3f * UnskewFactor3D);
        float y3 = y0 - 1f + (3f * UnskewFactor3D);
        float z3 = z0 - 1f + (3f * UnskewFactor3D);

        int ii = i & 255;
        int jj = j & 255;
        int kk = k & 255;

        float total =
            Corner3D(x0, y0, z0, _gradientIndex[ii + _permutation[jj + _permutation[kk]]]) +
            Corner3D(x1, y1, z1, _gradientIndex[ii + i1 + _permutation[jj + j1 + _permutation[kk + k1]]]) +
            Corner3D(x2, y2, z2, _gradientIndex[ii + i2 + _permutation[jj + j2 + _permutation[kk + k2]]]) +
            Corner3D(x3, y3, z3, _gradientIndex[ii + 1 + _permutation[jj + 1 + _permutation[kk + 1]]]);

        return 32f * total;
    }

    /// <summary>
    /// Součet několika oktáv se zmenšující se vahou. Dává členitější tvar než jediná vrstva.
    /// </summary>
    /// <param name="octaves">Počet vrstev.</param>
    /// <param name="lacunarity">Kolikrát se s každou vrstvou zvýší frekvence.</param>
    /// <param name="gain">Kolikrát se s každou vrstvou sníží váha.</param>
    public float Fractal(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float normalization = 0f;

        for (int octave = 0; octave < octaves; octave++)
        {
            sum += Sample(x * frequency, y * frequency) * amplitude;
            normalization += amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return normalization > 0f ? sum / normalization : 0f;
    }

    /// <summary>
    /// Hřebenový multifraktál. Vrací <b>0 až 1</b>, kde 1 je hřeben.
    ///
    /// <para>Základ je <c>1 − |šum|</c>, což udělá ostrou hranu tam, kde šum prochází nulou.
    /// Kdyby se vzala jediná oktáva, dostaneš síť stejně širokých a stejně vysokých valů,
    /// protože nulová vrstevnice hladké funkce jsou uzavřené smyčky, které rovnoměrně
    /// pokrývají rovinu. Tak vypadal Tesseris do 28. 7. 2026.</para>
    ///
    /// <para>Rozdíl proti <see cref="Fractal"/> je násobení předchozí oktávou (<c>weight</c>):
    /// jemné hřebeny se objeví jen tam, kde už je hřeben hrubý. Tím se hory větví do
    /// postranních hřbetů místo aby se šum jen sečetl, a rozdíl mezi hřebenem a údolím
    /// zůstane ostrý.</para>
    /// </summary>
    /// <param name="octaves">Počet vrstev.</param>
    /// <param name="lacunarity">Kolikrát se s každou vrstvou zvýší frekvence.</param>
    /// <param name="gain">Kolikrát se s každou vrstvou sníží váha.</param>
    public float Ridged(float x, float y, int octaves, float lacunarity = 2.05f, float gain = 0.5f)
    {
        float sum = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float normalization = 0f;
        float weight = 1f;

        for (int octave = 0; octave < octaves; octave++)
        {
            float signal = 1f - MathF.Abs(Sample(x * frequency, y * frequency));

            // Umocnění zaostří hřeben; bez něj je vrchol kulatý a hora vypadá jako duna.
            signal *= signal;
            signal *= weight;

            // Váha pro další oktávu. Ořez nahoře brání tomu, aby se detail rozjel do špic.
            weight = Math.Clamp(signal * 2f, 0f, 1f);

            sum += signal * amplitude;
            normalization += amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return normalization > 0f ? sum / normalization : 0f;
    }

    private static float Corner2D(float x, float y, int gradient)
    {
        float falloff = 0.5f - (x * x) - (y * y);
        if (falloff < 0f)
        {
            return 0f;
        }

        falloff *= falloff;
        int[] g = Gradients[gradient];
        return falloff * falloff * ((g[0] * x) + (g[1] * y));
    }

    private static float Corner3D(float x, float y, float z, int gradient)
    {
        float falloff = 0.6f - (x * x) - (y * y) - (z * z);
        if (falloff < 0f)
        {
            return 0f;
        }

        falloff *= falloff;
        int[] g = Gradients[gradient];
        return falloff * falloff * ((g[0] * x) + (g[1] * y) + (g[2] * z));
    }

    /// <summary>Dolní celá část. Přetypování na int zaokrouhluje k nule, což u záporných čísel nestačí.</summary>
    private static int FastFloor(float value)
    {
        int truncated = (int)value;
        return value < truncated ? truncated - 1 : truncated;
    }
}
