using System.Buffers.Binary;
using Tesseris.ModApi;

namespace TotalConversionMod;

/// <summary>
/// A standalone Mod API v2 example. It deliberately combines independent generic registries instead of
/// asking the game for a built-in "machine" or "total conversion" type.
/// </summary>
public sealed class TotalConversionEntry : IMod
{
    public void Configure(IModContext context)
    {
        if (context is not IModContextV2 v2)
        {
            throw new NotSupportedException("Total Conversion Example requires Tesseris Mod API v2.");
        }

        RegisterSerialization(v2);
        RegisterWorld(context, v2);
        RegisterGameplay(context, v2);
        RegisterClient(context, v2);
        RegisterIntegration(context, v2);

        context.Logger.Info("Total Conversion v2 registered its world and gameplay platform.");
    }

    private static void RegisterSerialization(IModContextV2 context)
    {
        context.Serialization.Register(
            Ids.Int32Serializer,
            currentSchemaVersion: 1,
            new Int32Serializer(),
            Array.Empty<IModDataMigration>());
    }

    private static void RegisterWorld(IModContext legacy, IModContextV2 context)
    {
        var generator = new SkylandsGenerator();
        var biomeSource = new SkylandsBiomeSource();

        context.WorldDefinitions.RegisterGenerator(Ids.Generator, generator);
        context.WorldDefinitions.RegisterBiomeSource(Ids.BiomeSource, biomeSource);
        context.WorldDefinitions.RegisterBiome(new ModBiomeDefinition(
            Ids.Biome,
            "Azure Skylands",
            Temperature: 0.45f,
            Humidity: 0.3f,
            FeatureIds: Array.Empty<ResourceId>(),
            Properties: Array.Empty<ModComponentValue>()));
        context.WorldDefinitions.RegisterDimension(new ModDimensionDefinition(
            Ids.Dimension,
            "Skylands",
            Ids.Generator,
            Ids.BiomeSource,
            MinimumY: -64,
            Height: 384,
            HasSky: true,
            Properties: Array.Empty<ModComponentValue>()));
        context.WorldDefinitions.RegisterPreset(new ModWorldPresetDefinition(
            Ids.Preset,
            "Total Conversion: Skylands",
            Ids.Dimension,
            new[] { Ids.Dimension },
            Array.Empty<ModComponentValue>()));

        // The world-definition registry describes selectable worlds. This call also replaces the legacy
        // overworld pipeline so the same generator owns every terrain chunk in hosts supporting that route.
        legacy.WorldGeneration.SetBaseGenerator(Ids.Generator, generator);

        // This is a useful, approved shared-contract service other mods can discover without referencing
        // this assembly. Larger ecosystems normally put their own contracts in a loader-approved assembly.
        context.InterModServices.Publish<IModBiomeSource>(
            Ids.BiomeService,
            version: "1.0.0",
            biomeSource);
    }

    private static void RegisterGameplay(IModContext legacy, IModContextV2 context)
    {
        // Entity/component definitions are registered before the host freezes serializers. Defaults therefore
        // carry their explicit schema and bytes; runtime code may use context.Serialization afterwards.
        ModSerializedValue zero = new(
            Ids.Int32Serializer,
            SchemaVersion: 1,
            new byte[sizeof(int)]);

        context.Entities.RegisterComponent(new ModComponentDescriptor(
            Ids.EntityEnergy,
            Ids.Int32Serializer,
            Replicated: true,
            Persisted: true));
        context.Entities.RegisterArchetype(new ModEntityArchetypeDefinition(
            Ids.WispArchetype,
            new[] { new ModComponentValue(Ids.EntityEnergy, zero) }));
        context.Entities.RegisterSystem(
            Ids.WispSystem,
            ModSystemPhase.Simulation,
            priority: 20,
            new WispEnergySystem());

        context.ItemStacks.RegisterComponent(new ModStackComponentDescriptor(
            Ids.StackCharge,
            Ids.Int32Serializer,
            CopyWhenSplit: true,
            RequireEqualToMerge: true));

        context.Containers.RegisterContainerType(new ModContainerTypeDefinition(
            Ids.AltarContainer,
            SlotCount: 4,
            StateSerializerId: Ids.Int32Serializer));
        context.Containers.RegisterMenu(
            new ModMenuDefinition(Ids.AltarMenu, Ids.AltarContainer, Ids.AltarScreen),
            new AltarMenuHandler());

        // A content item can execute arbitrary code. Here it opens the same screen as the input binding.
        legacy.Behaviors.RegisterItemUse(
            Ids.CompassUse,
            Ids.SkyCompassItem,
            priority: 0,
            new CompassUseBehavior(context.Client.Ui, context.Client.Audio));
    }

