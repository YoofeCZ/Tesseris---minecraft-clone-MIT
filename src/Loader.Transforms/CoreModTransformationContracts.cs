using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Loader.Transforms;

public sealed record CoreModTransformationRequest(
    string InputAssemblyPath,
    string CacheDirectory,
    string LoaderVersion,
    string ModApiVersion,
    ModLoadPlan LoadPlan,
    IReadOnlyList<ModMethodPatchDescriptor> Patches,
    IReadOnlyDictionary<string, string> PatchAssemblyPaths);

public sealed record CoreModTransformationResult(
    string OutputAssemblyPath,
    string Fingerprint,
    bool CacheHit,
    IReadOnlyList<ResourceId> AppliedPatches);

public sealed class CoreModTransformationException : Exception
{
    public CoreModTransformationException(string message) : base(message) { }

    public CoreModTransformationException(string message, Exception innerException) : base(message, innerException) { }
}
