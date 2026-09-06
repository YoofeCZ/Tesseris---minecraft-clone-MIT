using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

public sealed class ModNetworkHandlerException : Exception
{
    public ModNetworkHandlerException(
        string modId,
        ResourceId channelId,
        ModNetworkSide receiver,
        Exception innerException)
        : base($"Mod '{modId}' network channel '{channelId}' failed on {receiver}.", innerException)
    {
        ModId = modId;
        ChannelId = channelId;
        Receiver = receiver;
    }

    public string ModId { get; }

    public ResourceId ChannelId { get; }

    public ModNetworkSide Receiver { get; }
}

/// <summary>
/// Transport-neutral logical channel registry. The built-in loopback endpoint serializes the complete
/// envelope before delivery, so single-player cannot bypass protocol, direction or payload checks.
/// </summary>
public sealed class ModNetworkRegistry
{
    private const int EnvelopeMagic = 0x314E4D56; // VMN1
    private const int HardMaximumPayloadBytes = 16 * 1024 * 1024;
    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, Registration> registrations = [];
    private readonly ConcurrentQueue<Action> gameThreadActions = new();
    private readonly ModNetworkSide side;
    private ModNetworkRegistry? peer;

    public ModNetworkRegistry(ModNetworkSide side) => this.side = side;

    public bool IsFrozen { get; private set; }

    public ModNetworkSide Side => side;

    public static (ModNetworkRegistry Client, ModNetworkRegistry Server) CreateLoopbackPair()
    {
        var client = new ModNetworkRegistry(ModNetworkSide.Client);
        var server = new ModNetworkRegistry(ModNetworkSide.Server);
        client.peer = server;
        server.peer = client;
        return (client, server);
    }

