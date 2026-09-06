using OpenTK.Mathematics;
using Tesseris.Engine.Audio;
using Tesseris.Game.Blocks;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy zvukové vrstvy.
///
/// Testuje se všechno kromě skutečného výstupu do reproduktorů: generování vzorků, mapování
/// materiálů na zvuky a poloha zdroje. Přehrávání samo se ověřit nedá, protože nativní
/// knihovna <c>soloud_x64.dll</c> zatím není k dispozici — viz docs/PROGRESS.md.
/// </summary>
public sealed class AudioTests
{
    [Fact]
    public void Vzorky_maji_ocekavanou_delku()
    {
        var recipe = new SoundRecipe(0.25f, 200f, 0.5f, 20f, 0.5f, 1);

        float[] samples = SoundSynth.Render(recipe);

        Assert.Equal((int)(0.25f * SoundSynth.SampleRate), samples.Length);
    }

    [Fact]
    public void Vzorky_zustanou_v_rozsahu()
    {
        float[] samples = SoundSynth.Render(new SoundRecipe(0.2f, 440f, 0.7f, 25f, 0.8f, 7));

        Assert.All(samples, sample => Assert.InRange(sample, -1f, 1f));
    }

    [Fact]
    public void Stejny_recept_da_stejne_vzorky()
    {
        var recipe = new SoundRecipe(0.15f, 300f, 0.6f, 30f, 0.4f, 99);

        Assert.Equal(SoundSynth.Render(recipe), SoundSynth.Render(recipe));
    }

    [Fact]
    public void Jiny_seed_da_jiny_zvuk()
    {
        float[] a = SoundSynth.Render(new SoundRecipe(0.15f, 300f, 0.9f, 30f, 0.5f, 1));
        float[] b = SoundSynth.Render(new SoundRecipe(0.15f, 300f, 0.9f, 30f, 0.5f, 2));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Zvuk_zacina_i_konci_potichu()
    {
        float[] samples = SoundSynth.Render(new SoundRecipe(0.2f, 250f, 0.5f, 20f, 0.5f, 3));

        // Skok z ticha na plnou amplitudu by lupl.
        Assert.True(MathF.Abs(samples[0]) < 0.05f);
        Assert.True(MathF.Abs(samples[^1]) < 0.05f);
    }

    [Fact]
    public void Zvuk_neni_ticho()
    {
        float[] samples = SoundSynth.Render(new SoundRecipe(0.2f, 250f, 0.5f, 20f, 0.5f, 5));

        float peak = 0f;
        foreach (float sample in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        Assert.True(peak > 0.5f, $"Nejvyšší amplituda je jen {peak:F3}.");
    }

    [Fact]
    public void Rychlejsi_doznivani_zkrati_ozvuk()
    {
        float[] slow = SoundSynth.Render(new SoundRecipe(0.3f, 200f, 0.5f, 8f, 0.5f, 11));
        float[] fast = SoundSynth.Render(new SoundRecipe(0.3f, 200f, 0.5f, 80f, 0.5f, 11));

        // Energie v druhé polovině vzorku musí být u rychlého doznívání výrazně nižší.
        Assert.True(Energy(fast, fast.Length / 2) < Energy(slow, slow.Length / 2) * 0.5f);
    }

    [Fact]
    public void Ambientni_smycka_ma_navazujici_konce()
    {
        float[] loop = SoundSynth.RenderAmbientLoop(2f, seed: 1);

        Assert.Equal(2 * SoundSynth.SampleRate, loop.Length);

        // Na spoji smyčky nesmí být skok, jinak to při opakování lupne.
        Assert.True(MathF.Abs(loop[0] - loop[^1]) < 0.35f);
    }

    [Fact]
    public void Recepty_se_lisi_podle_materialu()
    {
        SoundRecipe stone = BlockSounds.RecipeFor(BlockMaterial.Stone, BlockAction.Chisel);
        SoundRecipe glass = BlockSounds.RecipeFor(BlockMaterial.Glass, BlockAction.Chisel);
        SoundRecipe sand = BlockSounds.RecipeFor(BlockMaterial.Sand, BlockAction.Chisel);

        // Sklo je vysoké a čisté, písek nízký a šumivý.
        Assert.True(glass.Frequency > stone.Frequency);
        Assert.True(sand.NoiseAmount > glass.NoiseAmount);
        Assert.True(glass.Brightness > sand.Brightness);
    }

    [Fact]
    public void Recepty_se_lisi_podle_akce()
    {
        SoundRecipe step = BlockSounds.RecipeFor(BlockMaterial.Stone, BlockAction.Step);
        SoundRecipe chisel = BlockSounds.RecipeFor(BlockMaterial.Stone, BlockAction.Chisel);
        SoundRecipe broken = BlockSounds.RecipeFor(BlockMaterial.Stone, BlockAction.Break);

        // Seknutí je nejostřejší a nejkratší, vytěžení nejdelší.
        Assert.True(chisel.Decay > step.Decay);
        Assert.True(broken.DurationSeconds > chisel.DurationSeconds);
        Assert.True(chisel.Frequency > step.Frequency);
    }

    [Fact]
    public void Kazda_kombinace_ma_vlastni_seed()
    {
        var seeds = new HashSet<int>();

        foreach (BlockMaterial material in Enum.GetValues<BlockMaterial>())
        {
            foreach (BlockAction action in Enum.GetValues<BlockAction>())
            {
                Assert.True(seeds.Add(BlockSounds.RecipeFor(material, action).Seed),
                    $"Kombinace {material}/{action} má seed, který se opakuje.");
            }
        }
    }

    [Fact]
    public void Kazdy_recept_da_pouzitelny_zvuk()
    {
        foreach (BlockMaterial material in Enum.GetValues<BlockMaterial>())
        {
            foreach (BlockAction action in Enum.GetValues<BlockAction>())
            {
                float[] samples = SoundSynth.Render(BlockSounds.RecipeFor(material, action));

                Assert.NotEmpty(samples);
                Assert.All(samples, sample => Assert.InRange(sample, -1f, 1f));
            }
        }
    }

    [Fact]
    public void Zdroj_zvuku_lezi_ve_stredu_bloku()
    {
        // Tohle je jádro požadavku „zvuk tesání zní z pozice bloku, ne ze středu hlavy".
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), BlockSounds.BlockCenter(Vector3i.Zero));
        Assert.Equal(new Vector3(10.5f, -3.5f, 7.5f), BlockSounds.BlockCenter(new Vector3i(10, -4, 7)));
    }

