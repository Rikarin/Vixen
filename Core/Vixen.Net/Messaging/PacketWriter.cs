// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;

namespace Vixen.Net.Messaging;

/// <summary>
///     Writes a packet into a caller-owned buffer.
/// </summary>
/// <remarks>
///     <para>
///         <b>Little-endian, always, on every platform.</b> The wire format is fixed by this file
///         rather than by the machine that happens to be running, which is what makes the
///         bit-exactness gate — the same payload bytes from the same values on Windows, Linux and
///         macOS — a thing that can be asserted in CI.
///     </para>
///     <para>
///         <b>Running out of room is not an exception.</b> The writer sets
///         <see cref="Overflowed" /> and stops writing, and <see cref="TryFinish" /> refuses to hand
///         over a truncated packet. A bandwidth spike is an ordinary event in a frame loop and the
///         right response to it is to shed the packet, not to unwind the stack — that is what the
///         bandwidth budget and its priority shedding are built on.
///     </para>
///     <para>
///         It is a <c>ref struct</c> over a span the caller owns: no allocation, and no way to
///         accidentally keep it past the buffer it writes into.
///     </para>
/// </remarks>
public ref struct PacketWriter {
    readonly Span<byte> buffer;
    int position;

    /// <summary>How many bytes the buffer holds.</summary>
    public readonly int Capacity => buffer.Length;

    /// <summary>How many bytes have been written.</summary>
    public readonly int Position => position;

    /// <summary>How many bytes are left.</summary>
    public readonly int Remaining => buffer.Length - position;

    /// <summary>Whether a write did not fit. Once set, nothing more is written.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>What has been written so far, whether or not it is complete.</summary>
    public readonly ReadOnlySpan<byte> Written => buffer[..position];

    /// <summary>Creates a writer over a buffer.</summary>
    /// <param name="buffer">Where to write. The writer never grows it.</param>
    public PacketWriter(Span<byte> buffer) {
        this.buffer = buffer;
        position = 0;
        Overflowed = false;
    }

    /// <summary>Hands over the packet, if all of it fits.</summary>
    /// <param name="packet">The bytes written, or empty if anything overflowed.</param>
    /// <returns><see langword="false" /> if the packet is incomplete and must not be sent.</returns>
    public readonly bool TryFinish(out ReadOnlySpan<byte> packet) {
        packet = Overflowed ? default : buffer[..position];

        return !Overflowed;
    }

    /// <summary>Writes one byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteByte(byte value) {
        var span = Allocate(1);

        if (!span.IsEmpty) {
            span[0] = value;
        }
    }

    /// <summary>Writes a boolean as one byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>Writes an unsigned 16-bit value, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt16(ushort value) {
        var span = Allocate(sizeof(ushort));

        if (!span.IsEmpty) {
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        }
    }

    /// <summary>Writes an unsigned 32-bit value, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value) {
        var span = Allocate(sizeof(uint));

        if (!span.IsEmpty) {
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }
    }

    /// <summary>Writes a signed 32-bit value, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt32(int value) => WriteUInt32((uint)value);

    /// <summary>Writes an unsigned 64-bit value, little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value) {
        var span = Allocate(sizeof(ulong));

        if (!span.IsEmpty) {
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        }
    }

    /// <summary>Writes a 32-bit float by its bits, so the bytes do not depend on the platform.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));

    /// <summary>Writes a tick.</summary>
    /// <param name="value">The tick.</param>
    public void WriteTick(Tick value) => WriteUInt32(value.Value);

    /// <summary>
    ///     Writes an unsigned value in one to five bytes, seven bits at a time.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     Most of the numbers on a wire are small — a message id, a count, a length — and paying
    ///     four bytes for a three is what makes a packet twice the size it needs to be.
    /// </remarks>
    public void WriteVariable(uint value) {
        while (value >= 0x80) {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    /// <summary>Writes bytes with no length in front of them.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteRaw(ReadOnlySpan<byte> bytes) {
        var span = Allocate(bytes.Length);

        if (!span.IsEmpty || bytes.IsEmpty) {
            bytes.CopyTo(span);
        }
    }

    /// <summary>Writes bytes with their length in front of them.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteBlob(ReadOnlySpan<byte> bytes) {
        WriteVariable((uint)bytes.Length);
        WriteRaw(bytes);
    }

    /// <summary>Writes a string as length-prefixed UTF-8.</summary>
    /// <param name="value">The string. Null is written as empty — the wire does not distinguish.</param>
    public void WriteString(string? value) {
        if (string.IsNullOrEmpty(value)) {
            WriteVariable(0);

            return;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        WriteVariable((uint)count);
        var span = Allocate(count);

        if (!span.IsEmpty) {
            Encoding.UTF8.GetBytes(value, span);
        }
    }

    Span<byte> Allocate(int count) {
        if (Overflowed) {
            return default;
        }

        if (count > Remaining) {
            Overflowed = true;

            return default;
        }

        var span = buffer.Slice(position, count);
        position += count;

        return span;
    }
}
