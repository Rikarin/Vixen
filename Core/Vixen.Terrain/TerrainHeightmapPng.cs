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

    /// <summary>The largest heightmap this will decode, on either axis.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An absolute cap rather than one derived from the file's length, because deflate
    ///         means there is no honest ratio to derive it from.</b> A kilobyte of IDAT legitimately
    ///         expands to a 4096² heightmap, so "proportionate to the input" is not a property a PNG
    ///         reader can have; what it can have is a refusal to believe a header. An IHDR is four
    ///         bytes of width and four of height and nothing checks them, so 65535×65535 is a
    ///         seventeen-gigabyte allocation requested by eight bytes, and the failure is an
    ///         <c>OutOfMemoryException</c> or an <c>OverflowException</c> out of an importer that is
    ///         catching <see cref="ArgumentException" />.
    ///     </para>
    ///     <para>
    ///         16384 is four times the largest terrain the engine builds and is a 512 MB decode at
    ///         sixteen bits — large enough that no real heightmap meets it, small enough that meeting
    ///         it is a diagnosable error rather than a dead process.
    ///     </para>
    /// </remarks>
    public const int MaximumSize = 16384;

    /// <summary>The most the image data may expand to, as a multiple of the IDAT bytes present.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>1032 is DEFLATE's own ceiling rather than a figure somebody picked</b>, which is
    ///         what makes it safe to enforce: the format's longest match is 258 bytes and its cheapest
    ///         encoding of one is two bits, so no valid deflate stream exceeds it and anything
    ///         claiming to is not compressed data.
    ///     </para>
    ///     <para>
    ///         <b>This is the check <see cref="MaximumSize" /> cannot make.</b> A cap on the
    ///         dimensions stops a header asking for a terabyte and still leaves forty bytes of file
    ///         able to ask for half a gigabyte, because the row buffer is allocated from the header
    ///         before a byte of image data is read. Tying it to the IDAT actually present is what
    ///         makes the cost of a decode proportional to the file — a 4096² heightmap needs about
    ///         thirty kilobytes of IDAT before this will believe in it, which every real one has.
    ///     </para>
    /// </remarks>
    public const int MaximumExpansion = 1032;

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
            // ⚠ Unsigned, and checked against what is left before it is narrowed. A chunk length of
            // 0x80000000 becomes a negative int, Math.Min hands that straight to Slice, and the
            // reader throws an ArgumentOutOfRangeException naming no chunk and no file — a refusal
            // whose message is "Specified argument was out of the range of valid values".
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

            if (length > (uint)(data.Length - at - 8)) {
                throw new ArgumentException(
                    $"The PNG's chunk at offset {at} claims {length} bytes and {data.Length - at - 8} are left. "
                    + "The file is truncated or is not a PNG.",
                    nameof(data)
                );
            }

            var kind = data.Slice(at + 4, 4);
            var body = data.Slice(at + 8, (int)length);

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
            //
            // The length was bounded against what remains above, so this cannot run backwards or
            // past the end — both of which a wrapped or negative length would otherwise do.
            at += 12 + (int)length;
        }

        if (!seenHeader) {
            throw new ArgumentException("The PNG has no IHDR chunk.", nameof(data));
        }

        var stride = (width * BytesPerSample) + 1;
        var wanted = (long)stride * height;

        if (wanted > compressed.Length * (long)MaximumExpansion) {
            throw new ArgumentException(
                $"That PNG declares {width}×{height}, which is {wanted} bytes of image data, and carries "
                + $"{compressed.Length} bytes of IDAT to produce them from. No deflate stream expands more than "
                + $"{MaximumExpansion}×, so the header and the data do not describe the same file.",
                nameof(data)
            );
        }

        compressed.Position = 0;

        var raw = new byte[wanted];

        // ⚠ Every way a zlib stream can disappoint, turned into the refusal this method documents.
        // A truncated IDAT is an EndOfStreamException, a corrupt one an InvalidDataException, and a
        // stream the native inflater rejects a ZLibException — which is an IOException and is
        // neither of the first two, so a filter naming only those still lets it out. None of the
        // three is an ArgumentException, so before this an importer catching ArgumentException to
        // report a bad file caught the cases that cannot happen and none of the ones that do.
        try {
            using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
            inflate.ReadExactly(raw);
        } catch (Exception failure) when (failure is InvalidDataException or IOException) {
            throw new ArgumentException(
                $"The PNG's image data does not decompress into the {width}×{height} it declares: {failure.Message}",
                nameof(data),
                failure
            );
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

        // ⚠ Unsigned, and bounded before either is narrowed or multiplied. These eight bytes are the
        // whole of what decides how large the decode buffers are, so they are the file's cheapest
        // way to ask for an allocation: 0x80000000 narrows to a negative width, and 500000² is a
        // half-terabyte row buffer, and neither is refused by anything downstream — the first throws
        // OverflowException out of `new byte[]` and the second the same, both past the
        // ArgumentException an importer is catching.
        var declaredWidth = BinaryPrimitives.ReadUInt32BigEndian(body);
        var declaredHeight = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);

        if (declaredWidth is 0 or > MaximumSize || declaredHeight is 0 or > MaximumSize) {
            throw new ArgumentException(
                $"That PNG is {declaredWidth}×{declaredHeight}, and a heightmap this reads is between 1 and "
                + $"{MaximumSize} on each axis. A zero is not an image and the cap is four times the largest "
                + "terrain the engine builds — past it, the header is asking for an allocation rather than "
                + "describing a file.",
                nameof(body)
            );
        }

        var width = (int)declaredWidth;
        var height = (int)declaredHeight;

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
