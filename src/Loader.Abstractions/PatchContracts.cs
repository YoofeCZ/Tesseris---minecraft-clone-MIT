using Tesseris.ModApi;

namespace Tesseris.Loader.Abstractions;

public enum ModMethodPatchKind
{
    Prefix,
    Postfix,
    Replace
}

/// <summary>
/// Exact managed method identity. Type names use reflection full-name syntax and parameter entries use
/// assembly-qualified names. Constructors use <c>.ctor</c>. Return type participates in validation.
/// </summary>
public sealed record ModMethodTarget(
    string AssemblyName,
    string TypeName,
    string MethodName,
    int GenericArity,
    bool IsStatic,
    string ReturnType,
    IReadOnlyList<string> ParameterTypes);

/// <summary>Exact static patch method identity inside a trusted coremod assembly.</summary>
public sealed record ModPatchEntrypoint(
    string AssemblyName,
    string TypeName,
    string MethodName,
    string ReturnType,
    IReadOnlyList<string> ParameterTypes);

/// <summary>
/// Deterministic pre-load patch declaration. IDs must belong to the declaring coremod. Explicit before/after
/// edges are resolved first; remaining ties use priority then patch ID ordinal. Cycles and more than one
/// applicable Replace are hard startup failures. Prefixes wrap from first to last, postfixes unwind from last
/// to first, and Replace suppresses the original body. Callback exceptions retain owner and patch identity.
/// </summary>
public sealed record ModMethodPatchDescriptor(
    ResourceId Id,
    string OwnerModId,
    ModMethodPatchKind Kind,
    ModMethodTarget Target,
    ModPatchEntrypoint Entrypoint,
    int Priority,
    IReadOnlyList<ResourceId> Before,
    IReadOnlyList<ResourceId> After);

/// <summary>
/// Prelaunch-only registry. Registration happens before the Game assembly is loaded and freezes before any
/// transform executes. The transformer writes to a content-addressed cache and must never modify installation
/// assemblies in place.
/// </summary>
public interface IModPatchRegistry
{
    IReadOnlyList<ModMethodPatchDescriptor> Registered { get; }

    void Register(ModMethodPatchDescriptor descriptor);
}
