// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;

namespace Vixen.Live.Gameplay;

/// <summary>Writes a section's bytes.</summary>
/// <remarks>
///     Internal, and deliberately so: a section's bytes are read only by the codec that wrote them,
///     so this is not a format anybody else needs to speak. What it saves is four codecs each doing
///     their own offset arithmetic, which is where an off-by-four lives.
/// </remarks>
struct ProfileWriter {
    byte[] buffer;

    public ProfileWriter(int capacity = 64) {
        buffer = new byte[Math.Max(16, capacity)];
        Length = 0;
    }

    /// <summary>How many bytes have been written.</summary>
    public int Length { get; private set; }

    /// <summary>What was written.</summary>
    /// <returns>The bytes, trimmed to length.</returns>
    public readonly ReadOnlyMemory<byte> Written() => buffer.AsMemory(0, Length);

    public void Int32(int value) {
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
    }

    public void UInt32(uint value) {
        BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);
    }

    public void Int64(long value) {
        BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);
    }

    public void Single(float value) {
        BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);
    }

    public void UInt64(ulong value) {
        BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);
    }

    /// <summary>Writes a length-prefixed UTF-8 string.</summary>
    /// <param name="value">It, or null for empty.</param>
    public void Text(string? value) {
        var count = string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

        Int32(count);

        if (count > 0) {
            Encoding.UTF8.GetBytes(value!, Take(count));
        }
    }

    Span<byte> Take(int count) {
        if (Length + count > buffer.Length) {
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, Length + count));
        }

        var span = buffer.AsSpan(Length, count);

        Length += count;

        return span;
    }
}

/// <summary>Reads a section's bytes.</summary>
/// <remarks>
///     ⚠ <b>Truncation is not an exception here, it is the end.</b> A section is read back by a build
///     that may be older than the one that wrote it, and doc 27 § Upgrades makes that a normal state
///     rather than a corrupt one. <see cref="IsDone" /> going true early leaves whatever was read
///     already, which is the same posture the profile container takes towards a section it does not
///     know: keep what is understood, drop nothing that is not.
/// </remarks>
ref struct ProfileReader {
    readonly ReadOnlySpan<byte> bytes;
    int offset;

    public ProfileReader(ReadOnlySpan<byte> bytes) {
        this.bytes = bytes;
        offset = 0;
    }

    /// <summary>Whether there is nothing left, or something was truncated.</summary>
    public bool IsDone { get; private set; }

    public int Int32() => Has(4) ? BinaryPrimitives.ReadInt32LittleEndian(Take(4)) : 0;

    public uint UInt32() => Has(4) ? BinaryPrimitives.ReadUInt32LittleEndian(Take(4)) : 0u;

    public long Int64() => Has(8) ? BinaryPrimitives.ReadInt64LittleEndian(Take(8)) : 0L;

    public ulong UInt64() => Has(8) ? BinaryPrimitives.ReadUInt64LittleEndian(Take(8)) : 0ul;

    public float Single() => Has(4) ? BinaryPrimitives.ReadSingleLittleEndian(Take(4)) : 0f;

    public string Text() {
        var count = Int32();

        if (count <= 0 || !Has(count)) {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(Take(count));
    }

    /// <summary>How many entries a count field says there are, bounded by what could possibly fit.</summary>
    /// <param name="size">The smallest an entry can be.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    ///     ⚠ <b>Bounded, because the count comes off the wire.</b> A truncated or hostile section
    ///     saying it holds two billion entries would otherwise allocate against a length that is not
    ///     there — the loop would end at <see cref="IsDone" />, but only after being asked to try.
    /// </remarks>
    public int Count(int size) {
        var count = Int32();

        return count <= 0 ? 0 : Math.Min(count, (bytes.Length - offset) / Math.Max(1, size));
    }

    bool Has(int count) {
        if (IsDone || offset + count > bytes.Length) {
            IsDone = true;

            return false;
        }

        return true;
    }

    ReadOnlySpan<byte> Take(int count) {
        var span = bytes.Slice(offset, count);

        offset += count;

        return span;
    }
}
