// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Core.Serialization;

/// <summary>Reads and writes an enum as an element of a collection.</summary>
/// <typeparam name="TEnum">The enum type.</typeparam>
/// <remarks>
///     <para>
///         <b>An enum <em>member</em> never needs this and that is why it was missing.</b>
///         <c>DataContractGenerator</c> resolves a member of enum type to
///         <c>MemberShape.Enum</c> and emits <c>writer.WriteInt32((int)value.Direction)</c>
///         inline, so no enum type has ever needed a registered serializer — and
///         <c>DataContractGenerator.Describe</c> returns nothing for <c>TypeKind.Enum</c>
///         deliberately, on exactly that reasoning.
///     </para>
///     <para>
///         ⚠ <b>An enum <em>element</em> takes the other path, and there was no value of that
///         member shape that serialised.</b> <c>WriteArray&lt;T&gt;</c>,
///         <c>WriteList&lt;T&gt;</c> and <c>WriteDictionary&lt;K,V&gt;</c> ask the registry and
///         then call <c>WriteElement</c>, which for a value type with no serializer throws — and
///         the exception told the author to annotate the enum with <c>[DataContract]</c>, which
///         the generator explicitly declines to act on. So <c>Facing[]</c>,
///         <c>List&lt;Facing&gt;</c> and <c>Dictionary&lt;string, Facing&gt;</c> were all
///         unserialisable and no annotation an author could write changed that (#1177).
///     </para>
///     <para>
///         ⚠ <b>The generator instantiates it, and it has to be the generator.</b>
///         <c>SerializerRegistry</c> cannot build the closed generic at run time — NativeAOT has
///         no <c>MakeGenericType</c>, and the whole registry is built on that constraint — so the
///         set of enums a build can read is decided by what the compiler saw. It is the same
///         mechanism <c>ContentReferenceSerializer{T}</c> reaches the registry by, emitted from the
///         same module initialiser.
///     </para>
///     <para>
///         <b>The bytes are the member path's bytes.</b> Each primitive writer is fixed-width
///         little-endian, so writing the underlying value at the enum's own width produces exactly
///         what <c>writer.WriteInt32((int)…)</c> would have — an enum moved between a member and a
///         collection element does not change the stream. <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)" />
///         rather than a cast because a cast needs the underlying type named, which generic code
///         does not have; reinterpreting the reference reads the value in native order and the
///         writer puts it out little-endian, which is correct on either endianness rather than only
///         on the one Vixen runs on.
///     </para>
/// </remarks>
public sealed class EnumSerializer<TEnum> : DataSerializer<TEnum> where TEnum : unmanaged, Enum {
    /// <inheritdoc />
    public override void Serialize(ref SerializationWriter writer, in TEnum value) {
        var local = value;

        switch (Unsafe.SizeOf<TEnum>()) {
            case 1:
                writer.WriteByte(Unsafe.As<TEnum, byte>(ref local));
                break;

            case 2:
                writer.WriteUInt16(Unsafe.As<TEnum, ushort>(ref local));
                break;

            case 4:
                writer.WriteUInt32(Unsafe.As<TEnum, uint>(ref local));
                break;

            default:
                writer.WriteUInt64(Unsafe.As<TEnum, ulong>(ref local));
                break;
        }
    }

    /// <inheritdoc />
    public override void Deserialize(ref SerializationReader reader, ref TEnum value) {
        switch (Unsafe.SizeOf<TEnum>()) {
            case 1: {
                var raw = reader.ReadByte();
                value = Unsafe.As<byte, TEnum>(ref raw);
                break;
            }

            case 2: {
                var raw = reader.ReadUInt16();
                value = Unsafe.As<ushort, TEnum>(ref raw);
                break;
            }

            case 4: {
                var raw = reader.ReadUInt32();
                value = Unsafe.As<uint, TEnum>(ref raw);
                break;
            }

            default: {
                var raw = reader.ReadUInt64();
                value = Unsafe.As<ulong, TEnum>(ref raw);
                break;
            }
        }
    }
}