    internal IModNetworkRegistry ForMod(string modId)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return new View(this, modId.Trim().ToLowerInvariant());
    }

    internal void Freeze()
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            return;
        }

        if (peer is not null)
        {
            foreach ((ResourceId id, Registration registration) in registrations)
            {
                if (!peer.registrations.TryGetValue(id, out Registration? remote))
                {
                    throw new ModHostException($"Network channel '{id}' is missing on the {peer.side} endpoint.");
                }

                if (!string.Equals(
                        registration.Definition.ProtocolVersion,
                        remote.Definition.ProtocolVersion,
                        StringComparison.Ordinal)
                    || registration.Definition.Direction != remote.Definition.Direction
                    || registration.Definition.MaximumPayloadBytes != remote.Definition.MaximumPayloadBytes)
                {
                    throw new ModHostException(
                        $"Network channel '{id}' handshake does not match between {side} and {peer.side}.");
                }
            }

            foreach (ResourceId remoteId in peer.registrations.Keys)
            {
                if (!registrations.ContainsKey(remoteId))
                {
                    throw new ModHostException($"Network channel '{remoteId}' is missing on the {side} endpoint.");
                }
            }
        }

        IsFrozen = true;
    }

    /// <summary>Executes packet-requested mutations on the endpoint's owning game thread.</summary>
    public int DrainGameThreadActions(int maximum = int.MaxValue)
    {
        EnsureMainThread();
        if (maximum < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        int executed = 0;
        while (executed < maximum && gameThreadActions.TryDequeue(out Action? action))
        {
            action();
            executed++;
        }

        return executed;
    }

    private IModNetworkChannel Register(
        string owner,
        ModNetworkChannelDefinition definition,
        IModNetworkHandler handler)
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            throw new InvalidOperationException("Network channel registration is frozen.");
        }

        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        if (!definition.Id.Value.StartsWith(owner + ":", StringComparison.Ordinal))
        {
            throw new ModHostException($"Mod '{owner}' may only register network channels in its own namespace.");
        }

        if (string.IsNullOrWhiteSpace(definition.ProtocolVersion))
        {
            throw new ArgumentException("A network protocol version is required.", nameof(definition));
        }

        if (!Enum.IsDefined(definition.Direction) || definition.Direction == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "A valid network direction is required.");
        }

        if (definition.MaximumPayloadBytes is <= 0 or > HardMaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                $"Maximum payload must be between 1 and {HardMaximumPayloadBytes} bytes.");
        }

        var channel = new Channel(this, definition);
        if (!registrations.TryAdd(definition.Id, new Registration(owner, definition, handler, channel)))
        {
            throw new ModHostException($"Network channel '{definition.Id}' is already registered.");
        }

        return channel;
    }

    private bool Send(ModNetworkChannelDefinition definition, ModConnectionId connection, ReadOnlyMemory<byte> payload)
    {
        if (!IsFrozen || peer is null || !peer.IsFrozen || connection.Value == 0)
        {
            return false;
        }

        if (!CanSend(definition.Direction, side) || payload.Length > definition.MaximumPayloadBytes)
        {
            return false;
        }

        byte[] envelope = Encode(definition.Id, connection, side, payload);
        return peer.Receive(envelope);
    }

    private bool Receive(ReadOnlyMemory<byte> envelope)
    {
        ModNetworkMessage message;
        try
        {
            message = Decode(envelope);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (!registrations.TryGetValue(message.ChannelId, out Registration? registration)
            || !CanReceive(registration.Definition.Direction, side, message.Sender)
            || message.Payload.Length > registration.Definition.MaximumPayloadBytes)
        {
            return false;
        }

        try
        {
            registration.Handler.Receive(new MessageContext(message, gameThreadActions));
            return true;
        }
        catch (Exception exception)
        {
            throw new ModNetworkHandlerException(
                registration.Owner,
                registration.Definition.Id,
                side,
                exception);
        }
    }

    private static bool CanSend(ModNetworkDirection direction, ModNetworkSide sender) => sender switch
    {
        ModNetworkSide.Client => direction.HasFlag(ModNetworkDirection.ClientToServer),
        ModNetworkSide.Server => direction.HasFlag(ModNetworkDirection.ServerToClient),
        _ => false,
    };

    private static bool CanReceive(
        ModNetworkDirection direction,
        ModNetworkSide receiver,
        ModNetworkSide sender) =>
        receiver != sender && CanSend(direction, sender);

    private static byte[] Encode(
        ResourceId channelId,
        ModConnectionId connection,
        ModNetworkSide sender,
        ReadOnlyMemory<byte> payload)
    {
        byte[] id = Encoding.UTF8.GetBytes(channelId.Value);
        var bytes = new byte[checked(4 + 4 + id.Length + 8 + 1 + 4 + payload.Length)];
        Span<byte> target = bytes;
        BinaryPrimitives.WriteInt32LittleEndian(target, EnvelopeMagic);
        BinaryPrimitives.WriteInt32LittleEndian(target[4..], id.Length);
        id.CopyTo(target[8..]);
        int offset = 8 + id.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(target[offset..], connection.Value);
        target[offset + 8] = (byte)sender;
        BinaryPrimitives.WriteInt32LittleEndian(target[(offset + 9)..], payload.Length);
        payload.Span.CopyTo(target[(offset + 13)..]);
        return bytes;
    }

    private static ModNetworkMessage Decode(ReadOnlyMemory<byte> envelope)
    {
        ReadOnlySpan<byte> bytes = envelope.Span;
        if (bytes.Length < 17 || BinaryPrimitives.ReadInt32LittleEndian(bytes) != EnvelopeMagic)
        {
            throw new InvalidDataException("Invalid mod network envelope.");
        }

        int idLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        if (idLength <= 0 || idLength > 1024 || bytes.Length < 8 + idLength + 13)
        {
            throw new InvalidDataException("Invalid channel ID length.");
        }

        string id = Encoding.UTF8.GetString(bytes.Slice(8, idLength));
        int offset = 8 + idLength;
        ulong connection = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
        var sender = (ModNetworkSide)bytes[offset + 8];
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 9)..]);
        if (!Enum.IsDefined(sender) || connection == 0 || payloadLength < 0
            || payloadLength > HardMaximumPayloadBytes || bytes.Length != offset + 13 + payloadLength)
        {
            throw new InvalidDataException("Invalid mod network envelope fields.");
        }

        return new ModNetworkMessage(
            new ResourceId(id),
            new ModConnectionId(connection),
            sender,
            bytes.Slice(offset + 13, payloadLength).ToArray());
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("Network registration and draining require the game thread.");
        }
    }

    private sealed record Registration(
        string Owner,
        ModNetworkChannelDefinition Definition,
        IModNetworkHandler Handler,
        IModNetworkChannel Channel);

    private sealed class Channel(ModNetworkRegistry registry, ModNetworkChannelDefinition definition)
        : IModNetworkChannel
    {
        public ModNetworkChannelDefinition Definition { get; } = definition;

        public bool Send(ModConnectionId connection, ReadOnlyMemory<byte> payload) =>
            registry.Send(Definition, connection, payload);
    }

    private sealed class MessageContext(
        ModNetworkMessage message,
        ConcurrentQueue<Action> gameThreadActions) : IModNetworkMessageContext
    {
        public ModNetworkMessage Message { get; } = message;

        public void Enqueue(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            gameThreadActions.Enqueue(callback);
        }
    }

    private sealed class View(ModNetworkRegistry registry, string owner) : IModNetworkRegistry
    {
        public IModNetworkChannel Register(ModNetworkChannelDefinition definition, IModNetworkHandler handler) =>
            registry.Register(owner, definition, handler);

        public bool TryGet(ResourceId channelId, out IModNetworkChannel? channel)
        {
            if (registry.registrations.TryGetValue(channelId, out Registration? registration))
            {
                channel = registration.Channel;
                return true;
            }

            channel = null;
            return false;
        }
    }
}
