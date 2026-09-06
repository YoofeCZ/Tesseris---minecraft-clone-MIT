using Tesseris.Engine.Audio;
using Tesseris.Game.Content;
using Tesseris.Game.Modding;
using Tesseris.Game.Modding.Client;
using Tesseris.ModApi;
using Xunit;
using ModResourceId = Tesseris.ModApi.ResourceId;

namespace Tesseris.Tests;

public sealed class ModAudioBridgeTests
{
    [Fact]
    public void Resolver_accepts_wav_and_ogg_and_rejects_missing_unsafe_unsupported_and_duplicates()
    {
        using var temporary = new AudioTemporaryDirectory();
        string first = temporary.Directory("first");
        string second = temporary.Directory("second");
        string ogg = temporary.File("first", "audio", "effects", "beep.ogg");
        string wav = temporary.File("first", "audio", "effects", "beep.wav");
        var resolver = new ContentAssetResolver(new ContentCatalog(
        [
            new ContentSource("soundmod", first),
            new ContentSource("other", second)
        ]));

        Assert.Equal(ogg, resolver.ResolveAudioPath(new ModResourceId("soundmod:effects/beep.ogg")));
        Assert.Equal(wav, resolver.ResolveAudioPath(new ModResourceId("soundmod:effects/beep.wav")));
        Assert.Null(resolver.ResolveAudioPath(new ModResourceId("unknown:effects/beep.ogg")));
        Assert.Null(resolver.ResolveAudioPath(new ModResourceId("soundmod:effects/missing.ogg")));
        Assert.Throws<InvalidDataException>(() =>
            resolver.ResolveAudioPath(new ModResourceId("soundmod:effects/beep.mp3")));
        Assert.Throws<InvalidDataException>(() =>
            resolver.ResolveAudioPath(new ModResourceId("soundmod:../escape.ogg")));

        temporary.File("second", "audio", "effects", "beep.ogg");
        var duplicate = new ContentAssetResolver(new ContentCatalog(
        [
            new ContentSource("same", first),
            new ContentSource("same", second)
        ]));
        Assert.Throws<InvalidDataException>(() =>
            duplicate.ResolveAudioPath(new ModResourceId("same:effects/beep.ogg")));
    }

    [Fact]
    public void Bridge_loads_each_asset_once_and_processes_play_stop_in_issue_order()
    {
        using var temporary = new AudioTemporaryDirectory();
        string root = temporary.Directory("pack");
        string path = temporary.File("pack", "audio", "beep.ogg");
        var resolver = new ContentAssetResolver(new ContentCatalog([new ContentSource("test", root)]));
        (ModClientPlatform platform, IModAudioRegistry audio) = AudioPlatform(
            new ModSoundDefinition(
                new ModResourceId("test:beep"), new ModResourceId("test:beep.ogg"), 0.5f, 2f),
            new ModSoundDefinition(
                new ModResourceId("test:beep_alias"), new ModResourceId("test:beep.ogg"), 1f, 1f));
        var backend = new FakeAudioBackend();
        var warnings = new List<string>();
        using var bridge = new GameModAudioBridge(platform.Audio, resolver, backend, warnings.Add);

        ulong global = audio.Play(new ModSoundPlayRequest(
            new ModResourceId("test:beep"), Volume: 0.4f, Pitch: 1.5f, Loop: true));
        ulong positional = audio.Play(new ModSoundPlayRequest(
            new ModResourceId("test:beep_alias"), new ModVector3(1, 2, 3), 0.25f, 0.75f));
        Assert.True(audio.Stop(global));
        bridge.ProcessPending();

        Assert.Equal([path], backend.LoadedPaths);
        Assert.Equal(
            ["load:1", "global:1:0.200000003:3:True:100", "at:1:1,2,3:0.25:0.75:False:101", "stop:100"],
            backend.Events);
        Assert.Empty(warnings);

        bridge.Dispose();
        Assert.Equal("stop:101", backend.Events[^2]);
        Assert.Equal("destroy:1", backend.Events[^1]);
        Assert.DoesNotContain(backend.Events, value => value == "stop-all");
    }

