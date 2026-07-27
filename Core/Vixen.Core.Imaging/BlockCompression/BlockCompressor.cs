// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Graphics;

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>Encodes a texture into a block-compressed format, and back for inspection.</summary>
/// <remarks>
///     <para>
///         <b>This is build-time code.</b> The runtime never decodes a block: a shipped texture is
///         already in the format the GPU samples, and loading it is a header parse and an upload.
///         <see cref="Decode" /> exists so that an editor can show what a compressed texture will
///         actually look like, and so that these encoders can be tested against something other than
///         themselves.
///     </para>
///     <para>
///         <b>What is here is BC, and only BC.</b> ASTC and ETC2 are not, and are not coming in
///         managed code — [03](../../../docs/plan/03-core-foundation.md) says so and gives the
///         reason: ASTC encoding is measured in minutes per gigabyte outside a vectorised native
///         encoder, and <c>astcenc</c> is already in doc 01's dependency register for exactly this.
///         Both formats are carried through <see cref="TextureData" /> and <see cref="Ktx2" /> so
///         that a build which has that encoder can ship them.
///     </para>
///     <para>
///         <b>What quality to expect.</b> BC1, BC3, BC4 and BC5 here are complete encoders: they fit
///         the principal axis of the block's own colours and refine the endpoints by least squares,
///         which is what a production BCn encoder does, and the formats have no modes left to choose
///         between. BC7 and BC6H each write one mode of eight and fourteen — see
///         <see cref="Bc7Block" /> and <see cref="Bc6HBlock" /> — which is a real quality ceiling on
///         blocks with an edge running through them, and is stated where each one is written.
///     </para>
/// </remarks>
public static class BlockCompressor {
    /// <summary>The half-float bit pattern for one.</summary>
    const ushort OneAsHalf = 0x3C00;

    /// <summary>Whether this can produce that format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether <see cref="Encode" /> accepts it as a target.</returns>
    public static bool CanEncode(PixelFormat format) =>
        format.ToLinear() is PixelFormat.Bc1RgbaUNorm or PixelFormat.Bc3RgbaUNorm
            or PixelFormat.Bc4RUNorm or PixelFormat.Bc5RgUNorm or PixelFormat.Bc7RgbaUNorm
            or PixelFormat.Bc6HRgbUFloat;

    /// <summary>Whether the format holds high dynamic range, and so is encoded from float rather than bytes.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether it is <see cref="PixelFormat.Bc6HRgbUFloat" />.</returns>
    public static bool IsHighDynamicRange(PixelFormat format) => format == PixelFormat.Bc6HRgbUFloat;

