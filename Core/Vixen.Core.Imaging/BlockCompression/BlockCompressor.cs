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
///         <b>What quality to expect.</b> BC1, BC3, BC4 and BC5 fit the principal axis of the block's
///         own colours and refine the endpoints by least squares, and those formats have no modes
///         left to choose between. BC7 and BC6H search every mode, and how hard is the caller's choice
///         of <see cref="BlockQuality" />; the README has what each level costs and buys, measured.
///     </para>
///     <para>
///         <b>Parallel, and the same bytes either way.</b> A whole texture is cut into runs of blocks
///         that go to the thread pool, and every block is a pure function of its own sixteen texels,
///         so nothing about the result depends on the number of threads or on who finished first.
///         That is what lets a content-addressed cache key an encoded texture by its inputs.
///     </para>
/// </remarks>
public static class BlockCompressor {
    /// <summary>The half-float bit pattern for one.</summary>
    const ushort OneAsHalf = 0x3C00;

    /// <summary>How many blocks one piece of work is.</summary>
    /// <remarks>
    ///     Long enough that the pool's overhead is nothing beside it and short enough that a
    ///     cancellation lands within a fraction of a second at the slowest level.
    /// </remarks>
    const int BlocksPerRun = 64;

    /// <summary>Whether this can produce that format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether <see cref="Encode(TextureData, PixelFormat)" /> accepts it as a target.</returns>
    public static bool CanEncode(PixelFormat format) =>
        format.ToLinear() is PixelFormat.Bc1RgbaUNorm or PixelFormat.Bc3RgbaUNorm
            or PixelFormat.Bc4RUNorm or PixelFormat.Bc5RgUNorm or PixelFormat.Bc7RgbaUNorm
            or PixelFormat.Bc6HRgbUFloat or PixelFormat.Bc6HRgbSFloat;

    /// <summary>Whether the format holds high dynamic range, and so is encoded from float rather than bytes.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether it is one of the two BC6H formats.</returns>
    public static bool IsHighDynamicRange(PixelFormat format) =>
        format is PixelFormat.Bc6HRgbUFloat or PixelFormat.Bc6HRgbSFloat;

    /// <summary>Compresses every level of a texture, at <see cref="BlockQuality.Balanced" />.</summary>
    /// <param name="source">The texture. See <see cref="Encode(TextureData, PixelFormat, BlockEncodeOptions)" />.</param>
    /// <param name="target">What to compress it to.</param>
    /// <returns>A new texture, in <paramref name="target" />.</returns>
    /// <exception cref="NotSupportedException">The source or the target format is not one of these.</exception>
    /// <exception cref="ArgumentException">The source is sRGB and the target is not, or the other way round.</exception>
    public static TextureData Encode(TextureData source, PixelFormat target) => Encode(source, target, new());

    /// <summary>Compresses every level of a texture, on as many threads as the options allow.</summary>
    /// <param name="source">
    ///     The texture. BC1, BC3, BC4, BC5 and BC7 read eight-bit channels, and round sixteen-bit
    ///     and float ones down to them, clamping float to zero to one. BC6H reads half or single
    ///     precision float or sixteen-bit unsigned norm. A one- or two-channel source leaves the
    ///     missing channels at zero and alpha opaque.
    /// </param>
    /// <param name="target">What to compress it to.</param>
    /// <param name="options">How hard to try, on how many threads, and how to report and stop.</param>
    /// <returns>A new texture, in <paramref name="target" />.</returns>
    /// <exception cref="NotSupportedException">The source or the target format is not one of these.</exception>
    /// <exception cref="ArgumentException">The source is sRGB and the target is not, or the other way round.</exception>
    /// <exception cref="OperationCanceledException">The options' token was cancelled.</exception>
    public static TextureData Encode(TextureData source, PixelFormat target, BlockEncodeOptions options) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        if (!CanEncode(target)) {
            throw new NotSupportedException(
                $"{target} is not a format this encodes. BC1, BC3, BC4, BC5, BC6H and BC7 are; ASTC and ETC2 "
                + "need the native encoder doc 03 calls for and doc 01 registers as astcenc."
            );
        }

