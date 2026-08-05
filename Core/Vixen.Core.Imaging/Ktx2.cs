// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>A file that is not KTX2, or is KTX2 in a way this does not implement.</summary>
public sealed class Ktx2Exception(string message) : Exception(message);

/// <summary>Reads and writes the KTX2 container.</summary>
/// <remarks>
///     <para>
///         KTX2 is what a Vixen build ships textures in, and the runtime reads it with this rather
///         than with an image codec: the bytes in the file are the bytes the GPU wants, so loading a
///         texture is a header parse and an upload. That is also why <c>Vixen.Core.Imaging</c> has no
///         PNG decoder — decoding a PNG is import-time work, and ADR-015 keeps ImageSharp out of
///         every runtime assembly for licence reasons as well.
///     </para>
///     <para>
///         <b>Level data is stored smallest first.</b> The level index is ordered largest first, but
///         the bytes it points at run the other way, so a streaming loader can read the small mips
///         off the front of the file and show something before the rest has arrived. It is the one
///         part of the format that reads like a mistake and is not, so it is the part most worth a
///         test.
///     </para>
///     <para>
///         <b>What is implemented:</b> the identifier, the header, the level index, the data format
///         descriptor, key/value data, and level data for uncompressed and block-compressed formats.
///         <b>What is not:</b> supercompression — neither Basis Universal nor Zstd — and therefore
///         supercompression global data, which is written as absent and refused on read. A build that
///         wants smaller bundles compresses the chunk the texture lives in, which
///         [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) already does per bundle.
///     </para>
///     <para>
///         <b>What has not been done:</b> validated against an independent KTX2 implementation. The
///         layout here is written from the specification and checked byte-for-byte against a
///         hand-computed file in the tests, which catches a misread of the spec but not a
///         misunderstanding of it. Running the Khronos <c>ktx validate</c> tool over what this writes
///         is an owed step, and until it has been run, "valid KTX2" is a claim about intent.
///     </para>
/// </remarks>
public static class Ktx2 {
    /// <summary>The twelve bytes every KTX2 file starts with.</summary>
    public static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>How long the header is, up to and including the supercompression global data pointers.</summary>
    public const int HeaderLength = 80;

    /// <summary>How long one level index entry is.</summary>
    public const int LevelIndexEntryLength = 24;

