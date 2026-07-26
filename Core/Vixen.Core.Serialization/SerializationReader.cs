// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Vixen.Core.Serialization;

/// <summary>Reads the binary format out of a span.</summary>
/// <remarks>
///     <para>
///         Span-only, with no stream form, and that is a deliberate pair with
///         <c>Vixen.Core.IO</c>'s memory mapping. A bundle on disk is mapped rather than read, so
///         "the whole file in a span" costs no copy and no allocation, and the pages holding the two
///         hundred assets nobody asked for are never faulted in. A stream-based reader would give up
///         both of those to support a case — reading content off a socket without buffering it —
///         that does not exist.
///     </para>
///     <para>
///         Reading past the end throws <see cref="SerializationException" /> rather than returning
///         garbage. Deserialisation runs over data that may be truncated, corrupt, or from a future
///         version, and the difference between an exception and silence is the difference between a
///         diagnosable failure and a mysterious one.
///     </para>
/// </remarks>
public ref partial struct SerializationReader {
    readonly ReadOnlySpan<byte> span;
    int position;

    /// <summary>How many bytes have been consumed.</summary>
    public readonly int BytesRead => position;

    /// <summary>How many bytes are left.</summary>
    public readonly int Remaining => span.Length - position;

    /// <summary>Reads from a span.</summary>
    /// <param name="source">The bytes.</param>
    public SerializationReader(ReadOnlySpan<byte> source) => span = source;

    /// <summary>Reads one byte.</summary>
    /// <returns>The byte.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte() {
        Ensure(1);
        return span[position++];
    }

    /// <summary>Reads a boolean.</summary>
    /// <returns>The value.</returns>
    public bool ReadBoolean() => ReadByte() != 0;

    /// <summary>Reads a signed byte.</summary>
    /// <returns>The value.</returns>
    public sbyte ReadSByte() => (sbyte)ReadByte();

    /// <summary>Reads a 16-bit integer.</summary>
    /// <returns>The value.</returns>
    public short ReadInt16() {
        Ensure(sizeof(short));
        var value = BinaryPrimitives.ReadInt16LittleEndian(span[position..]);
        position += sizeof(short);
        return value;
    }

    /// <summary>Reads an unsigned 16-bit integer.</summary>
    /// <returns>The value.</returns>
    public ushort ReadUInt16() {
        Ensure(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(span[position..]);
        position += sizeof(ushort);
        return value;
    }

    /// <summary>Reads a 32-bit integer.</summary>
    /// <returns>The value.</returns>
    public int ReadInt32() {
        Ensure(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(span[position..]);
        position += sizeof(int);
        return value;
    }

    /// <summary>Reads an unsigned 32-bit integer.</summary>
    /// <returns>The value.</returns>
    public uint ReadUInt32() {
        Ensure(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
        position += sizeof(uint);
        return value;
    }

    /// <summary>Reads a 64-bit integer.</summary>
    /// <returns>The value.</returns>
    public long ReadInt64() {
        Ensure(sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(span[position..]);
        position += sizeof(long);
        return value;
    }

    /// <summary>Reads an unsigned 64-bit integer.</summary>
    /// <returns>The value.</returns>
    public ulong ReadUInt64() {
        Ensure(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(span[position..]);
        position += sizeof(ulong);
        return value;
    }

    /// <summary>Reads a 16-bit character.</summary>
    /// <returns>The value.</returns>
    public char ReadChar() => (char)ReadUInt16();

    /// <summary>Reads a half-precision float.</summary>
    /// <returns>The value.</returns>
    public Half ReadHalf() => BitConverter.UInt16BitsToHalf(ReadUInt16());

    /// <summary>Reads a single-precision float.</summary>
    /// <returns>The value.</returns>
    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadUInt32());

    /// <summary>Reads a double-precision float.</summary>
    /// <returns>The value.</returns>
    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadUInt64());

    /// <summary>Reads a decimal.</summary>
    /// <returns>The value.</returns>
    public decimal ReadDecimal() {
        Span<int> bits = [ReadInt32(), ReadInt32(), ReadInt32(), ReadInt32()];
        return new(bits);
    }

    /// <summary>Reads a <see cref="Guid" />.</summary>
    /// <returns>The value.</returns>
    public Guid ReadGuid() {
        Ensure(16);
        var value = new Guid(span.Slice(position, 16), bigEndian: false);
        position += 16;
        return value;
    }

    /// <summary>Reads a <see cref="DateTime" />.</summary>
    /// <returns>The value.</returns>
    public DateTime ReadDateTime() {
        var ticks = ReadInt64();
        return new(ticks, (DateTimeKind)ReadByte());
    }

    /// <summary>Reads a <see cref="DateTimeOffset" />.</summary>
    /// <returns>The value.</returns>
    public DateTimeOffset ReadDateTimeOffset() {
        var ticks = ReadInt64();
        return new(ticks, new(ReadInt64()));
    }

    /// <summary>Reads a <see cref="TimeSpan" />.</summary>
    /// <returns>The value.</returns>
    public TimeSpan ReadTimeSpan() => new(ReadInt64());

    /// <summary>Reads a LEB128 unsigned integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="SerializationException">The encoding is longer than ten bytes.</exception>
    public ulong ReadVarUInt64() {
        var result = 0UL;

        for (var shift = 0; shift < 64; shift += 7) {
            var current = ReadByte();
            result |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0) {
                return result;
            }
        }

        // Ten bytes is the most a 64-bit value can take. More means corrupt data, and continuing to
        // shift would loop over the rest of the file looking for a terminator.
        throw new SerializationException("A LEB128 value did not terminate within ten bytes.");
    }

    /// <summary>Reads a zig-zag LEB128 signed integer.</summary>
    /// <returns>The value.</returns>
    public long ReadVarInt64() {
        var raw = ReadVarUInt64();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    /// <summary>Reads a length-prefixed UTF-8 string.</summary>
    /// <returns>The string, or <see langword="null" />.</returns>
    public string? ReadString() {
        var encoded = ReadVarUInt64();

        if (encoded == 0) {
            return null;
        }

        var count = checked((int)(encoded - 1));
        Ensure(count);
        var value = Encoding.UTF8.GetString(span.Slice(position, count));
        position += count;
        return value;
    }

    /// <summary>Reads raw bytes.</summary>
    /// <param name="count">How many.</param>
    /// <returns>A view into the source. Valid as long as the source is.</returns>
    public ReadOnlySpan<byte> ReadBytes(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Ensure(count);
        var value = span.Slice(position, count);
        position += count;
        return value;
    }

    /// <summary>Reads a blittable array as one bulk copy.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="destination">Where to put them.</param>
    /// <remarks>
    ///     Copies into the destination rather than reinterpreting the source in place. A span of
    ///     bytes at an arbitrary offset has no alignment guarantee, and handing out a
    ///     <c>ReadOnlySpan&lt;float&gt;</c> over an odd address is the sort of thing that works on
    ///     x86 and faults on a platform that has not been tried yet.
    /// </remarks>
    public void ReadBlittable<T>(Span<T> destination) where T : unmanaged {
        var bytes = MemoryMarshal.AsBytes(destination);
        ReadBytes(bytes.Length).CopyTo(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    readonly void Ensure(int count) {
        if (position + count > span.Length) {
            Throw(count);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    readonly void Throw(int count) =>
        throw new SerializationException(
            $"Tried to read {count} bytes at offset {position} of {span.Length}. The data is "
            + "truncated, or was written by a version that does not agree with this one about the layout."
        );
}