    private static void RegisterClient(IModContext legacy, IModContextV2 context)
    {
        context.Client.Audio.Register(new ModSoundDefinition(
            Ids.ChimeSound,
            Ids.ChimeAsset,
            DefaultVolume: 0.65f,
            DefaultPitch: 1f));
        context.Client.Particles.Register(new ModParticleDefinition(
            Ids.SkySparkParticle,
            Ids.SkyStoneTexture,
            Lifetime: TimeSpan.FromSeconds(1.5),
            InitialSize: 0.22f,
            Color: new ModColor(110, 225, 255, 220)));
        context.Client.Rendering.Register(
            Ids.SkyMarkerRender,
            ModRenderPhase.AfterWorld,
            priority: 0,
            new SkyMarkerRender());

        context.Client.Ui.RegisterScreen(
            Ids.AltarScreen,
            new AltarScreenFactory(context.Client.Ui, context.Client.Audio));
        context.Client.Ui.RegisterHud(
            Ids.StatusHud,
            ModHudAnchor.TopLeft,
            priority: 10,
            new StatusHud());
        context.Client.Input.Register(
            new ModInputBinding(
                Ids.OpenAltarInput,
                "Open Sky Altar",
                ModInputScope.Gameplay,
                DefaultKeyCode: 77),
            priority: 0,
            new OpenAltarInputHandler(context.Client.Ui, context.Client.Audio));
        context.Client.Commands.Register(
            Ids.OpenAltarCommand,
            usage: "/total_conversion altar",
            priority: 0,
            new OpenAltarCommand(context.Client.Ui, context.Client.Audio));

        // The older overlay API can coexist with the v2 anchored HUD.
        legacy.Ui.Register(Ids.LegacyStatusOverlay, priority: 10, new LegacyStatusOverlay());
    }

    private static void RegisterIntegration(IModContext context, IModContextV2 v2)
    {
        v2.EventBus.Subscribe(
            Ids.TickSubscription,
            ModEventPhase.After,
            priority: 0,
            new TickObserver(context.Logger, context.Game, v2.Client.Particles));

        v2.Network.Register(
            new ModNetworkChannelDefinition(
                Ids.SyncChannel,
                ProtocolVersion: "1.0.0",
                ModNetworkDirection.Bidirectional,
                MaximumPayloadBytes: 1024),
            new SyncNetworkHandler(context.Logger));
    }
}

internal static class Ids
{
    public static readonly ResourceId Int32Serializer = new("total_conversion:int32");
    public static readonly ResourceId Preset = new("total_conversion:skylands");
    public static readonly ResourceId Dimension = new("total_conversion:skylands");
    public static readonly ResourceId Biome = new("total_conversion:azure_skylands");
    public static readonly ResourceId Generator = new("total_conversion:skylands");
    public static readonly ResourceId BiomeSource = new("total_conversion:skylands");
    public static readonly ResourceId BiomeService = new("total_conversion:biome_sampler");
    public static readonly ResourceId EntityEnergy = new("total_conversion:wisp_energy");
    public static readonly ResourceId WispArchetype = new("total_conversion:sky_wisp");
    public static readonly ResourceId WispSystem = new("total_conversion:wisp_energy_system");
    public static readonly ResourceId WispSpawnRequest = new("total_conversion:initial_wisp");
    public static readonly ResourceId StackCharge = new("total_conversion:charge");
    public static readonly ResourceId AltarContainer = new("total_conversion:sky_altar");
    public static readonly ResourceId AltarMenu = new("total_conversion:sky_altar");
    public static readonly ResourceId AltarScreen = new("total_conversion:sky_altar");
    public static readonly ResourceId ContainerEnergy = new("total_conversion:altar_energy");
    public static readonly ResourceId AddEnergyAction = new("total_conversion:add_energy");
    public static readonly ResourceId CloseMenuAction = new("total_conversion:close");
    public static readonly ResourceId SkyCompassItem = new("total_conversion:sky_compass");
    public static readonly ResourceId CompassUse = new("total_conversion:sky_compass/use");
    public static readonly ResourceId ChimeSound = new("total_conversion:ui/chime");
    public static readonly ResourceId ChimeAsset = new("total_conversion:ui/chime.wav");
    public static readonly ResourceId SkyStoneTexture = new("total_conversion:sky_stone");
    public static readonly ResourceId SkySparkParticle = new("total_conversion:sky_spark");
    public static readonly ResourceId SkyMarkerRender = new("total_conversion:sky_marker");
    public static readonly ResourceId StatusHud = new("total_conversion:status_hud");
    public static readonly ResourceId OpenAltarInput = new("total_conversion:open_altar");
    public static readonly ResourceId OpenAltarCommand = new("total_conversion:altar");
    public static readonly ResourceId LegacyStatusOverlay = new("total_conversion:legacy_status");
    public static readonly ResourceId TickSubscription = new("total_conversion:tick_observer");
    public static readonly ResourceId SyncChannel = new("total_conversion:sync");
}

internal sealed class Int32Serializer : IModSerializer<int>
{
    public ReadOnlyMemory<byte> Serialize(int value)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    public int Deserialize(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != sizeof(int))
        {
            throw new InvalidDataException("Expected one little-endian Int32.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
    }
}
