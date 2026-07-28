// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Ui.Text.Outlines;

/// <summary>A cursor over a font table. Big-endian, because <c>sfnt</c> is.</summary>
/// <remarks>
///     ⚠ <b>Every read advances, and that is what makes <c>Position += Read()</c> a trap.</b> A
///     compound assignment reads its target before evaluating the right-hand side, so
///     <c>Position += U16()</c> lands two bytes before where it means to. It cost this parser every
///     glyph in every font and read perfectly on the page; the fix is a local, and this note is here
///     so the next person writing a table reader does not spend the afternoon finding it again.
/// </remarks>
internal ref struct SfntReader(ReadOnlySpan<byte> data) {
    readonly ReadOnlySpan<byte> data = data;

    /// <summary>Where the cursor is.</summary>
    public int Position { get; set; }

    /// <summary>How much is left.</summary>
    public readonly int Remaining => data.Length - Position;

    /// <summary>Whether a read of that many bytes would stay inside the table.</summary>
    /// <param name="bytes">How many.</param>
    public readonly bool Has(int bytes) => Position >= 0 && Position + bytes <= data.Length;

    public byte U8() => data[Position++];

    public sbyte S8() => (sbyte)data[Position++];

    public ushort U16() {
        var value = BinaryPrimitives.ReadUInt16BigEndian(data[Position..]);
        Position += 2;
        return value;
    }

    public short S16() => (short)U16();

    public uint U32() {
        var value = BinaryPrimitives.ReadUInt32BigEndian(data[Position..]);
        Position += 4;
        return value;
    }

    /// <summary>An offset of 1 to 4 bytes, as CFF's INDEX structures store them.</summary>
    /// <param name="size">How many bytes wide.</param>
    public int Offset(int size) {
        var value = 0;
        for (var i = 0; i < size; i++) {
            value = (value << 8) | data[Position++];
        }

        return value;
    }

    /// <summary>An F2Dot14 fixed-point number, as a composite glyph's transform stores them.</summary>
    public float F2Dot14() => S16() / 16384f;
}
