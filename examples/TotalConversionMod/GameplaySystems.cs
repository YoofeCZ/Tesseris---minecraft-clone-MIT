using System.Buffers.Binary;
using Tesseris.ModApi;

namespace TotalConversionMod;

internal sealed class WispEnergySystem : IModSystem
{
    public void Execute(IModSystemContext context)
    {
        IReadOnlyList<ModEntitySnapshot> wisps = context.Entities.WithAll(new[] { Ids.EntityEnergy });
        if (wisps.Count == 0)
        {
            context.Commands.Create(Ids.WispArchetype, Ids.WispSpawnRequest);
        }

        if (context.Tick % 20 != 0)
        {
            return;
        }

        foreach (ModEntitySnapshot entity in wisps)
        {
            ModComponentValue old = entity.Components.Single(component => component.ComponentId == Ids.EntityEnergy);
            int energy = BinaryPrimitives.ReadInt32LittleEndian(old.Value.Payload.Span);
            byte[] payload = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(payload, unchecked(energy + 1));
            context.Commands.SetComponent(
                entity.Id,
                entity.Revision,
                new ModComponentValue(
                    Ids.EntityEnergy,
                    new ModSerializedValue(Ids.Int32Serializer, SchemaVersion: 1, payload)));
        }
    }
}

internal sealed class AltarMenuHandler : IModMenuHandler
{
    public ModActionResult Handle(
        ModMenuSessionSnapshot session,
        ModMenuAction action,
        IModContainerCommandBuffer commands)
    {
        if (action.Id == Ids.CloseMenuAction)
        {
            commands.Close(session.Id, action.ExpectedSessionRevision);
            return ModActionResult.Handled;
        }

        if (action.Id != Ids.AddEnergyAction)
        {
            return ModActionResult.Pass;
        }

        int energy = 0;
        ModComponentValue? current = session.Container.Components
            .SingleOrDefault(component => component.ComponentId == Ids.ContainerEnergy);
        if (current is not null)
        {
            energy = BinaryPrimitives.ReadInt32LittleEndian(current.Value.Value.Payload.Span);
        }

        byte[] payload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, checked(energy + 1));
        commands.SetState(
            session.Container.Id,
            session.Container.Revision,
            new ModComponentValue(
                Ids.ContainerEnergy,
                new ModSerializedValue(Ids.Int32Serializer, SchemaVersion: 1, payload)));
        return ModActionResult.Handled;
    }
}

internal sealed class CompassUseBehavior(IModClientUiRegistry ui, IModAudioRegistry audio)
    : IModItemUseBehavior
{
    public ModActionResult OnUse(IModItemUseContext context)
    {
        if (context.Use != ModUseKind.Secondary || context.Phase != ModInputPhase.Pressed)
        {
            return ModActionResult.Pass;
        }

        OpenAltar(ui, audio);
        return ModActionResult.Handled;
    }

    internal static void OpenAltar(IModClientUiRegistry ui, IModAudioRegistry audio)
    {
        if (ui.Open(new ModScreenOpenRequest(Ids.AltarScreen)))
        {
            audio.Play(new ModSoundPlayRequest(Ids.ChimeSound));
        }
    }
}
