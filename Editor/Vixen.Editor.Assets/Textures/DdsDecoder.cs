// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.Editor.Assets.Textures;

/// <summary>Reads a DDS, which is mostly a header over block-compressed data the engine already speaks.</summary>
/// <remarks>
///     <para>
///         <b>Doc 01 names Pfim for this and it turned out not to be needed.</b> The plan's
///         dependency table has carried a row for Pfim (MIT, DDS/TGA) since it was written, and
///         neither half of that row survives contact with what got built: TGA has been read by
///         <see cref="StbImageDecoder" /> since the ImageSharp swap, and DDS in this engine is a
///         <i>container</i> rather than a codec. The payload a game ships in one is BCn, and
///         <c>Vixen.Core.Imaging</c> has understood BC1 through BC7 and BC6H for as long as
///         <c>Ktx2</c> has. What was missing was header parsing and a format table — not a decoder,
///         and not a package. Adding one would have bought a dependency, a licence line and a
///         restore-graph change to do work the repository can already do.
///     </para>
///     <para>
///         <b>Which way up.</b> DDS has no origin flag: row zero is the top, in every file the
///         format has ever described. That is already this pipeline's order — <c>MinimalPng</c>
///         writes its rows top first and <c>TextureImporter</c> ships them unchanged, and
///         <c>SpriteRect.Y</c> is measured down from the top of a sheet — so the bytes are copied
///         straight through and nothing here flips anything. The engine's clip-space convention
///         (y = +1 is the top, and the screen helpers negate y) is a statement about vertices,
///         settled long after a texture is uploaded, and does not reach the import.
///     </para>
///     <para>
///         <b>What it claims.</b> A plain 2D texture: one array element, one face. A compressed
///         payload passes through with its whole mip chain and its own format, so a BC7 file an
///         artist encoded with a better tool than <c>BlockCompressor</c> ships exactly as it
///         arrived — <i>including</i> whether its header said sRGB. An uncompressed payload comes
///         back as level zero in <c>Rgba8UNorm</c>, which is what <see cref="StbImageDecoder" />
///         returns for a PNG and what <c>TextureImporter</c>'s uncompressed path is written
///         against; the mip chain is regenerated and the sRGB decision is the settings', exactly as
///         it is for a PNG.
///     </para>
///     <para>
///         <b>What it refuses, by name and out loud: cube maps, texture arrays, volumes, and every
///         uncompressed format wider than eight bits a channel.</b> <c>VideoImporter</c>'s
///         precedent, for its reason — an artist who drops a file in and finds it silently became
///         something else has learned nothing. The first three have a concrete reason as well: DDS
///         orders a cube map and an array element-major (every mip of face 0, then every mip of face
///         1) and <see cref="TextureData" /> and KTX2 order them level-major. Half-reading one does
///         not give a slightly wrong texture, it gives six faces interleaved into the wrong levels,
///         and that transpose is the work — so it waits for something that needs it instead of being
///         guessed at.
///     </para>
/// </remarks>
public sealed class DdsDecoder : IImageDecoder {
    const uint Magic = 0x20534444;          // 'DDS ', little-endian.
    const uint Dx10FourCc = 0x30315844;     // 'DX10', little-endian.
    const int HeaderLength = 124;
    const int PixelFormatOffset = 0x4C;
    const int ExtensionOffset = 128;
    const int ExtensionLength = 20;

    const uint FourCcFlag = 0x4;
    const uint RgbFlag = 0x40;
    const uint LuminanceFlag = 0x20000;
    const uint AlphaPixelsFlag = 0x1;

    const uint CubeMapCap = 0x200;
    const uint VolumeCap = 0x200000;
    const uint TextureCubeMisc = 0x4;

    /// <summary>The extension it reads.</summary>
    public const string Extension = ".dds";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [Extension];