    /// <summary>Compresses every level of a texture.</summary>
    /// <param name="source">
    ///     The texture. Eight-bit uncompressed for every format but BC6H, which takes
    ///     <see cref="PixelFormat.Rgba16Float" /> or <see cref="PixelFormat.Rgba32Float" />.
    /// </param>
    /// <param name="target">What to compress it to.</param>
    /// <returns>A new texture, in <paramref name="target" />.</returns>
    /// <exception cref="NotSupportedException">The source or the target format is not one of these.</exception>
    /// <exception cref="ArgumentException">The source is sRGB and the target is not, or the other way round.</exception>
    public static TextureData Encode(TextureData source, PixelFormat target) {
        ArgumentNullException.ThrowIfNull(source);

        if (!CanEncode(target)) {
            throw new NotSupportedException(
                $"{target} is not a format this encodes. BC1, BC3, BC4, BC5, BC6H and BC7 are; ASTC and ETC2 "
                + "need the native encoder doc 03 calls for and doc 01 registers as astcenc."
            );
        }

        if (source.Format.IsSrgb() != target.IsSrgb()) {
            throw new ArgumentException(
                $"A {source.Format} source cannot be encoded as {target}: one is sRGB and the other is not, and "
                + "the hardware would apply — or fail to apply — a transfer function nobody asked for. Name "
                + $"{(source.Format.IsSrgb() ? target.ToSrgb() : target.ToLinear())} instead.",
                nameof(target)
            );
        }

        var destination = new TextureData(
            target,
            source.Width,
            source.Height,
            source.LevelCount,
            source.Depth,
            source.LayerCount,
            source.FaceCount
        );

        Span<byte> rgba = stackalloc byte[Bc1Block.Texels * 4];
        Span<ushort> rgb = stackalloc ushort[Bc6HBlock.Texels * 3];
        var blockBytes = target.BlockSize();

        for (var level = 0; level < source.LevelCount; level++) {
            var extent = ExtentOf(source, level);
            var sourceStride = (int)source.Format.LevelSize(extent.Width, extent.Height);
            var targetStride = (int)target.LevelSize(extent.Width, extent.Height);

            for (var image = 0; image < extent.Images; image++) {
                var from = source.Level(level).Slice(image * sourceStride, sourceStride);
                var to = destination.LevelSpan(level).Slice(image * targetStride, targetStride);

                for (var row = 0; row < extent.Rows; row++) {
                    for (var column = 0; column < extent.Columns; column++) {
                        var block = to.Slice(((row * extent.Columns) + column) * blockBytes, blockBytes);

                        if (IsHighDynamicRange(target)) {
                            GatherHdr(from, source.Format, extent, column * 4, row * 4, rgb);
                            Bc6HBlock.Encode(rgb, block);
                            continue;
                        }

                        Gather(from, source.Format, extent, column * 4, row * 4, rgba);
                        EncodeBlock(target, rgba, block);
                    }
                }
            }
        }

        return destination;
    }

    /// <summary>Decompresses every level of a texture, for inspection.</summary>
    /// <param name="source">The texture, in a block-compressed format.</param>
    /// <returns>
    ///     A new texture: <see cref="PixelFormat.Rgba16Float" /> for BC6H, and
    ///     <see cref="PixelFormat.Rgba8UNorm" /> or its sRGB form for everything else.
    /// </returns>
    /// <exception cref="NotSupportedException">The format is not one of these.</exception>
    public static TextureData Decode(TextureData source) {
        ArgumentNullException.ThrowIfNull(source);

        var highDynamicRange = IsHighDynamicRange(source.Format);

        var target = highDynamicRange
            ? PixelFormat.Rgba16Float
            : source.Format.IsSrgb() ? PixelFormat.Rgba8UNormSrgb : PixelFormat.Rgba8UNorm;

        var destination = new TextureData(
            target,
            source.Width,
            source.Height,
            source.LevelCount,
            source.Depth,
            source.LayerCount,
            source.FaceCount
        );

        Span<byte> rgba = stackalloc byte[Bc1Block.Texels * 4];
        Span<ushort> rgb = stackalloc ushort[Bc6HBlock.Texels * 3];
        var blockBytes = source.Format.BlockSize();

        for (var level = 0; level < source.LevelCount; level++) {
            var extent = ExtentOf(source, level);
            var sourceStride = (int)source.Format.LevelSize(extent.Width, extent.Height);
            var targetStride = (int)target.LevelSize(extent.Width, extent.Height);

            for (var image = 0; image < extent.Images; image++) {
                var from = source.Level(level).Slice(image * sourceStride, sourceStride);
                var to = destination.LevelSpan(level).Slice(image * targetStride, targetStride);

                for (var row = 0; row < extent.Rows; row++) {
                    for (var column = 0; column < extent.Columns; column++) {
                        var block = from.Slice(((row * extent.Columns) + column) * blockBytes, blockBytes);

                        if (highDynamicRange) {
                            Bc6HBlock.Decode(block, rgb);
                            ScatterHdr(rgb, extent, column * 4, row * 4, to);
                            continue;
                        }

                        DecodeBlock(source.Format, block, rgba);
                        Scatter(rgba, extent, column * 4, row * 4, to);
                    }
                }
            }
        }

        return destination;
    }

