using OpenTK.Mathematics;

namespace Tesseris.Engine.Rendering;

/// <summary>
/// Osvětlovací model převzatý z Luanti. Jedno místo, kde leží všechna čísla, aby se
/// nedala rozejít mezi mesherem, shaderem a denním cyklem.
/// </summary>
/// <remarks>
/// <para><b>Původ.</b> Přeneseno z Luanti (LGPL-2.1-or-later), soubory
/// <c>src/light.cpp</c>, <c>src/light.h</c>, <c>src/daynightratio.h</c>,
/// <c>src/client/mesh.cpp</c> a <c>src/client/mapblock_mesh.cpp</c>. Přenášejí se
/// <b>algoritmy a konstanty</b>, ne soubory — Luanti má GLSL proti Irrlichtu a OpenGL,
/// Tesseris má Vulkan a jiný formát vrcholu.</para>
///
/// <para><b>Dvě banky.</b> Každý voxel nese zvlášť denní (sluneční) a noční (umělou)
/// úroveň 0–15. Denní se v shaderu násobí barvou slunce, která s denní dobou hasne;
/// noční je na denní době nezávislá. Proto pochodeň svítí v noci stejně jako ve dne
/// a jeskyně nezesvětlá s východem slunce.</para>
/// </remarks>
public static class LuantiLight
{
    /// <summary>Nejvyšší úroveň světla. Vyhrazená pro přímé slunce.</summary>
    public const int LightSun = 15;

    // Výchozí nastavení Luanti (builtin/settingtypes.txt):
    //   lighting_alpha 0.0, lighting_beta 1.5, lighting_boost 0.2,
    //   lighting_boost_center 0.5, lighting_boost_spread 0.2, display_gamma 1.0.
    private const float Alpha = 0.0f;
    private const float Beta = 1.5f;
    private const float Boost = 0.2f;
    private const float BoostCenter = 0.5f;
    private const float BoostSpread = 0.2f;
    private const float Gamma = 1.0f;

    /// <summary>Gama stínění rohů. Luanti <c>ambient_occlusion_gamma</c>.</summary>
    private const float AmbientOcclusionGamma = 1.8f;

    /// <summary>
    /// Převodní tabulka úrovně světla na jas 0–255.
    /// </summary>
    /// <remarks>
    /// Hodnoty vycházejí přesně: {0, 6, 15, 30, 50, 73, 98, 120, 139, 155, 168, 181,
    /// 196, 213, 234, 255}. Křivka není lineární ani gama — je to kubický polynom
    /// s gaussovským zesílením uprostřed, takže mezistupně nejsou šedivé bahno.
    /// </remarks>
    private static readonly byte[] DecodeTable = BuildDecodeTable();

    /// <summary>
    /// Stínění rohu podle toho, kolik sousedů ho zaclání. Luanti
    /// <c>getSmoothLightCombined</c>: 0,75 / 0,50 / 0,25 v gama prostoru.
    /// </summary>
    private static readonly float[] AmbientOcclusionTable =
    [
        MathF.Pow(0.25f, 1f / AmbientOcclusionGamma),   // roh uzavřený ze všech stran
        MathF.Pow(0.50f, 1f / AmbientOcclusionGamma),
        MathF.Pow(0.75f, 1f / AmbientOcclusionGamma),
        1f,                                             // volný roh
    ];

    /// <summary>Jas 0–255 pro úroveň 0–15.</summary>
    public static byte Decode(int level) =>
        DecodeTable[Math.Clamp(level, 0, LightSun)];

    /// <summary>Jas 0–1 pro úroveň 0–15.</summary>
    public static float Brightness(int level) => Decode(level) / 255f;

    /// <summary>Jas 0–1 pro spojitou úroveň 0–1. Luanti <c>decode_light_f</c>.</summary>
    public static float BrightnessF(float light)
    {
        light = MathF.Max(light, 0f);
        float scaled = light * LightSun;
        int index = (int)scaled;

        if (index >= LightSun)
        {
            return 1f;
        }

        float fraction = scaled - index;
        return ((DecodeTable[index] * (1f - fraction)) + (DecodeTable[index + 1] * fraction)) / 255f;
    }

    /// <summary>
    /// Jas stěny podle její normály. Luanti <c>applyFacesShading</c> z
    /// <c>src/client/mesh.cpp</c>.
    /// </summary>
    /// <remarks>
    /// <para>Tohle je jediný zdroj plastičnosti terénu bez stínové mapy: vršek 1,000,
    /// spodek 0,447, stěny podél X 0,671 a podél Z 0,837. Čísla jsou odmocniny z 1,0 /
    /// 0,2 / 0,45 / 0,7.</para>
    ///
    /// <para><b>Nezávisí to na poloze slunce, a to je záměr.</b> Předchozí řešení počítalo
    /// jas z <c>dot(normála, směr ke slunci)</c>, takže se s během dne měnil jas stěn —
    /// jenže jas je zapečený ve vrcholu z doby meshování, takže se měnil jen u chunků,
    /// které se zrovna přemeshovaly. Sousední kopce pak měly každý jiné stínování.</para>
    /// </remarks>
    /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
    public static float FaceShade(int axis, bool positive) => axis switch
    {
        1 => positive ? 1.000000f : 0.447213f,
        2 => 0.836660f,
        _ => 0.670820f,
    };

