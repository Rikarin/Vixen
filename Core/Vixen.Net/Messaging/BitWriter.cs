// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Messaging;

/// <summary>
///     Writes fields that are not a whole number of bytes long.
/// </summary>
/// <remarks>
///     <para>
///         The whole of delta compression is here. A tick number that only ever advances by one costs
///         one bit to say so; a health value between 0 and 100 costs seven; a "this field did not
///         change" flag costs one instead of the four bytes of the field it is standing in for.
///         Rounding each of those up to a byte is what makes a snapshot three times the size it needs
///         to be, and a bandwidth budget that then has to shed things it did not need to.
///     </para>
///     <para>
///         Bits are packed low-to-high within each byte, and bytes ascend. That is stated because it
///         is a wire format rather than an implementation detail: the same values produce the same
///         bytes on every platform, which is what the bit-exactness gate asserts.
///     </para>
///     <para>
///         Overflow behaves as <see cref="PacketWriter" />'s does — a flag and a refusal to hand over
///         a truncated packet — for the same reason, and because the two are used together on the
///         same buffer.
///     </para>
/// </remarks>
public ref struct BitWriter {
    readonly Span<byte> buffer;
    int bits;

    /// <summary>How many bits have been written.</summary>
    public readonly int BitsWritten => bits;

    /// <summary>How many bytes those bits occupy, rounded up.</summary>
    public readonly int BytesWritten => (bits + 7) >> 3;

    /// <summary>How many bits are left.</summary>
    public readonly int BitsRemaining => (buffer.Length * 8) - bits;

    /// <summary>Whether a write did not fit. Once set, nothing more is written.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Creates a writer over a buffer.</summary>
    /// <param name="buffer">Where to write. The writer never grows it.</param>
    public BitWriter(Span<byte> buffer) {
        this.buffer = buffer;
        bits = 0;
        Overflowed = false;
    }

    /// <summary>Writes the low <paramref name="count" /> bits of a value.</summary>
    /// <param name="value">The value. Bits above <paramref name="count" /> are ignored.</param>
    /// <param name="count">How many bits, from 1 to 32.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is not between 1 and 32.</exception>
    public void Write(uint value, int count) {
        if (count is < 1 or > 32) {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A field is between 1 and 32 bits wide.");
        }

        if (Overflowed) {
            return;
        }

        if (count > BitsRemaining) {
            Overflowed = true;

            return;
        }

        if (count < 32) {
            value &= (1u << count) - 1;
        }

        while (count > 0) {
            var index = bits >> 3;
            var offset = bits & 7;

            // Clear on first touch rather than clearing the whole buffer up front: the buffer is as
            // large as the biggest packet and most packets are not, so zeroing it would cost more
            // than everything else this type does.
            if (offset == 0) {
                buffer[index] = 0;
            }

            var take = Math.Min(8 - offset, count);
            var chunk = (byte)(value & ((1u << take) - 1));
            buffer[index] |= (byte)(chunk << offset);

            value >>= take;
            count -= take;
            bits += take;
        }
    }

    /// <summary>Writes one bit.</summary>
    /// <param name="value">The bit.</param>
    public void WriteBool(bool value) => Write(value ? 1u : 0u, 1);

    /// <summary>Writes a whole 32-bit value.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value) => Write(value, 32);

    /// <summary>Writes a whole 32-bit signed value.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt32(int value) => Write((uint)value, 32);

    /// <summary>Writes a float by its bits, when it has no declared range to be quantized into.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value) => Write(BitConverter.SingleToUInt32Bits(value), 32);

    /// <summary>Writes a float in as many bits as its range says it is worth.</summary>
    /// <param name="value">The value.</param>
    /// <param name="range">What the value is a value of.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range cannot be encoded with.</exception>
    public void WriteQuantized(float value, in QuantizeRange range) {
        if (!range.IsValid) {
            throw new ArgumentOutOfRangeException(nameof(range), range, "The range is not one values can be put in.");
        }

        Write(range.Encode(value), range.Bits);
    }

    /// <summary>
    ///     Writes a value in as few seven-bit groups as it needs, each with a bit saying whether
    ///     another follows.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteVariable(uint value) {
        while (value >= 0x80) {
            Write((value & 0x7F) | 0x80, 8);
            value >>= 7;
        }

        Write(value, 8);
    }

    /// <summary>Copies bits that are already encoded, at whatever offset this writer is at.</summary>
    /// <param name="source">The bits, packed as this writer packs them.</param>
    /// <param name="count">How many of them to take.</param>
    /// <remarks>
    ///     What lets a snapshot be encoded once and sent to fifty connections: the expensive part —
    ///     reading the component, quantizing it, packing it — happens once a tick, and each
    ///     connection's snapshot is this.
    /// </remarks>
    public void WriteBitsFrom(ReadOnlySpan<byte> source, int count) {
        var reader = new BitReader(source);

        while (count > 0) {
            var take = Math.Min(32, count);

            if (!reader.TryRead(take, out var chunk)) {
                Overflowed = true;

                return;
            }

            Write(chunk, take);
            count -= take;
        }
    }

    /// <summary>
    ///     Puts the writer back to an earlier position, discarding what was written after it.
    /// </summary>
    /// <param name="bitPosition">A position previously read from <see cref="BitsWritten" />.</param>
    /// <remarks>
    ///     For a bandwidth budget: the only way to know what a record costs is to write it, so the
    ///     budget writes one and takes it back if it did not fit. The partial byte at the new
    ///     position is cleared above it, so the bits that were rolled back cannot survive into what
    ///     is written next.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The position is not one this writer has been at.</exception>
    public void Rewind(int bitPosition) {
        ArgumentOutOfRangeException.ThrowIfNegative(bitPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitPosition, bits);

        bits = bitPosition;
        var offset = bits & 7;

        if (offset != 0) {
            buffer[bits >> 3] &= (byte)((1 << offset) - 1);
        }
    }

    /// <summary>Moves to the next byte boundary, so bytes can be written whole.</summary>
    public void Align() {
        var offset = bits & 7;

        if (offset != 0) {
            Write(0, 8 - offset);
        }
    }

    /// <summary>Writes bytes, aligning first.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> bytes) {
        Align();

        if (Overflowed) {
            return;
        }

        if (bytes.Length * 8 > BitsRemaining) {
            Overflowed = true;

            return;
        }

        bytes.CopyTo(buffer[(bits >> 3)..]);
        bits += bytes.Length * 8;
    }

    /// <summary>Hands over the packet, if all of it fits.</summary>
    /// <param name="packet">
    ///     The bytes written, padded with zeroes to a byte boundary, or empty if anything overflowed.
    /// </param>
    /// <returns><see langword="false" /> if the packet is incomplete and must not be sent.</returns>
    public readonly bool TryFinish(out ReadOnlySpan<byte> packet) {
        packet = Overflowed ? default : buffer[..BytesWritten];

        return !Overflowed;
    }
}
