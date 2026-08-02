// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;

namespace Vixen.Terrain;

/// <summary>A decoded heightmap: its size and its samples, big-endian order already undone.</summary>
/// <param name="Width">How many samples across.</param>
/// <param name="Height">And down.</param>
/// <param name="Samples">The samples, row-major, 0…65535.</param>
public readonly record struct TerrainHeightmapImage(int Width, int Height, ushort[] Samples);

/// <summary>
///     Sixteen-bit greyscale PNG, which is what every terrain generator writes and what
///     <c>Vixen.Core.Imaging</c> deliberately does not read.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T3]'s owed import and export.</b> Raw <c>.r16</c> has been wired since
///         T1 and is lossless, but it carries no header — so a person importing one has to know its
///         size and its endianness, and getting either wrong produces a terrain that looks like
///         static. A PNG says both.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Core.Imaging</c>, and the split is the bit depth.</b>
///         That library's decoder reads eight bits a channel, which is the right answer for a texture
///         and the wrong one for a heightfield: a terrain quantised to 256 heights is a faint terrace
///         on every slope, and it gets attributed to the generator rather than to the import. This
///         reads sixteen and refuses everything else rather than narrowing it.
///     </para>
///     <para>
///         ⚠ <b>Greyscale only, and a colour PNG is refused rather than averaged.</b> There is no
///         defensible way to turn three channels into one height — a luminance weighting is a
///         photographic convention and a heightfield is not a photograph — and averaging silently
///         would make a terrain that is subtly wrong everywhere.
///     </para>
///     <para>
///         ⚠ <b>Every filter type is decoded and only one is encoded.</b> A file this writes is one
///         we control; a file it reads came from World Machine, Gaea or Photoshop, and each picks
///         filters per row. Refusing a filter would refuse most real files.
///     </para>
///     <para>
///         ⚠ <b>PNG is big-endian and always has been.</b> Sixteen-bit samples are stored high byte
///         first whatever the machine is, which is the one place a heightmap import cannot be asked
///         about endianness — and the one place a raw <c>.r16</c> always must be.
///     </para>
/// </remarks>
public static class TerrainHeightmapPng {
    /// <summary>The eight bytes every PNG starts with.</summary>
    static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>How many bytes one sample is.</summary>
    public const int BytesPerSample = 2;

