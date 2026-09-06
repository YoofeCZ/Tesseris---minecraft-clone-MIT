using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModBehaviorTests
{
    private static readonly ResourceId Stone = new("tesseris:stone");
    private static readonly ModBlockPosition Origin = new(0, 0, 0);

    [Fact]
    public void Registrations_require_owner_namespace_allow_foreign_targets_and_freeze()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");

        behaviors.RegisterItemUse(
            new ResourceId("example:use_stone"),
            new ResourceId("tesseris:stone"),
            0,
            new ItemUseBehavior(_ => ModActionResult.Pass));

        Assert.Throws<ModHostException>(() => behaviors.RegisterItemUse(
            new ResourceId("other:not_owned"),
            new ResourceId("example:item"),
            0,
            new ItemUseBehavior(_ => ModActionResult.Pass)));

        registry.Freeze();
        Assert.True(registry.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => behaviors.RegisterItemUse(
            new ResourceId("example:too_late"),
            new ResourceId("example:item"),
            0,
            new ItemUseBehavior(_ => ModActionResult.Pass)));
    }

    [Fact]
    public void Item_handlers_are_deterministic_and_handled_stops_dispatch()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");
        var calls = new List<string>();
        var target = new ResourceId("other:multi_tool");
        behaviors.RegisterItemUse(new ResourceId("example:z"), target, 5,
            new ItemUseBehavior(_ => Add(calls, "z", ModActionResult.Pass)));
        behaviors.RegisterItemUse(new ResourceId("example:b"), target, -1,
            new ItemUseBehavior(_ => Add(calls, "b", ModActionResult.Handled)));
        behaviors.RegisterItemUse(new ResourceId("example:a"), target, -1,
            new ItemUseBehavior(context =>
            {
                Assert.Equal(target, context.ItemId);
                Assert.Equal(ModUseKind.Secondary, context.Use);
                Assert.Equal(ModInputPhase.Pressed, context.Phase);
                Assert.Equal(42UL, context.GameTick);
                return Add(calls, "a", ModActionResult.Pass);
            }));
        registry.Freeze();

        ModActionResult result = registry.DispatchItemUse(
            target, ModUseKind.Secondary, ModInputPhase.Pressed, null, 42);

        Assert.Equal(ModActionResult.Handled, result);
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public void Item_and_block_behaviors_receive_the_actual_held_stack_snapshot()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");
        var item = new ResourceId("example:charged_tool");
        var snapshot = new ModInventoryStackSnapshot(2, item, 1, 4, []);
        ModInventoryStackSnapshot? seenItem = null;
        ModInventoryStackSnapshot? seenBlock = null;
        behaviors.RegisterItemUse(new ResourceId("example:item_stack"), item, 0,
            new ItemUseBehavior(context =>
            {
                seenItem = context.Stack;
                return ModActionResult.Pass;
            }));
        behaviors.RegisterBlockInteraction(new ResourceId("example:block_stack"), Stone, 0,
            new BlockInteractionBehavior(context =>
            {
                seenBlock = context.HeldStack;
                return ModActionResult.Pass;
            }));
        registry.Freeze();

        registry.DispatchItemUse(item, ModUseKind.Primary, ModInputPhase.Pressed, null, 1, snapshot);
        registry.DispatchBlockInteraction(
            Stone, Origin, item, ModUseKind.Secondary, ModInputPhase.Pressed,
            new ModHit(Origin, default, default), 1, snapshot);

        Assert.Same(snapshot, seenItem);
        Assert.Same(snapshot, seenBlock);
    }

    [Fact]
    public void Block_interaction_denied_stops_lower_priority_handlers()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");
        var calls = new List<string>();
        behaviors.RegisterBlockInteraction(new ResourceId("example:first"), Stone, 0,
            new BlockInteractionBehavior(context =>
            {
                Assert.Equal(Origin, context.Position);
                Assert.Equal(new ResourceId("example:wrench"), context.HeldItemId);
                return Add(calls, "first", ModActionResult.Denied);
            }));
        behaviors.RegisterBlockInteraction(new ResourceId("example:last"), Stone, 1,
            new BlockInteractionBehavior(_ => Add(calls, "last", ModActionResult.Pass)));
        registry.Freeze();

        ModActionResult result = registry.DispatchBlockInteraction(
            Stone,
            Origin,
            new ResourceId("example:wrench"),
            ModUseKind.Primary,
            ModInputPhase.Pressed,
            new ModHit(Origin, default, new ModInt3(0, 1, 0)),
            7);

        Assert.Equal(ModActionResult.Denied, result);
        Assert.Equal(["first"], calls);
    }

    [Fact]
    public void Callback_failures_include_mod_registration_and_callback_kind()
    {
        var registry = new ModBehaviorRegistry();
        registry.ForMod("example").RegisterBlockLifecycle(
            new ResourceId("example:failing_lifecycle"),
            Stone,
            0,
            new LifecycleBehavior(_ => throw new ApplicationException("boom")));
        registry.Freeze();

        ModBehaviorCallbackException exception = Assert.Throws<ModBehaviorCallbackException>(() =>
            registry.DispatchBlockLifecycle(ModBlockLifecycleKind.Loaded, Stone, Origin, null, 0));

        Assert.Equal("example", exception.ModId);
        Assert.Equal(new ResourceId("example:failing_lifecycle"), exception.RegistrationId);
        Assert.Equal(ModBehaviorCallbackKind.BlockLifecycle, exception.CallbackKind);
        Assert.IsType<ApplicationException>(exception.InnerException);
    }

    [Fact]
    public void One_shot_schedule_replaces_due_tick_checks_target_and_can_schedule_again()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");
        var calls = new List<ulong>();
        var registrationId = new ResourceId("example:delayed_action");
        behaviors.RegisterScheduledBlockBehavior(
            registrationId,
            Stone,
            0,
            new TickBehavior(context =>
            {
                calls.Add(context.GameTick);
                if (calls.Count == 1) Assert.True(context.ScheduleAgain(2));
            }));
        registry.Freeze();

        Assert.False(behaviors.ScheduleBlock(registrationId, Origin, 1));
        var world = new FakeWorld();
        world.Set(Origin, Stone);
        registry.AttachWorld(world);
        registry.Tick(10);
        Assert.True(behaviors.ScheduleBlock(registrationId, Origin, 5));
        Assert.True(behaviors.ScheduleBlock(registrationId, Origin, 1));

        registry.Tick(10);
        Assert.Empty(calls);
        registry.Tick(11);
        Assert.Equal([11UL], calls);
        registry.Tick(12);
        Assert.Single(calls);
        registry.Tick(13);
        Assert.Equal([11UL, 13UL], calls);

        Assert.True(behaviors.ScheduleBlock(registrationId, Origin, 1));
        world.Set(Origin, new ResourceId("tesseris:dirt"));
        registry.Tick(14);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void Interval_ticks_use_only_explicitly_activated_positions_and_stable_order()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");
        var calls = new List<string>();
        behaviors.RegisterIntervalBlockBehavior(
            new ResourceId("example:z_interval"), Stone, 2, 2,
            new TickBehavior(context => calls.Add($"z:{context.Position.X}")));
        behaviors.RegisterIntervalBlockBehavior(
            new ResourceId("example:a_interval"), Stone, 2, 2,
            new TickBehavior(context => calls.Add($"a:{context.Position.X}")));
        registry.Freeze();
        var world = new FakeWorld();
        var left = new ModBlockPosition(-2, 4, 0);
        var right = new ModBlockPosition(3, 4, 0);
        world.Set(left, Stone);
        world.Set(right, Stone);
        registry.AttachWorld(world);

        registry.Tick(2);
        Assert.Empty(calls);
        registry.DispatchBlockLifecycle(ModBlockLifecycleKind.Loaded, Stone, right, null, 2);
        registry.DispatchBlockLifecycle(ModBlockLifecycleKind.Loaded, Stone, left, null, 2);
        registry.Tick(3);
        Assert.Empty(calls);
        registry.Tick(4);
        Assert.Equal(["a:-2", "a:3", "z:-2", "z:3"], calls);

        registry.DispatchBlockLifecycle(ModBlockLifecycleKind.Unloaded, Stone, left, null, 5);
        calls.Clear();
        registry.Tick(6);
        Assert.Equal(["a:3", "z:3"], calls);
    }

    [Fact]
    public void Lifecycle_and_interval_targets_are_queryable_without_world_scan()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");
        behaviors.RegisterBlockLifecycle(new ResourceId("example:lifecycle"), Stone, 0, new LifecycleBehavior(_ => { }));
        behaviors.RegisterIntervalBlockBehavior(
            new ResourceId("example:interval"),
            new ResourceId("other:custom_block"),
            0,
            10,
            new TickBehavior(_ => { }));
        registry.Freeze();

        Assert.Equal([Stone], registry.LifecycleTargetBlockIds);
        Assert.Equal([new ResourceId("other:custom_block")], registry.IntervalTargetBlockIds);
        Assert.True(registry.HasLifecycleTarget(Stone));
        Assert.False(registry.HasIntervalTarget(Stone));
    }

    [Fact]
    public void Registration_scheduling_and_dispatch_are_main_thread_only()
    {
        var registry = new ModBehaviorRegistry();
        IModBehaviors behaviors = registry.ForMod("example");
        var registrationId = new ResourceId("example:scheduled");
        behaviors.RegisterScheduledBlockBehavior(registrationId, Stone, 0, new TickBehavior(_ => { }));
        registry.Freeze();
        registry.AttachWorld(new FakeWorld());

        AssertWorkerThreadRejected(() => behaviors.ScheduleBlock(registrationId, Origin, 1));
        AssertWorkerThreadRejected(() => registry.DispatchItemUse(
            new ResourceId("example:item"), ModUseKind.Primary, ModInputPhase.Pressed, null, 0));
        AssertWorkerThreadRejected(() => registry.Tick(1));
    }

    [Fact]
    public void Empty_dispatch_has_pass_fast_path()
    {
        var registry = new ModBehaviorRegistry();
        registry.Freeze();

        Assert.Equal(
            ModActionResult.Pass,
            registry.DispatchItemUse(
                new ResourceId("missing:item"),
                ModUseKind.Primary,
                ModInputPhase.Held,
                null,
                0));
    }

    private static ModActionResult Add(List<string> calls, string call, ModActionResult result)
    {
        calls.Add(call);
        return result;
    }

    private static void AssertWorkerThreadRejected(Action action)
    {
        Exception? actual = null;
        var worker = new Thread(() => actual = Record.Exception(action));
        worker.Start();
        worker.Join();
        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("main game thread", exception.Message, StringComparison.Ordinal);
    }

    private sealed class ItemUseBehavior : IModItemUseBehavior
    {
        private readonly Func<IModItemUseContext, ModActionResult> callback;
        public ItemUseBehavior(Func<IModItemUseContext, ModActionResult> callback) => this.callback = callback;
        public ModActionResult OnUse(IModItemUseContext context) => callback(context);
    }

    private sealed class BlockInteractionBehavior : IModBlockInteractionBehavior
    {
        private readonly Func<IModBlockInteractionContext, ModActionResult> callback;
        public BlockInteractionBehavior(Func<IModBlockInteractionContext, ModActionResult> callback) => this.callback = callback;
        public ModActionResult OnInteract(IModBlockInteractionContext context) => callback(context);
    }

    private sealed class LifecycleBehavior : IModBlockLifecycleBehavior
    {
        private readonly Action<IModBlockLifecycleContext> callback;
        public LifecycleBehavior(Action<IModBlockLifecycleContext> callback) => this.callback = callback;
        public void OnBlockLifecycle(IModBlockLifecycleContext context) => callback(context);
    }

    private sealed class TickBehavior : IModBlockScheduledBehavior
    {
        private readonly Action<IModBlockTickContext> callback;
        public TickBehavior(Action<IModBlockTickContext> callback) => this.callback = callback;
        public void OnScheduledTick(IModBlockTickContext context) => callback(context);
    }

    private sealed class FakeWorld : IModWorld
    {
        private static readonly ResourceId Air = new("tesseris:air");
        private readonly Dictionary<ModBlockPosition, ResourceId> blocks = [];

        public void Set(ModBlockPosition position, ResourceId blockId) => blocks[position] = blockId;

        public ResourceId GetBlockId(int x, int y, int z) =>
            blocks.GetValueOrDefault(new ModBlockPosition(x, y, z), Air);

        public bool TrySetBlockId(int x, int y, int z, ResourceId blockId)
        {
            blocks[new ModBlockPosition(x, y, z)] = blockId;
            return true;
        }

        public bool IsChunkLoaded(int chunkX, int chunkY, int chunkZ) => true;
    }
}
