// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using CsCheck;
using Xunit;

namespace Vixen.Core.Serialization.Tests;

public class SerializationTests {
    [Fact]
    public void AMutableStructRoundTrips() {
        var value = new MutableStruct { Number = 42, Text = "hello", Flag = true };
        Assert.Equal(value, RoundTrip(value));
    }

    [Fact]
    public void APositionalRecordRoundTripsThroughItsConstructor() {
        // Every member is get-only, so this only works if the generator found the constructor and
        // matched its parameters to the members by name.
        var value = new PositionalStruct(7, 1.5f, "name");
        Assert.Equal(value, RoundTrip(value));
    }

    [Fact]
    public void AClassRoundTrips() {
        var value = new SettableClass { Id = 3, Name = "thing", Weight = 12.5 };
        var result = RoundTrip(value);

        Assert.Equal(value.Id, result.Id);
        Assert.Equal(value.Name, result.Name);
        Assert.Equal(value.Weight, result.Weight);
    }

    [Fact]
    public void NullAndEmptyStringsStayDistinct() {
        Assert.Null(RoundTrip(new MutableStruct { Text = null }).Text);
        Assert.Equal(string.Empty, RoundTrip(new MutableStruct { Text = string.Empty }).Text);
    }

    [Fact]
    public void ANullReferenceMemberRoundTrips() {
        var value = new NestedClass { Child = null, Inner = new() { Number = 1 } };
        var result = RoundTrip(value);

        Assert.Null(result.Child);
        Assert.Equal(1, result.Inner.Number);
    }

    [Fact]
    public void OrderAndIgnoreAreHonoured() {
        var value = new AnnotatedClass { First = 1, Second = 2, Third = 3, Cache = 99 };
        var result = RoundTrip(value);

        Assert.Equal(1, result.First);
        Assert.Equal(2, result.Second);
        Assert.Equal(3, result.Third);
        Assert.Equal(0, result.Cache);

        // Header is version and member count; then three ints in Order order, not declaration order.
        var bytes = Serializer.ToBytes(value);
        Assert.Equal([0, 3, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0], bytes);
    }

    [Fact]
    public void CollectionsRoundTrip() {
        var value = new CollectionsClass {
            Numbers = [1, 2, 3],
            Names = ["a", null, "c"],
            Scores = [10, 20],
            Counts = new() { ["x"] = 1, ["y"] = 2 },
            Optional = 5,
            Direction = Facing.West
        };

        var result = RoundTrip(value);

        Assert.Equal(value.Numbers, result.Numbers);
        Assert.Equal(value.Names, result.Names);
        Assert.Equal(value.Scores, result.Scores);
        Assert.Equal(value.Counts, result.Counts);
        Assert.Equal(value.Optional, result.Optional);
        Assert.Equal(Facing.West, result.Direction);
    }

    [Fact]
    public void NullAndEmptyCollectionsStayDistinct() {
        var empty = RoundTrip(new CollectionsClass { Numbers = [], Names = [], Scores = [] });
        Assert.NotNull(empty.Numbers);
        Assert.Empty(empty.Numbers);
        Assert.NotNull(empty.Scores);
        Assert.Empty(empty.Scores);

        var missing = RoundTrip(new CollectionsClass());
        Assert.Null(missing.Numbers);
        Assert.Null(missing.Names);
        Assert.Null(missing.Scores);
        Assert.Null(missing.Counts);
        Assert.Null(missing.Optional);
    }

    [Fact]
    public void NestedContractsRoundTrip() {
        var value = new NestedClass {
            Inner = new() { Number = 1, Text = "inner", Flag = true },
            Child = new() { Id = 2, Name = "child", Weight = 3.5 },
            Positional = new(4, 5f, "pos")
        };

        var result = RoundTrip(value);

        Assert.Equal(value.Inner, result.Inner);
        Assert.Equal(value.Child.Id, result.Child!.Id);
        Assert.Equal(value.Positional, result.Positional);
    }

