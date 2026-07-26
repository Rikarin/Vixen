// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Vixen.Core.Serialization;

/// <summary>Writes the binary format, either into a fixed span or into a growable sink.</summary>
/// <remarks>
///     <para>
///         A <see langword="ref" /> <see langword="struct" />, so it lives on the stack and the span
///         it writes into cannot outlive the frame that owns it. Everything here is a bounds check
///         and a store; no allocation happens on this path unless the sink grows.
///     </para>
///     <para>
///         <b>Little-endian, always, asserted rather than assumed.</b> Every multi-byte write goes
///         through <see cref="BinaryPrimitives" /> with an explicit endianness, so content written on
///         one machine reads identically on another regardless of what either CPU prefers. No
///         big-endian target exists today; the point is that adding one would not silently corrupt
///         every existing asset.
///     </para>
///     <para>
///         Constructed over a span, it writes until the span is full and then throws. Constructed
///         over an <see cref="IBufferWriter{T}" />, it grows — and must be <see cref="Flush" />ed
///         before the sink is read, because the bytes written since the last growth have not been
///         handed back yet.
///     </para>
/// </remarks>
public ref partial struct SerializationWriter {
    const int MinimumChunk = 1024;

    readonly IBufferWriter<byte>? sink;
    Span<byte> span;
    int position;
    long flushed;

    /// <summary>How many bytes have been written in total.</summary>
    public readonly long BytesWritten => flushed + position;

    /// <summary>Writes into a fixed buffer. Overflowing it throws.</summary>
    /// <param name="destination">Where to write.</param>
    public SerializationWriter(Span<byte> destination) {
        span = destination;
        sink = null;
    }

    /// <summary>Writes into a sink that can grow.</summary>
    /// <param name="sink">Where to write. <see cref="Flush" /> before reading it back.</param>
    public SerializationWriter(IBufferWriter<byte> sink) {
        ArgumentNullException.ThrowIfNull(sink);
        this.sink = sink;
        span = sink.GetSpan(MinimumChunk);
    }

    /// <summary>Hands everything written back to the sink. Required before the sink is read.</summary>
    public void Flush() {
        if (sink is not null && position > 0) {
            sink.Advance(position);
            flushed += position;
            position = 0;
            span = default;
        }
    }

    /// <summary>Writes one byte.</summary>
    /// <param name="value">The byte.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value) {
        Ensure(1);
        span[position++] = value;
    }

    /// <summary>Writes a boolean as one byte, 0 or 1.</summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>Writes a signed byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    /// <summary>Writes a 16-bit integer, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt16(short value) {
        Ensure(sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[position..], value);
        position += sizeof(short);
    }

    /// <summary>Writes an unsigned 16-bit integer, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt16(ushort value) {
        Ensure(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span[position..], value);
        position += sizeof(ushort);
    }

    /// <summary>Writes a 32-bit integer, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt32(int value) {
        Ensure(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span[position..], value);
        position += sizeof(int);
    }

    /// <summary>Writes an unsigned 32-bit integer, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value) {
        Ensure(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span[position..], value);
        position += sizeof(uint);
    }

    /// <summary>Writes a 64-bit integer, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt64(long value) {
        Ensure(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(span[position..], value);
        position += sizeof(long);
    }

    /// <summary>Writes an unsigned 64-bit integer, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value) {
        Ensure(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span[position..], value);
        position += sizeof(ulong);
    }

    /// <summary>Writes a 16-bit character, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteChar(char value) => WriteUInt16(value);

    /// <summary>Writes a half-precision float.</summary>
    /// <param name="value">The value.</param>
    public void WriteHalf(Half value) => WriteUInt16(BitConverter.HalfToUInt16Bits(value));

    /// <summary>Writes a single-precision float.</summary>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     By its bits, not by its value: <c>-0f</c> and <c>+0f</c> stay distinct, and every NaN
    ///     payload survives a round trip. Content determinism is a byte comparison, so a format that
    ///     normalised either of those would produce two different files for one asset.
    /// </remarks>
    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));

    /// <summary>Writes a double-precision float, by its bits.</summary>
    /// <param name="value">The value.</param>
    public void WriteDouble(double value) => WriteUInt64(BitConverter.DoubleToUInt64Bits(value));

    /// <summary>Writes a decimal as its four constituent integers.</summary>
    /// <param name="value">The value.</param>
    public void WriteDecimal(decimal value) {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        foreach (var part in bits) {
            WriteInt32(part);
        }
    }

    /// <summary>Writes a <see cref="Guid" /> in its 16-byte little-endian form.</summary>
    /// <param name="value">The value.</param>
    public void WriteGuid(Guid value) {
        Ensure(16);
        value.TryWriteBytes(span[position..], bigEndian: false, out _);
        position += 16;
    }

    /// <summary>Writes a <see cref="DateTime" /> as its ticks and kind.</summary>
    /// <param name="value">The value.</param>
    public void WriteDateTime(DateTime value) {
        WriteInt64(value.Ticks);
        WriteByte((byte)value.Kind);
    }

    /// <summary>Writes a <see cref="DateTimeOffset" /> as its ticks and offset.</summary>
    /// <param name="value">The value.</param>
    public void WriteDateTimeOffset(DateTimeOffset value) {
        WriteInt64(value.Ticks);
        WriteInt64(value.Offset.Ticks);
    }

    /// <summary>Writes a <see cref="TimeSpan" /> as its ticks.</summary>
    /// <param name="value">The value.</param>
    public void WriteTimeSpan(TimeSpan value) => WriteInt64(value.Ticks);

    /// <summary>Writes an unsigned integer in LEB128, seven bits per byte.</summary>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     Lengths and counts go through this. Almost every one of them is under 128 and costs one
    ///     byte instead of four, which across a scene graph of small objects is most of the header
    ///     overhead there is.
    /// </remarks>
    public void WriteVarUInt64(ulong value) {
        while (value >= 0x80) {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    /// <summary>Writes a signed integer in zig-zag LEB128.</summary>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     Zig-zag first, so that −1 costs one byte rather than ten. A plain LEB128 of a negative
    ///     number is all ones in the high bits, which is the worst case for a format that exists to
    ///     make small numbers small.
    /// </remarks>
    public void WriteVarInt64(long value) => WriteVarUInt64((ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Writes a length-prefixed UTF-8 string, or a null marker.</summary>
    /// <param name="value">The string, which may be <see langword="null" />.</param>
    public void WriteString(string? value) {
        if (value is null) {
            // Zero is null and n+1 is a string of length n, so null and empty stay distinguishable
            // in one byte rather than in a flag plus a length.
            WriteVarUInt64(0);
            return;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt64((ulong)count + 1);
        Ensure(count);
        Encoding.UTF8.GetBytes(value, span[position..]);
        position += count;
    }

    /// <summary>Writes raw bytes, without a length.</summary>
    /// <param name="value">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> value) {
        Ensure(value.Length);
        value.CopyTo(span[position..]);
        position += value.Length;
    }

    /// <summary>Writes a blittable span as one bulk copy, without a length.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="value">The elements.</param>
    /// <remarks>
    ///     The reason vertex buffers and ECS chunks are cheap to serialise. Correct only for types
    ///     whose in-memory form is already the wire form, which on the little-endian targets Vixen
    ///     supports means any unmanaged type without padding surprises — the generator only emits
    ///     this for the primitive element types where that is unconditionally true.
    /// </remarks>
    public void WriteBlittable<T>(ReadOnlySpan<T> value) where T : unmanaged =>
        WriteBytes(MemoryMarshal.AsBytes(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Ensure(int count) {
        if (position + count <= span.Length) {
            return;
        }

        Grow(count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Grow(int count) {
        if (sink is null) {
            throw new SerializationException(
                $"The destination buffer holds {span.Length} bytes and {position + count} were needed. "
                + "Construct the writer over an IBufferWriter<byte> if the size is not known in advance."
            );
        }

        sink.Advance(position);
        flushed += position;
        position = 0;
        span = sink.GetSpan(Math.Max(count, MinimumChunk));
    }
}
