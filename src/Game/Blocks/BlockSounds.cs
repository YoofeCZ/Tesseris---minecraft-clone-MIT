using OpenTK.Mathematics;
using Tesseris.Engine.Audio;

namespace Tesseris.Game.Blocks;

/// <summary>Co se s blokem právě stalo. Určuje, jak zvuk zní.</summary>
public enum BlockAction
{
    /// <summary>Krok po povrchu.</summary>
    Step,

    /// <summary>Seknutí dlátem.</summary>
    Chisel,

    /// <summary>Vytěžení celého bloku.</summary>
    Break,

    /// <summary>Položení bloku.</summary>
    Place,
}

/// <summary>
/// Zvuky odvozené od materiálu bloku.
///
/// Vzorky se generují při startu, protože zadání zakazuje binární assety. Materiál rozhoduje
/// o barvě zvuku, akce o jeho ostrosti a délce — krok po kameni zní jinak než seknutí do něj.
///
/// Zvuky se vytvářejí předem a jen jednou. Generování při každém úderu by znamenalo desítky
/// tisíc vzorků uprostřed framu.
/// </summary>
public sealed class BlockSounds
{
    private readonly Dictionary<(BlockMaterial Material, BlockAction Action), SoundHandle> _sounds = [];
    private readonly AudioEngine _audio;

    public BlockSounds(AudioEngine audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        _audio = audio;

        foreach (BlockMaterial material in Enum.GetValues<BlockMaterial>())
        {
            foreach (BlockAction action in Enum.GetValues<BlockAction>())
            {
                float[] samples = SoundSynth.Render(RecipeFor(material, action));
                _sounds[(material, action)] = _audio.CreateSound(samples, looping: false, minDistance: 1.5f, maxDistance: 40f);
            }
        }

        Ambient = _audio.CreateSound(SoundSynth.RenderAmbientLoop(8f, seed: 4242), looping: true);
    }

    /// <summary>Ambientní smyčka. Hraje bez umístění, takže je slyšet všude stejně.</summary>
    public SoundHandle Ambient { get; }

    /// <summary>Kolik zvuků se podařilo vytvořit. Bez nativní knihovny nula.</summary>
    public int LoadedSounds => _sounds.Values.Count(handle => handle.IsValid);

    /// <summary>
    /// Přehraje zvuk z místa, kde se to stalo.
    ///
    /// Souřadnice je střed bloku, ne poloha hráče — tesání musí znít z bloku, do kterého
    /// se seklo, jinak by celý prostorový zvuk nedával smysl.
    /// </summary>
    public void Play(BlockMaterial material, BlockAction action, Vector3i blockPosition, float volume = 1f)
    {
        if (_sounds.TryGetValue((material, action), out SoundHandle sound))
        {
            _audio.PlayAt(sound, BlockCenter(blockPosition), volume);
        }
    }

    /// <summary>Střed bloku ve světových souřadnicích.</summary>
    public static Vector3 BlockCenter(Vector3i blockPosition) =>
        new(blockPosition.X + 0.5f, blockPosition.Y + 0.5f, blockPosition.Z + 0.5f);

    /// <summary>
    /// Recept pro dvojici materiál a akce.
    ///
    /// Materiál dá základní barvu, akce ji upraví: krok je tupý a krátký, seknutí ostré,
    /// vytěžení delší a hlubší, položení tlumené.
    /// </summary>
    public static SoundRecipe RecipeFor(BlockMaterial material, BlockAction action)
    {
        (float frequency, float noise, float brightness) = material switch
        {
            BlockMaterial.Stone => (180f, 0.75f, 0.55f),
            BlockMaterial.Soil => (120f, 0.60f, 0.20f),
            BlockMaterial.Wood => (240f, 0.35f, 0.40f),
            BlockMaterial.Sand => (300f, 0.95f, 0.35f),
            BlockMaterial.Glass => (900f, 0.25f, 0.90f),
            _ => (200f, 0.60f, 0.40f),
        };

        (float duration, float decay, float pitchScale, float extraNoise) = action switch
        {
            BlockAction.Step => (0.13f, 42f, 0.8f, 0.05f),
            BlockAction.Chisel => (0.10f, 60f, 1.6f, 0.00f),
            BlockAction.Break => (0.32f, 16f, 0.7f, 0.10f),
            BlockAction.Place => (0.16f, 30f, 1.0f, -0.10f),
            _ => (0.15f, 30f, 1.0f, 0f),
        };

        // Seed z dvojice, aby stejná kombinace zněla pokaždé stejně a různé se nepodobaly.
        int seed = ((int)material * 977) + ((int)action * 31) + 1;

        return new SoundRecipe(
            duration,
            frequency * pitchScale,
            Math.Clamp(noise + extraNoise, 0f, 1f),
            decay,
            brightness,
            seed);
    }
}
