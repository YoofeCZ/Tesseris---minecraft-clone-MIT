using System.Runtime.CompilerServices;
using Tesseris.Loader;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class InterModServiceTests
{
    [Fact]
    public void Services_are_versioned_owner_scoped_and_return_sorted_snapshots()
    {
        var registry = new ModServiceRegistry([typeof(ITestService).Assembly]);
        IModServiceRegistry alpha = registry.ForOwner("alpha");
        IModServiceRegistry beta = registry.ForOwner("beta");
        var service = new TestService("ready");

        alpha.Publish<ITestService>(new ResourceId("alpha:zeta"), "1.2.0", service);
        alpha.Publish<ITestService>(new ResourceId("alpha:alpha"), "2.0.0", new TestService("second"));

        Assert.True(beta.TryGet<ITestService>(new ResourceId("alpha:zeta"), "^1.0.0", out ITestService? found));
        Assert.Same(service, found);
        Assert.False(beta.TryGet<ITestService>(new ResourceId("alpha:zeta"), ">=2.0.0", out _));
        Assert.Equal(new[] { "alpha:alpha", "alpha:zeta" }, registry.Published.Select(item => item.Id.Value));
        Assert.All(registry.Published, descriptor => Assert.Equal("alpha", descriptor.OwnerModId));

        Assert.False(beta.Remove(new ResourceId("alpha:zeta")));
        Assert.True(alpha.Remove(new ResourceId("alpha:zeta")));
    }

    [Fact]
    public void Namespace_duplicate_and_unapproved_contracts_fail_closed_with_owner()
    {
        var registry = new ModServiceRegistry([typeof(ITestService).Assembly]);
        IModServiceRegistry alpha = registry.ForOwner("alpha");
        IModServiceRegistry beta = registry.ForOwner("beta");
        alpha.Publish<ITestService>(new ResourceId("alpha:service"), "1.0.0", new TestService("one"));

        LoaderException namespaceError = Assert.Throws<LoaderException>(() =>
            alpha.Publish<ITestService>(new ResourceId("beta:foreign"), "1.0.0", new TestService("bad")));
        Assert.Contains("alpha", namespaceError.Message, StringComparison.Ordinal);

        LoaderException duplicate = Assert.Throws<LoaderException>(() =>
            beta.Publish<ITestService>(new ResourceId("alpha:service"), "1.0.0", new TestService("two")));
        Assert.Contains("alpha:service", duplicate.Message, StringComparison.Ordinal);

        var restricted = new ModServiceRegistry();
        LoaderException contract = Assert.Throws<LoaderException>(() =>
            restricted.ForOwner("alpha").Publish<ITestService>(
                new ResourceId("alpha:test"), "1.0.0", new TestService("bad")));
        Assert.Contains("unapproved assembly", contract.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unregister_owner_releases_every_service_and_allows_context_collection()
    {
        var registry = new ModServiceRegistry([typeof(ITestService).Assembly]);
        (WeakReference weak, int removed) = PublishThenUnregister(registry);

        ForceCollection(weak);

        Assert.Equal(2, removed);
        Assert.False(weak.IsAlive);
        Assert.Empty(registry.Published);
    }

    [Fact]
    public void Service_registry_rejects_worker_thread_access()
    {
        var registry = new ModServiceRegistry([typeof(ITestService).Assembly]);
        Exception? actual = null;
        var worker = new Thread(() => actual = Record.Exception(() => registry.ForOwner("alpha")));

        worker.Start();
        worker.Join();

        Assert.IsType<InvalidOperationException>(actual);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Weak, int Removed) PublishThenUnregister(ModServiceRegistry registry)
    {
        var service = new TestService("temporary");
        var weak = new WeakReference(service);
        IModServiceRegistry owner = registry.ForOwner("owner");
        owner.Publish<ITestService>(new ResourceId("owner:one"), "1.0.0", service);
        owner.Publish<ITestService>(new ResourceId("owner:two"), "1.0.0", service);
        int removed = registry.UnregisterOwner("owner");
        return (weak, removed);
    }

    private static void ForceCollection(WeakReference weak)
    {
        for (int index = 0; index < 10 && weak.IsAlive; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    public interface ITestService
    {
        string Value { get; }
    }

    private sealed record TestService(string Value) : ITestService;
}
