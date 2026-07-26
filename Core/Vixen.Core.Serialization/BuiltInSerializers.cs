// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Serialization;

/// <summary>Serializers for the types the generator cannot generate.</summary>
/// <remarks>
///     Everything here is either a primitive, a BCL type nobody can annotate, or one of
///     <c>Vixen.Core</c>'s identity structs. They are hand-written because there is no
///     <c>[DataContract]</c> to hang a generated one off, and because their wire form is a decision
///     rather than a mechanical translation — <see cref="Guid" /> as sixteen little-endian bytes,
///     <see cref="DateTime" /> as ticks plus kind, floats by their bits.
/// </remarks>
static class BuiltInSerializers {
    static bool registered;

    internal static void Register() {
        if (registered) {
            return;
        }

        registered = true;

        SerializerRegistry.Register(new BooleanSerializer());
        SerializerRegistry.Register(new ByteSerializer());
        SerializerRegistry.Register(new SByteSerializer());
        SerializerRegistry.Register(new Int16Serializer());
        SerializerRegistry.Register(new UInt16Serializer());
        SerializerRegistry.Register(new Int32Serializer());
        SerializerRegistry.Register(new UInt32Serializer());
        SerializerRegistry.Register(new Int64Serializer());
        SerializerRegistry.Register(new UInt64Serializer());
        SerializerRegistry.Register(new CharSerializer());
        SerializerRegistry.Register(new HalfSerializer());
        SerializerRegistry.Register(new SingleSerializer());
        SerializerRegistry.Register(new DoubleSerializer());
        SerializerRegistry.Register(new DecimalSerializer());
        SerializerRegistry.Register(new StringSerializer());
        SerializerRegistry.Register(new GuidSerializer());
        SerializerRegistry.Register(new DateTimeSerializer());
        SerializerRegistry.Register(new DateTimeOffsetSerializer());
        SerializerRegistry.Register(new TimeSpanSerializer());

        SerializerRegistry.Register(new AssetIdSerializer());
        SerializerRegistry.Register(new ObjectIdSerializer());
        SerializerRegistry.Register(new EntityIdSerializer());
        SerializerRegistry.Register(new ComponentTypeIdSerializer());
    }

    sealed class BooleanSerializer : DataSerializer<bool> {
        public override void Serialize(ref SerializationWriter writer, in bool value) => writer.WriteBoolean(value);
        public override void Deserialize(ref SerializationReader reader, ref bool value) => value = reader.ReadBoolean();
    }

    sealed class ByteSerializer : DataSerializer<byte> {
        public override void Serialize(ref SerializationWriter writer, in byte value) => writer.WriteByte(value);
        public override void Deserialize(ref SerializationReader reader, ref byte value) => value = reader.ReadByte();
    }

    sealed class SByteSerializer : DataSerializer<sbyte> {
        public override void Serialize(ref SerializationWriter writer, in sbyte value) => writer.WriteSByte(value);
        public override void Deserialize(ref SerializationReader reader, ref sbyte value) => value = reader.ReadSByte();
    }

    sealed class Int16Serializer : DataSerializer<short> {
        public override void Serialize(ref SerializationWriter writer, in short value) => writer.WriteInt16(value);
        public override void Deserialize(ref SerializationReader reader, ref short value) => value = reader.ReadInt16();
    }

    sealed class UInt16Serializer : DataSerializer<ushort> {
        public override void Serialize(ref SerializationWriter writer, in ushort value) => writer.WriteUInt16(value);
        public override void Deserialize(ref SerializationReader reader, ref ushort value) => value = reader.ReadUInt16();
    }

    sealed class Int32Serializer : DataSerializer<int> {
        public override void Serialize(ref SerializationWriter writer, in int value) => writer.WriteInt32(value);
        public override void Deserialize(ref SerializationReader reader, ref int value) => value = reader.ReadInt32();
    }

    sealed class UInt32Serializer : DataSerializer<uint> {
        public override void Serialize(ref SerializationWriter writer, in uint value) => writer.WriteUInt32(value);
        public override void Deserialize(ref SerializationReader reader, ref uint value) => value = reader.ReadUInt32();
    }

    sealed class Int64Serializer : DataSerializer<long> {
        public override void Serialize(ref SerializationWriter writer, in long value) => writer.WriteInt64(value);
        public override void Deserialize(ref SerializationReader reader, ref long value) => value = reader.ReadInt64();
    }

    sealed class UInt64Serializer : DataSerializer<ulong> {
        public override void Serialize(ref SerializationWriter writer, in ulong value) => writer.WriteUInt64(value);
        public override void Deserialize(ref SerializationReader reader, ref ulong value) => value = reader.ReadUInt64();
    }