    /// <summary>Compresses one 4×4 block of eight-bit colour.</summary>
    /// <param name="target">The format to write.</param>
    /// <param name="rgba">Sixty-four bytes: sixteen texels of RGBA, row-major.</param>
    /// <param name="block">The block's bytes to fill.</param>
    /// <exception cref="NotSupportedException">The format is not one of these, or is BC6H.</exception>
    public static void EncodeBlock(PixelFormat target, ReadOnlySpan<byte> rgba, Span<byte> block) {
        Span<byte> channel = stackalloc byte[Bc4Block.Texels];

        switch (target.ToLinear()) {
            case PixelFormat.Bc1RgbaUNorm:
                Bc1Block.Encode(rgba, allowAlpha: true, block);
                return;

            case PixelFormat.Bc3RgbaUNorm:
                Extract(rgba, 3, channel);
                Bc4Block.Encode(channel, block);
                Bc1Block.Encode(rgba, allowAlpha: false, block[8..]);
                return;

            case PixelFormat.Bc4RUNorm:
                Extract(rgba, 0, channel);
                Bc4Block.Encode(channel, block);
                return;

            case PixelFormat.Bc5RgUNorm:
                Extract(rgba, 0, channel);
                Bc4Block.Encode(channel, block);
                Extract(rgba, 1, channel);
                Bc4Block.Encode(channel, block[8..]);
                return;

            case PixelFormat.Bc7RgbaUNorm:
                Bc7Block.Encode(rgba, block);
                return;

            case PixelFormat.Bc6HRgbUFloat:
                throw new NotSupportedException(
                    "BC6H holds high dynamic range and cannot be encoded from eight-bit colour; every block "
                    + "would be crushed into the first hundredth of its range. Use EncodeHdrBlock."
                );

            default:
                throw new NotSupportedException($"{target} is not a format this encodes.");
        }
    }

    /// <summary>Compresses one 4×4 block of high dynamic range colour, as BC6H.</summary>
    /// <param name="rgb">Forty-eight half-float bit patterns: sixteen texels of RGB, row-major.</param>
    /// <param name="block">Sixteen bytes to fill.</param>
    public static void EncodeHdrBlock(ReadOnlySpan<ushort> rgb, Span<byte> block) => Bc6HBlock.Encode(rgb, block);

    /// <summary>Decompresses one block.</summary>
    /// <param name="source">The format the block is in.</param>
    /// <param name="block">The block's bytes.</param>
    /// <param name="rgba">Sixty-four bytes to fill: sixteen texels of RGBA, row-major.</param>
    /// <exception cref="NotSupportedException">The format is not one of these, or is BC6H.</exception>
    public static void DecodeBlock(PixelFormat source, ReadOnlySpan<byte> block, Span<byte> rgba) {
        Span<byte> channel = stackalloc byte[Bc4Block.Texels];

        switch (source.ToLinear()) {
            case PixelFormat.Bc1RgbaUNorm:
                Bc1Block.Decode(block, opaque: false, rgba);
                return;

            case PixelFormat.Bc3RgbaUNorm:
                Bc1Block.Decode(block[8..], opaque: true, rgba);
                Bc4Block.Decode(block, channel);
                Insert(channel, 3, rgba);
                return;

            case PixelFormat.Bc4RUNorm:
                Bc4Block.Decode(block, channel);
                Fill(rgba, channel, default, default);
                return;

            case PixelFormat.Bc5RgUNorm: {
                Span<byte> green = stackalloc byte[Bc4Block.Texels];
                Bc4Block.Decode(block, channel);
                Bc4Block.Decode(block[8..], green);
                Fill(rgba, channel, green, default);
                return;
            }

            case PixelFormat.Bc7RgbaUNorm:
                Bc7Block.Decode(block, rgba);
                return;

            case PixelFormat.Bc6HRgbUFloat:
                throw new NotSupportedException(
                    "BC6H decodes to half-float, not to eight-bit colour. Use DecodeHdrBlock."
                );

            default:
                throw new NotSupportedException($"{source} is not a format this decodes.");
        }
    }

