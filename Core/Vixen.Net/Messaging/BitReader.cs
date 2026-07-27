// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Messaging;

/// <summary>Reads what <see cref="BitWriter" /> wrote, and refuses to be surprised by it.</summary>
/// <remarks>
///     The same contract <see cref="PacketReader" /> keeps, for the same reasons: nothing throws on
///     the contents of a packet, every read returns a <see cref="bool" />, and the first failure is
///     sticky so a decoder can read a whole message and check once at the end.
/// </remarks>
public ref struct BitReader {
    readonly ReadOnlySpan<byte> buffer;
    int bits;

    /// <summary>Whether any read has failed. Once set, every later read fails too.</summary>
    public bool Failed { get; private set; }

    /// <summary>How many bits have been read.</summary>
    public readonly int BitsRead => bits;

    /// <summary>How many bits are left.</summary>
    public readonly int BitsRemaining => (buffer.Length * 8) - bits;

    /// <summary>Creates a reader over a packet.</summary>
    /// <param name="buffer">The bytes, which the reader does not take ownership of.</param>
    public BitReader(ReadOnlySpan<byte> buffer) {
        this.buffer = buffer;
        bits = 0;
        Failed = false;
    }

    /// <summary>Reads a field of a given width.</summary>
    /// <param name="count">How many bits, from 1 to 32.</param>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is not between 1 and 32.</exception>
    public bool TryRead(int count, out uint value) {
        if (count is < 1 or > 32) {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A field is between 1 and 32 bits wide.");
        }

        value = 0;

        if (Failed) {
            return false;
        }

        if (count > BitsRemaining) {
            Failed = true;

            return false;
        }

        var shift = 0;

        while (count > 0) {
            var index = bits >> 3;
            var offset = bits & 7;
            var take = Math.Min(8 - offset, count);
            var chunk = (uint)((buffer[index] >> offset) & ((1 << take) - 1));

            value |= chunk << shift;
            shift += take;
            count -= take;
            bits += take;
        }

        return true;
    }

    /// <summary>Reads one bit.</summary>
    /// <param name="value">The bit, or false.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadBool(out bool value) {
        var read = TryRead(1, out var raw);
        value = raw != 0;

        return read;
    }

    /// <summary>Reads a whole 32-bit value.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadUInt32(out uint value) => TryRead(32, out value);

    /// <summary>Reads a whole 32-bit signed value.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadInt32(out int value) {
        var read = TryRead(32, out var raw);
        value = (int)raw;

        return read;
    }

    /// <summary>Reads a float written by its bits.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public bool TryReadSingle(out float value) {
        var read = TryRead(32, out var raw);
        value = BitConverter.UInt32BitsToSingle(raw);

        return read;
    }

    /// <summary>Reads a float that was quantized into a range.</summary>
    /// <param name="range">The same range it was written with.</param>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether it was there.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range cannot be encoded with.</exception>
    public bool TryReadQuantized(in QuantizeRange range, out float value) {
        if (!range.IsValid) {
            throw new ArgumentOutOfRangeException(nameof(range), range, "The range is not one values can be put in.");
        }

        value = 0;

        if (!TryRead(range.Bits, out var encoded)) {
            return false;
        }

        value = range.Decode(encoded);

        return true;
    }

    /// <summary>Reads a value written by <see cref="BitWriter.WriteVariable" />.</summary>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether a well-formed one was there.</returns>
    public bool TryReadVariable(out uint value) {
        value = 0;

        for (var shift = 0; shift < 32; shift += 7) {
            if (!TryRead(8, out var raw)) {
                return false;
            }

            var payload = raw & 0x7F;

            if (shift == 28 && payload > 0x0F) {
                Failed = true;

                return false;
            }

            value |= payload << shift;

            if ((raw & 0x80) == 0) {
                return true;
            }
        }

        Failed = true;

        return false;
    }

    /// <summary>Moves to the next byte boundary, matching <see cref="BitWriter.Align" />.</summary>
    /// <returns>Whether there was room to.</returns>
    public bool Align() {
        var offset = bits & 7;

        return offset == 0 || TryRead(8 - offset, out _);
    }

    /// <summary>Reads bytes, aligning first.</summary>
    /// <param name="count">How many.</param>
    /// <param name="bytes">The bytes, pointing into the packet rather than a copy of them.</param>
    /// <returns>Whether they were there.</returns>
    public bool TryReadBytes(int count, out ReadOnlySpan<byte> bytes) {
        bytes = default;

        if (count < 0 || !Align() || Failed) {
            Failed = true;

            return false;
        }

        if (count * 8 > BitsRemaining) {
            Failed = true;

            return false;
        }

        bytes = buffer.Slice(bits >> 3, count);
        bits += count * 8;

        return true;
    }
}
