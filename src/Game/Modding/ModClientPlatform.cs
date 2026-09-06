using Tesseris.Game.Modding.Client;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>
/// Owner-scoped v2 client capability kernel. It contains no window, renderer, audio-backend or
/// input-library types; concrete game adapters consume its deterministic dispatch queues.
/// </summary>
public sealed class ModClientPlatform
{
    private readonly ClientThreadGuard guard = new();
    private readonly Dictionary<string, IModClientPlatform> views = new(StringComparer.Ordinal);

    public ModClientPlatform(bool available = true)
    {
        IsAvailable = available;
        Input = new ModInputHost(guard, available);
        Commands = new ModCommandHost(guard, available);
        Ui = new ModClientUiHost(guard, available);
        Rendering = new ModRenderHost(guard, available);
        Particles = new ModParticleHost(guard, available);
        Audio = new ModAudioHost(guard, available);
    }

    public bool IsAvailable { get; }
    public bool IsFrozen { get; private set; }

    public ModInputHost Input { get; }
    public ModCommandHost Commands { get; }
    public ModClientUiHost Ui { get; }
    public ModRenderHost Rendering { get; }
    public ModParticleHost Particles { get; }
    public ModAudioHost Audio { get; }

    public ModCapabilityDescriptor? CapabilityDescriptor => IsAvailable
        ? new ModCapabilityDescriptor(ModCapabilityIds.Client, "2.0.0", typeof(IModClientPlatform))
        : null;

    public static ModClientPlatform CreateDisabled() => new(available: false);

    internal IModClientPlatform ForMod(string modId)
    {
        guard.Ensure();
        string normalized = ClientRegistration.NormalizeModId(modId);
        if (views.TryGetValue(normalized, out IModClientPlatform? existing)) return existing;
        IModClientPlatform created = new OwnerView(this, normalized);
        views.Add(normalized, created);
        return created;
    }

    public bool TryGetCapability(string modId, out IModClientPlatform? capability)
    {
        guard.Ensure();
        if (!IsAvailable)
        {
            capability = null;
            return false;
        }

        capability = ForMod(modId);
        return true;
    }

    public void Freeze()
    {
        guard.Ensure();
        if (IsFrozen) return;
        Input.Freeze();
        Commands.Freeze();
        Ui.Freeze();
        Rendering.Freeze();
        Particles.Freeze();
        Audio.Freeze();
        IsFrozen = true;
    }

    public void Shutdown()
    {
        guard.Ensure();
        Ui.CloseActiveScreen();
        _ = Particles.DrainSpawns();
        _ = Audio.DrainCommands();
    }

    private sealed class OwnerView : IModClientPlatform
    {
        public OwnerView(ModClientPlatform platform, string modId)
        {
            Input = platform.Input.ForMod(modId);
            Commands = platform.Commands.ForMod(modId);
            Ui = platform.Ui.ForMod(modId);
            Rendering = platform.Rendering.ForMod(modId);
            Particles = platform.Particles.ForMod(modId);
            Audio = platform.Audio.ForMod(modId);
        }

        public IModInputRegistry Input { get; }
        public IModCommandRegistry Commands { get; }
        public IModClientUiRegistry Ui { get; }
        public IModRenderRegistry Rendering { get; }
        public IModParticleRegistry Particles { get; }
        public IModAudioRegistry Audio { get; }
    }
}
