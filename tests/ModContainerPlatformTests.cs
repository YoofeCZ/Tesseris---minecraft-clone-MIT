using Tesseris.Game.Items;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModContainerPlatformTests
{
    [Fact]
    public void Definitions_freeze_in_order_and_slot_transactions_are_constrained_and_atomic()
    {
        var platform = new ModContainerPlatform();
        IModContainerPlatform alpha = platform.ForMod("alpha");
        IModContainerPlatform beta = platform.ForMod("beta");
        beta.RegisterContainerType(new ModContainerTypeDefinition(new ResourceId("beta:z"), 0));
        alpha.RegisterContainerType(new ModContainerTypeDefinition(new ResourceId("alpha:box"), 2, Serializer));
        Assert.Throws<InvalidOperationException>(() =>
            alpha.RegisterContainerType(new ModContainerTypeDefinition(new ResourceId("beta:no"), 1)));
        platform.Freeze();
        Assert.Equal(["alpha:box", "beta:z"], platform.ContainerTypes.Select(value => value.Definition.Id.Value));

        ModItemStackSnapshot stack = Stack(1, 2);
        ModContainerId container = platform.Create(
            new ResourceId("alpha:box"),
            [new ModContainerSlotSnapshot(0, null, false, true),
             new ModContainerSlotSnapshot(1, null, true, true)]);
        IModContainerCommandBuffer denied = platform.CreateCommandBuffer("alpha");
        denied.SetSlot(container, 0, 0, stack);
        Assert.False(platform.Commit(denied));
        Assert.Null(Get(platform, container).Slots[0].Stack);

        IModContainerCommandBuffer accepted = platform.CreateCommandBuffer("alpha");
        accepted.SetSlot(container, 0, 1, stack);
        accepted.SetState(container, 0, State("alpha:energy", [7]));
        Assert.True(platform.Commit(accepted));
        ModContainerSnapshot changed = Get(platform, container);
        Assert.Equal(1U, changed.Revision);
        Assert.Equal(stack.Id, changed.Slots[1].Stack!.Id);
        Assert.Equal(7, changed.Components.Single().Value.Payload.Span[0]);

        IModContainerCommandBuffer stale = platform.CreateCommandBuffer("alpha");
        stale.SetSlot(container, 0, 1, null);
        stale.SetState(container, 0, State("alpha:energy", [9]));
        Assert.False(platform.Commit(stale));
        Assert.NotNull(Get(platform, container).Slots[1].Stack);
        Assert.Equal(7, Get(platform, container).Components.Single().Value.Payload.Span[0]);
    }

    [Fact]
    public void Menu_actions_share_loopback_and_wire_validation_and_advance_session_revision()
    {
        var handler = new DelegateMenuHandler((session, action, commands) =>
        {
            commands.SetState(session.Container.Id, session.Container.Revision,
                State("test:last_action", action.Payload!.Value.Payload.ToArray()));
            return ModActionResult.Handled;
        });
        (ModContainerPlatform platform, IModContainerPlatform view, ModContainerId container) =
            MenuPlatform(handler);
        Assert.True(view.TryOpen(Menu, container, out ModMenuSessionSnapshot? opened));
        byte[] callerPayload = [3];
        var local = new ModMenuAction(new ResourceId("test:set"), 0,
            new ModSerializedValue(Serializer, 1, callerPayload));
        Assert.Equal(ModActionResult.Handled, platform.HandleAction(opened!.Id, local));
        callerPayload[0] = 99;
        Assert.True(view.TryGetSession(opened.Id, out ModMenuSessionSnapshot? afterLocal));
        Assert.Equal(1U, afterLocal!.Revision);
        Assert.Equal(3, afterLocal.Container.Components.Single().Value.Payload.Span[0]);

        var network = new ModMenuAction(new ResourceId("test:set"), 1,
            new ModSerializedValue(Serializer, 1, new byte[] { 8 }));
        Assert.Equal(
            ModActionResult.Handled,
            platform.HandleSerializedAction(opened.Id, ModContainerPlatform.SerializeAction(network)));
        Assert.Equal(ModActionResult.Denied, platform.HandleAction(opened.Id, network));
        Assert.True(view.TryGetSession(opened.Id, out ModMenuSessionSnapshot? afterNetwork));
        Assert.Equal(2U, afterNetwork!.Revision);
        Assert.Equal(8, afterNetwork.Container.Components.Single().Value.Payload.Span[0]);
        Assert.Equal(ModActionResult.Denied, platform.HandleAction(
            opened.Id,
            new ModMenuAction(new ResourceId("other:not_owned"), 2)));
    }

    [Fact]
    public void Menu_close_lifecycle_and_exceptions_keep_attribution_and_discard_commands()
    {
        var closing = new DelegateMenuHandler((session, _, commands) =>
        {
            commands.Close(session.Id, session.Revision);
            return ModActionResult.Handled;
        });
        (ModContainerPlatform platform, IModContainerPlatform view, ModContainerId container) = MenuPlatform(closing);
        Assert.True(view.TryOpen(Menu, container, out ModMenuSessionSnapshot? opened));
        Assert.Equal(ModActionResult.Handled, platform.HandleAction(
            opened!.Id, new ModMenuAction(new ResourceId("test:close"), 0)));
        Assert.True(view.TryGetSession(opened.Id, out ModMenuSessionSnapshot? closed));
        Assert.False(closed!.IsOpen);
        Assert.Equal(1U, closed.Revision);
        Assert.Equal(ModActionResult.Denied, platform.HandleAction(
            opened.Id, new ModMenuAction(new ResourceId("test:close"), 1)));

        var failing = new DelegateMenuHandler((session, _, commands) =>
        {
            commands.SetState(session.Container.Id, session.Container.Revision, State("test:value", [1]));
            throw new InvalidOperationException("boom");
        });
        (platform, view, container) = MenuPlatform(failing);
        Assert.True(view.TryOpen(Menu, container, out opened));
        ModMenuActionException exception = Assert.Throws<ModMenuActionException>(() => platform.HandleAction(
            opened!.Id, new ModMenuAction(new ResourceId("test:explode"), 0)));
        Assert.Equal("test", exception.ModId);
        Assert.Equal(Menu, exception.MenuId);
        Assert.Equal(new ResourceId("test:explode"), exception.ActionId);
        Assert.Empty(Get(platform, container).Components);
    }

    [Fact]
    public void Container_save_is_deterministic_and_preserves_unknown_state_and_stack_values()
    {
        var source = new ModContainerPlatform(idHigh: 77);
        IModContainerPlatform view = source.ForMod("absent");
        view.RegisterContainerType(new ModContainerTypeDefinition(
            new ResourceId("absent:box"), 1, new ResourceId("absent:state_serializer")));
        source.Freeze();
        var unknownStack = new ModItemStackSnapshot(
            new ModStackId(9, 4), new ResourceId("absent:item"), 2, 5,
            [new ModStackComponentValue(
                new ResourceId("absent:stack_data"),
                new ModSerializedValue(new ResourceId("absent:stack_serializer"), 7, new byte[] { 1, 4 }))]);
        ModContainerId id = source.Create(
            new ResourceId("absent:box"),
            [new ModContainerSlotSnapshot(0, unknownStack, true, false)],
            [new ModComponentValue(
                new ResourceId("absent:state"),
                new ModSerializedValue(new ResourceId("absent:state_serializer"), 12, new byte[] { 5, 6 }))]);
        byte[] bytes = source.SaveToBytes();
        Assert.Equal(bytes, source.SaveToBytes());

        var loaded = new ModContainerPlatform();
        loaded.Freeze();
        loaded.Load(new MemoryStream(bytes));
        ModContainerSnapshot snapshot = Get(loaded, id);
        Assert.Equal(new byte[] { 5, 6 }, snapshot.Components.Single().Value.Payload.ToArray());
        Assert.Equal(new byte[] { 1, 4 }, snapshot.Slots.Single().Stack!.Components.Single().Value.Payload.ToArray());
        Assert.False(snapshot.Slots.Single().CanExtract);
        Assert.Equal(bytes, loaded.SaveToBytes());
    }

    private static readonly ResourceId Serializer = new("test:bytes");
    private static readonly ResourceId Type = new("test:box");
    private static readonly ResourceId Menu = new("test:menu");

    private static (ModContainerPlatform Platform, IModContainerPlatform View, ModContainerId Container)
        MenuPlatform(IModMenuHandler handler)
    {
        var platform = new ModContainerPlatform();
        IModContainerPlatform view = platform.ForMod("test");
        view.RegisterContainerType(new ModContainerTypeDefinition(Type, 1, Serializer));
        view.RegisterMenu(new ModMenuDefinition(Menu, Type, new ResourceId("test:screen")), handler);
        platform.Freeze();
        return (platform, view, platform.Create(Type));
    }

    private static ModContainerSnapshot Get(ModContainerPlatform platform, ModContainerId id)
    {
        Assert.True(platform.TryGetContainer(id, out ModContainerSnapshot? value));
        return value!;
    }

    private static ModComponentValue State(string id, byte[] payload) =>
        new(new ResourceId(id), new ModSerializedValue(Serializer, 1, payload));

    private static ModItemStackSnapshot Stack(ulong low, int count) =>
        new(new ModStackId(1, low), new ResourceId("test:item"), count, 0, []);

    private sealed class DelegateMenuHandler(
        Func<ModMenuSessionSnapshot, ModMenuAction, IModContainerCommandBuffer, ModActionResult> callback)
        : IModMenuHandler
    {
        public ModActionResult Handle(
            ModMenuSessionSnapshot session,
            ModMenuAction action,
            IModContainerCommandBuffer commands) => callback(session, action, commands);
    }
}