    [Fact]
    public void Missing_invalid_and_failed_assets_log_and_do_not_crash_or_create_voice_mappings()
    {
        using var temporary = new AudioTemporaryDirectory();
        string root = temporary.Directory("pack");
        temporary.File("pack", "audio", "broken.ogg");
        var resolver = new ContentAssetResolver(new ContentCatalog([new ContentSource("test", root)]));
        (ModClientPlatform platform, IModAudioRegistry audio) = AudioPlatform(
            Sound("test:missing", "test:missing.ogg"),
            Sound("test:invalid", "test:invalid.mp3"),
            Sound("test:broken", "test:broken.ogg"));
        var backend = new FakeAudioBackend { FailLoads = true };
        var warnings = new List<string>();
        using var bridge = new GameModAudioBridge(platform.Audio, resolver, backend, warnings.Add);

        audio.Play(new ModSoundPlayRequest(new ModResourceId("test:missing")));
        audio.Play(new ModSoundPlayRequest(new ModResourceId("test:invalid")));
        audio.Play(new ModSoundPlayRequest(new ModResourceId("test:broken")));
        bridge.ProcessPending();

        Assert.Equal(6, warnings.Count);
        Assert.Contains(warnings, value => value.Contains("was not found", StringComparison.Ordinal));
        Assert.Contains(warnings, value => value.Contains("unsupported extension", StringComparison.Ordinal));
        Assert.Contains(warnings, value => value.Contains("could not be loaded", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Events, value => value.StartsWith("global:", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Events, value => value.StartsWith("at:", StringComparison.Ordinal));
    }

    [Fact]
    public void Headless_backend_drains_commands_without_loading_playing_or_stopping_foreign_audio()
    {
        using var temporary = new AudioTemporaryDirectory();
        string root = temporary.Directory("pack");
        temporary.File("pack", "audio", "beep.wav");
        var resolver = new ContentAssetResolver(new ContentCatalog([new ContentSource("test", root)]));
        (ModClientPlatform platform, IModAudioRegistry audio) = AudioPlatform(Sound("test:beep", "test:beep.wav"));
        var backend = new FakeAudioBackend { IsAvailable = false };
        using var bridge = new GameModAudioBridge(platform.Audio, resolver, backend);
        ulong playback = audio.Play(new ModSoundPlayRequest(new ModResourceId("test:beep")));
        Assert.True(audio.Stop(playback));

        bridge.ProcessPending();
        bridge.Dispose();

        Assert.Empty(backend.Events);
        Assert.Empty(platform.Audio.DrainCommands());
    }

    [Fact]
    public void Bridge_requires_frozen_definitions_and_replaces_duplicate_playback_mapping_safely()
    {
        using var temporary = new AudioTemporaryDirectory();
        string root = temporary.Directory("pack");
        temporary.File("pack", "audio", "beep.ogg");
        var resolver = new ContentAssetResolver(new ContentCatalog([new ContentSource("test", root)]));
        var unfrozen = new ModClientPlatform();
        Assert.Throws<InvalidOperationException>(() =>
            new GameModAudioBridge(unfrozen.Audio, resolver, new FakeAudioBackend()));

        (ModClientPlatform platform, _) = AudioPlatform(Sound("test:beep", "test:beep.ogg"));
        var backend = new FakeAudioBackend();
        using var bridge = new GameModAudioBridge(platform.Audio, resolver, backend);
        var request = new ModSoundPlayRequest(new ModResourceId("test:beep"));
        bridge.Process(
        [
            new ModAudioPlayCommand(5, request),
            new ModAudioPlayCommand(5, request),
            new ModAudioStopCommand(5)
        ]);

        Assert.Equal(
            ["load:1", "global:1:1:1:False:100", "stop:100", "global:1:1:1:False:101", "stop:101"],
            backend.Events);
    }

    private static (ModClientPlatform Platform, IModAudioRegistry Audio) AudioPlatform(
        params ModSoundDefinition[] definitions)
    {
        var platform = new ModClientPlatform();
        IModAudioRegistry audio = platform.ForMod("test").Audio;
        foreach (ModSoundDefinition definition in definitions) audio.Register(definition);
        platform.Freeze();
        return (platform, audio);
    }

    private static ModSoundDefinition Sound(string id, string asset) =>
        new(new ModResourceId(id), new ModResourceId(asset));

    private sealed class FakeAudioBackend : IGameModAudioBackend
    {
        private uint nextSound = 1;
        private uint nextVoice = 100;
        public bool IsAvailable { get; init; } = true;
        public bool FailLoads { get; init; }
        public List<string> LoadedPaths { get; } = [];
        public List<string> Events { get; } = [];

        public SoundHandle CreateSoundFromFile(string path)
        {
            LoadedPaths.Add(path);
            if (FailLoads) return default;
            var handle = new SoundHandle((IntPtr)nextSound++);
            Events.Add($"load:{handle.Value}");
            return handle;
        }

        public VoiceHandle PlayGlobal(SoundHandle sound, float volume, float pitch, bool looping)
        {
            var voice = new VoiceHandle(nextVoice++);
            Events.Add(FormattableString.Invariant(
                $"global:{sound.Value}:{volume:G9}:{pitch:G9}:{looping}:{voice.Value}"));
            return voice;
        }

        public VoiceHandle PlayAt(SoundHandle sound, ModVector3 position, float volume, float pitch, bool looping)
        {
            var voice = new VoiceHandle(nextVoice++);
            Events.Add(FormattableString.Invariant(
                $"at:{sound.Value}:{position.X:G9},{position.Y:G9},{position.Z:G9}:{volume:G9}:{pitch:G9}:{looping}:{voice.Value}"));
            return voice;
        }

        public void StopVoice(VoiceHandle voice) => Events.Add($"stop:{voice.Value}");
        public void DestroySound(SoundHandle sound) => Events.Add($"destroy:{sound.Value}");
    }

    private sealed class AudioTemporaryDirectory : IDisposable
    {
        public AudioTemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"tesseris-audio-bridge-tests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(params string[] segments)
        {
            string path = segments.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, [0]);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
