namespace Tesseris.ModApi;

public enum ModNetworkSide
{
    Client,
    Server
}

[Flags]
public enum ModNetworkDirection
{
    ClientToServer = 1 << 0,
    ServerToClient = 1 << 1,
    Bidirectional = ClientToServer | ServerToClient
}

public readonly record struct ModConnectionId(ulong Value);

public sealed record ModNetworkChannelDefinition(
    ResourceId Id,
    string ProtocolVersion,
    ModNetworkDirection Direction,
    int MaximumPayloadBytes);

/// <summary>Immutable packet snapshot delivered identically over a socket or a single-player loopback.</summary>
public sealed record ModNetworkMessage(
    ResourceId ChannelId,
    ModConnectionId Connection,
    ModNetworkSide Sender,
    ReadOnlyMemory<byte> Payload);

public interface IModNetworkMessageContext
{
    ModNetworkMessage Message { get; }

    /// <summary>
    /// Enqueues a game-thread action. Packet handlers must use this for runtime mutation even when the
    /// transport happens to invoke them on a worker thread.
    /// </summary>
    void Enqueue(Action callback);
}

public interface IModNetworkHandler
{
    void Receive(IModNetworkMessageContext context);
}

public interface IModNetworkChannel
{
    ModNetworkChannelDefinition Definition { get; }

    bool Send(ModConnectionId connection, ReadOnlyMemory<byte> payload);
}

/// <summary>
/// Versioned logical channels independent of transport. Registration is owner-namespaced and frozen before
/// connections are accepted. Protocol mismatch rejects the channel during handshake. Loopback must serialize,
/// size-check and dispatch through the same path as remote transport.
/// </summary>
public interface IModNetworkRegistry
{
    IModNetworkChannel Register(ModNetworkChannelDefinition definition, IModNetworkHandler handler);

    bool TryGet(ResourceId channelId, out IModNetworkChannel? channel);
}