    /// <summary>Decompresses one BC6H block.</summary>
    /// <param name="block">Its sixteen bytes.</param>
    /// <param name="rgb">Forty-eight half-float bit patterns to fill.</param>
    public static void DecodeHdrBlock(ReadOnlySpan<byte> block, Span<ushort> rgb) => Bc6HBlock.Decode(block, rgb);

    /// <summary>
    ///     One mip level's 2D shape: how big each image is, how many blocks that is, and how many
    ///     images the level holds. A level of a cube map array holds faces × layers × slices images
    ///     end to end, and that count is the one thing both directions have to agree about.
    /// </summary>
    readonly record struct ImageExtent(int Width, int Height, int Columns, int Rows, int Images);

    static ImageExtent ExtentOf(TextureData texture, int level) {
        var described = texture.Levels[level];

        return new(
            described.Width,
            described.Height,
            (described.Width + 3) / 4,
            (described.Height + 3) / 4,
            texture.LayerCount * texture.FaceCount * described.Depth
        );
    }

    static void Extract(ReadOnlySpan<byte> rgba, int channel, Span<byte> values) {
        for (var texel = 0; texel < Bc4Block.Texels; texel++) {
            values[texel] = rgba[(texel * 4) + channel];
        }
    }

    static void Insert(ReadOnlySpan<byte> values, int channel, Span<byte> rgba) {
        for (var texel = 0; texel < Bc4Block.Texels; texel++) {
            rgba[(texel * 4) + channel] = values[texel];
        }
    }

    /// <summary>
    ///     A one- or two-channel block decodes to red, or red and green, with the channels it does
    ///     not carry set to zero and alpha to opaque — which is what the hardware returns when a
    ///     shader samples a BC4 or BC5 texture.
    /// </summary>
    static void Fill(Span<byte> rgba, ReadOnlySpan<byte> red, ReadOnlySpan<byte> green, ReadOnlySpan<byte> blue) {
        for (var texel = 0; texel < Bc4Block.Texels; texel++) {
            rgba[texel * 4] = red[texel];
            rgba[(texel * 4) + 1] = green.IsEmpty ? (byte)0 : green[texel];
            rgba[(texel * 4) + 2] = blue.IsEmpty ? (byte)0 : blue[texel];
            rgba[(texel * 4) + 3] = 255;
        }
    }

    /// <summary>
    ///     Reads a 4×4 block out of an image, clamping at the edges. A texture whose width is not a
    ///     multiple of four still has whole blocks in the file, and the texels past the edge have to
    ///     be <i>something</i>: repeating the last real one keeps the endpoint fit inside the colours
    ///     the block actually contains, where padding with black would drag it towards a colour no
    ///     texel has.
    /// </summary>
    static void Gather(
        ReadOnlySpan<byte> image,
        PixelFormat format,
        ImageExtent extent,
        int originX,
        int originY,
        Span<byte> rgba
    ) {
        for (var y = 0; y < 4; y++) {
            var sourceY = Math.Min(originY + y, extent.Height - 1);

            for (var x = 0; x < 4; x++) {
                var sourceX = Math.Min(originX + x, extent.Width - 1);
                ReadTexel(image, format, (sourceY * extent.Width) + sourceX, rgba[(((y * 4) + x) * 4)..]);
            }
        }
    }

    static void GatherHdr(
        ReadOnlySpan<byte> image,
        PixelFormat format,
        ImageExtent extent,
        int originX,
        int originY,
        Span<ushort> rgb
    ) {
        for (var y = 0; y < 4; y++) {
            var sourceY = Math.Min(originY + y, extent.Height - 1);

            for (var x = 0; x < 4; x++) {
                var sourceX = Math.Min(originX + x, extent.Width - 1);
                ReadHdrTexel(image, format, (sourceY * extent.Width) + sourceX, rgb[(((y * 4) + x) * 3)..]);
            }
        }
    }

    static void Scatter(ReadOnlySpan<byte> rgba, ImageExtent extent, int originX, int originY, Span<byte> image) {
        for (var y = 0; y < 4 && originY + y < extent.Height; y++) {
            for (var x = 0; x < 4 && originX + x < extent.Width; x++) {
                var texel = ((originY + y) * extent.Width) + originX + x;
                rgba.Slice(((y * 4) + x) * 4, 4).CopyTo(image[(texel * 4)..]);
            }
        }
    }

