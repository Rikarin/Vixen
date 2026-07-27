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
        if (file.Length < HeaderLength || !file[..Identifier.Length].SequenceEqual(Identifier)) {
            throw new Ktx2Exception("This is not a KTX2 file: the twelve-byte identifier does not match.");
        }

        var header = file[Identifier.Length..];
        var vkFormat = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var depth = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var layerCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var faceCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        var levelCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        var supercompression = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);

        if (supercompression != 0) {
            throw new Ktx2Exception(
                $"This file uses supercompression scheme {supercompression}, which is not implemented. A Vixen "
                + "build compresses the bundle chunk a texture lives in instead."
            );
        }

        var texture = new TextureData(
            VkFormats.To(vkFormat),
            width,
            height,
            Math.Max(1, levelCount),
            Math.Max(1, depth),
            Math.Max(1, layerCount),
            Math.Max(1, faceCount)
        );

        for (var level = 0; level < texture.LevelCount; level++) {
            var entry = file[(HeaderLength + (level * LevelIndexEntryLength))..];
            var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var length = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var expected = texture.Levels[level].Length;

            if (length != expected) {
                throw new Ktx2Exception(
                    $"Level {level} says it is {length} bytes; a {texture.Format} texture of "
                    + $"{texture.Levels[level].Width}x{texture.Levels[level].Height} is {expected}."
                );
            }

            if (offset < 0 || offset + length > file.Length) {
                throw new Ktx2Exception($"Level {level} points outside the file.");
            }

            file.Slice((int)offset, (int)length).CopyTo(texture.LevelSpan(level));
        }

        return texture;
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
