using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class LoaderDependencyGraphTests
{
    [Fact]
    public void Required_optional_before_after_and_id_ties_produce_deterministic_plan()
    {
        DiscoveredModPackage[] packages =
        [
            Package("z-addon", Dependency("core", ModDependencyKind.Required)),
            Package("a-free"),
            Package("core", Dependency("optional", ModDependencyKind.Optional)),
            Package("optional"),
            Package("early", Dependency("core", ModDependencyKind.LoadBefore)),
            Package("late", Dependency("z-addon", ModDependencyKind.LoadAfter)),
        ];

        ModLoadPlan plan = ModDependencyResolver.Resolve(packages, Options());

        Assert.Equal(
            new[] { "a-free", "early", "optional", "core", "z-addon", "late" },
            plan.Entries.Select(entry => entry.Package.Id));
        Assert.Equal(Enumerable.Range(0, plan.Entries.Count), plan.Entries.Select(entry => entry.LoadIndex));
        Assert.Equal(64, plan.Fingerprint.Length);
        Assert.Equal(plan.Fingerprint, ModDependencyResolver.Resolve(packages.Reverse().ToArray(), Options()).Fingerprint);
    }

    [Fact]
    public void Missing_wrong_version_conflict_and_cycles_fail_closed_with_owner()
    {
        LoaderException missing = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve(
                [Package("addon", Dependency("missing", ModDependencyKind.Required))], Options()));
        Assert.Contains("addon", missing.Message, StringComparison.Ordinal);
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);

        LoaderException wrong = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve(
            [
                Package("core", version: "1.0.0"),
                Package("addon", new ModPackageDependency("core", "^2.0.0", ModDependencyKind.Optional)),
            ], Options()));
        Assert.Contains("2.0.0", wrong.Message, StringComparison.Ordinal);

        LoaderException conflict = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve(
            [
                Package("bad"),
                Package("owner", Dependency("bad", ModDependencyKind.Incompatible)),
            ], Options()));
        Assert.Contains("conflicts", conflict.Message, StringComparison.Ordinal);

        LoaderException cycle = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve(
            [
                Package("one", Dependency("two", ModDependencyKind.LoadAfter)),
                Package("two", Dependency("one", ModDependencyKind.LoadAfter)),
            ], Options()));
        Assert.Contains("cycle", cycle.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    [InlineData(">=1.0.0 <3.0.0")]
    public void Loader_negotiates_legacy_and_v2_api_constraints(string constraint)
    {
        ModLoadPlan plan = ModDependencyResolver.Resolve([Package("api", api: constraint)], Options());
        Assert.Single(plan.Entries);
    }

    [Fact]
    public void Loader_game_and_api_incompatibility_and_denied_trust_fail_before_loading()
    {
        LoaderException api = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve([Package("future", api: ">=3.0.0")], Options()));
        Assert.Contains("ModApi", api.Message, StringComparison.Ordinal);

        DiscoveredModPackage game = Package("game") with { GameVersionConstraint = ">=2.0.0" };
        LoaderException gameError = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve([game], Options()));
        Assert.Contains("requires game", gameError.Message, StringComparison.Ordinal);

        LoaderCompatibilityOptions denied = Options() with { TrustPolicy = new DenyPolicy() };
        LoaderException trust = Assert.Throws<LoaderException>(() =>
            ModDependencyResolver.Resolve([Package("denied")], denied));
        Assert.Contains("denied", trust.Message, StringComparison.Ordinal);
    }

    private static LoaderCompatibilityOptions Options() => LoaderCompatibilityOptions.Create("1.0.0");

    private static ModPackageDependency Dependency(string id, ModDependencyKind kind) => new(id, "*", kind);

    private static DiscoveredModPackage Package(
        string id,
        ModPackageDependency? dependency = null,
        string version = "1.0.0",
        string api = "*")
    {
        var descriptor = new ModPackageDescriptor(
            2, id, id, version, "^2.0.0", api, $"/{id}", ModTrustLevel.ContentOnly,
            [], dependency is null ? [] : [dependency], [], [], [], id.PadRight(64, '0')[..64]);
        return new DiscoveredModPackage(descriptor, "*", $"/{id}/tesseris.mod.json");
    }

    private sealed class DenyPolicy : IModTrustPolicy
    {
        public ModTrustDecision Evaluate(ModPackageDescriptor package) =>
            new(package.Id, package.Trust, false, package.PackageHash, "policy denied test package");
    }
}
