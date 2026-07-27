// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>Writes a block's fields as a little-endian bit stream.</summary>
/// <remarks>
///     BC6H and BC7 describe their blocks as one 128-bit field list starting at bit zero, and every
///     field boundary in them lands mid-byte. Walking a cursor a bit at a time is slower than
///     shifting whole words into place and is the version that can be checked against the
///     specification by reading it, which for a format where one misplaced bit changes the mode is
///     the trade worth making.
/// </remarks>
ref struct BlockBitWriter {
    readonly Span<byte> block;
    int cursor;

    /// <summary>Starts at bit zero of a cleared block.</summary>
    /// <param name="block">The block's bytes.</param>
    public BlockBitWriter(Span<byte> block) {
        block.Clear();
        this.block = block;
        cursor = 0;
    }

    /// <summary>How many bits have been written.</summary>
    public readonly int Cursor => cursor;

    /// <summary>Writes a field, least significant bit first.</summary>
    /// <param name="value">The value.</param>
    /// <param name="bits">How many of its bits to write.</param>
    public void Write(uint value, int bits) {
        for (var bit = 0; bit < bits; bit++) {
            if (((value >> bit) & 1) != 0) {
                block[cursor >> 3] |= (byte)(1 << (cursor & 7));
            }

            cursor++;
        }
    }
}

/// <summary>Reads a block's fields as a little-endian bit stream. The inverse of <see cref="BlockBitWriter" />.</summary>
ref struct BlockBitReader {
    readonly ReadOnlySpan<byte> block;
    int cursor;

    /// <summary>Starts at bit zero.</summary>
    /// <param name="block">The block's bytes.</param>
    public BlockBitReader(ReadOnlySpan<byte> block) {
        this.block = block;
        cursor = 0;
    }

    /// <summary>How many bits have been read.</summary>
    public readonly int Cursor => cursor;

    /// <summary>Reads a field, least significant bit first.</summary>
    /// <param name="bits">How many bits.</param>
    /// <returns>The value.</returns>
    public uint Read(int bits) {
        uint value = 0;

        for (var bit = 0; bit < bits; bit++) {
            value |= (uint)((block[cursor >> 3] >> (cursor & 7)) & 1) << bit;
            cursor++;
        }

        return value;
    }
}
