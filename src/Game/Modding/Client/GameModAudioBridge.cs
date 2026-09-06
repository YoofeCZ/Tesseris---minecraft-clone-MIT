using OpenTK.Mathematics;
using Tesseris.Engine.Audio;
using Tesseris.Engine.Core;
using Tesseris.Game.Content;
using Tesseris.ModApi;
using ModResourceId = Tesseris.ModApi.ResourceId;

namespace Tesseris.Game.Modding.Client;

internal interface IGameModAudioBackend
{
    bool IsAvailable { get; }
    SoundHandle CreateSoundFromFile(string path);
    VoiceHandle PlayGlobal(SoundHandle sound, float volume, float pitch, bool looping);
    VoiceHandle PlayAt(SoundHandle sound, ModVector3 position, float volume, float pitch, bool looping);
    void StopVoice(VoiceHandle voice);
    void DestroySound(SoundHandle sound);
}

internal sealed class AudioEngineModAudioBackend(AudioEngine audio) : IGameModAudioBackend
{
    public bool IsAvailable => audio.IsAvailable;
    public SoundHandle CreateSoundFromFile(string path) => audio.CreateSoundFromFile(path);
    public VoiceHandle PlayGlobal(SoundHandle sound, float volume, float pitch, bool looping) =>
        audio.PlayGlobal(sound, volume, pitch, looping);
    public VoiceHandle PlayAt(SoundHandle sound, ModVector3 position, float volume, float pitch, bool looping) =>
        audio.PlayAt(sound, new Vector3(position.X, position.Y, position.Z), volume, pitch, looping);
    public void StopVoice(VoiceHandle voice) => audio.StopVoice(voice);
    public void DestroySound(SoundHandle sound) => audio.DestroySound(sound);
}

/// <summary>Consumes frozen mod audio definitions and ordered playback commands on the client thread.</summary>
public sealed class GameModAudioBridge : IDisposable
{
    private readonly ModAudioHost host;
    private readonly IGameModAudioBackend backend;
    private readonly Action<string> warn;
    private readonly Dictionary<ModResourceId, LoadedSound> definitions = [];
    private readonly Dictionary<string, SoundHandle> soundsByPath;
    private readonly Dictionary<ulong, VoiceHandle> voices = [];
    private bool disposed;

    public GameModAudioBridge(
        ModAudioHost host,
        ContentAssetResolver assets,
        AudioEngine audio,
        Action<string>? warningSink = null)
        : this(host, assets, CreateBackend(audio), warningSink)
    {
    }

    internal GameModAudioBridge(
        ModAudioHost host,
        ContentAssetResolver assets,
        IGameModAudioBackend backend,
        Action<string>? warningSink = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(assets);
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        warn = warningSink ?? (message => Log.Warn("Mod audio: " + message));
        soundsByPath = new Dictionary<string, SoundHandle>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!host.IsFrozen) throw new InvalidOperationException("Freeze mod audio definitions before creating the audio bridge.");
        if (!backend.IsAvailable) return;

        foreach (RegisteredModSound registered in host.Definitions)
        {
            try
            {
                string? path = assets.ResolveAudioPath(registered.Definition.AssetId);
                if (path is null)
                {
                    warn($"Sound '{registered.Definition.Id}' asset '{registered.Definition.AssetId}' was not found.");
                    continue;
                }
                if (!soundsByPath.TryGetValue(path, out SoundHandle sound))
                {
                    sound = backend.CreateSoundFromFile(path);
                    if (!sound.IsValid)
                    {
                        warn($"Sound '{registered.Definition.Id}' asset '{registered.Definition.AssetId}' could not be loaded.");
                        continue;
                    }
                    soundsByPath.Add(path, sound);
                }
                definitions.Add(registered.Definition.Id, new LoadedSound(registered, sound));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
            {
                warn($"Sound '{registered.Definition.Id}' asset '{registered.Definition.AssetId}' is invalid: {exception.Message}");
            }
        }
    }

    /// <summary>Window call: drain and execute all commands once per client frame.</summary>
    public void ProcessPending()
    {
        ThrowIfDisposed();
        Process(host.DrainCommands());
    }

    internal void Process(IReadOnlyList<ModAudioCommand> commands)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(commands);
        foreach (ModAudioCommand command in commands)
        {
            switch (command)
            {
                case ModAudioPlayCommand play:
                    Play(play);
                    break;
                case ModAudioStopCommand stop:
                    Stop(stop.PlaybackId);
                    break;
                default:
                    warn($"Ignored unknown audio command '{command.GetType().Name}'.");
                    break;
            }
        }
    }

    private void Play(ModAudioPlayCommand command)
    {
        if (!backend.IsAvailable) return;
        if (!definitions.TryGetValue(command.Request.SoundId, out LoadedSound? loaded))
        {
            warn($"Playback {command.PlaybackId} references unavailable sound '{command.Request.SoundId}'.");
            return;
        }
        float volume = loaded.Registration.Definition.DefaultVolume * command.Request.Volume;
        float pitch = loaded.Registration.Definition.DefaultPitch * command.Request.Pitch;
        if (!float.IsFinite(volume) || volume < 0f || !float.IsFinite(pitch) || pitch <= 0f)
        {
            warn($"Playback {command.PlaybackId} for '{command.Request.SoundId}' has invalid effective volume or pitch.");
            return;
        }
        if (voices.Remove(command.PlaybackId, out VoiceHandle previous)) backend.StopVoice(previous);
        VoiceHandle voice = command.Request.Position is { } position
            ? backend.PlayAt(loaded.Sound, position, volume, pitch, command.Request.Loop)
            : backend.PlayGlobal(loaded.Sound, volume, pitch, command.Request.Loop);
        if (voice.IsValid) voices[command.PlaybackId] = voice;
    }

    private void Stop(ulong playbackId)
    {
        if (voices.Remove(playbackId, out VoiceHandle voice)) backend.StopVoice(voice);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (VoiceHandle voice in voices.OrderBy(value => value.Key).Select(value => value.Value))
            backend.StopVoice(voice);
        voices.Clear();
        foreach (SoundHandle sound in soundsByPath.Values.Distinct()) backend.DestroySound(sound);
        soundsByPath.Clear();
        definitions.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static IGameModAudioBackend CreateBackend(AudioEngine audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return new AudioEngineModAudioBackend(audio);
    }

    private sealed record LoadedSound(RegisteredModSound Registration, SoundHandle Sound);
}
