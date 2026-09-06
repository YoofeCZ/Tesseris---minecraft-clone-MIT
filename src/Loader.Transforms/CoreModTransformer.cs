using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;
using CecilMethodBody = Mono.Cecil.Cil.MethodBody;

namespace Tesseris.Loader.Transforms;

/// <summary>
/// Managed pre-load IL transformer. It reads the original into memory, writes only to a content-addressed
/// cache and never loads or mutates the target assembly in the current process.
/// </summary>
public sealed class CoreModTransformer
{
    public CoreModTransformationResult Transform(CoreModTransformationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string inputPath = Path.GetFullPath(request.InputAssemblyPath);
        string cacheRoot = Path.GetFullPath(request.CacheDirectory);
        if (!File.Exists(inputPath))
            throw new CoreModTransformationException($"Transform input assembly does not exist: {inputPath}");
        EnsureTargetNotLoaded(inputPath);

        IReadOnlyList<ModMethodPatchDescriptor> ordered = PatchOrdering.Order(request.Patches, request.LoadPlan);
        IReadOnlyDictionary<string, string> patchPaths = ValidatePatchAssemblyPaths(ordered, request.PatchAssemblyPaths);
        var patchHashes = patchPaths.ToDictionary(
            pair => pair.Key,
            pair => TransformationFingerprint.FileHash(pair.Value),
            StringComparer.Ordinal);
        byte[] inputBytes = File.ReadAllBytes(inputPath);
        string fingerprint = TransformationFingerprint.Compute(inputBytes, request, ordered, patchHashes);
        string outputDirectory = Path.Combine(cacheRoot, fingerprint);
        string outputPath = Path.Combine(outputDirectory, Path.GetFileName(inputPath));
        string checksumPath = outputPath + ".sha256";
        if (IsValidCacheEntry(outputPath, checksumPath))
            return Result(outputPath, fingerprint, cacheHit: true, ordered);

        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(inputPath)}.{Guid.NewGuid():N}.tmp");
        string temporaryChecksum = temporaryPath + ".sha256";
        try
        {
            TransformToFile(inputBytes, inputPath, temporaryPath, ordered, patchPaths);
            string outputHash = TransformationFingerprint.FileHash(temporaryPath);
            File.WriteAllText(temporaryChecksum, outputHash);
            File.Move(temporaryPath, outputPath, overwrite: true);
            File.Move(temporaryChecksum, checksumPath, overwrite: true);
        }
        catch (CoreModTransformationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or BadImageFormatException
                                           or AssemblyResolutionException)
        {
            throw new CoreModTransformationException(
                $"Coremod transformation failed for target '{inputPath}': {exception.Message}", exception);
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryChecksum);
        }

        return Result(outputPath, fingerprint, cacheHit: false, ordered);
    }

    private static CoreModTransformationResult Result(
        string path,
        string fingerprint,
        bool cacheHit,
        IReadOnlyList<ModMethodPatchDescriptor> ordered) => new(
            path,
            fingerprint,
            cacheHit,
            ordered.Select(patch => patch.Id).ToArray());

    private static void TransformToFile(
        byte[] inputBytes,
        string inputPath,
        string outputPath,
        IReadOnlyList<ModMethodPatchDescriptor> ordered,
        IReadOnlyDictionary<string, string> patchPaths)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath)!);
        foreach (string directory in patchPaths.Values.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.Ordinal))
            resolver.AddSearchDirectory(directory);
        using var input = new MemoryStream(inputBytes, writable: false);
        using AssemblyDefinition targetAssembly = AssemblyDefinition.ReadAssembly(
            input,
            new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadSymbols = false });
        var patchAssemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        try
        {
            foreach ((string owner, string path) in patchPaths)
            {
                patchAssemblies.Add(owner, AssemblyDefinition.ReadAssembly(
                    path,
                    new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadSymbols = false }));
            }

            var resolved = new List<ResolvedPatch>(ordered.Count);
            foreach (ModMethodPatchDescriptor patch in ordered)
            {
                MethodDefinition target = ResolveTarget(targetAssembly, patch);
                MethodDefinition entrypoint = ResolveEntrypoint(patchAssemblies[patch.OwnerModId], patch);
                ValidatePatchShape(target, entrypoint, patch);
                resolved.Add(new ResolvedPatch(
                    patch,
                    target,
                    entrypoint,
                    targetAssembly.MainModule.ImportReference(entrypoint)));
            }

            foreach (IGrouping<MethodDefinition, ResolvedPatch> group in resolved.GroupBy(item => item.Target))
            {
                ResolvedPatch[] replacements = group.Where(item => item.Descriptor.Kind == ModMethodPatchKind.Replace).ToArray();
                if (replacements.Length > 1)
                {
                    string ids = string.Join(", ", replacements.Select(item => item.Descriptor.Id.Value));
                    throw PatchOrdering.Error(replacements[1].Descriptor,
                        $"target '{Describe(group.Key)}' has multiple Replace patches: {ids}");
                }
                Apply(
                    group.Key,
                    group.Where(item => item.Descriptor.Kind == ModMethodPatchKind.Prefix).ToArray(),
                    group.Where(item => item.Descriptor.Kind == ModMethodPatchKind.Postfix).Reverse().ToArray(),
                    replacements.SingleOrDefault());
            }

            targetAssembly.Write(outputPath, new WriterParameters { WriteSymbols = false });
        }
        finally
        {
            foreach (AssemblyDefinition assembly in patchAssemblies.Values) assembly.Dispose();
        }
    }

    private static MethodDefinition ResolveTarget(AssemblyDefinition assembly, ModMethodPatchDescriptor patch)
    {
        string actualAssembly = assembly.Name.Name;
        if (!string.Equals(SimpleAssemblyName(patch.Target.AssemblyName), actualAssembly, StringComparison.Ordinal))
            throw PatchOrdering.Error(patch,
                $"targets assembly '{patch.Target.AssemblyName}', but transform input is '{actualAssembly}'");
        TypeDefinition? type = AllTypes(assembly.MainModule.Types)
            .SingleOrDefault(candidate => TypeName(candidate) == NormalizeTypeName(patch.Target.TypeName));
        if (type is null) throw PatchOrdering.Error(patch, $"target type '{patch.Target.TypeName}' was not found");

        MethodDefinition[] named = type.Methods.Where(method => method.Name == patch.Target.MethodName).ToArray();
        MethodDefinition[] exact = named.Where(method =>
                method.GenericParameters.Count == patch.Target.GenericArity
                && method.IsStatic == patch.Target.IsStatic
                && TypeMatches(method.ReturnType, patch.Target.ReturnType)
                && ParametersMatch(method.Parameters, patch.Target.ParameterTypes))
            .ToArray();
        if (exact.Length != 1)
        {
            string candidates = named.Length == 0 ? "none" : string.Join("; ", named.Select(Describe));
            throw PatchOrdering.Error(patch,
                $"exact target signature was not found uniquely; candidates: {candidates}");
        }

        MethodDefinition target = exact[0];
        if (!target.HasBody) throw PatchOrdering.Error(patch, $"target '{Describe(target)}' has no managed body");
        if (target.IsConstructor) throw PatchOrdering.Error(patch, "constructor patching is not supported by this backend");
        if (target.HasGenericParameters) throw PatchOrdering.Error(patch, "generic method patching is not supported by this backend");
        if (!target.IsStatic && target.DeclaringType.IsValueType)
            throw PatchOrdering.Error(patch, "value-type instance method patching is not supported by this backend");
        return target;
    }

    private static MethodDefinition ResolveEntrypoint(AssemblyDefinition assembly, ModMethodPatchDescriptor patch)
    {
        if (!string.Equals(SimpleAssemblyName(patch.Entrypoint.AssemblyName), assembly.Name.Name, StringComparison.Ordinal))
            throw PatchOrdering.Error(patch,
                $"entrypoint assembly descriptor '{patch.Entrypoint.AssemblyName}' does not match '{assembly.Name.Name}'");
        TypeDefinition? type = AllTypes(assembly.MainModule.Types)
            .SingleOrDefault(candidate => TypeName(candidate) == NormalizeTypeName(patch.Entrypoint.TypeName));
        if (type is null) throw PatchOrdering.Error(patch, $"entrypoint type '{patch.Entrypoint.TypeName}' was not found");
        MethodDefinition[] exact = type.Methods.Where(method =>
                method.Name == patch.Entrypoint.MethodName
                && method.IsStatic
                && TypeMatches(method.ReturnType, patch.Entrypoint.ReturnType)
                && ParametersMatch(method.Parameters, patch.Entrypoint.ParameterTypes))
            .ToArray();
        if (exact.Length != 1)
            throw PatchOrdering.Error(patch, $"entrypoint method '{patch.Entrypoint.MethodName}' signature was not found uniquely");
        MethodDefinition method = exact[0];
        if (!method.IsPublic || method.HasGenericParameters)
            throw PatchOrdering.Error(patch, "entrypoint must be a public non-generic static method");
        return method;
    }

    private static void ValidatePatchShape(
        MethodDefinition target,
        MethodDefinition entrypoint,
        ModMethodPatchDescriptor patch)
    {
        var expectedParameters = new List<string>();
        if (!target.IsStatic) expectedParameters.Add(target.DeclaringType.FullName);
        expectedParameters.AddRange(target.Parameters.Select(parameter => parameter.ParameterType.FullName));
        if (patch.Kind == ModMethodPatchKind.Postfix && target.ReturnType.MetadataType != MetadataType.Void)
            expectedParameters.Add(target.ReturnType.FullName);
        string expectedReturn = patch.Kind switch
        {
            ModMethodPatchKind.Prefix => "System.Void",
            ModMethodPatchKind.Postfix when target.ReturnType.MetadataType == MetadataType.Void => "System.Void",
            _ => target.ReturnType.FullName,
        };
        if (!TypeMatches(entrypoint.ReturnType, expectedReturn)
            || !ParametersMatch(entrypoint.Parameters, expectedParameters))
        {
            throw PatchOrdering.Error(patch,
                $"{patch.Kind} entrypoint shape must be ({string.Join(", ", expectedParameters)}) -> {expectedReturn}, "
                + $"but found {Describe(entrypoint)}");
        }
    }

    private static void Apply(
        MethodDefinition target,
        IReadOnlyList<ResolvedPatch> prefixes,
        IReadOnlyList<ResolvedPatch> postfixes,
        ResolvedPatch? replacement)
    {
        CecilMethodBody body = target.Body;
        ILProcessor il = body.GetILProcessor();
        body.MaxStackSize = Math.Max(body.MaxStackSize, target.Parameters.Count + 4);
        if (replacement is not null)
        {
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.Instructions.Clear();
            body.InitLocals = false;
            EmitArguments(il, target);
            il.Append(il.Create(OpCodes.Call, replacement.ImportedEntrypoint));
            il.Append(il.Create(OpCodes.Ret));
        }

        Instruction first = body.Instructions[0];
        foreach (ResolvedPatch prefix in prefixes)
        {
            foreach (Instruction argument in CreateArgumentLoads(il, target)) il.InsertBefore(first, argument);
            il.InsertBefore(first, il.Create(OpCodes.Call, prefix.ImportedEntrypoint));
        }

        if (postfixes.Count == 0) return;
        bool returnsValue = target.ReturnType.MetadataType != MetadataType.Void;
        VariableDefinition? result = null;
        if (returnsValue)
        {
            result = new VariableDefinition(target.ReturnType);
            body.Variables.Add(result);
            body.InitLocals = true;
        }
        Instruction[] returns = body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray();
        foreach (Instruction ret in returns)
        {
            var injected = new List<Instruction>();
            if (returnsValue) injected.Add(il.Create(OpCodes.Stloc, result!));
            foreach (ResolvedPatch postfix in postfixes)
            {
                injected.AddRange(CreateArgumentLoads(il, target));
                if (returnsValue) injected.Add(il.Create(OpCodes.Ldloc, result!));
                injected.Add(il.Create(OpCodes.Call, postfix.ImportedEntrypoint));
                if (returnsValue) injected.Add(il.Create(OpCodes.Stloc, result!));
            }
            if (returnsValue) injected.Add(il.Create(OpCodes.Ldloc, result!));
            foreach (Instruction instruction in injected) il.InsertBefore(ret, instruction);
            if (injected.Count > 0) RedirectBranches(body, ret, injected[0]);
        }
    }

    private static IEnumerable<Instruction> CreateArgumentLoads(ILProcessor il, MethodDefinition target)
    {
        if (!target.IsStatic) yield return il.Create(OpCodes.Ldarg_0);
        foreach (ParameterDefinition parameter in target.Parameters) yield return il.Create(OpCodes.Ldarg, parameter);
    }

    private static void EmitArguments(ILProcessor il, MethodDefinition target)
    {
        foreach (Instruction instruction in CreateArgumentLoads(il, target)) il.Append(instruction);
    }

    private static void RedirectBranches(CecilMethodBody body, Instruction from, Instruction to)
    {
        foreach (Instruction instruction in body.Instructions)
        {
            if (ReferenceEquals(instruction.Operand, from)) instruction.Operand = to;
            else if (instruction.Operand is Instruction[] targets)
            {
                for (int index = 0; index < targets.Length; index++)
                    if (ReferenceEquals(targets[index], from)) targets[index] = to;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ValidatePatchAssemblyPaths(
        IReadOnlyList<ModMethodPatchDescriptor> patches,
        IReadOnlyDictionary<string, string> supplied)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string owner in patches.Select(patch => patch.OwnerModId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!supplied.TryGetValue(owner, out string? path) || string.IsNullOrWhiteSpace(path))
                throw new CoreModTransformationException($"Trusted coremod '{owner}' has no patch assembly path.");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new CoreModTransformationException($"Trusted coremod '{owner}' patch assembly does not exist: {full}");
            result.Add(owner, full);
        }
        return result;
    }

    private static bool IsValidCacheEntry(string outputPath, string checksumPath)
    {
        if (!File.Exists(outputPath) || !File.Exists(checksumPath)) return false;
        string expected = File.ReadAllText(checksumPath).Trim();
        return expected.Length == 64
            && string.Equals(expected, TransformationFingerprint.FileHash(outputPath), StringComparison.OrdinalIgnoreCase);
    }

    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3002",
        Justification = "Bundled modules report <Unknown> and are skipped; transformed targets are physical cache files.")]
    private static void EnsureTargetNotLoaded(string inputPath)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            string location = assembly.ManifestModule.FullyQualifiedName;
            if (string.IsNullOrEmpty(location) || location[0] == '<') continue;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(Path.GetFullPath(location), inputPath, comparison))
                throw new CoreModTransformationException(
                    $"Target assembly '{inputPath}' is already loaded; coremod transforms must run before target load.");
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (TypeDefinition type in roots)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    private static string TypeName(TypeDefinition type) => type.FullName.Replace('/', '+');

    private static bool ParametersMatch(
        IList<ParameterDefinition> actual,
        IReadOnlyList<string> expected) =>
        actual.Count == expected.Count
        && actual.Select(parameter => NormalizeTypeName(parameter.ParameterType.FullName))
            .SequenceEqual(expected.Select(NormalizeTypeName), StringComparer.Ordinal);

    private static bool TypeMatches(TypeReference actual, string expected) =>
        string.Equals(NormalizeTypeName(actual.FullName), NormalizeTypeName(expected), StringComparison.Ordinal);

    private static string NormalizeTypeName(string value)
    {
        string trimmed = value.Trim();
        int nesting = 0;
        for (int index = 0; index < trimmed.Length; index++)
        {
            nesting += trimmed[index] == '[' ? 1 : trimmed[index] == ']' ? -1 : 0;
            if (trimmed[index] == ',' && nesting == 0)
            {
                trimmed = trimmed[..index];
                break;
            }
        }
        return trimmed.Trim().Replace('+', '/');
    }

    private static string SimpleAssemblyName(string value)
    {
        try { return new AssemblyName(value).Name ?? value; }
        catch (FileLoadException) { return value; }
    }

    private static string Describe(MethodDefinition method) =>
        $"{method.DeclaringType.FullName}::{method.Name}({string.Join(", ", method.Parameters.Select(p => p.ParameterType.FullName))}) -> {method.ReturnType.FullName}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ResolvedPatch(
        ModMethodPatchDescriptor Descriptor,
        MethodDefinition Target,
        MethodDefinition Entrypoint,
        MethodReference ImportedEntrypoint);
}