    /// <inheritdoc />
    public TextureData Decode(Stream stream, string extension) {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>Reads a DDS file's bytes.</summary>
    /// <param name="file">The whole file.</param>
    /// <returns>The texture: a compressed one with its mip chain, or level zero as <c>Rgba8UNorm</c>.</returns>
    /// <exception cref="InvalidDataException">It is not a DDS, or its header contradicts its length.</exception>
    /// <exception cref="NotSupportedException">It is a shape or a format this does not claim.</exception>
    public static TextureData Read(ReadOnlySpan<byte> file) {
        if (file.Length < 4 + HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic) {
            throw new InvalidDataException(
                "This does not start with DDS's four-byte magic, so it is not a DDS whatever it is called."
            );
        }

        var header = file[4..];

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != HeaderLength) {
            throw new InvalidDataException(
                $"A DDS header states its own size and it has to be {HeaderLength}. This one does not, which "
                + "means the file is truncated or is another format wearing the magic."
            );
        }

        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var depth = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var declaredLevels = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(header[0x6C..]);

        var pixelFormat = file[PixelFormatOffset..];
        var pixelFormatFlags = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[4..]);
        var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[8..]);

        SourceFormat source;
        ReadOnlySpan<byte> payload;

        if ((pixelFormatFlags & FourCcFlag) != 0 && fourCc == Dx10FourCc) {
            if (file.Length < ExtensionOffset + ExtensionLength) {
                throw new InvalidDataException(
                    "The pixel format says DX10, so a twenty-byte extension header follows it — and this file "
                    + "ends first."
                );
            }

            var extension = file[ExtensionOffset..];

            Refuse(
                (BinaryPrimitives.ReadUInt32LittleEndian(extension[8..]) & TextureCubeMisc) != 0,
                "a cube map",
                "its extension header sets D3D10_RESOURCE_MISC_TEXTURECUBE"
            );

            var arraySize = BinaryPrimitives.ReadUInt32LittleEndian(extension[12..]);
            Refuse(arraySize > 1, "a texture array", $"its extension header says arraySize is {arraySize}");

            Refuse(
                BinaryPrimitives.ReadUInt32LittleEndian(extension[4..]) == 4,
                "a volume texture",
                "its extension header says the resource dimension is 3D"
            );

            source = FormatOf(BinaryPrimitives.ReadUInt32LittleEndian(extension));
            payload = extension[ExtensionLength..];
        } else {
            source = LegacyFormatOf(pixelFormatFlags, fourCc, pixelFormat);
            payload = file[ExtensionOffset..];
        }

        Refuse((caps2 & CubeMapCap) != 0, "a cube map", "its caps2 field sets DDSCAPS2_CUBEMAP");
        Refuse((caps2 & VolumeCap) != 0, "a volume texture", "its caps2 field sets DDSCAPS2_VOLUME");
        Refuse(depth > 1, "a volume texture", $"its header says the depth is {depth}");

        if (width < 1 || height < 1) {
            throw new InvalidDataException($"The header says this is {width}×{height}, which is not a picture.");
        }

        return source.Layout is null
            ? Compressed(source.Format, width, height, declaredLevels, payload)
            : Uncompressed(source.Layout.Value, width, height, payload);
    }

    /// <summary>Takes the blocks as they are, mip chain and all.</summary>
    static TextureData Compressed(
        PixelFormat format,
        int width,
        int height,
        int declaredLevels,
        ReadOnlySpan<byte> payload
    ) {
        // Zero and one both mean "just the top level". The mip-count field is only meaningful when
        // DDSD_MIPMAPCOUNT is set, and a writer that leaves the flag off leaves the field at zero.
        var levels = Math.Max(1, declaredLevels);
        var possible = PixelFormats.MipLevelCount(width, height, 1);

        if (levels > possible) {
            throw new InvalidDataException(
                $"The header claims {levels} mip levels, and a {width}×{height} texture only has {possible}."
            );
        }

        var texture = new TextureData(format, width, height, levels);

        if (payload.Length < texture.ByteLength) {
            throw new InvalidDataException(
                $"The header describes {texture.ByteLength} bytes of {format} and only {payload.Length} follow "
                + "it. A DDS whose mip chain stops early is a file something truncated."
            );
        }

        // ⚠ Straight through, with no flip and no re-ordering. DDS is top-row-first by definition,
        // which is already this pipeline's order; and every level of a 2D single-layer texture is
        // contiguous and in the same sequence in both layouts, which is only true because arrays and
        // cube maps were refused above.
        payload[..texture.ByteLength].CopyTo(texture.PixelSpan());

        return texture;
    }

    /// <summary>Widens level zero to <c>Rgba8UNorm</c>, which is what the pipeline is written against.</summary>
    /// <remarks>
    ///     Level zero only, deliberately. <c>TextureImporter</c> regenerates the chain through the
    ///     filter that knows what the bytes mean — a normal map's mips come back unit length and a
    ///     colour texture's are averaged in light — and a chain some exporter built with a box filter
    ///     in the wrong space is not worth preferring to that. A <i>compressed</i> chain is different
    ///     and is kept: re-encoding it would lose more than the filter gains.
    /// </remarks>
    static TextureData Uncompressed(ChannelLayout layout, int width, int height, ReadOnlySpan<byte> payload) {
        var stride = width * layout.BytesPerPixel;
        var needed = stride * height;

        if (payload.Length < needed) {
            throw new InvalidDataException(
                $"The header describes {needed} bytes of {layout.BytesPerPixel * 8}-bit pixels and only "
                + $"{payload.Length} follow it."
            );
        }

        var texture = new TextureData(PixelFormat.Rgba8UNorm, width, height, levelCount: 1);
        var pixels = texture.PixelSpan();

        for (var texel = 0; texel < width * height; texel++) {
            var from = texel * layout.BytesPerPixel;
            var into = texel * 4;

            pixels[into] = payload[from + layout.Red];
            pixels[into + 1] = layout.Green < 0 ? (byte)0 : payload[from + layout.Green];
            pixels[into + 2] = layout.Blue < 0 ? (byte)0 : payload[from + layout.Blue];
            pixels[into + 3] = layout.Alpha < 0 ? (byte)255 : payload[from + layout.Alpha];
        }

        return texture;
    }

    static void Refuse(bool condition, string what, string how) {
        if (condition) {
            throw new NotSupportedException(
                $"This is {what} — {how} — and DdsDecoder reads a plain 2D texture. DDS stores a cube map and "
                + "an array element-major, one whole mip chain per face, where KTX2 stores them level-major; "
                + "reading one as the other silently interleaves the faces into the wrong levels. Convert it "
                + "to .ktx2, which the pipeline passes through untouched."
            );
        }
    }

    /// <summary>Which channel of an eight-bit-a-channel source ends up where, by byte offset.</summary>
    /// <param name="BytesPerPixel">How wide one source pixel is.</param>
    /// <param name="Red">Where red is, in bytes from the start of the pixel.</param>
    /// <param name="Green">Where green is, or -1 for none.</param>
    /// <param name="Blue">Where blue is, or -1 for none.</param>
    /// <param name="Alpha">Where alpha is, or -1 for opaque.</param>
    readonly record struct ChannelLayout(int BytesPerPixel, int Red, int Green, int Blue, int Alpha);

    /// <summary>A compressed format to pass through, or a layout to widen.</summary>
    readonly record struct SourceFormat(PixelFormat Format, ChannelLayout? Layout) {
        public static SourceFormat Blocks(PixelFormat format) => new(format, null);
        public static SourceFormat Channels(int bytes, int red, int green, int blue, int alpha) =>
            new(PixelFormat.Rgba8UNorm, new ChannelLayout(bytes, red, green, blue, alpha));
    }

    /// <summary>Maps a DXGI format number to what this engine calls it.</summary>
    /// <remarks>
    ///     <para>
    ///         Only the formats there is something to do with. BC2 is the notable absentee and is
    ///         refused by name rather than quietly read as BC3: the two have the same block size and
    ///         the same colour half, so a mislabelled BC2 decodes to a picture with garbage alpha —
    ///         which looks like a texture with a bad mask rather than like a bug in an importer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>_SRGB</c> variants are kept distinct, and that is the whole sRGB decision
    ///         for a compressed file.</b> A compressed payload passes through <c>TextureImporter</c>
    ///         untouched, so nothing downstream gets a second chance to label it: mapping
    ///         <c>BC7_UNORM_SRGB</c> onto <c>Bc7RgbaUNorm</c> would ship an albedo the hardware never
    ///         applies the transfer function to, which is the "washed out and nothing in the log"
    ///         failure the importer's own tests were written for.
    ///     </para>
    /// </remarks>
    static SourceFormat FormatOf(uint dxgi) =>
        dxgi switch {
            71 or 70 => SourceFormat.Blocks(PixelFormat.Bc1RgbaUNorm),      // BC1_UNORM, BC1_TYPELESS
            72 => SourceFormat.Blocks(PixelFormat.Bc1RgbaUNormSrgb),        // BC1_UNORM_SRGB
            77 or 76 => SourceFormat.Blocks(PixelFormat.Bc3RgbaUNorm),      // BC3_UNORM, BC3_TYPELESS
            78 => SourceFormat.Blocks(PixelFormat.Bc3RgbaUNormSrgb),        // BC3_UNORM_SRGB
            80 or 79 => SourceFormat.Blocks(PixelFormat.Bc4RUNorm),         // BC4_UNORM, BC4_TYPELESS
            83 or 82 => SourceFormat.Blocks(PixelFormat.Bc5RgUNorm),        // BC5_UNORM, BC5_TYPELESS
            95 or 94 => SourceFormat.Blocks(PixelFormat.Bc6HRgbUFloat),     // BC6H_UF16, BC6H_TYPELESS
            98 or 97 => SourceFormat.Blocks(PixelFormat.Bc7RgbaUNorm),      // BC7_UNORM, BC7_TYPELESS
            99 => SourceFormat.Blocks(PixelFormat.Bc7RgbaUNormSrgb),        // BC7_UNORM_SRGB

            // R8G8B8A8 and its sRGB twin; B8G8R8A8, B8G8R8X8 and theirs. The sRGB-ness is dropped
            // here on purpose — see Uncompressed, and TextureImporter's uncompressed path, where the
            // format is rebuilt from TextureContent exactly as it is for a PNG.
            28 or 29 => SourceFormat.Channels(4, 0, 1, 2, 3),
            87 or 91 => SourceFormat.Channels(4, 2, 1, 0, 3),
            88 or 93 => SourceFormat.Channels(4, 2, 1, 0, -1),
            61 => SourceFormat.Channels(1, 0, -1, -1, -1),                  // R8_UNORM
            49 => SourceFormat.Channels(2, 0, 1, -1, -1),                   // R8G8_UNORM

            74 or 73 or 75 => throw Unsupported(
                "BC2",
                "the engine has no BC2 format — it is BC3's colour half with four-bit explicit alpha, which "
                + "nothing has authored this century"
            ),
            81 => throw Unsupported("BC4_SNORM", "the engine's BC4 is the unsigned one"),
            84 or 85 => throw Unsupported("BC5_SNORM", "the engine's BC5 is the unsigned one"),
            96 => throw Unsupported("BC6H_SF16", "the engine's BC6H is the unsigned one"),
            2 or 10 or 11 => throw Unsupported(
                "an uncompressed high-dynamic-range surface",
                "this widens eight-bit channels and would have to narrow those, which is the one thing a "
                + "high-range image exists to avoid — BC6H is read, and so is .hdr"
            ),
            _ => throw Unsupported($"DXGI format {dxgi}", "this engine has no PixelFormat for it")
        };

    /// <summary>Maps a pre-D3D10 pixel format block to what this engine calls it.</summary>
    /// <remarks>
    ///     Every DDS written before D3D10, and most written since, describes itself this way: a
    ///     four-character code for a compressed format, or a set of channel bit masks. The masks are
    ///     matched against the arrangements tools actually write rather than decoded in general,
    ///     because an arbitrary bit-mask shuffle is a swizzle nothing would ever exercise with a file
    ///     that was not hand-made to exercise it.
    /// </remarks>
    static SourceFormat LegacyFormatOf(uint flags, uint fourCc, ReadOnlySpan<byte> pixelFormat) {
        if ((flags & FourCcFlag) != 0) {
            return fourCc switch {
                0x31545844 => SourceFormat.Blocks(PixelFormat.Bc1RgbaUNorm),                    // 'DXT1'
                0x35545844 or 0x34545844 => SourceFormat.Blocks(PixelFormat.Bc3RgbaUNorm),      // 'DXT5', 'DXT4'
                0x31495441 or 0x55344342 => SourceFormat.Blocks(PixelFormat.Bc4RUNorm),         // 'ATI1', 'BC4U'
                0x32495441 or 0x55354342 => SourceFormat.Blocks(PixelFormat.Bc5RgUNorm),        // 'ATI2', 'BC5U'
                0x33545844 or 0x32545844 => throw Unsupported(                                  // 'DXT3', 'DXT2'
                    "DXT2 or DXT3",
                    "the engine has no BC2 format, and DXT2's colour is premultiplied besides"
                ),
                _ => throw Unsupported(
                    $"the four-character code 0x{fourCc:X8}",
                    "this engine has no PixelFormat for it"
                )
            };
        }

        if ((flags & (RgbFlag | LuminanceFlag)) == 0) {
            throw Unsupported(
                "a pixel format that is neither a four-character code nor RGB nor luminance",
                "there is nothing in it to read"
            );
        }

        var bitCount = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[12..]);
        var red = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[16..]);
        var green = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[20..]);
        var blue = BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[24..]);
        var alpha = (flags & AlphaPixelsFlag) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[28..]) : 0u;

        return (bitCount, red, green, blue, alpha) switch {
            (32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000) => SourceFormat.Channels(4, 2, 1, 0, 3),
            (32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0) => SourceFormat.Channels(4, 2, 1, 0, -1),
            (32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000) => SourceFormat.Channels(4, 0, 1, 2, 3),
            (32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0) => SourceFormat.Channels(4, 0, 1, 2, -1),
            (24, 0x00FF0000, 0x0000FF00, 0x000000FF, 0) => SourceFormat.Channels(3, 2, 1, 0, -1),
            (24, 0x000000FF, 0x0000FF00, 0x00FF0000, 0) => SourceFormat.Channels(3, 0, 1, 2, -1),
            (8, 0xFF, 0, 0, 0) => SourceFormat.Channels(1, 0, -1, -1, -1),
            (16, 0x00FF, 0xFF00, 0, 0) => SourceFormat.Channels(2, 0, 1, -1, -1),
            _ => throw Unsupported(
                $"a {bitCount}-bit layout with masks {red:X8}/{green:X8}/{blue:X8}/{alpha:X8}",
                "this reads the arrangements tools actually write and does not shuffle arbitrary masks"
            )
        };
    }

    static NotSupportedException Unsupported(string what, string why) =>
        new(
            $"This DDS is {what}, and {why}. Re-export it as BC7, BC5 or eight-bit RGBA, or convert it to "
            + ".ktx2 — the pipeline passes a compressed .ktx2 through untouched."
        );
}
