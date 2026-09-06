namespace Tesseris.Engine.Audio;

/// <summary>Jak zvuk zní. Parametry stačí na to, aby se materiály daly rozeznat.</summary>
/// <param name="DurationSeconds">Délka vzorku.</param>
/// <param name="Frequency">Základní výška tónové složky v hertzech.</param>
/// <param name="NoiseAmount">Podíl šumu proti tónu, 0 až 1. Kámen skřípe, dřevo duní.</param>
/// <param name="Decay">Jak rychle zvuk odezní. Vyšší číslo znamená kratší doznění.</param>
/// <param name="Brightness">Míra otevření dolní propusti, 0 až 1. Vyšší je ostřejší.</param>
/// <param name="Seed">Seed pro šum, aby byl výsledek pokaždé stejný.</param>
public readonly record struct SoundRecipe(
    float DurationSeconds,
    float Frequency,
    float NoiseAmount,
    float Decay,
    float Brightness,
    int Seed);

/// <summary>
/// Generování zvuků v kódu.
///
/// Zadání zakazuje vytvářet binární assety, takže se nic nenačítá ze souboru — vzorky se
/// spočítají při startu. Není to náhrada za nahrávky, ale materiály jdou od sebe rozeznat
/// a hlavně je celý řetězec od úderu po výstup funkční a testovatelný.
///
/// Výstup je mono v plovoucí čárce v rozsahu −1 až 1, což je přesně to, co SoLoud přijímá
/// přes <c>Wav_loadRawWaveEx</c>.
/// </summary>
public static class SoundSynth
{
    /// <summary>Vzorkovací frekvence generovaných zvuků.</summary>
    public const int SampleRate = 44100;

    /// <summary>Vyrobí vzorky podle receptu.</summary>
    public static float[] Render(in SoundRecipe recipe)
    {
        int count = Math.Max(1, (int)(recipe.DurationSeconds * SampleRate));
        float[] samples = new float[count];

        // Vlastní generátor místo Random: stejný seed musí dát stejný zvuk i po změně verze .NET.
        uint state = (uint)recipe.Seed | 1u;

        // Jednopólová dolní propust. Stačí na to, aby šel odlišit tupý úder od ostrého.
        float filtered = 0f;
        float cutoff = 0.02f + (0.6f * Math.Clamp(recipe.Brightness, 0f, 1f));

        float phase = 0f;
        float phaseStep = MathF.Tau * recipe.Frequency / SampleRate;

        for (int i = 0; i < count; i++)
        {
            float time = i / (float)SampleRate;
            float envelope = MathF.Exp(-recipe.Decay * time);

            state = (state * 1664525u) + 1013904223u;
            float noise = ((state >> 8) / 8388608f) - 1f;

            filtered += cutoff * (noise - filtered);

            float tone = MathF.Sin(phase);
            phase += phaseStep;

            float mix = (filtered * recipe.NoiseAmount) + (tone * (1f - recipe.NoiseAmount));
            samples[i] = mix * envelope;
        }

        Normalize(samples);
        FadeEdges(samples);
        return samples;
    }

    /// <summary>
    /// Vyrobí ambientní smyčku. Je to pomalu se převalující šum, který má hlavně vyplnit ticho.
    /// Konce se prolnou, aby na spoji nelupalo.
    /// </summary>
    public static float[] RenderAmbientLoop(float durationSeconds, int seed)
    {
        int count = Math.Max(1, (int)(durationSeconds * SampleRate));
        float[] samples = new float[count];

        uint state = (uint)seed | 1u;
        float slow = 0f;
        float slower = 0f;

        for (int i = 0; i < count; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            float noise = ((state >> 8) / 8388608f) - 1f;

            // Dvě propusti za sebou udělají ze šumu vítr místo syčení.
            slow += 0.004f * (noise - slow);
            slower += 0.02f * (slow - slower);

            samples[i] = slower;
        }

        Normalize(samples);

        // Prolnutí konce se začátkem, aby smyčka navazovala.
        int blend = Math.Min(count / 4, SampleRate);
        for (int i = 0; i < blend; i++)
        {
            float t = i / (float)blend;
            samples[i] = (samples[i] * t) + (samples[count - blend + i] * (1f - t));
        }

        return samples;
    }

    private static void Normalize(float[] samples)
    {
        float peak = 0f;
        foreach (float sample in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        if (peak < 1e-6f)
        {
            return;
        }

        float gain = 0.9f / peak;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    /// <summary>Krátký náběh a doběh. Bez něj lupne skok z ticha na plnou amplitudu.</summary>
    private static void FadeEdges(float[] samples)
    {
        int fade = Math.Min(64, samples.Length / 8);
        if (fade <= 0)
        {
            return;
        }

        for (int i = 0; i < fade; i++)
        {
            float t = i / (float)fade;
            samples[i] *= t;
            samples[samples.Length - 1 - i] *= t;
        }
    }
}