    /// <summary>
    /// Stínění rohu 0–3, kde 3 je volný roh. Násobitel jasu.
    /// </summary>
    public static float AmbientOcclusion(int corner) =>
        AmbientOcclusionTable[Math.Clamp(corner, 0, 3)];

    /// <summary>
    /// Podíl denního světla, 0–1000. Luanti <c>time_to_daynight_ratio</c>.
    /// </summary>
    /// <remarks>
    /// <para>Devět bodů mezi 4375 a 6375 tisícin dne, mezi nimi lineární interpolace.
    /// Křivka je symetrická kolem poledne, takže se západ chová jako převrácený východ.</para>
    ///
    /// <para><b>Noc není nula, ale 175.</b> Plná tma by znamenala, že hráč bez pochodně
    /// nevidí vůbec nic, a tak to Luanti nedělá — venku v noci je vidět zhruba na pětinu
    /// denního jasu.</para>
    /// </remarks>
    /// <param name="timeOfDay">Podíl dne 0–1, kde 0 je půlnoc a 0,5 poledne.</param>
    public static float DayNightRatio(float timeOfDay, bool smooth = true)
    {
        float t = timeOfDay * 24000f;

        if (t < 0f)
        {
            t += (int)(-t / 24000f) * 24000f;
        }

        if (t >= 24000f)
        {
            t -= (int)(t / 24000f) * 24000f;
        }

        if (t > 12000f)
        {
            t = 24000f - t;
        }

        ReadOnlySpan<float> times =
        [
            4375f, 4625f, 4875f, 5125f, 5375f, 5625f, 5875f, 6125f, 6375f,
        ];
        ReadOnlySpan<float> values =
        [
            175f, 175f, 250f, 350f, 500f, 675f, 875f, 1000f, 1000f,
        ];

        if (!smooth)
        {
            float last = times[0];
            for (int i = 1; i < times.Length; i++)
            {
                float switchTime = (times[i] + last) / 2f;
                last = times[i];
                if (switchTime > t)
                {
                    return values[i];
                }
            }

            return 1000f;
        }

        if (t <= 4625f)
        {
            return values[0];
        }

        if (t >= 6125f)
        {
            return 1000f;
        }

        for (int i = 1; i < times.Length; i++)
        {
            if (times[i] <= t)
            {
                continue;
            }

            float span = times[i] - times[i - 1];
            float f = (t - times[i - 1]) / span;
            return (f * values[i]) + ((1f - f) * values[i - 1]);
        }

        return 1000f;
    }

    /// <summary>
    /// Barva slunečního světla pro daný denní podíl. Luanti <c>get_sunlight_color</c>.
    /// </summary>
    /// <remarks>
    /// Modrá je vyšší než červená se zelenou a v noci neklesne pod 0,10 — noc je proto
    /// modrá, ne šedá. Tohle je jediné místo, kterým denní doba do osvětlení vstupuje:
    /// jas ve vrcholu je zapečený a nemění se, mění se jen barva, kterou se násobí.
    /// </remarks>
    /// <param name="ratio">Podíl denního světla 0–1000 z <see cref="DayNightRatio"/>.</param>
    public static Vector3 SunlightColor(float ratio)
    {
        float rg = (ratio / 1000f) - 0.04f;
        float b = ((0.98f * ratio) / 1000f) + 0.078f;
        return new Vector3(rg, rg, b);
    }

    private static byte[] BuildDecodeTable()
    {
        float a = Alpha + Beta - 2f;
        float b = 3f - (2f * Alpha) - Beta;
        float c = Alpha;

        var table = new byte[LightSun + 1];

        // Krajní hodnoty jsou dané, ne dopočítané.
        table[0] = 0;
        table[LightSun] = 255;

        for (int i = 1; i < LightSun; i++)
        {
            float x = (float)i / LightSun;
            float brightness = ((((a * x) + b) * x) + c) * x;
            brightness += Boost * MathF.Exp(-0.5f * Square((x - BoostCenter) / BoostSpread));
            brightness = brightness <= 0f ? 0f : MathF.Pow(MathF.Min(brightness, 1f), 1f / Gamma);

            int value = Math.Clamp((int)(255f * brightness), 0, 255);

            // Každý stupeň musí být světlejší než předchozí, jinak by dva sousední stupně
            // splynuly a přechod světla by měl schod.
            if (value <= table[i - 1])
            {
                value = Math.Min(254, (int)table[i - 1]) + 1;
            }

            table[i] = (byte)value;
        }

        return table;
    }

    private static float Square(float value) => value * value;
}
