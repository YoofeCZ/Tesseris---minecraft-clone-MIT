using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client;

public sealed record RegisteredModParticle(string ModId, ModParticleDefinition Definition);
public sealed record RegisteredModSound(string ModId, ModSoundDefinition Definition);

public abstract record ModAudioCommand;
public sealed record ModAudioPlayCommand(ulong PlaybackId, ModSoundPlayRequest Request) : ModAudioCommand;
public sealed record ModAudioStopCommand(ulong PlaybackId) : ModAudioCommand;

public sealed class ModParticleHost
{
    private readonly ClientThreadGuard guard;
    private readonly bool enabled;
    private readonly Dictionary<ResourceId, RegisteredModParticle> definitions = [];
    private readonly List<ModParticleSpawn> pending = [];
    private RegisteredModParticle[] frozen = [];

    internal ModParticleHost(ClientThreadGuard guard, bool enabled)
    {
        this.guard = guard;
        this.enabled = enabled;
    }

    public bool IsFrozen { get; private set; }
    public IReadOnlyList<RegisteredModParticle> Definitions => IsFrozen
        ? Array.AsReadOnly(frozen)
        : Array.AsReadOnly(definitions.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray());
    internal IModParticleRegistry ForMod(string modId) => new View(this, modId);

    internal void Freeze()
    {
        guard.Ensure();
        if (IsFrozen) return;
        frozen = definitions.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray();
        IsFrozen = true;
    }

    public IReadOnlyList<ModParticleSpawn> DrainSpawns()
    {
        guard.Ensure();
        if (pending.Count == 0) return [];
        ModParticleSpawn[] result = pending.ToArray();
        pending.Clear();
        return Array.AsReadOnly(result);
    }

    private void Register(string modId, ModParticleDefinition definition)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(definition);
        if (!enabled) return;
        if (IsFrozen) throw new InvalidOperationException("Mod particle registration is frozen.");
        ClientRegistration.EnsureOwned(modId, definition.Id, "particle");
        if (string.IsNullOrWhiteSpace(definition.TextureId.Value))
            throw new ArgumentException("A particle texture ID is required.", nameof(definition));
        if (definition.Lifetime <= TimeSpan.Zero || definition.Lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(definition));
        if (!float.IsFinite(definition.InitialSize) || definition.InitialSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(definition));
        if (!definitions.TryAdd(definition.Id, new RegisteredModParticle(modId, definition)))
            throw new InvalidOperationException($"Particle '{definition.Id}' is already registered.");
    }

    private void Spawn(ModParticleSpawn spawn)
    {
        guard.Ensure();
        if (!enabled) return;
        if (!IsFrozen) throw new InvalidOperationException("Freeze particle definitions before spawning.");
        if (!definitions.ContainsKey(spawn.ParticleId))
            throw new KeyNotFoundException($"Unknown particle '{spawn.ParticleId}'.");
        if (!Finite(spawn.Position) || !Finite(spawn.Velocity))
            throw new ArgumentOutOfRangeException(nameof(spawn));
        pending.Add(spawn);
    }

    private static bool Finite(ModVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class View : IModParticleRegistry
    {
        private readonly ModParticleHost host;
        private readonly string modId;
        public View(ModParticleHost host, string modId) { this.host = host; this.modId = modId; }
        public void Register(ModParticleDefinition definition) => host.Register(modId, definition);
        public void Spawn(ModParticleSpawn spawn) => host.Spawn(spawn);
    }
}

public sealed class ModAudioHost
{
    private readonly ClientThreadGuard guard;
    private readonly bool enabled;
    private readonly Dictionary<ResourceId, RegisteredModSound> definitions = [];
    private readonly List<ModAudioCommand> pending = [];
    private readonly HashSet<ulong> active = [];
    private RegisteredModSound[] frozen = [];
    private ulong nextPlaybackId = 1;

    internal ModAudioHost(ClientThreadGuard guard, bool enabled)
    {
        this.guard = guard;
        this.enabled = enabled;
    }

    public bool IsFrozen { get; private set; }
    public IReadOnlyList<RegisteredModSound> Definitions => IsFrozen
        ? Array.AsReadOnly(frozen)
        : Array.AsReadOnly(definitions.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray());
    internal IModAudioRegistry ForMod(string modId) => new View(this, modId);

    internal void Freeze()
    {
        guard.Ensure();
        if (IsFrozen) return;
        frozen = definitions.Values.OrderBy(value => value.Definition.Id.Value, StringComparer.Ordinal).ToArray();
        IsFrozen = true;
    }

    public IReadOnlyList<ModAudioCommand> DrainCommands()
    {
        guard.Ensure();
        if (pending.Count == 0) return [];
        ModAudioCommand[] result = pending.ToArray();
        pending.Clear();
        return Array.AsReadOnly(result);
    }

    private void Register(string modId, ModSoundDefinition definition)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(definition);
        if (!enabled) return;
        if (IsFrozen) throw new InvalidOperationException("Mod audio registration is frozen.");
        ClientRegistration.EnsureOwned(modId, definition.Id, "sound");
        if (string.IsNullOrWhiteSpace(definition.AssetId.Value))
            throw new ArgumentException("A sound asset ID is required.", nameof(definition));
        ValidatePositiveFinite(definition.DefaultVolume, nameof(definition.DefaultVolume), allowZero: true);
        ValidatePositiveFinite(definition.DefaultPitch, nameof(definition.DefaultPitch), allowZero: false);
        if (!definitions.TryAdd(definition.Id, new RegisteredModSound(modId, definition)))
            throw new InvalidOperationException($"Sound '{definition.Id}' is already registered.");
    }

    private ulong Play(ModSoundPlayRequest request)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(request);
        if (!enabled) return 0;
        if (!IsFrozen) throw new InvalidOperationException("Freeze sound definitions before playback.");
        if (!definitions.ContainsKey(request.SoundId))
            throw new KeyNotFoundException($"Unknown sound '{request.SoundId}'.");
        if (request.Position is { } position && !Finite(position))
            throw new ArgumentOutOfRangeException(nameof(request));
        ValidatePositiveFinite(request.Volume, nameof(request.Volume), allowZero: true);
        ValidatePositiveFinite(request.Pitch, nameof(request.Pitch), allowZero: false);
        if (nextPlaybackId == 0) throw new InvalidOperationException("The mod audio playback ID space is exhausted.");
        ulong id = nextPlaybackId++;
        active.Add(id);
        pending.Add(new ModAudioPlayCommand(id, request));
        return id;
    }

    private bool Stop(ulong playbackId)
    {
        guard.Ensure();
        if (!enabled || playbackId == 0 || !active.Remove(playbackId)) return false;
        pending.Add(new ModAudioStopCommand(playbackId));
        return true;
    }

    private static void ValidatePositiveFinite(float value, string parameter, bool allowZero)
    {
        if (!float.IsFinite(value) || (allowZero ? value < 0f : value <= 0f))
            throw new ArgumentOutOfRangeException(parameter);
    }

    private static bool Finite(ModVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class View : IModAudioRegistry
    {
        private readonly ModAudioHost host;
        private readonly string modId;
        public View(ModAudioHost host, string modId) { this.host = host; this.modId = modId; }
        public void Register(ModSoundDefinition definition) => host.Register(modId, definition);
        public ulong Play(ModSoundPlayRequest request) => host.Play(request);
        public bool Stop(ulong playbackId) => host.Stop(playbackId);
    }
}
