using System.Buffers.Binary;
using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModSerializationRegistryTests
{
    [Fact]
    public void Values_round_trip_with_owned_payloads()
    {
        var registry = new ModSerializationRegistry();
        IModSerializationRegistry ruby = registry.ForMod("ruby");
        ruby.Register(new ResourceId("ruby:int"), 1, new IntSerializer(), []);
        registry.Freeze();

        ModSerializedValue value = ruby.Serialize(new ResourceId("ruby:int"), 42);
        byte[] external = value.Payload.ToArray();
        external[0] = 99;

        Assert.Equal(42, ruby.Deserialize<int>(value));
        Assert.Equal("ruby:int", Assert.Single(registry.Registered).Id.Value);
    }

    [Fact]
    public void Older_values_follow_the_validated_migration_chain()
    {
        var registry = new ModSerializationRegistry();
        IModSerializationRegistry ruby = registry.ForMod("ruby");
        ruby.Register(
            new ResourceId("ruby:int"),
            3,
            new IntSerializer(),
            [new AddMigration(1, 2, 10), new AddMigration(2, 3, 20)]);
        registry.Freeze();

        var old = new ModSerializedValue(new ResourceId("ruby:int"), 1, IntSerializer.Bytes(5));

        Assert.Equal(35, ruby.Deserialize<int>(old));
    }

    [Fact]
    public void Freeze_rejects_migration_gaps()
    {
        var registry = new ModSerializationRegistry();
        registry.ForMod("ruby").Register(
            new ResourceId("ruby:int"),
            4,
            new IntSerializer(),
            [new AddMigration(1, 2, 1), new AddMigration(3, 4, 1)]);

        ModHostException failure = Assert.Throws<ModHostException>(() => registry.Freeze());
        Assert.Contains("gap", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registration_is_owner_scoped_unique_and_frozen()
    {
        var registry = new ModSerializationRegistry();
        IModSerializationRegistry ruby = registry.ForMod("ruby");
        Assert.Throws<ModHostException>(() => ruby.Register(
            new ResourceId("foreign:int"), 1, new IntSerializer(), []));

        ruby.Register(new ResourceId("ruby:int"), 1, new IntSerializer(), []);
        Assert.Throws<ModHostException>(() => ruby.Register(
            new ResourceId("ruby:int"), 1, new IntSerializer(), []));
        registry.Freeze();
        Assert.Throws<InvalidOperationException>(() => ruby.Register(
            new ResourceId("ruby:late"), 1, new IntSerializer(), []));
    }

    [Fact]
    public void Serializer_failures_are_attributed()
    {
        var registry = new ModSerializationRegistry();
        IModSerializationRegistry ruby = registry.ForMod("ruby");
        ruby.Register(new ResourceId("ruby:broken"), 1, new BrokenSerializer(), []);
        registry.Freeze();

        ModSerializationException failure = Assert.Throws<ModSerializationException>(() =>
            ruby.Serialize(new ResourceId("ruby:broken"), 1));
        Assert.Equal("ruby", failure.OwnerModId);
        Assert.Equal("ruby:broken", failure.SerializerId.Value);
    }

    private sealed class IntSerializer : IModSerializer<int>
    {
        public ReadOnlyMemory<byte> Serialize(int value) => Bytes(value);

        public int Deserialize(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length != sizeof(int)) throw new InvalidDataException();
            return BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
        }

        public static byte[] Bytes(int value)
        {
            var bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return bytes;
        }
    }

    private sealed class AddMigration(int from, int to, int amount) : IModDataMigration
    {
        public int FromVersion => from;

        public int ToVersion => to;

        public ReadOnlyMemory<byte> Migrate(ReadOnlyMemory<byte> payload)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
            return IntSerializer.Bytes(value + amount);
        }
    }

    private sealed class BrokenSerializer : IModSerializer<int>
    {
        public ReadOnlyMemory<byte> Serialize(int value) => throw new InvalidOperationException("broken");

        public int Deserialize(ReadOnlyMemory<byte> payload) => 0;
    }
}
