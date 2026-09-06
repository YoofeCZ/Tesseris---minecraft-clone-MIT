using Tesseris.ModApi;

namespace TotalConversionMod;

internal sealed class TickObserver(
    IModLogger logger,
    IModGame game,
    IModParticleRegistry particles) : IModEventHandler<ModSimulationTickEvent>
{
    public ModEventResult Handle(ModSimulationTickEvent value)
    {
        if (value.Tick > 0 && value.Tick % 10 == 0)
        {
            ModVector3 player = game.Player.Position;
            particles.Spawn(new ModParticleSpawn(
                Ids.SkySparkParticle,
                new ModVector3(player.X, player.Y + 1.2f, player.Z),
                new ModVector3(0f, 0.45f, 0f),
                DeterministicSeed: value.Tick));
        }

        if (value.Tick > 0 && value.Tick % 1200 == 0)
        {
            logger.Info($"Skylands simulation reached tick {value.Tick}.");
        }

        return ModEventResult.Continue;
    }
}

internal sealed class SyncNetworkHandler(IModLogger logger) : IModNetworkHandler
{
    public void Receive(IModNetworkMessageContext context)
    {
        byte[] ownedPayload = context.Message.Payload.ToArray();
        context.Enqueue(() =>
            logger.Info($"Received {ownedPayload.Length} validated sync bytes on the game thread."));
    }
}
