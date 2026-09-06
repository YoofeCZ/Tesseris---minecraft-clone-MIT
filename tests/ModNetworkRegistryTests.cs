using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModNetworkRegistryTests
{
    [Fact]
    public void Loopback_serializes_dispatches_and_queues_game_thread_mutation()
    {
        (ModNetworkRegistry client, ModNetworkRegistry server) = ModNetworkRegistry.CreateLoopbackPair();
        var serverHandler = new Handler();
        IModNetworkChannel clientChannel = client.ForMod("ruby").Register(Definition(), new Handler());
        server.ForMod("ruby").Register(Definition(), serverHandler);
        client.Freeze();
        server.Freeze();

        byte[] payload = [1, 2, 3];
        Assert.True(clientChannel.Send(new ModConnectionId(1), payload));
        payload[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, serverHandler.Payload);
        Assert.False(serverHandler.GameActionRan);
        Assert.Equal(1, server.DrainGameThreadActions());
        Assert.True(serverHandler.GameActionRan);
    }

    [Fact]
    public void Direction_size_and_connection_are_enforced()
    {
        (ModNetworkRegistry client, ModNetworkRegistry server) = ModNetworkRegistry.CreateLoopbackPair();
        IModNetworkChannel clientChannel = client.ForMod("ruby").Register(Definition(maximum: 2), new Handler());
        IModNetworkChannel serverChannel = server.ForMod("ruby").Register(Definition(maximum: 2), new Handler());
        client.Freeze();
        server.Freeze();

        Assert.False(clientChannel.Send(new ModConnectionId(1), new byte[3]));
        Assert.False(clientChannel.Send(default, new byte[1]));
        Assert.False(serverChannel.Send(new ModConnectionId(1), new byte[1]));
    }

    [Fact]
    public void Handshake_rejects_protocol_mismatch()
    {
        (ModNetworkRegistry client, ModNetworkRegistry server) = ModNetworkRegistry.CreateLoopbackPair();
        client.ForMod("ruby").Register(Definition(protocol: "1"), new Handler());
        server.ForMod("ruby").Register(Definition(protocol: "2"), new Handler());

        ModHostException failure = Assert.Throws<ModHostException>(() => client.Freeze());
        Assert.Contains("handshake", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registration_is_owner_scoped_unique_and_frozen()
    {
        var registry = new ModNetworkRegistry(ModNetworkSide.Client);
        IModNetworkRegistry ruby = registry.ForMod("ruby");
        Assert.Throws<ModHostException>(() => ruby.Register(
            Definition() with { Id = new ResourceId("foreign:test") }, new Handler()));
        ruby.Register(Definition(), new Handler());
        Assert.Throws<ModHostException>(() => ruby.Register(Definition(), new Handler()));
        registry.Freeze();
        Assert.Throws<InvalidOperationException>(() => ruby.Register(
            Definition() with { Id = new ResourceId("ruby:late") }, new Handler()));
    }

    [Fact]
    public void Handler_failures_are_attributed()
    {
        (ModNetworkRegistry client, ModNetworkRegistry server) = ModNetworkRegistry.CreateLoopbackPair();
        IModNetworkChannel channel = client.ForMod("ruby").Register(Definition(), new Handler());
        server.ForMod("ruby").Register(Definition(), new BrokenHandler());
        client.Freeze();
        server.Freeze();

        ModNetworkHandlerException failure = Assert.Throws<ModNetworkHandlerException>(() =>
            channel.Send(new ModConnectionId(1), new byte[] { 1 }));
        Assert.Equal("ruby", failure.ModId);
        Assert.Equal(ModNetworkSide.Server, failure.Receiver);
    }

    private static ModNetworkChannelDefinition Definition(string protocol = "1", int maximum = 32) => new(
        new ResourceId("ruby:test"), protocol, ModNetworkDirection.ClientToServer, maximum);

    private sealed class Handler : IModNetworkHandler
    {
        public byte[]? Payload { get; private set; }

        public bool GameActionRan { get; private set; }

        public void Receive(IModNetworkMessageContext context)
        {
            Payload = context.Message.Payload.ToArray();
            context.Enqueue(() => GameActionRan = true);
        }
    }

    private sealed class BrokenHandler : IModNetworkHandler
    {
        public void Receive(IModNetworkMessageContext context) => throw new InvalidOperationException("broken");
    }
}