    [Fact]
    public void ADerivedContractWritesItsBaseMembersFirst() {
        var value = new DerivedContract { BaseNumber = 7, DerivedText = "d" };
        var result = RoundTrip(value);

        Assert.Equal(7, result.BaseNumber);
        Assert.Equal("d", result.DerivedText);
    }

    [Fact]
    public void ADerivedInstanceInABaseTypedMemberKeepsItsOwnType() {
        var value = new Drawing { Root = new Circle { Label = "c", Radius = 2f } };
        var result = RoundTrip(value);

        var circle = Assert.IsType<Circle>(result.Root);
        Assert.Equal("c", circle.Label);
        Assert.Equal(2f, circle.Radius);
    }

    [Fact]
    public void ACollectionOfABaseTypeHoldsWhateverEachElementActuallyIs() {
        var value = new Drawing {
            Children = [new Circle { Radius = 1f }, new Box { Width = 2f, Height = 3f }, null]
        };

        var result = RoundTrip(value);

        Assert.Equal(3, result.Children!.Length);
        Assert.Equal(1f, Assert.IsType<Circle>(result.Children[0]).Radius);
        Assert.Equal(3f, Assert.IsType<Box>(result.Children[1]).Height);
        Assert.Null(result.Children[2]);
    }

    [Fact]
    public void APolymorphicMemberStillHandlesNull() =>
        Assert.Null(RoundTrip(new Drawing { Root = null }).Root);

