using System.Security.Cryptography;
using System.Text;
using Tesseris.Loader.Abstractions;

namespace Tesseris.Loader.Transforms;

internal static class TransformationFingerprint
{
    public static string Compute(
        ReadOnlySpan<byte> inputAssembly,
        CoreModTransformationRequest request,
        IReadOnlyList<ModMethodPatchDescriptor> orderedPatches,
        IReadOnlyDictionary<string, string> patchAssemblyHashes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "tesseris-coremod-transform-v2");
        hash.AppendData(SHA256.HashData(inputAssembly));
        Append(hash, request.LoaderVersion);
        Append(hash, request.ModApiVersion);
        Append(hash, request.LoadPlan.Fingerprint);
        foreach ((string owner, string assemblyHash) in patchAssemblyHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(hash, owner);
            Append(hash, assemblyHash);
        }
        foreach (ModMethodPatchDescriptor patch in orderedPatches)
        {
            Append(hash, patch.Id.Value);
            Append(hash, patch.OwnerModId);
            Append(hash, ((int)patch.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, patch.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, patch.Target.AssemblyName);
            Append(hash, patch.Target.TypeName);
            Append(hash, patch.Target.MethodName);
            Append(hash, patch.Target.GenericArity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, patch.Target.IsStatic ? "static" : "instance");
            Append(hash, patch.Target.ReturnType);
            foreach (string parameter in patch.Target.ParameterTypes) Append(hash, parameter);
            Append(hash, patch.Entrypoint.AssemblyName);
            Append(hash, patch.Entrypoint.TypeName);
            Append(hash, patch.Entrypoint.MethodName);
            Append(hash, patch.Entrypoint.ReturnType);
            foreach (string parameter in patch.Entrypoint.ParameterTypes) Append(hash, parameter);
            foreach (string before in patch.Before.Select(id => id.Value).Order(StringComparer.Ordinal)) Append(hash, "before:" + before);
            foreach (string after in patch.After.Select(id => id.Value).Order(StringComparer.Ordinal)) Append(hash, "after:" + after);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
