namespace Tesseris.ModApi;

public readonly record struct ModQuaternion(float X, float Y, float Z, float W);

public readonly record struct ModTransform(ModVector3 Position, ModQuaternion Rotation, ModVector3 Scale);

public enum ModRenderPhase
{
    BeforeWorld = 0,
    OpaqueWorld = 100,
    TransparentWorld = 200,
    AfterWorld = 300,
    Hud = 400
}

public sealed record ModRenderViewSnapshot(
    ModVector3 CameraPosition,
    ModQuaternion CameraRotation,
    int ViewportWidth,
    int ViewportHeight,
    float PartialTick);

/// <summary>
/// Renderer-neutral commands recorded during a render callback. Resources are stable content IDs; the host
/// owns all GPU objects and may reject unavailable resources. Commands preserve issue order within a callback.
/// </summary>
public interface IModRenderCommandBuffer
{
    void DrawModel(ResourceId modelId, ModTransform transform, ModColor tint);

    void DrawBillboard(ResourceId textureId, ModVector3 position, float width, float height, ModColor tint);

    void DrawLine(ModVector3 from, ModVector3 to, ModColor color, float width);
}

public interface IModRenderCallback
{
    void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands);
}

public interface IModRenderRegistry
{
    void Register(ResourceId id, ModRenderPhase phase, int priority, IModRenderCallback callback);
}

public sealed record ModParticleDefinition(
    ResourceId Id,
    ResourceId TextureId,
    TimeSpan Lifetime,
    float InitialSize,
    ModColor Color);

public sealed record ModParticleSpawn(
    ResourceId ParticleId,
    ModVector3 Position,
    ModVector3 Velocity,
    ulong DeterministicSeed);

public interface IModParticleRegistry
{
    void Register(ModParticleDefinition definition);

    void Spawn(ModParticleSpawn spawn);
}

public sealed record ModSoundDefinition(
    ResourceId Id,
    ResourceId AssetId,
    float DefaultVolume = 1f,
    float DefaultPitch = 1f);

public sealed record ModSoundPlayRequest(
    ResourceId SoundId,
    ModVector3? Position = null,
    float Volume = 1f,
    float Pitch = 1f,
    bool Loop = false);

public interface IModAudioRegistry
{
    void Register(ModSoundDefinition definition);

    ulong Play(ModSoundPlayRequest request);

    bool Stop(ulong playbackId);
}

/// <summary>
/// Client-only capability. Access is game-thread-only; dedicated/headless hosts omit it from capability
/// discovery. Render callbacks may only record commands and must not retain the provided buffer.
/// </summary>
public interface IModClientPlatform
{
    IModInputRegistry Input { get; }

    IModCommandRegistry Commands { get; }

    IModClientUiRegistry Ui { get; }

    IModRenderRegistry Rendering { get; }

    IModParticleRegistry Particles { get; }

    IModAudioRegistry Audio { get; }
}
