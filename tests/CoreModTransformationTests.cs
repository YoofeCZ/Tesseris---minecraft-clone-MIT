using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Tesseris.Loader.Abstractions;
using Tesseris.Loader.Transforms;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class CoreModTransformationTests
{
    private static readonly Lazy<FixturePaths> Fixtures = new(BuildFixtures, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Prefix_postfix_and_replace_change_real_target_behavior_in_deterministic_order()
    {
        using var temporary = new TransformTemporaryDirectory();
        FixturePaths fixture = Fixtures.Value;
        CoreModTransformationRequest request = Request(
            fixture.Target,
            temporary.Path,
            [Postfix("core_b", "post_b", "PostfixB", 0), Prefix("core_b", "prefix_b", "PrefixB", -100),
             Replace("core_a", "replace", "Replace", 500), Postfix("core_a", "post_a", "PostfixA", 100),
             Prefix("core_a", "prefix_a", "PrefixA", 100)],
            fixture.Patches);
        byte[] original = File.ReadAllBytes(fixture.Target);

        CoreModTransformationResult result = new CoreModTransformer().Transform(request);
        (int value, string trace) = Execute(result.OutputAssemblyPath, fixture.Patches, 5);

        Assert.Equal(116, value);
        Assert.Equal("ABRba", trace);
        Assert.Equal(new[] { "core_a:post_a", "core_a:prefix_a", "core_a:replace", "core_b:prefix_b", "core_b:post_b" },
            result.AppliedPatches.Select(id => id.Value));
        Assert.Equal(original, File.ReadAllBytes(fixture.Target));
        Assert.NotEqual(Path.GetFullPath(fixture.Target), Path.GetFullPath(result.OutputAssemblyPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(result.OutputAssemblyPath)!, "*.tmp"));
    }

    [Fact]
    public void Prefix_and_postfix_wrap_the_original_body_when_no_replace_is_registered()
    {
        using var temporary = new TransformTemporaryDirectory();
        FixturePaths fixture = Fixtures.Value;
        CoreModTransformationRequest request = Request(
            fixture.Target,
            temporary.Path,
            [Postfix("core_b", "post_b", "PostfixB", 0), Prefix("core_b", "prefix_b", "PrefixB", 0),
             Postfix("core_a", "post_a", "PostfixA", 0), Prefix("core_a", "prefix_a", "PrefixA", 0)],
            fixture.Patches);

        CoreModTransformationResult result = new CoreModTransformer().Transform(request);
        (int value, string trace) = Execute(result.OutputAssemblyPath, fixture.Patches, 4);

        Assert.Equal(19, value); // original 4*2, then +10 and +1
        Assert.Equal("ABba", trace);
    }

    [Fact]
    public void Descriptor_input_order_does_not_change_fingerprint_or_output()
    {
        using var temporary = new TransformTemporaryDirectory();
        FixturePaths fixture = Fixtures.Value;
        ModMethodPatchDescriptor[] patches =
        [
            Prefix("core_b", "prefix_b", "PrefixB", 0),
            Postfix("core_a", "post_a", "PostfixA", 0),
            Prefix("core_a", "prefix_a", "PrefixA", 0),
        ];
        var transformer = new CoreModTransformer();

        CoreModTransformationResult first = transformer.Transform(Request(
            fixture.Target, temporary.Path, patches, fixture.Patches));
        CoreModTransformationResult second = transformer.Transform(Request(
            fixture.Target, temporary.Path, patches.Reverse().ToArray(), fixture.Patches));

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.OutputAssemblyPath, second.OutputAssemblyPath);
        Assert.Equal(File.ReadAllBytes(first.OutputAssemblyPath), File.ReadAllBytes(second.OutputAssemblyPath));
    }

    [Fact]
    public void Invalid_exact_signature_is_attributed_to_owner_and_patch()
    {
        using var temporary = new TransformTemporaryDirectory();
        FixturePaths fixture = Fixtures.Value;
        ModMethodPatchDescriptor invalid = Prefix("core_a", "invalid", "PrefixA", 0) with
        {
            Target = Target() with { ParameterTypes = [typeof(string).AssemblyQualifiedName!] },
        };

        CoreModTransformationException exception = Assert.Throws<CoreModTransformationException>(() =>
            new CoreModTransformer().Transform(Request(
                fixture.Target, temporary.Path, [invalid], fixture.Patches)));

        Assert.Contains("core_a", exception.Message, StringComparison.Ordinal);
        Assert.Contains("core_a:invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exact target signature", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_replace_patches_for_one_target_fail_closed()
    {
        using var temporary = new TransformTemporaryDirectory();
        FixturePaths fixture = Fixtures.Value;
        ModMethodPatchDescriptor first = Replace("core_a", "replace", "Replace", 0);
        ModMethodPatchDescriptor second = Replace("core_b", "replace", "ReplaceConflict", 0);

        CoreModTransformationException exception = Assert.Throws<CoreModTransformationException>(() =>
            new CoreModTransformer().Transform(Request(
                fixture.Target, temporary.Path, [second, first], fixture.Patches)));

        Assert.Contains("multiple Replace", exception.Message, StringComparison.Ordinal);
        Assert.Contains("core_a:replace", exception.Message, StringComparison.Ordinal);
        Assert.Contains("core_b:replace", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_invalidates_for_input_loader_api_and_patch_assembly_hashes()
    {
        using var temporary = new TransformTemporaryDirectory();
        FixturePaths fixture = Fixtures.Value;
        string input = Path.Combine(temporary.Path, "PatchTarget.dll");
        string patchCopy = Path.Combine(temporary.Path, "PatchMethods.dll");
        File.Copy(fixture.Target, input);
        File.Copy(fixture.Patches, patchCopy);
        ModMethodPatchDescriptor patch = Prefix("core_a", "prefix", "PrefixA", 0);
        var transformer = new CoreModTransformer();
        CoreModTransformationRequest baseline = Request(input, Path.Combine(temporary.Path, "cache"), [patch], patchCopy);

        string first = transformer.Transform(baseline).Fingerprint;
        string loader = transformer.Transform(baseline with { LoaderVersion = "2.0.1" }).Fingerprint;
        string api = transformer.Transform(baseline with { ModApiVersion = "2.0.1" }).Fingerprint;
        AppendByte(input);
        string target = transformer.Transform(baseline).Fingerprint;
        AppendByte(patchCopy);
        string patchAssembly = transformer.Transform(baseline).Fingerprint;

        Assert.Equal(5, new[] { first, loader, api, target, patchAssembly }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ordering_cycle_and_unknown_constraint_fail_before_writing_output()
    {
        using var temporary = new TransformTemporaryDirectory();
        FixturePaths fixture = Fixtures.Value;
        ModMethodPatchDescriptor first = Prefix("core_a", "first", "PrefixA", 0) with
        {
            After = [new ResourceId("core_b:second")],
        };
        ModMethodPatchDescriptor second = Prefix("core_b", "second", "PrefixB", 0) with
        {
            After = [new ResourceId("core_a:first")],
        };

        CoreModTransformationException cycle = Assert.Throws<CoreModTransformationException>(() =>
            new CoreModTransformer().Transform(Request(
                fixture.Target, temporary.Path, [first, second], fixture.Patches)));
        Assert.Contains("cycle", cycle.Message, StringComparison.OrdinalIgnoreCase);

        ModMethodPatchDescriptor unknown = first with { After = [new ResourceId("missing:patch")] };
        CoreModTransformationException missing = Assert.Throws<CoreModTransformationException>(() =>
            new CoreModTransformer().Transform(Request(
                fixture.Target, temporary.Path, [unknown], fixture.Patches)));
        Assert.Contains("unknown patch", missing.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.dll", SearchOption.AllDirectories));
    }

    private static CoreModTransformationRequest Request(
        string target,
        string cache,
        IReadOnlyList<ModMethodPatchDescriptor> patches,
        string patchAssembly)
    {
        ModLoadPlan plan = Plan();
        return new CoreModTransformationRequest(
            target,
            cache,
            "2.0.0",
            "2.0.0",
            plan,
            patches,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["core_a"] = patchAssembly,
                ["core_b"] = patchAssembly,
            });
    }

    private static ModLoadPlan Plan()
    {
        ModLoadPlanEntry Entry(string id, int index)
        {
            var package = new ModPackageDescriptor(
                2, id, id, "1.0.0", "2.0.0", "2.0.0", "/fixture", ModTrustLevel.CoreMod,
                [], [], [], [], [], id.PadRight(64, '0')[..64]);
            return new ModLoadPlanEntry(
                package,
                index,
                new ModTrustDecision(id, ModTrustLevel.CoreMod, true, package.PackageHash, "test trust"));
        }
        return new ModLoadPlan([Entry("core_a", 0), Entry("core_b", 1)], "fixture-load-plan");
    }

    private static ModMethodTarget Target() => new(
        "Tesseris.Tests.PatchTarget",
        "Tesseris.Tests.PatchTarget.Calculator",
        "Compute",
        0,
        true,
        typeof(int).AssemblyQualifiedName!,
        [typeof(int).AssemblyQualifiedName!]);

    private static ModMethodPatchDescriptor Prefix(string owner, string id, string method, int priority) =>
        Descriptor(owner, id, ModMethodPatchKind.Prefix, method, priority, typeof(void), [typeof(int)]);

    private static ModMethodPatchDescriptor Postfix(string owner, string id, string method, int priority) =>
        Descriptor(owner, id, ModMethodPatchKind.Postfix, method, priority, typeof(int), [typeof(int), typeof(int)]);

    private static ModMethodPatchDescriptor Replace(string owner, string id, string method, int priority) =>
        Descriptor(owner, id, ModMethodPatchKind.Replace, method, priority, typeof(int), [typeof(int)]);

    private static ModMethodPatchDescriptor Descriptor(
        string owner,
        string id,
        ModMethodPatchKind kind,
        string method,
        int priority,
        Type returnType,
        IReadOnlyList<Type> parameters) => new(
            new ResourceId($"{owner}:{id}"),
            owner,
            kind,
            Target(),
            new ModPatchEntrypoint(
                "Tesseris.Tests.PatchMethods",
                "Tesseris.Tests.PatchMethods.Hooks",
                method,
                returnType.AssemblyQualifiedName!,
                parameters.Select(type => type.AssemblyQualifiedName!).ToArray()),
            priority,
            [],
            []);

    private static (int Value, string Trace) Execute(string targetPath, string patchesPath, int argument)
    {
        var context = new FixtureLoadContext(patchesPath);
        try
        {
            Assembly target;
            using (FileStream stream = new(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                target = context.LoadFromStream(stream);
            Assembly patches = context.LoadFromAssemblyPath(Path.GetFullPath(patchesPath));
            Type hooks = patches.GetType("Tesseris.Tests.PatchMethods.Hooks", throwOnError: true)!;
            hooks.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            Type calculator = target.GetType("Tesseris.Tests.PatchTarget.Calculator", throwOnError: true)!;
            int value = (int)calculator.GetMethod("Compute", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [argument])!;
            string trace = (string)hooks.GetProperty("Trace", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            return (value, trace);
        }
        finally
        {
            context.Unload();
        }
    }

    private static FixturePaths BuildFixtures()
    {
        string root = Path.Combine(FindRepositoryRoot(), "tests", "Fixtures", "PatchTarget");
        string targetProject = Path.Combine(root, "Target", "PatchTarget.csproj");
        string patchProject = Path.Combine(root, "Patches", "PatchMethods.csproj");
        Build(targetProject);
        Build(patchProject);
        return new FixturePaths(
            Path.Combine(root, "Target", "bin", "Release", "net8.0", "Tesseris.Tests.PatchTarget.dll"),
            Path.Combine(root, "Patches", "bin", "Release", "net8.0", "Tesseris.Tests.PatchMethods.dll"));
    }

    private static string FindRepositoryRoot()
    {
        string? current = Path.GetFullPath(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "Tesseris.sln"))) return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate Tesseris.sln above the test output directory.");
    }

    private static void Build(string project)
    {
        var start = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Release --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not build patch fixture.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Patch fixture build failed.\n{output}\n{error}");
    }

    private static void AppendByte(string path)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.WriteByte(0);
    }

    private sealed record FixturePaths(string Target, string Patches);

    private sealed class FixtureLoadContext : AssemblyLoadContext
    {
        private readonly string patchPath;

        public FixtureLoadContext(string patchPath) : base(isCollectible: true) => this.patchPath = Path.GetFullPath(patchPath);

        protected override Assembly? Load(AssemblyName assemblyName) =>
            assemblyName.Name == "Tesseris.Tests.PatchMethods" ? LoadFromAssemblyPath(patchPath) : null;
    }

    private sealed class TransformTemporaryDirectory : IDisposable
    {
        public TransformTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Tesseris.CoreModTransformTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