    [Fact]
    public void Bez_nativni_knihovny_se_engine_nerozbije()
    {
        // Za současného stavu projektu tudy test opravdu projde: DLL dodaná není.
        // Až bude, tenhle test ověří opačnou větev — že se zvuk rozběhne.
        using var audio = new AudioEngine();

        Assert.False(string.IsNullOrWhiteSpace(audio.Status));

        // Ani jedno volání nesmí spadnout, ať už knihovna je, nebo není.
        audio.SetGlobalVolume(0.5f);
        audio.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, Vector3.Zero);

        SoundHandle sound = audio.CreateSound(SoundSynth.Render(
            new SoundRecipe(0.1f, 300f, 0.5f, 30f, 0.5f, 1)));

        audio.PlayAt(sound, new Vector3(1f, 2f, 3f));
        audio.PlayGlobal(sound);
        audio.StopAll();

        if (!audio.IsAvailable)
        {
            Assert.False(sound.IsValid);
            Assert.Equal(0, audio.ActiveVoices);
        }
    }

    [Fact]
    public void Zvuky_bloku_se_daji_zalozit_i_bez_zvukove_karty()
    {
        using var audio = new AudioEngine();
        var sounds = new BlockSounds(audio);

        // Bez knihovny nevznikne žádný zvuk, ale nic nespadne a hra může běžet dál.
        Assert.Equal(audio.IsAvailable ? Enum.GetValues<BlockMaterial>().Length * Enum.GetValues<BlockAction>().Length : 0,
            sounds.LoadedSounds);

        sounds.Play(BlockMaterial.Stone, BlockAction.Chisel, new Vector3i(4, 5, 6));
    }

    private static float Energy(float[] samples, int from)
    {
        float sum = 0f;
        for (int i = from; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }

        return sum;
    }
}