    /// <summary>Reads a sixteen-bit greyscale PNG.</summary>
    /// <param name="data">The file.</param>
    /// <returns>Its size and its samples.</returns>
    /// <exception cref="ArgumentException">It is not a sixteen-bit greyscale PNG.</exception>
    public static TerrainHeightmapImage Decode(ReadOnlySpan<byte> data) {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature)) {
            throw new ArgumentException("That is not a PNG: the eight-byte signature is missing.", nameof(data));
        }

        var at = Signature.Length;
        var width = 0;
        var height = 0;
        var compressed = new MemoryStream();
        var seenHeader = false;

        while (at + 8 <= data.Length) {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            var kind = data.Slice(at + 4, 4);
            var body = data.Slice(at + 8, Math.Min(length, data.Length - at - 8));

            if (kind.SequenceEqual("IHDR"u8)) {
                (width, height) = Header(body);
                seenHeader = true;
            } else if (kind.SequenceEqual("IDAT"u8)) {
                compressed.Write(body);
            } else if (kind.SequenceEqual("IEND"u8)) {
                break;
            }

            // Length, type, body, CRC. The CRC is not checked on read: a heightmap that arrives
            // corrupt produces a terrain somebody can see is wrong, and refusing a file over a
            // checksum somebody's tool computed differently helps nobody.
            at += 12 + length;
        }

        if (!seenHeader) {
            throw new ArgumentException("The PNG has no IHDR chunk.", nameof(data));
        }

        compressed.Position = 0;

        var stride = (width * BytesPerSample) + 1;
        var raw = new byte[(long)stride * height];

        using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress)) {
            inflate.ReadExactly(raw);
        }

        return new(width, height, Unfilter(raw, width, height));
    }

    /// <summary>Writes a terrain's composited heights as a sixteen-bit greyscale PNG.</summary>
    /// <param name="terrain">The terrain. Composited first, so what is written is what is drawn.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentNullException">There is no terrain.</exception>
    public static byte[] Encode(Terrain terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        terrain.Resolve();

        var description = terrain.Description;

        return Encode(description.SamplesX, description.SamplesZ, terrain.Composite.Span);
    }

    /// <summary>Writes samples as a sixteen-bit greyscale PNG.</summary>
    /// <param name="width">How many samples across.</param>
    /// <param name="height">And down.</param>
    /// <param name="samples">The samples, row-major.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
    /// <exception cref="ArgumentException">There are not that many samples.</exception>
    public static byte[] Encode(int width, int height, ReadOnlySpan<ushort> samples) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (samples.Length < (long)width * height) {
            throw new ArgumentException(
                $"A {width}×{height} heightmap is {(long)width * height} samples and {samples.Length} were given.",
                nameof(samples)
            );
        }

        var stride = (width * BytesPerSample) + 1;
        var raw = new byte[(long)stride * height];

        // ⚠ Filter 2 — "Up" — on every row, which is one subtraction per sample and is what makes a
        // heightfield compress. A terrain's rows differ from each other by a few metres and from
        // nothing else, so the filtered bytes are near zero and deflate eats them; filter 0 writes
        // the heights themselves and a 4096² terrain comes out four times larger. Paeth would be
        // better again and is four comparisons a sample, which is not worth it for an export.
        for (var y = 0; y < height; y++) {
            var row = (long)y * stride;

            raw[row] = 2;

            for (var x = 0; x < width; x++) {
                var value = samples[(y * width) + x];
                var above = y > 0 ? samples[((y - 1) * width) + x] : (ushort)0;

                // Big-endian, and the filter is applied per *byte* rather than per sample — which is
                // what the specification says and is not the same arithmetic as filtering the
                // sixteen-bit values and then splitting them.
                var high = (byte)(value >> 8);
                var low = (byte)value;
                var aboveHigh = (byte)(above >> 8);
                var aboveLow = (byte)above;

                raw[row + 1 + (x * 2)] = (byte)(high - aboveHigh);
                raw[row + 2 + (x * 2)] = (byte)(low - aboveLow);
            }
        }

        var compressed = new MemoryStream();

        using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) {
            deflate.Write(raw);
        }

        var file = new MemoryStream();

        file.Write(Signature);

        Span<byte> header = stackalloc byte[13];

        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);

        header[8] = 16;                       // bit depth
        header[9] = 0;                        // colour type: greyscale
        header[10] = 0;                       // compression: deflate
        header[11] = 0;                       // filter: adaptive
        header[12] = 0;                       // interlace: none

        Chunk(file, "IHDR"u8, header);
        Chunk(file, "IDAT"u8, compressed.ToArray());
        Chunk(file, "IEND"u8, []);

        return file.ToArray();
    }

    /// <summary>Reads an IHDR, refusing everything this is not for.</summary>
    static (int Width, int Height) Header(ReadOnlySpan<byte> body) {
        if (body.Length < 13) {
            throw new ArgumentException("The PNG's IHDR chunk is too short to be one.", nameof(body));
        }

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);

        if (body[8] != 16) {
            throw new ArgumentException(
                $"That PNG is {body[8]} bits a channel and a heightmap needs sixteen. An eight-bit "
                + "import is a terrain quantised to 256 heights, which reads as a faint terrace on "
                + "every slope and gets blamed on the generator.",
                nameof(body)
            );
        }

        if (body[9] != 0) {
            throw new ArgumentException(
                "That PNG is not greyscale. There is no defensible way to turn three channels into "
                + "one height — a luminance weighting is a photographic convention and a heightfield "
                + "is not a photograph.",
                nameof(body)
            );
        }

        if (body[12] != 0) {
            throw new ArgumentException("That PNG is interlaced, which this reader does not undo.", nameof(body));
        }

        return (width, height);
    }

    /// <summary>Undoes the per-row filters and unpacks the big-endian samples.</summary>
    /// <remarks>
    ///     ⚠ <b>Every filter type, because a file this reads came from somebody else's tool.</b> An
    ///     encoder picks a filter per row from five, and most pick per row rather than once — so a
    ///     reader that handled only the one it writes would refuse most real files.
    /// </remarks>
    static ushort[] Unfilter(byte[] raw, int width, int height) {
        var bytes = width * BytesPerSample;
        var stride = bytes + 1;
        var samples = new ushort[(long)width * height];
        var previous = new byte[bytes];
        var current = new byte[bytes];

        for (var y = 0; y < height; y++) {
            var row = (long)y * stride;
            var filter = raw[row];

            raw.AsSpan((int)row + 1, bytes).CopyTo(current);

            for (var x = 0; x < bytes; x++) {
                // The byte to the left is BytesPerSample back, not one — the specification's "a" is
                // the corresponding byte of the previous *pixel*.
                var left = x >= BytesPerSample ? current[x - BytesPerSample] : (byte)0;
                var above = previous[x];
                var corner = x >= BytesPerSample ? previous[x - BytesPerSample] : (byte)0;

                current[x] = filter switch {
                    0 => current[x],
                    1 => (byte)(current[x] + left),
                    2 => (byte)(current[x] + above),
                    3 => (byte)(current[x] + ((left + above) / 2)),
                    4 => (byte)(current[x] + Paeth(left, above, corner)),
                    _ => throw new ArgumentException($"The PNG uses filter {filter}, which is not one of the five.")
                };
            }

            for (var x = 0; x < width; x++) {
                samples[(y * width) + x] = (ushort)((current[x * 2] << 8) | current[(x * 2) + 1]);
            }

            (previous, current) = (current, previous);
        }

        return samples;
    }

    /// <summary>The specification's Paeth predictor, which is the one worth getting exactly right.</summary>
    static byte Paeth(byte left, byte above, byte corner) {
        var estimate = left + above - corner;
        var dl = Math.Abs(estimate - left);
        var da = Math.Abs(estimate - above);
        var dc = Math.Abs(estimate - corner);

        return dl <= da && dl <= dc ? left : da <= dc ? above : corner;
    }

    /// <summary>Writes one chunk: its length, its type, its body and its CRC.</summary>
    static void Chunk(Stream into, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> body) {
        Span<byte> length = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        into.Write(length);
        into.Write(kind);
        into.Write(body);

        // ⚠ Over the type *and* the body, and not over the length. Every PNG reader checks it, and a
        // CRC computed over the wrong range produces a file that this library reads back happily and
        // no other tool will open.
        var crc = Crc(kind, body);

        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        into.Write(length);
    }

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable() {
        var table = new uint[256];

        for (var index = 0u; index < 256u; index++) {
            var value = index;

            for (var bit = 0; bit < 8; bit++) {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    static uint Crc(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> body) {
        var crc = 0xFFFFFFFFu;

        foreach (var value in kind) {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in body) {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