    sealed class CharSerializer : DataSerializer<char> {
        public override void Serialize(ref SerializationWriter writer, in char value) => writer.WriteChar(value);
        public override void Deserialize(ref SerializationReader reader, ref char value) => value = reader.ReadChar();
    }

    sealed class HalfSerializer : DataSerializer<Half> {
        public override void Serialize(ref SerializationWriter writer, in Half value) => writer.WriteHalf(value);
        public override void Deserialize(ref SerializationReader reader, ref Half value) => value = reader.ReadHalf();
    }

    sealed class SingleSerializer : DataSerializer<float> {
        public override void Serialize(ref SerializationWriter writer, in float value) => writer.WriteSingle(value);
        public override void Deserialize(ref SerializationReader reader, ref float value) => value = reader.ReadSingle();
    }

    sealed class DoubleSerializer : DataSerializer<double> {
        public override void Serialize(ref SerializationWriter writer, in double value) => writer.WriteDouble(value);
        public override void Deserialize(ref SerializationReader reader, ref double value) => value = reader.ReadDouble();
    }

    sealed class DecimalSerializer : DataSerializer<decimal> {
        public override void Serialize(ref SerializationWriter writer, in decimal value) => writer.WriteDecimal(value);
        public override void Deserialize(ref SerializationReader reader, ref decimal value) => value = reader.ReadDecimal();
    }

    sealed class StringSerializer : DataSerializer<string> {
        public override void Serialize(ref SerializationWriter writer, in string value) => writer.WriteString(value);
        public override void Deserialize(ref SerializationReader reader, ref string value) => value = reader.ReadString()!;
    }

    sealed class GuidSerializer : DataSerializer<Guid> {
        public override void Serialize(ref SerializationWriter writer, in Guid value) => writer.WriteGuid(value);
        public override void Deserialize(ref SerializationReader reader, ref Guid value) => value = reader.ReadGuid();
    }

    sealed class DateTimeSerializer : DataSerializer<DateTime> {
        public override void Serialize(ref SerializationWriter writer, in DateTime value) => writer.WriteDateTime(value);
        public override void Deserialize(ref SerializationReader reader, ref DateTime value) => value = reader.ReadDateTime();
    }

    sealed class DateTimeOffsetSerializer : DataSerializer<DateTimeOffset> {
        public override void Serialize(ref SerializationWriter writer, in DateTimeOffset value) =>
            writer.WriteDateTimeOffset(value);

        public override void Deserialize(ref SerializationReader reader, ref DateTimeOffset value) =>
            value = reader.ReadDateTimeOffset();
    }

    sealed class TimeSpanSerializer : DataSerializer<TimeSpan> {
        public override void Serialize(ref SerializationWriter writer, in TimeSpan value) => writer.WriteTimeSpan(value);
        public override void Deserialize(ref SerializationReader reader, ref TimeSpan value) => value = reader.ReadTimeSpan();
    }

    sealed class AssetIdSerializer : DataSerializer<AssetId> {
        public override void Serialize(ref SerializationWriter writer, in AssetId value) => writer.WriteGuid(value.Value);
        public override void Deserialize(ref SerializationReader reader, ref AssetId value) => value = new(reader.ReadGuid());
    }

    sealed class ObjectIdSerializer : DataSerializer<ObjectId> {
        public override void Serialize(ref SerializationWriter writer, in ObjectId value) {
            // Two 64-bit halves rather than the big-endian text form: the id is already a hash, so
            // the wire form only has to round-trip, and this is two stores.
            writer.WriteUInt64(value.High);
            writer.WriteUInt64(value.Low);
        }

        public override void Deserialize(ref SerializationReader reader, ref ObjectId value) {
            var high = reader.ReadUInt64();
            value = new(high, reader.ReadUInt64());
        }
    }

    sealed class EntityIdSerializer : DataSerializer<EntityId> {
        public override void Serialize(ref SerializationWriter writer, in EntityId value) => writer.WriteUInt64(value.Packed);

        public override void Deserialize(ref SerializationReader reader, ref EntityId value) {
            var packed = reader.ReadUInt64();
            value = new((uint)packed, (uint)(packed >> 32));
        }
    }

    sealed class ComponentTypeIdSerializer : DataSerializer<ComponentTypeId> {
        public override void Serialize(ref SerializationWriter writer, in ComponentTypeId value) =>
            writer.WriteVarUInt64((ulong)value.Value);

        public override void Deserialize(ref SerializationReader reader, ref ComponentTypeId value) =>
            value = new((int)reader.ReadVarUInt64());
    }
}