        if (!(target.IsSrgb() && options.SourceIsSrgbEncoded) && source.Format.IsSrgb() != target.IsSrgb()) {
            throw new ArgumentException(
                $"A {source.Format} source cannot be encoded as {target}: one is sRGB and the other is not, and "
                + "the hardware would apply — or fail to apply — a transfer function nobody asked for. Name "
                + $"{(source.Format.IsSrgb() ? target.ToSrgb() : target.ToLinear())} instead, or say "
                + "SourceIsSrgbEncoded if these values are encoded in a format that cannot say so.",
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

        var runs = RunsOf(source);
        var total = 0L;

        foreach (var run in runs) {
            total += run.Blocks;
        }

        var report = new ProgressReport(options.Progress, total);
        var token = options.CancellationToken;

        token.ThrowIfCancellationRequested();

        if (runs.Length <= 1 || options.MaxDegreeOfParallelism == 1) {
            foreach (var run in runs) {
                token.ThrowIfCancellationRequested();
                EncodeRun(source, destination, target, options.Quality, run);
                report.Add(run.Blocks);
            }

            return destination;
        }

        // The token stops the loop between runs, and a run is a fraction of a second at the slowest
        // level. Nothing here can see what any other run produced, which is the whole of why the
        // thread count cannot reach the bytes.
        Parallel.For(
            0,
            runs.Length,
            new ParallelOptions {
                CancellationToken = token,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism == 0 ? -1 : options.MaxDegreeOfParallelism
            },
            index => {
                EncodeRun(source, destination, target, options.Quality, runs[index]);
                report.Add(runs[index].Blocks);
            }
        );

        return destination;
    }

    /// <summary>A stretch of one row of blocks, the unit the thread pool is handed.</summary>
    /// <param name="Level">Which mip level.</param>
    /// <param name="Image">Which image of that level: slice, layer and face run together.</param>
    /// <param name="Row">Which row of blocks.</param>
    /// <param name="Column">The first block.</param>
    /// <param name="Blocks">How many blocks.</param>
    readonly record struct Run(int Level, int Image, int Row, int Column, int Blocks);

    static Run[] RunsOf(TextureData texture) {
        var count = 0;

        for (var level = 0; level < texture.LevelCount; level++) {
            var extent = ExtentOf(texture, level);

            count += extent.Images * extent.Rows * (((extent.Columns - 1) / BlocksPerRun) + 1);
        }

        var runs = new Run[count];
        var filled = 0;

        for (var level = 0; level < texture.LevelCount; level++) {
            var extent = ExtentOf(texture, level);

            for (var image = 0; image < extent.Images; image++) {
                for (var row = 0; row < extent.Rows; row++) {
                    for (var column = 0; column < extent.Columns; column += BlocksPerRun) {
                        runs[filled++] = new(level, image, row, column, Math.Min(BlocksPerRun, extent.Columns - column));
                    }
                }
            }
        }

        return runs;
    }

    static void EncodeRun(TextureData source, TextureData destination, PixelFormat target, BlockQuality quality, Run run) {
        Span<byte> rgba = stackalloc byte[Bc1Block.Texels * 4];
        Span<ushort> rgb = stackalloc ushort[Bc6HBlock.Texels * 3];

        var extent = ExtentOf(source, run.Level);
        var blockBytes = target.BlockSize();
        var sourceStride = (int)source.Format.LevelSize(extent.Width, extent.Height);
        var targetStride = (int)target.LevelSize(extent.Width, extent.Height);
        var from = source.Level(run.Level).Slice(run.Image * sourceStride, sourceStride);
        var to = destination.LevelSpan(run.Level).Slice(run.Image * targetStride, targetStride);
        var signed = target == PixelFormat.Bc6HRgbSFloat;

        for (var column = run.Column; column < run.Column + run.Blocks; column++) {
            var block = to.Slice(((run.Row * extent.Columns) + column) * blockBytes, blockBytes);

            if (IsHighDynamicRange(target)) {
                GatherHdr(from, source.Format, extent, column * 4, run.Row * 4, signed, rgb);
                Bc6HBlock.Encode(rgb, block, quality, signed);
                continue;
            }

            Gather(from, source.Format, extent, column * 4, run.Row * 4, rgba);
            EncodeBlock(target, rgba, block, quality);
        }
    }

    /// <summary>Totals finished blocks and reports them, one at a time and in order.</summary>
    /// <remarks>
    ///     The lock only exists when somebody is listening: without it, two threads could report 50%
    ///     and 49% the wrong way round, which a host's progress bar would show as going backwards.
    /// </remarks>
    sealed class ProgressReport(IProgress<BlockEncodeProgress>? progress, long total) {
        readonly Lock gate = new();
        long done;

        public void Add(int blocks) {
            if (progress is null) {
                return;
            }

            lock (gate) {
                done += blocks;
                progress.Report(new(done, total));
            }
        }
    }

    /// <summary>Decompresses every level of a texture, for inspection.</summary>
    /// <param name="source">The texture, in a block-compressed format.</param>
    /// <returns>
    ///     A new texture: <see cref="PixelFormat.Rgba16Float" /> for either BC6H, and
    ///     <see cref="PixelFormat.Rgba8UNorm" /> or its sRGB form for everything else.
    /// </returns>
    /// <exception cref="NotSupportedException">The format is not one of these.</exception>
    public static TextureData Decode(TextureData source) {
        ArgumentNullException.ThrowIfNull(source);

        var highDynamicRange = IsHighDynamicRange(source.Format);
        var signed = source.Format == PixelFormat.Bc6HRgbSFloat;

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
                            Bc6HBlock.Decode(block, rgb, signed);
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

    /// <summary>Compresses one 4×4 block of eight-bit colour, at <see cref="BlockQuality.Balanced" />.</summary>
    /// <param name="target">The format to write.</param>
    /// <param name="rgba">Sixty-four bytes: sixteen texels of RGBA, row-major.</param>
    /// <param name="block">The block's bytes to fill.</param>
    /// <exception cref="NotSupportedException">The format is not one of these, or is BC6H.</exception>
    public static void EncodeBlock(PixelFormat target, ReadOnlySpan<byte> rgba, Span<byte> block) =>
        EncodeBlock(target, rgba, block, BlockQuality.Balanced);

    /// <summary>Compresses one 4×4 block of eight-bit colour.</summary>
    /// <param name="target">The format to write.</param>
    /// <param name="rgba">Sixty-four bytes: sixteen texels of RGBA, row-major.</param>
    /// <param name="block">The block's bytes to fill.</param>
    /// <param name="quality">How hard to search, where the format has a search.</param>
    /// <exception cref="NotSupportedException">The format is not one of these, or is BC6H.</exception>
    public static void EncodeBlock(PixelFormat target, ReadOnlySpan<byte> rgba, Span<byte> block, BlockQuality quality) {
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
                Bc7Block.Encode(rgba, block, quality);
                return;

            case PixelFormat.Bc6HRgbUFloat or PixelFormat.Bc6HRgbSFloat:
                throw new NotSupportedException(
                    "BC6H holds high dynamic range and cannot be encoded from eight-bit colour; every block "
                    + "would be crushed into the first hundredth of its range. Use EncodeHdrBlock."
                );

            default:
                throw new NotSupportedException($"{target} is not a format this encodes.");
        }
    }

    /// <summary>Compresses one 4×4 block of high dynamic range colour, as unsigned BC6H.</summary>
    /// <param name="rgb">Forty-eight half-float bit patterns: sixteen texels of RGB, row-major.</param>
    /// <param name="block">Sixteen bytes to fill.</param>
    public static void EncodeHdrBlock(ReadOnlySpan<ushort> rgb, Span<byte> block) => Bc6HBlock.Encode(rgb, block);

    /// <summary>Compresses one 4×4 block of high dynamic range colour.</summary>
    /// <param name="rgb">Forty-eight half-float bit patterns: sixteen texels of RGB, row-major.</param>
    /// <param name="block">Sixteen bytes to fill.</param>
    /// <param name="quality">How hard to search.</param>
    /// <param name="signed">Whether to write the signed format. Unsigned clamps negatives to zero.</param>
    public static void EncodeHdrBlock(ReadOnlySpan<ushort> rgb, Span<byte> block, BlockQuality quality, bool signed) =>
        Bc6HBlock.Encode(rgb, block, quality, signed);

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

            case PixelFormat.Bc6HRgbUFloat or PixelFormat.Bc6HRgbSFloat:
                throw new NotSupportedException(
                    "BC6H decodes to half-float, not to eight-bit colour. Use DecodeHdrBlock."
                );

            default:
                throw new NotSupportedException($"{source} is not a format this decodes.");
        }
    }

    /// <summary>Decompresses one unsigned BC6H block.</summary>
    /// <param name="block">Its sixteen bytes.</param>
    /// <param name="rgb">Forty-eight half-float bit patterns to fill.</param>
    public static void DecodeHdrBlock(ReadOnlySpan<byte> block, Span<ushort> rgb) => Bc6HBlock.Decode(block, rgb);

    /// <summary>Decompresses one BC6H block of either signedness.</summary>
    /// <param name="block">Its sixteen bytes.</param>
    /// <param name="rgb">Forty-eight half-float bit patterns to fill.</param>
    /// <param name="signed">Whether it is the signed format.</param>
    public static void DecodeHdrBlock(ReadOnlySpan<byte> block, Span<ushort> rgb, bool signed) =>
        Bc6HBlock.Decode(block, rgb, signed);

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
        bool signed,
        Span<ushort> rgb
    ) {
        for (var y = 0; y < 4; y++) {
            var sourceY = Math.Min(originY + y, extent.Height - 1);

            for (var x = 0; x < 4; x++) {
                var sourceX = Math.Min(originX + x, extent.Width - 1);
                ReadHdrTexel(image, format, (sourceY * extent.Width) + sourceX, signed, rgb[(((y * 4) + x) * 3)..]);
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
        }

        var channels = WideChannelsOf(format);

        if (channels == 0) {
            throw new NotSupportedException(
                $"{format} cannot be block-compressed from. The encoders read eight- and sixteen-bit unsigned "
                + "channels and half or single precision float; convert to Rgba8UNorm first."
            );
        }

        var width = format.BlockSize() / channels;

        for (var channel = 0; channel < 4; channel++) {
            rgba[channel] = channel >= channels
                ? channel == 3 ? (byte)255 : (byte)0
                : ByteOf(image, format, (texel * format.BlockSize()) + (channel * width));
        }
    }

    /// <summary>How many channels a sixteen- or thirty-two-bit source has. Zero if it is not one.</summary>
    static int WideChannelsOf(PixelFormat format) =>
        format switch {
            PixelFormat.R16UNorm or PixelFormat.R16Float or PixelFormat.R32Float => 1,
            PixelFormat.Rg16UNorm or PixelFormat.Rg16Float or PixelFormat.Rg32Float => 2,
            PixelFormat.Rgba16UNorm or PixelFormat.Rgba16Float or PixelFormat.Rgba32Float => 4,
            _ => 0
        };

    /// <summary>
    ///     One sixteen- or thirty-two-bit channel rounded to the nearest eight-bit value, which is
    ///     what every format here but BC6H stores. Float is clamped: these formats end at one.
    /// </summary>
    static byte ByteOf(ReadOnlySpan<byte> image, PixelFormat format, int at) {
        switch (format) {
            case PixelFormat.R16UNorm or PixelFormat.Rg16UNorm or PixelFormat.Rgba16UNorm:
                return (byte)(((BinaryPrimitives.ReadUInt16LittleEndian(image[at..]) * 255) + 32767) / 65535);

            case PixelFormat.R16Float or PixelFormat.Rg16Float or PixelFormat.Rgba16Float:
                return ByteOfFloat((float)BinaryPrimitives.ReadHalfLittleEndian(image[at..]));

            default:
                return ByteOfFloat(BinaryPrimitives.ReadSingleLittleEndian(image[at..]));
        }
    }

    /// <remarks><see cref="Math.Clamp(float, float, float)" /> lets a NaN through, and a cast of NaN differs between CPUs.</remarks>
    static byte ByteOfFloat(float value) => float.IsNaN(value) ? (byte)0 : (byte)((Math.Clamp(value, 0f, 1f) * 255f) + 0.5f);

    static void ReadHdrTexel(ReadOnlySpan<byte> image, PixelFormat format, int texel, bool signed, Span<ushort> rgb) {
        var channels = WideChannelsOf(format);

        if (channels == 0) {
            throw new NotSupportedException(
                $"{format} cannot be encoded as BC6H. It reads half or single precision colour or sixteen-bit "
                + "norm; convert to Rgba16Float first."
            );
        }

        var width = format.BlockSize() / channels;

        for (var channel = 0; channel < 3; channel++) {
            if (channel >= channels) {
                rgb[channel] = 0;
                continue;
            }

            var at = (texel * format.BlockSize()) + (channel * width);

            var value = format switch {
                PixelFormat.R16UNorm or PixelFormat.Rg16UNorm or PixelFormat.Rgba16UNorm =>
                    (Half)(BinaryPrimitives.ReadUInt16LittleEndian(image[at..]) / 65535f),
                PixelFormat.R16Float or PixelFormat.Rg16Float or PixelFormat.Rgba16Float =>
                    BinaryPrimitives.ReadHalfLittleEndian(image[at..]),
                _ => (Half)BinaryPrimitives.ReadSingleLittleEndian(image[at..])
            };

            rgb[channel] = ClampToEncodable(value, signed);
        }
    }

    /// <summary>
    ///     Half's bit pattern is what BC6H's arithmetic runs on, and it is only monotonic in the
    ///     value for non-negative finite numbers. Unsigned BC6H has no way to say "negative", and
    ///     neither form has a way to say "not a number" or "infinite", so all of it is clamped here
    ///     rather than reinterpreted as an enormous positive number further down.
    /// </summary>
    static ushort ClampToEncodable(Half value, bool signed) {
        if (Half.IsNaN(value)) {
            return 0;
        }

        var bits = BitConverter.HalfToUInt16Bits(value);
        var magnitude = Math.Min(bits & 0x7FFF, Bc6HBlock.LargestHalf);

        // The sign bit is bit fifteen; zero is the nearest thing unsigned BC6H has to a negative.
        return (bits & 0x8000) == 0 ? (ushort)magnitude : signed ? (ushort)(magnitude | 0x8000) : (ushort)0;
    }
}