    /// <summary>Writes a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="Ktx2Exception">The format has no Vulkan number this knows.</exception>
    public static byte[] Write(TextureData texture) {
        ArgumentNullException.ThrowIfNull(texture);

        var vkFormat = VkFormats.From(texture.Format);
        var descriptor = DataFormatDescriptor.Build(texture.Format);

        var levelIndexOffset = HeaderLength + (Identifier.Length - Identifier.Length);
        var indexLength = texture.LevelCount * LevelIndexEntryLength;
        var descriptorOffset = HeaderLength + indexLength;
        var levelDataOffset = descriptorOffset + descriptor.Length;

        var file = new byte[levelDataOffset + texture.ByteLength];
        var span = file.AsSpan();

        Identifier.CopyTo(span);
        var header = span[Identifier.Length..];

        BinaryPrimitives.WriteUInt32LittleEndian(header, vkFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], TypeSizeOf(texture.Format));
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)texture.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)texture.Height);

        // Zero rather than one for a 2D texture: the specification says pixelDepth is 0 when the
        // texture is not 3D, and a reader that saw 1 would build a 3D texture one slice deep.
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], texture.Depth > 1 ? (uint)texture.Depth : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], texture.LayerCount > 1 ? (uint)texture.LayerCount : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)texture.FaceCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)texture.LevelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)descriptorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[52..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[60..], 0);

        // Smallest level first in the data, largest first in the index.
        var cursor = levelDataOffset + texture.ByteLength;

        for (var level = 0; level < texture.LevelCount; level++) {
            var described = texture.Levels[level];
            cursor -= described.Length;

            var entry = span[(levelIndexOffset + (level * LevelIndexEntryLength))..];
            BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)cursor);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)described.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], (ulong)described.Length);

            texture.Level(level).CopyTo(span[cursor..]);
        }

        descriptor.CopyTo(span[descriptorOffset..]);
        return file;
    }

    /// <summary>Reads a texture.</summary>
    /// <param name="file">The file's bytes.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="Ktx2Exception">It is not KTX2, or uses something this does not implement.</exception>
    public static TextureData Read(ReadOnlySpan<byte> file) {
        var layout = ReadLayout(file);
        var texture = layout.Allocate(0);

        for (var level = 0; level < layout.LevelCount; level++) {
            var described = layout.Levels[level];

            if (described.Offset + described.Length > file.Length) {
                throw new Ktx2Exception($"Level {level} points outside the file.");
            }

            file.Slice((int)described.Offset, (int)described.Length).CopyTo(texture.LevelSpan(level));
        }

        return texture;
    }

    /// <summary>How many bytes of the front of a file <see cref="ReadLayout" /> needs.</summary>
    /// <param name="head">At least <see cref="HeaderLength" /> bytes of the file.</param>
    /// <returns>The header plus the level index.</returns>
    /// <remarks>
    ///     Two reads rather than one, because the level index's length is a header field: a caller
    ///     streaming from a network or an archive reads <see cref="HeaderLength" /> bytes, asks this,
    ///     and reads the rest. A caller that already has the whole file passes the whole file to
    ///     <see cref="ReadLayout" /> and never calls this.
    /// </remarks>
    /// <exception cref="Ktx2Exception">It is not KTX2.</exception>
    public static int LayoutLength(ReadOnlySpan<byte> head) {
        if (head.Length < HeaderLength || !head[..Identifier.Length].SequenceEqual(Identifier)) {
            throw new Ktx2Exception("This is not a KTX2 file: the twelve-byte identifier does not match.");
        }

        var levelCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(head[(Identifier.Length + 28)..]));

        return HeaderLength + (levelCount * LevelIndexEntryLength);
    }

    /// <summary>Reads what a file says about itself, without reading a pixel of it.</summary>
    /// <param name="file">The file's bytes, or at least <see cref="LayoutLength" /> of the front of it.</param>
    /// <returns>The layout.</returns>
    /// <remarks>
    ///     ⚠ <b>This validates the level index against the format and the extents, and not against
    ///     the file's length.</b> A caller holding only the head has no length to check against, and
    ///     one holding the whole file gets the check from <see cref="Read" /> and
    ///     <see cref="ReadTail" /> where the copy happens. What is checked here is the part a short
    ///     read cannot excuse: a level whose declared size disagrees with what its extent and format
    ///     imply is a corrupt file however much of it arrived.
    /// </remarks>
    /// <exception cref="Ktx2Exception">It is not KTX2, or uses something this does not implement.</exception>
    public static Ktx2Layout ReadLayout(ReadOnlySpan<byte> file) {
        if (file.Length < HeaderLength || !file[..Identifier.Length].SequenceEqual(Identifier)) {
            throw new Ktx2Exception("This is not a KTX2 file: the twelve-byte identifier does not match.");
        }

        var header = file[Identifier.Length..];
        var vkFormat = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var depth = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]));
        var layerCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]));
        var faceCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..]));
        var levelCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(header[28..]));
        var supercompression = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);

        if (supercompression != 0) {
            throw new Ktx2Exception(
                $"This file uses supercompression scheme {supercompression}, which is not implemented. A Vixen "
                + "build compresses the bundle chunk a texture lives in instead."
            );
        }

        var indexEnd = HeaderLength + (levelCount * LevelIndexEntryLength);

        if (file.Length < indexEnd) {
            throw new Ktx2Exception(
                $"The level index of a {levelCount}-level file runs to byte {indexEnd} and only {file.Length} "
                + "were given."
            );
        }

        var format = VkFormats.To(vkFormat);
        var levels = new Ktx2Level[levelCount];

        for (var level = 0; level < levelCount; level++) {
            var entry = file[(HeaderLength + (level * LevelIndexEntryLength))..];
            var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var length = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);

            var (levelWidth, levelHeight, levelDepth) = MipChain.ExtentOf(width, height, depth, level);
            var expected = format.LevelSize(levelWidth, levelHeight, levelDepth) * layerCount * faceCount;

            if (length != expected) {
                throw new Ktx2Exception(
                    $"Level {level} says it is {length} bytes; a {format} texture of "
                    + $"{levelWidth}x{levelHeight} is {expected}."
                );
            }

            if (offset < indexEnd) {
                throw new Ktx2Exception($"Level {level} points outside the file.");
            }

            levels[level] = new(level, levelWidth, levelHeight, levelDepth, offset, length);
        }

        return new(format, width, height, depth, layerCount, faceCount, levels);
    }

    /// <summary>Reads a mip tail: every level from one down to the smallest, and nothing above it.</summary>
    /// <param name="file">The file's bytes, or at least its head and the tail's range.</param>
    /// <param name="layout">What <see cref="ReadLayout" /> said about it.</param>
    /// <param name="firstLevel">The largest level to read. Zero is the whole texture.</param>
    /// <returns>A texture of the tail's extent, with the tail's levels in it.</returns>
    /// <remarks>
    ///     The result is a complete smaller texture and not a larger one with holes — see
    ///     <see cref="Ktx2Layout.Allocate" /> — so its level <c>0</c> is the file's
    ///     <paramref name="firstLevel" />.
    /// </remarks>
    /// <exception cref="Ktx2Exception">The tail runs past the end of what was given.</exception>
    public static TextureData ReadTail(ReadOnlySpan<byte> file, Ktx2Layout layout, int firstLevel) {
        ArgumentNullException.ThrowIfNull(layout);

        var texture = layout.Allocate(firstLevel);

        for (var level = firstLevel; level < layout.LevelCount; level++) {
            var described = layout.Levels[level];

            if (described.Offset + described.Length > file.Length) {
                throw new Ktx2Exception($"Level {level} points outside the file.");
            }

            file.Slice((int)described.Offset, (int)described.Length).CopyTo(texture.LevelSpan(level - firstLevel));
        }

        return texture;
    }

    /// <summary>Reads what a stream's file says about itself, without reading a pixel of it.</summary>
    /// <param name="stream">The file, positioned at its start.</param>
    /// <param name="cancellation">Cancels the reads.</param>
    /// <returns>The layout.</returns>
    /// <remarks>
    ///     Sequential, so it works on a stream that cannot seek — the header and the level index are
    ///     the front of the file. <see cref="ReadTailAsync" /> and <see cref="ReadLevelAsync" /> seek
    ///     and say so.
    /// </remarks>
    /// <exception cref="Ktx2Exception">It is not KTX2, or the stream ended inside the index.</exception>
    public static async ValueTask<Ktx2Layout> ReadLayoutAsync(Stream stream, CancellationToken cancellation = default) {
        ArgumentNullException.ThrowIfNull(stream);

        var head = new byte[HeaderLength];
        await Fill(stream, head, cancellation).ConfigureAwait(false);

        var length = LayoutLength(head);

        if (length == HeaderLength) {
            return ReadLayout(head);
        }

        var full = new byte[length];
        head.CopyTo(full, 0);

        await Fill(stream, full.AsMemory(HeaderLength), cancellation).ConfigureAwait(false);

        return ReadLayout(full);
    }

    /// <summary>Reads a mip tail out of a stream, touching only the bytes it needs.</summary>
    /// <param name="stream">The file. It must seek.</param>
    /// <param name="layout">What <see cref="ReadLayoutAsync" /> said about it.</param>
    /// <param name="firstLevel">The largest level to read. Zero is the whole texture.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>A texture of the tail's extent, with the tail's levels in it.</returns>
    /// <remarks>
    ///     One seek and one read whatever the level, because the file stores levels smallest first
    ///     and a tail is therefore contiguous from <see cref="Ktx2Layout.DataOffset" />.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="stream" /> cannot seek.</exception>
    /// <exception cref="Ktx2Exception">The stream ended inside the tail.</exception>
    public static async ValueTask<TextureData> ReadTailAsync(
        Stream stream,
        Ktx2Layout layout,
        int firstLevel,
        CancellationToken cancellation = default
    ) {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(layout);

        if (!stream.CanSeek) {
            throw new ArgumentException(
                "A mip tail is read by seeking past the header to the level data, so the stream has to seek. "
                + "Read the whole file into memory and use ReadTail if it cannot.",
                nameof(stream)
            );
        }

        var run = new byte[layout.TailLength(firstLevel)];

        stream.Seek(layout.DataOffset, SeekOrigin.Begin);
        await Fill(stream, run, cancellation).ConfigureAwait(false);

        var texture = layout.Allocate(firstLevel);

        for (var level = firstLevel; level < layout.LevelCount; level++) {
            var described = layout.Levels[level];
            var at = (int)(described.Offset - layout.DataOffset);

            run.AsSpan(at, (int)described.Length).CopyTo(texture.LevelSpan(level - firstLevel));
        }

        return texture;
    }

    /// <summary>Reads one level out of a stream, touching only the bytes it needs.</summary>
    /// <param name="stream">The file. It must seek.</param>
    /// <param name="layout">What <see cref="ReadLayoutAsync" /> said about it.</param>
    /// <param name="level">Which level.</param>
    /// <param name="destination">Where to put it; at least the level's length.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>How many bytes were read.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="stream" /> cannot seek, or <paramref name="destination" /> is too small.
    /// </exception>
    /// <exception cref="Ktx2Exception">The stream ended inside the level.</exception>
    public static async ValueTask<int> ReadLevelAsync(
        Stream stream,
        Ktx2Layout layout,
        int level,
        Memory<byte> destination,
        CancellationToken cancellation = default
    ) {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(layout);

        if (!stream.CanSeek) {
            throw new ArgumentException("Reading one level of a file means seeking to it.", nameof(stream));
        }

        var described = layout.Levels[level];

        if (destination.Length < described.Length) {
            throw new ArgumentException(
                $"Level {level} is {described.Length} bytes and {destination.Length} were offered.",
                nameof(destination)
            );
        }

        stream.Seek(described.Offset, SeekOrigin.Begin);
        await Fill(stream, destination[..(int)described.Length], cancellation).ConfigureAwait(false);

        return (int)described.Length;
    }

    /// <summary>Reads exactly as many bytes as the buffer holds, or says the file was short.</summary>
    static async ValueTask Fill(Stream stream, Memory<byte> buffer, CancellationToken cancellation) {
        var read = 0;

        while (read < buffer.Length) {
            var got = await stream.ReadAsync(buffer[read..], cancellation).ConfigureAwait(false);

            if (got == 0) {
                throw new Ktx2Exception(
                    $"The file ended after {read} of the {buffer.Length} bytes this read wanted."
                );
            }

            read += got;
        }
    }

    /// <summary>
    ///     A texture's Vulkan <c>typeSize</c>: how many bytes one channel of one texel is, or one for
    ///     anything block-compressed.
    /// </summary>
    static uint TypeSizeOf(PixelFormat format) =>
        format.IsCompressed()
            ? 1u
            : format switch {
                PixelFormat.Rgba16Float or PixelFormat.Rg16Float or PixelFormat.R16Float
                    or PixelFormat.Rgba16UNorm => 2u,
                PixelFormat.Rgba32Float or PixelFormat.Rg32Float or PixelFormat.R32Float
                    or PixelFormat.Rgba32UInt or PixelFormat.R32UInt => 4u,
                _ => 1u
            };
}
