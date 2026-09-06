using OpenTK.Mathematics;
using Tesseris.Engine.Core;

namespace Tesseris.Engine.Audio;

/// <summary>Odkaz na načtený zvuk. Prázdná hodnota znamená, že se zvuk nepodařilo vytvořit.</summary>
public readonly record struct SoundHandle(IntPtr Value)
{
    public bool IsValid => Value != IntPtr.Zero;
}

/// <summary>SoLoud voice instance returned by one playback request.</summary>
public readonly record struct VoiceHandle(uint Value)
{
    public bool IsValid => Value != 0;
}

/// <summary>
/// Přehrávání zvuku přes SoLoud.
///
/// <para><b>Když nativní knihovna chybí, hra běží dál.</b> Bez <c>soloud_x64.dll</c> vedle
/// binárky by první volání skončilo výjimkou a shodilo start. Inicializace se proto celá
/// odchytává a engine se přepne do tichého režimu — všechny metody pak jen nic nedělají.
/// Zvuk je příjemnost, ne podmínka hraní.</para>
///
/// <para><b>Souřadnice.</b> SoLoud používá stejnou orientaci jako zbytek hry, takže se pozice
/// předávají beze změny. Posluchač se aktualizuje jednou za frame a hned poté se volá
/// přepočet — bez něj by se hlasitost a směr zvuků neměnily.</para>
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly List<IntPtr> _sounds = [];
    private IntPtr _handle;

    public AudioEngine()
    {
        try
        {
            _handle = SoLoudNative.Soloud_create();
            if (_handle == IntPtr.Zero)
            {
                Status = "SoLoud nevrátil objekt.";
                return;
            }

            int result = SoLoudNative.Soloud_initEx(
                _handle,
                flags: 0,
                SoLoudNative.AutoBackend,
                SoLoudNative.AutoSampleRate,
                SoLoudNative.AutoBufferSize,
                SoLoudNative.AutoChannels);

            if (result != 0)
            {
                Status = $"Inicializace SoLoud selhala, kód {result}.";
                SoLoudNative.Soloud_destroy(_handle);
                _handle = IntPtr.Zero;
                return;
            }

            IsAvailable = true;
            Status = "SoLoud běží.";
            Log.Info("Audio: " + Status);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            // Nejčastější případ za současného stavu projektu: knihovna prostě není dodaná.
            _handle = IntPtr.Zero;
            IsAvailable = false;
            Status = $"Nativní knihovna {SoLoudNative.Library} není k dispozici, hra běží bez zvuku.";
            Log.Warn("Audio: " + Status);
        }
    }

    /// <summary>Podařilo se zvuk rozběhnout?</summary>
    public bool IsAvailable { get; }

    /// <summary>Popis stavu pro overlay a pro selftest.</summary>
    public string Status { get; } = "Nespuštěno.";

    /// <summary>Kolik zvuků právě hraje. Bez zvuku vrací nulu.</summary>
    public int ActiveVoices => IsAvailable ? (int)SoLoudNative.Soloud_getActiveVoiceCount(_handle) : 0;

    /// <summary>Celková hlasitost, 0 až 1.</summary>
    public void SetGlobalVolume(float volume)
    {
        if (IsAvailable)
        {
            SoLoudNative.Soloud_setGlobalVolume(_handle, Math.Clamp(volume, 0f, 1f));
        }
    }

    /// <summary>
    /// Vytvoří zvuk z hotových vzorků.
    /// </summary>
    /// <param name="minDistance">Do téhle vzdálenosti hraje zvuk naplno.</param>
    /// <param name="maxDistance">Za touhle vzdáleností už není slyšet.</param>
    public SoundHandle CreateSound(float[] samples, bool looping = false, float minDistance = 1f, float maxDistance = 48f)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (!IsAvailable || samples.Length == 0)
        {
            return default;
        }

        IntPtr wav = SoLoudNative.Wav_create();
        if (wav == IntPtr.Zero)
        {
            return default;
        }

        // copy = 1: SoLoud si vzorky zkopíruje, takže náš buffer může sebrat úklid paměti.
        int result = SoLoudNative.Wav_loadRawWaveEx(
            wav, samples, (uint)samples.Length, SoundSynth.SampleRate, channels: 1, copy: 1, takeOwnership: 0);

        if (result != 0)
        {
            SoLoudNative.Wav_destroy(wav);
            Log.Warn($"Audio: načtení vzorků selhalo, kód {result}.");
            return default;
        }

        SoLoudNative.Wav_setLooping(wav, looping ? 1 : 0);
        SoLoudNative.Wav_set3dMinMaxDistance(wav, minDistance, maxDistance);
        SoLoudNative.Wav_set3dAttenuation(wav, SoLoudNative.InverseDistanceAttenuation, 1f);

        _sounds.Add(wav);
        return new SoundHandle(wav);
    }

    /// <summary>Loads a WAV or OGG file into an engine-owned sound source.</summary>
    public SoundHandle CreateSoundFromFile(
        string path,
        bool looping = false,
        float minDistance = 1f,
        float maxDistance = 48f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("SoLoud file sounds must use the .wav or .ogg extension.");
        }

        if (!File.Exists(path)) throw new FileNotFoundException("Audio file does not exist.", path);
        if (!float.IsFinite(minDistance) || minDistance <= 0f) throw new ArgumentOutOfRangeException(nameof(minDistance));
        if (!float.IsFinite(maxDistance) || maxDistance < minDistance) throw new ArgumentOutOfRangeException(nameof(maxDistance));
        if (!IsAvailable) return default;

        IntPtr wav = SoLoudNative.Wav_create();
        if (wav == IntPtr.Zero) return default;
        int result = SoLoudNative.Wav_load(wav, Path.GetFullPath(path));
        if (result != 0)
        {
            SoLoudNative.Wav_destroy(wav);
            Log.Warn($"Audio: načtení souboru '{path}' selhalo, kód {result}.");
            return default;
        }

        SoLoudNative.Wav_setLooping(wav, looping ? 1 : 0);
        SoLoudNative.Wav_set3dMinMaxDistance(wav, minDistance, maxDistance);
        SoLoudNative.Wav_set3dAttenuation(wav, SoLoudNative.InverseDistanceAttenuation, 1f);
        _sounds.Add(wav);
        return new SoundHandle(wav);
    }

    /// <summary>
    /// Přehraje zvuk na konkrétním místě ve světě.
    ///
    /// Tohle je ten rozdíl proti <see cref="PlayGlobal"/>: zvuk tesání musí znít z bloku,
    /// do kterého se seklo, ne ze středu hlavy.
    /// </summary>
    public VoiceHandle PlayAt(
        SoundHandle sound,
        Vector3 position,
        float volume = 1f,
        float pitch = 1f,
        bool looping = false)
    {
        if (!IsAvailable || !sound.IsValid) return default;
        ValidatePlayback(volume, pitch);
        uint voice = SoLoudNative.Soloud_play3dEx(
            _handle, sound.Value,
            position.X, position.Y, position.Z,
            velX: 0f, velY: 0f, velZ: 0f,
            volume, paused: 1, bus: 0);
        return ConfigureVoice(voice, pitch, looping);
    }

    /// <summary>Přehraje zvuk bez umístění — hraje pořád stejně nahlas, ať je hráč kdekoli.</summary>
    public VoiceHandle PlayGlobal(
        SoundHandle sound,
        float volume = 1f,
        float pitch = 1f,
        bool looping = false)
    {
        if (!IsAvailable || !sound.IsValid) return default;
        ValidatePlayback(volume, pitch);
        uint voice = SoLoudNative.Soloud_playEx(_handle, sound.Value, volume, pan: 0f, paused: 1, bus: 0);
        return ConfigureVoice(voice, pitch, looping);
    }

    public void StopVoice(VoiceHandle voice)
    {
        if (IsAvailable && voice.IsValid) SoLoudNative.Soloud_stop(_handle, voice.Value);
    }

    /// <summary>Stops voices using one source and releases only that source.</summary>
    public void DestroySound(SoundHandle sound)
    {
        if (!sound.IsValid || !_sounds.Remove(sound.Value)) return;
        if (IsAvailable) SoLoudNative.Soloud_stopAudioSource(_handle, sound.Value);
        SoLoudNative.Wav_destroy(sound.Value);
    }

    /// <summary>
    /// Nastaví polohu a orientaci posluchače. Volá se jednou za frame, jinak by se směr
    /// a hlasitost zvuků neměnily s pohybem hráče.
    /// </summary>
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up, Vector3 velocity)
    {
        if (!IsAvailable)
        {
            return;
        }

        SoLoudNative.Soloud_set3dListenerParametersEx(
            _handle,
            position.X, position.Y, position.Z,
            forward.X, forward.Y, forward.Z,
            up.X, up.Y, up.Z,
            velocity.X, velocity.Y, velocity.Z);

        SoLoudNative.Soloud_update3dAudio(_handle);
    }

    public void StopAll()
    {
        if (IsAvailable)
        {
            SoLoudNative.Soloud_stopAll(_handle);
        }
    }

    private VoiceHandle ConfigureVoice(uint voice, float pitch, bool looping)
    {
        if (voice == 0) return default;
        if (SoLoudNative.Soloud_setRelativePlaySpeed(_handle, voice, pitch) != 0)
        {
            SoLoudNative.Soloud_stop(_handle, voice);
            return default;
        }
        SoLoudNative.Soloud_setLooping(_handle, voice, looping ? 1 : 0);
        SoLoudNative.Soloud_setPause(_handle, voice, pause: 0);
        return new VoiceHandle(voice);
    }

    private static void ValidatePlayback(float volume, float pitch)
    {
        if (!float.IsFinite(volume) || volume < 0f) throw new ArgumentOutOfRangeException(nameof(volume));
        if (!float.IsFinite(pitch) || pitch <= 0f) throw new ArgumentOutOfRangeException(nameof(pitch));
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        SoLoudNative.Soloud_stopAll(_handle);

        foreach (IntPtr sound in _sounds)
        {
            SoLoudNative.Wav_destroy(sound);
        }

        _sounds.Clear();

        SoLoudNative.Soloud_deinit(_handle);
        SoLoudNative.Soloud_destroy(_handle);
        _handle = IntPtr.Zero;
    }
}
