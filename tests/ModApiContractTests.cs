using System.Reflection;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModApiContractTests
{
    [Fact]
    public void Public_mod_api_does_not_expose_game_engine_opentk_or_vulkan_types()
    {
        Assembly api = typeof(IModContextV2).Assembly;
        string[] forbiddenPrefixes =
        [
            "Tesseris.Game",
            "Tesseris.Engine",
            "OpenTK",
            "Vulkan",
        ];

        var exposed = new List<string>();
        foreach (Type type in api.GetExportedTypes())
        {
            foreach (Type referenced in PublicSignatureTypes(type))
            {
                string assemblyName = referenced.Assembly.GetName().Name ?? string.Empty;
                if (forbiddenPrefixes.Any(prefix =>
                        assemblyName.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    exposed.Add($"{type.FullName} -> {referenced.FullName} ({assemblyName})");
                }
            }
        }

        Assert.Empty(exposed);
    }

    [Fact]
    public void V2_context_is_additive_and_groups_focused_platforms()
    {
        Assert.True(typeof(IModContext).IsAssignableFrom(typeof(IModContextV2)));

        string[] properties = typeof(IModContextV2)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "CapabilitiesV2",
            "Client",
            "Containers",
            "Entities",
            "EventBus",
            "InterModServices",
            "ItemStacks",
            "Network",
            "Serialization",
            "WorldDefinitions",
        ],
            properties);
    }

    [Fact]
    public void Stable_ids_are_normalized_and_reject_non_namespaced_values()
    {
        Assert.Equal("example:machines/crusher", new ResourceId(" Example:Machines/Crusher ").Value);
        Assert.Throws<ArgumentException>(() => new ResourceId("missing_namespace"));
        Assert.Throws<ArgumentException>(() => new ResourceId("example:../escape?"));
    }

    [Fact]
    public void Snapshot_contracts_expose_read_only_collections_and_revisions()
    {
        Assert.Equal(typeof(IReadOnlyList<ModStackComponentValue>),
            typeof(ModItemStackSnapshot).GetProperty(nameof(ModItemStackSnapshot.Components))!.PropertyType);
        Assert.NotNull(typeof(ModItemStackSnapshot).GetProperty(nameof(ModItemStackSnapshot.Revision)));

        Assert.Equal(typeof(IReadOnlyList<ModContainerSlotSnapshot>),
            typeof(ModContainerSnapshot).GetProperty(nameof(ModContainerSnapshot.Slots))!.PropertyType);
        Assert.NotNull(typeof(ModContainerSnapshot).GetProperty(nameof(ModContainerSnapshot.Revision)));

        Assert.Equal(typeof(IReadOnlyList<ModComponentValue>),
            typeof(ModEntitySnapshot).GetProperty(nameof(ModEntitySnapshot.Components))!.PropertyType);
        Assert.NotNull(typeof(ModEntitySnapshot).GetProperty(nameof(ModEntitySnapshot.Revision)));
    }

    [Fact]
    public void Network_and_loopback_share_one_transport_neutral_message_contract()
    {
        PropertyInfo payload = typeof(ModNetworkMessage).GetProperty(nameof(ModNetworkMessage.Payload))!;
        Assert.Equal(typeof(ReadOnlyMemory<byte>), payload.PropertyType);
        Type[] signatureTypes = typeof(IModNetworkRegistry)
            .GetMethods()
            .SelectMany(method =>
                Flatten(method.ReturnType).Concat(
                    method.GetParameters().SelectMany(parameter => Flatten(parameter.ParameterType))))
            .ToArray();
        Assert.DoesNotContain(typeof(Stream), signatureTypes);
    }

    private static IEnumerable<Type> PublicSignatureTypes(Type owner)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        foreach (PropertyInfo property in owner.GetProperties(flags))
        {
            foreach (Type type in Flatten(property.PropertyType)) yield return type;
        }

        foreach (FieldInfo field in owner.GetFields(flags))
        {
            foreach (Type type in Flatten(field.FieldType)) yield return type;
        }

        foreach (MethodBase method in owner.GetMethods(flags).Cast<MethodBase>().Concat(owner.GetConstructors(flags)))
        {
            if (method is MethodInfo methodInfo)
            {
                foreach (Type type in Flatten(methodInfo.ReturnType)) yield return type;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                foreach (Type type in Flatten(parameter.ParameterType)) yield return type;
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.HasElementType)
        {
            foreach (Type nested in Flatten(type.GetElementType()!)) yield return nested;
            yield break;
        }

        if (type.IsGenericParameter)
        {
            yield break;
        }

        yield return type;
        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type nested in Flatten(argument)) yield return nested;
        }
    }
}