    static void ScatterHdr(ReadOnlySpan<ushort> rgb, ImageExtent extent, int originX, int originY, Span<byte> image) {
        for (var y = 0; y < 4 && originY + y < extent.Height; y++) {
            for (var x = 0; x < 4 && originX + x < extent.Width; x++) {
                var texel = ((originY + y) * extent.Width) + originX + x;
                var destination = image[(texel * 8)..];

                for (var channel = 0; channel < 3; channel++) {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        destination[(channel * 2)..],
                        rgb[((((y * 4) + x) * 3) + channel)]
                    );
                }

                // Alpha: one, because BC6H has no alpha channel and a zero here would make every
                // decoded preview of an environment map invisible.
                BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], OneAsHalf);
            }
        }
    }

    static void ReadTexel(ReadOnlySpan<byte> image, PixelFormat format, int texel, Span<byte> rgba) {
        switch (format) {
            case PixelFormat.R8UNorm:
                rgba[0] = image[texel];
                rgba[1] = 0;
                rgba[2] = 0;
                rgba[3] = 255;
                return;

            case PixelFormat.Rg8UNorm:
                rgba[0] = image[texel * 2];
                rgba[1] = image[(texel * 2) + 1];
                rgba[2] = 0;
                rgba[3] = 255;
                return;

            case PixelFormat.Rgba8UNorm:
            case PixelFormat.Rgba8UNormSrgb:
                image.Slice(texel * 4, 4).CopyTo(rgba);
                return;

            case PixelFormat.Bgra8UNorm:
            case PixelFormat.Bgra8UNormSrgb:
                rgba[0] = image[(texel * 4) + 2];
                rgba[1] = image[(texel * 4) + 1];
                rgba[2] = image[texel * 4];
                rgba[3] = image[(texel * 4) + 3];
                return;

            default:
                throw new NotSupportedException(
                    $"{format} cannot be block-compressed from. The encoders read eight-bit unsigned channels; "
                    + "convert to Rgba8UNorm first."
                );
        }
    }

    static void ReadHdrTexel(ReadOnlySpan<byte> image, PixelFormat format, int texel, Span<ushort> rgb) {
        switch (format) {
            case PixelFormat.Rgba16Float:
                for (var channel = 0; channel < 3; channel++) {
                    rgb[channel] = ClampToEncodable(
                        BitConverter.UInt16BitsToHalf(
                            BinaryPrimitives.ReadUInt16LittleEndian(image[((texel * 8) + (channel * 2))..])
                        )
                    );
                }

                return;

            case PixelFormat.Rgba32Float:
                for (var channel = 0; channel < 3; channel++) {
                    rgb[channel] = ClampToEncodable(
                        (Half)BinaryPrimitives.ReadSingleLittleEndian(image[((texel * 16) + (channel * 4))..])
                    );
                }

                return;

            default:
                throw new NotSupportedException(
                    $"{format} cannot be encoded as BC6H. It reads half or single precision colour; convert to "
                    + "Rgba16Float first."
                );
        }
    }

    /// <summary>
    ///     Half's bit pattern is what BC6H's arithmetic runs on, and it is only monotonic in the
    ///     value for non-negative finite numbers. Unsigned BC6H has no way to say "negative" or
    ///     "not a number", so both are clamped here rather than reinterpreted as an enormous
    ///     positive number further down.
    /// </summary>
    static ushort ClampToEncodable(Half value) {
        if (Half.IsNaN(value)) {
            return 0;
        }

        var bits = BitConverter.HalfToUInt16Bits(value);

        // The sign bit is bit fifteen; anything with it set is negative, and zero is the nearest
        // thing to it BC6H can hold.
        if ((bits & 0x8000) != 0) {
            return 0;
        }

        return Math.Min(bits, (ushort)Bc6HBlock.EndpointValue(Bc6HBlock.LargestEndpoint));
    }
}
