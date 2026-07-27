// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;

namespace Vixen.Net.Messaging;

/// <summary>
///     Reads a packet out of a span, and refuses to be surprised by it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Nothing here throws, ever.</b> Every read returns a <see cref="bool" />, and the first
///         one that fails sets <see cref="Failed" /> so that every read after it fails too. A decoder
///         can therefore read a whole message as straight-line code and check once at the end, and a
///         hostile packet takes the same path as a truncated one.
///     </para>
///     <para>
///         That is a security property, not a convenience. Inbound bytes come from a machine we do
///         not control; an exception escaping a decoder is a denial of service if it unwinds a frame
///         and a crash if it does not, and a <c>try</c>/<c>catch</c> around the whole of the receive
///         path is how a parser bug becomes exploitable. The reader is the closed door: it never
///         allocates on a length it was told (only <see cref="TryReadString" /> allocates at all,
///         and only up to a cap the caller states), never indexes outside its span, and never
///         believes a length field.
///     </para>
/// </remarks>
public ref struct PacketReader {
    readonly ReadOnlySpan<byte> buffer;
    int position;

    /// <summary>Whether any read has failed. Once set, every later read fails too.</summary>
    public bool Failed { get; private set; }

    /// <summary>How many bytes have been read.</summary>
    public readonly int Position => position;

    /// <summary>How many bytes are left.</summary>
    public readonly int Remaining => buffer.Length - position;

    /// <summary>Whether everything was read and nothing failed — what a well-formed packet looks like.</summary>
    public readonly bool IsComplete => !Failed && position == buffer.Length;

    /// <summary>
    ///     Everything not yet read, for handing the rest of a packet to whoever owns that part of it.
    /// </summary>
    /// <remarks>
    ///     Empty once a read has failed, so a caller that reads a header and passes the remainder on
    ///     cannot pass on the tail of a packet whose header it did not understand.
    /// </remarks>
    public readonly ReadOnlySpan<byte> RemainingBytes => Failed ? default : buffer[position..];

    /// <summary>Creates a reader over a packet.</summary>
    /// <param name="buffer">The bytes, which the reader does not take ownership of.</param>
    public PacketReader(ReadOnlySpan<byte> buffer) {
        this.buffer = buffer;
        position = 0;
        Failed = false;
    }

    /// <summary>Reads one byte.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadByte(out byte value) {
        if (!TryTake(1, out var span)) {
            value = 0;

            return false;
        }

        value = span[0];

        return true;
    }

    /// <summary>Reads a boolean.</summary>
    /// <param name="value">The value, or false.</param>
    /// <returns>Whether it was there, and was a 0 or a 1.</returns>
    /// <remarks>
    ///     A byte that is neither 0 nor 1 is rejected rather than treated as true. It cannot have come
    ///     from a writer of ours, so it came from something that is either broken or probing.
    /// </remarks>
    public bool TryReadBool(out bool value) {
        value = false;

        if (!TryReadByte(out var raw)) {
            return false;
        }

        if (raw > 1) {
            return Fail();
        }

        value = raw == 1;

        return true;
    }

    /// <summary>Reads an unsigned 16-bit value, little-endian.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadUInt16(out ushort value) {
        if (!TryTake(sizeof(ushort), out var span)) {
            value = 0;

            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(span);

        return true;
    }

    /// <summary>Reads an unsigned 32-bit value, little-endian.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadUInt32(out uint value) {
        if (!TryTake(sizeof(uint), out var span)) {
            value = 0;

            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(span);

        return true;
    }

    /// <summary>Reads a signed 32-bit value, little-endian.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadInt32(out int value) {
        var read = TryReadUInt32(out var raw);
        value = (int)raw;

        return read;
    }

    /// <summary>Reads an unsigned 64-bit value, little-endian.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadUInt64(out ulong value) {
        if (!TryTake(sizeof(ulong), out var span)) {
            value = 0;

            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(span);

        return true;
    }

    /// <summary>Reads a 32-bit float by its bits.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     Any bit pattern is a valid <see cref="float" />, including the NaNs, so this cannot reject
    ///     one. Bounds on a float are the business of the layer that knows what the number means —
    ///     which is what <c>[Quantize]</c> exists to state.
    /// </remarks>
    public bool TryReadSingle(out float value) {
        var read = TryReadUInt32(out var raw);
        value = BitConverter.UInt32BitsToSingle(raw);

        return read;
    }

    /// <summary>Reads a tick.</summary>
    /// <param name="value">The tick, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadTick(out Tick value) {
        var read = TryReadUInt32(out var raw);
        value = new(raw);

        return read;
    }

    /// <summary>Reads a value written by <see cref="PacketWriter.WriteVariable" />.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether a well-formed one was there.</returns>
    /// <remarks>
    ///     Five bytes at most, and the fifth may only carry the four bits that are left. A sixth
    ///     continuation byte, or a fifth with bits above the top of a <see cref="uint" />, is a
    ///     malformed encoding rather than a large number, and is refused — otherwise a packet could
    ///     spend as many bytes as it liked walking the reader forward.
    /// </remarks>
    public bool TryReadVariable(out uint value) {
        value = 0;

        for (var shift = 0; shift < 32; shift += 7) {
            if (!TryReadByte(out var raw)) {
                return false;
            }

            var payload = (uint)(raw & 0x7F);

            if (shift == 28 && payload > 0x0F) {
                return Fail();
            }

            value |= payload << shift;

            if ((raw & 0x80) == 0) {
                return true;
            }
        }

        return Fail();
    }

    /// <summary>Reads a fixed number of bytes.</summary>
    /// <param name="count">How many.</param>
    /// <param name="bytes">The bytes, pointing into the packet rather than a copy of them.</param>
    /// <returns>Whether they were there.</returns>
    public bool TryReadRaw(int count, out ReadOnlySpan<byte> bytes) {
        if (count < 0) {
            bytes = default;

            return Fail();
        }

        return TryTake(count, out bytes);
    }

    /// <summary>Reads bytes written by <see cref="PacketWriter.WriteBlob" />.</summary>
    /// <param name="maxBytes">
    ///     The largest blob this field is allowed to be. The length on the wire is not to be trusted,
    ///     and a caller that knows the field holds a 16-byte token should say so.
    /// </param>
    /// <param name="bytes">The bytes, pointing into the packet rather than a copy of them.</param>
    /// <returns>Whether a blob within the cap was there.</returns>
    public bool TryReadBlob(int maxBytes, out ReadOnlySpan<byte> bytes) {
        bytes = default;

        if (!TryReadVariable(out var length)) {
            return false;
        }

        if (length > (uint)maxBytes) {
            return Fail();
        }

        return TryTake((int)length, out bytes);
    }

    /// <summary>Reads a string written by <see cref="PacketWriter.WriteString" />.</summary>
    /// <param name="maxBytes">The largest the UTF-8 form of the string is allowed to be.</param>
    /// <param name="value">The string, or empty.</param>
    /// <returns>Whether a string within the cap was there.</returns>
    /// <remarks>
    ///     The only allocating read there is. The cap is what keeps a length field from turning into
    ///     a request to allocate, and it is required rather than defaulted so that every call site
    ///     has had to think about what its field is for.
    /// </remarks>
    public bool TryReadString(int maxBytes, out string value) {
        value = string.Empty;

        if (!TryReadBlob(maxBytes, out var bytes)) {
            return false;
        }

        value = bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);

        return true;
    }

    /// <summary>Marks the read as failed, for a decoder that found something it does not accept.</summary>
    /// <returns>Always <see langword="false" />, so a decoder can <c>return reader.Reject();</c>.</returns>
    public bool Reject() => Fail();

    bool TryTake(int count, out ReadOnlySpan<byte> span) {
        span = default;

        if (Failed) {
            return false;
        }

        if (count > Remaining) {
            return Fail();
        }

        span = buffer.Slice(position, count);
        position += count;

        return true;
    }

    bool Fail() {
        Failed = true;

        return false;
    }
}
