// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Vixen.Editor.Assets.Tests;

/// <summary>Writes a PNG, from the specification, so that the importer's input is not its own output.</summary>
/// <remarks>
///     <para>
///         A fixture encoded with the same library the importer decodes with tests very little: the
///         two agree by construction, and a decoder that read every file upside down would pass. This
///         is forty lines of PNG's own container — signature, IHDR, IDAT, IEND, each chunk with its
///         length and its CRC — so the bytes the importer is handed came from the format rather than
///         from StbImageSharp.
///     </para>
///     <para>
///         Only what a test needs: eight bits a channel, RGBA, no interlacing, and filter type zero
///         on every scanline. Deflate comes from the BCL, because compressing correctly is not the
///         part in question.
///     </para>
/// </remarks>
static class MinimalPng {
    static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Writes an eight-bit RGBA image.</summary>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rgba">Its pixels, four bytes each, row-major.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Write(int width, int height, ReadOnlySpan<byte> rgba) {
        using var file = new MemoryStream();
        file.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;      // Bits per channel.
        header[9] = 6;      // Colour type 6 is RGBA.
        header[10] = 0;     // Compression: deflate, the only one there is.
        header[11] = 0;     // Filtering: adaptive, the only one there is.
        header[12] = 0;     // Interlacing: none.
        Chunk(file, "IHDR", header);

        // Each scanline is preceded by its filter type. Zero means "stored as is", which is the one
        // choice a writer is always allowed to make.
        var raw = new byte[height * (1 + (width * 4))];

        for (var y = 0; y < height; y++) {
            var into = y * (1 + (width * 4));
            raw[into] = 0;
            rgba.Slice(y * width * 4, width * 4).CopyTo(raw.AsSpan(into + 1));
        }

        using var compressed = new MemoryStream();

        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) {
            deflate.Write(raw);
        }

        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);

        return file.ToArray();
    }

    static void Chunk(Stream file, string type, ReadOnlySpan<byte> data) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        file.Write(length);

        Span<byte> name = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, name);
        file.Write(name);
        file.Write(data);

        // The CRC covers the type and the data, and not the length — a detail that is only ever
        // discovered by getting it wrong.
        var crc = new Crc32();
        crc.Append(name);
        crc.Append(data);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        file.Write(checksum);
    }
}