    /// <summary>
    ///     A type carries its serialised name, not its CLR name, so it can be renamed and moved and
    ///     existing data still loads. `Box` used to be `Rectangle` and is written as `Rect`.
    /// </summary>
    [Fact]
    public void ATypeIsWrittenUnderItsAliasAndFoundUnderItsOldOnesToo() {
        var bytes = Serializer.ToBytes(new Drawing { Root = new Box { Width = 1f, Height = 2f } });
        Assert.Contains("Rect", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("Box", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        Assert.True(SerializerRegistry.TryGetByAlias("Rect", out var current));
        Assert.True(SerializerRegistry.TryGetByAlias("Rectangle", out var former));
        Assert.Same(current, former);
        Assert.Equal(typeof(Box), current.SerializedType);
    }

    [Fact]
    public void ASealedMemberTypePaysNothingForPolymorphism() {
        // `SettableClass` is sealed, so `NestedClass.Child` cannot be anything else and the name is
        // not written. The whole difference should be one byte of null flag.
        var bytes = Serializer.ToBytes(new NestedClass { Child = new() { Id = 1 } });
        Assert.DoesNotContain("SettableClass", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void DataNamingATypeThisBuildDoesNotHaveSaysSo() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(0);
        writer.WriteVarUInt64(2);
        writer.WriteByte(1);
        writer.WriteString("Triangle");
        writer.Flush();

        var thrown = Assert.Throws<SerializationException>(() => Serializer.Read<Drawing>(buffer.WrittenSpan));
        Assert.Contains("Triangle", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("[DataAlias]", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataNamingTheWrongKindOfTypeIsRefusedBeforeItBecomesACastError() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(0);
        writer.WriteVarUInt64(2);
        writer.WriteByte(1);
        writer.WriteString("SettableClass");
        writer.Flush();

        var thrown = Assert.Throws<SerializationException>(() => Serializer.Read<Drawing>(buffer.WrittenSpan));
        Assert.Contains("where a", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Writing a derived instance through its base serializer would drop everything the derived
    ///     type adds, and the loss would only surface wherever the data was read back.
    /// </summary>
    [Fact]
    public void WritingADerivedInstanceThroughItsBaseSerializerIsRefused() {
        BaseContract value = new DerivedContract { BaseNumber = 1, DerivedText = "lost" };

        var thrown = Assert.Throws<SerializationException>(() => Serializer.ToBytes(value));
        Assert.Contains("would drop everything the derived type adds", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The evolution that costs nothing: a member appended to a contract, and data written
    ///     before it existed. The member count in the stream is what makes it work.
    /// </summary>
    [Fact]
    public void DataWrittenBeforeAMemberWasAddedStillReads() {
        // Two members' worth of `SettableClass` data, hand-built the way the previous version of the
        // contract would have written it.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(0);
        writer.WriteVarUInt64(2);
        writer.WriteInt32(11);
        writer.WriteString("older");
        writer.Flush();

        var result = Serializer.Read<SettableClass>(buffer.WrittenSpan);

        Assert.Equal(11, result.Id);
        Assert.Equal("older", result.Name);
        Assert.Equal(0d, result.Weight);
    }

    [Fact]
    public void DataWithMoreMembersThanThisBuildKnowsIsRefused() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(0);
        writer.WriteVarUInt64(9);
        writer.Flush();

        // Members can be appended and older data still read. They cannot be removed or reordered,
        // and pretending otherwise would read the wrong bytes into the wrong fields.
        var thrown = Assert.Throws<SerializationException>(() => Serializer.Read<SettableClass>(buffer.WrittenSpan));
        Assert.Contains("cannot be removed or reordered", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionMismatchWithNoMigrationSaysWhatToDo() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(1);
        writer.WriteVarUInt64(1);
        writer.WriteInt32(5);
        writer.Flush();

        var thrown = Assert.Throws<SerializationVersionException>(
            () => Serializer.Read<VersionedClass>(buffer.WrittenSpan)
        );

        Assert.Equal(1, thrown.DataVersion);
        Assert.Equal(2, thrown.CurrentVersion);
        Assert.Contains("TryMigrate", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMigrationHookReadsTheOlderLayout() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(1);
        writer.WriteVarUInt64(1);
        writer.WriteString("41");
        writer.Flush();

        Assert.Equal(41, Serializer.Read<MigratedClass>(buffer.WrittenSpan).Value);
    }

    [Fact]
    public void AMigrationThatDeclinesStillReportsTheVersion() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(7);
        writer.WriteVarUInt64(1);
        writer.Flush();

        Assert.Throws<SerializationVersionException>(() => Serializer.Read<MigratedClass>(buffer.WrittenSpan));
    }

    /// <summary>
    ///     The content build's determinism gate is a byte comparison, so this is the property the
    ///     whole format exists to have.
    /// </summary>
    [Fact]
    public void EqualValuesProduceIdenticalBytes() {
        var first = new NestedClass {
            Inner = new() { Number = 1, Text = "x", Flag = true },
            Child = new() { Id = 2, Name = "y", Weight = 0.5 },
            Positional = new(3, 4f, "z")
        };

        var second = new NestedClass {
            Inner = new() { Number = 1, Text = "x", Flag = true },
            Child = new() { Id = 2, Name = "y", Weight = 0.5 },
            Positional = new(3, 4f, "z")
        };

        Assert.Equal(Serializer.ToBytes(first), Serializer.ToBytes(second));
        Assert.Equal(Serializer.ToBytes(first), Serializer.ToBytes(first));
    }

    [Fact]
    public void NegativeZeroAndNaNSurviveExactly() {
        var negativeZero = RoundTrip(new PositionalStruct(0, -0f, "n"));
        Assert.Equal(uint.MaxValue / 2 + 1, BitConverter.SingleToUInt32Bits(negativeZero.Y));

        var nan = BitConverter.UInt32BitsToSingle(0x7FC0_1234);
        var result = RoundTrip(new PositionalStruct(0, nan, "n"));
        Assert.Equal(0x7FC0_1234u, BitConverter.SingleToUInt32Bits(result.Y));
    }

    [Fact]
    public void AFixedBufferSaysSoRatherThanTruncating() {
        var value = new SettableClass { Id = 1, Name = "a rather long name", Weight = 1 };
        var thrown = Assert.Throws<SerializationException>(() => Serializer.Write(stackalloc byte[4], value));
        Assert.Contains("IBufferWriter", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedDataIsAnErrorRatherThanGarbage() {
        var bytes = Serializer.ToBytes(new SettableClass { Id = 1, Name = "abc", Weight = 2 });
        Assert.Throws<SerializationException>(() => Serializer.Read<SettableClass>(bytes.AsSpan(0, bytes.Length - 3)));
    }

    [Fact]
    public void ACorruptCollectionLengthIsRejectedBeforeItIsAllocated() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(0);
        writer.WriteVarUInt64(1);
        writer.WriteVarUInt64(ulong.MaxValue / 2);
        writer.Flush();

        var thrown = Assert.Throws<SerializationException>(() => Serializer.Read<CollectionsClass>(buffer.WrittenSpan));
        Assert.Contains("bytes remain", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnregisteredTypeSaysHowToRegisterIt() {
        var thrown = Assert.Throws<SerializationException>(() => Serializer.ToBytes(new Unregistered()));
        Assert.Contains("[DataContract]", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryContractInThisAssemblyRegisteredItself() {
        // The module initializer the generator emits runs before any of this assembly's code, so a
        // caller never has to know that registration is a thing that happens.
        Assert.True(SerializerRegistry.IsRegistered<MutableStruct>());
        Assert.True(SerializerRegistry.IsRegistered<PositionalStruct>());
        Assert.True(SerializerRegistry.IsRegistered<DerivedContract>());
    }

    [Fact]
    public void PrimitivesRoundTripForEveryValue() {
        Gen.Int.Sample(value => Assert.Equal(value, RoundTrip(new MutableStruct { Number = value }).Number));
        Gen.Float.Sample(value => Assert.Equal(
            BitConverter.SingleToUInt32Bits(value),
            BitConverter.SingleToUInt32Bits(RoundTrip(new PositionalStruct(0, value, "")).Y)
        ));
    }

    [Fact]
    public void VariableLengthIntegersRoundTripForEveryValue() {
        Gen.ULong.Sample(value => {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new SerializationWriter(buffer);
                writer.WriteVarUInt64(value);
                writer.Flush();

                var reader = new SerializationReader(buffer.WrittenSpan);
                Assert.Equal(value, reader.ReadVarUInt64());
                Assert.Equal(0, reader.Remaining);
            }
        );

        Gen.Long.Sample(value => {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new SerializationWriter(buffer);
                writer.WriteVarInt64(value);
                writer.Flush();

                Assert.Equal(value, new SerializationReader(buffer.WrittenSpan).ReadVarInt64());
            }
        );
    }

    [Fact]
    public void SmallNumbersCostOneByte() {
        // The entire reason lengths are LEB128: almost every collection in an asset is short.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);
        writer.WriteVarUInt64(127);
        writer.Flush();

        Assert.Equal(1, buffer.WrittenCount);
    }

    [Fact]
    public void ArbitraryStringsRoundTrip() =>
        Gen.String.Sample(value => Assert.Equal(value, RoundTrip(new MutableStruct { Text = value }).Text));

    [Fact]
    public void TheWriterGrowsAcrossChunkBoundaries() {
        // Larger than the writer's minimum chunk, so the growth path is exercised rather than
        // assumed — and the bytes on either side of a boundary have to join up.
        var numbers = new int[8192];

        for (var index = 0; index < numbers.Length; index++) {
            numbers[index] = index;
        }

        var result = RoundTrip(new CollectionsClass { Numbers = numbers });
        Assert.Equal(numbers, result.Numbers);
    }

    static T RoundTrip<T>(T value) => Serializer.Read<T>(Serializer.ToBytes(value));

    sealed class Unregistered {
        public int Value { get; set; }
    }
}
