using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client.Rendering;

internal readonly record struct ModParticleRenderSnapshot(
    ResourceId TextureId,
    ModVector3 Position,
    float Size,
    ModColor Color,
    ulong DeterministicSeed,
    ulong Serial);

internal sealed class ModParticleSimulation
{
    internal const int DefaultMaximumActiveParticles = 8_192;
    internal const int DefaultMaximumSpawnsPerUpdate = 2_048;

    private readonly int maximumActiveParticles;
    private readonly int maximumSpawnsPerUpdate;
    private readonly List<Particle> active = [];
    private ulong nextSerial;

    public ModParticleSimulation(
        int maximumActiveParticles = DefaultMaximumActiveParticles,
        int maximumSpawnsPerUpdate = DefaultMaximumSpawnsPerUpdate)
    {
        if (maximumActiveParticles <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActiveParticles));
        if (maximumSpawnsPerUpdate <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSpawnsPerUpdate));
        this.maximumActiveParticles = maximumActiveParticles;
        this.maximumSpawnsPerUpdate = maximumSpawnsPerUpdate;
    }

    internal int ActiveCount => active.Count;

    internal int Update(
        TimeSpan delta,
        IReadOnlyDictionary<ResourceId, ModParticleDefinition> definitions,
        IReadOnlyList<ModParticleSpawn> spawns)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(spawns);
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));

        long deltaTicks = delta.Ticks;
        float deltaSeconds = (float)delta.TotalSeconds;
        for (int index = active.Count - 1; index >= 0; index--)
        {
            Particle particle = active[index];
            long age = particle.AgeTicks > long.MaxValue - deltaTicks
                ? long.MaxValue
                : particle.AgeTicks + deltaTicks;
            if (age >= particle.Definition.Lifetime.Ticks)
            {
                active.RemoveAt(index);
                continue;
            }

            particle.AgeTicks = age;
            particle.Position = Add(particle.Position, Scale(particle.Velocity, deltaSeconds));
        }

        int accepted = 0;
        int considered = Math.Min(spawns.Count, maximumSpawnsPerUpdate);
        for (int index = 0; index < considered && active.Count < maximumActiveParticles; index++)
        {
            ModParticleSpawn spawn = spawns[index];
            if (!definitions.TryGetValue(spawn.ParticleId, out ModParticleDefinition? definition))
                continue;
            if (nextSerial == ulong.MaxValue)
                throw new InvalidOperationException("Particle serial space is exhausted.");
            active.Add(new Particle(
                definition,
                spawn.Position,
                spawn.Velocity,
                spawn.DeterministicSeed,
                ++nextSerial));
            accepted++;
        }

        return accepted;
    }

    internal IReadOnlyList<ModParticleRenderSnapshot> Snapshot()
    {
        if (active.Count == 0) return [];
        var result = new ModParticleRenderSnapshot[active.Count];
        for (int index = 0; index < active.Count; index++)
        {
            Particle particle = active[index];
            double remaining = 1.0 - (particle.AgeTicks / (double)particle.Definition.Lifetime.Ticks);
            byte alpha = (byte)Math.Clamp(
                (int)Math.Round(particle.Definition.Color.A * remaining, MidpointRounding.AwayFromZero),
                0,
                byte.MaxValue);
            result[index] = new ModParticleRenderSnapshot(
                particle.Definition.TextureId,
                particle.Position,
                particle.Definition.InitialSize,
                particle.Definition.Color with { A = alpha },
                particle.DeterministicSeed,
                particle.Serial);
        }

        return Array.AsReadOnly(result);
    }

    internal void Clear() => active.Clear();

    private static ModVector3 Add(ModVector3 left, ModVector3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    private static ModVector3 Scale(ModVector3 value, float scalar) =>
        new(value.X * scalar, value.Y * scalar, value.Z * scalar);

    private sealed class Particle(
        ModParticleDefinition definition,
        ModVector3 position,
        ModVector3 velocity,
        ulong deterministicSeed,
        ulong serial)
    {
        public ModParticleDefinition Definition { get; } = definition;
        public ModVector3 Position { get; set; } = position;
        public ModVector3 Velocity { get; } = velocity;
        public ulong DeterministicSeed { get; } = deterministicSeed;
        public ulong Serial { get; } = serial;
        public long AgeTicks { get; set; }
    }
}
