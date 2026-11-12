// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;

namespace Vixen.Texturing.Painting;

/// <summary>
///     A <c>.vxpaint</c>: one paint layer's pixels, one image per channel it writes.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D10 and Part 5: the stack is a file people merge and the pixels are not.</b>
///         <c>LayerAsset.Paint</c> and <c>MaskAsset.Paint</c> already hold a path and never a buffer —
///         <c>LayerStackShapeTests</c> walks that type closure and fails on a member that could carry
///         texels — and this is the file at the other end of the path.
///     </para>
///     <para>
///         ⚠ <b>One image per channel, not one RGBA image for the layer.</b> A paint layer restricts
///         its channels the same way a fill does, and a layer that paints roughness alone must not
///         also carry a base-colour buffer it never writes. A mask's <c>.vxpaint</c> is therefore the
///         degenerate case with one channel in it and no special format.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Deflated per channel since version 2, and the reason the first version was not is
///             wrong by more than an order of magnitude
///         </b>
///         —
///         <a href="https://github.com/Rikarin/Vixen/issues/850">#850</a>. This said, and the issue
///         said, that compressing a 4K channel was "a second of Deflate". Measured, it is <b>43 ms</b>
///         at <see cref="CompressionLevel.Fastest" /> — and the raw write it replaces is 36–66 ms of
///         its own, because writing 64 MB is I/O-bound and writing 4 MB is not. So the trade is not
///         CPU against disk: it is roughly the same wall clock, sixteen times less disk, and sixteen
///         times less to read back. A stroked 4K channel is 4.09 MB.
///     </para>
///     <para>
///         ⚠ <b><see cref="CompressionLevel.Fastest" /> and not <c>Optimal</c>.</b> Optimal reaches
///         2.6% against 6.4% — 2.4 MB on a file that was 64 MB — and costs 33 ms more per channel per
///         save, on the save of a document whose whole point is that the artist is dragging on it.
///     </para>
///     <para>
///         <b>A pack of tiles since version 3</b>, each deflated on its own and in order of position, so
///         that a tile nobody painted is not in the file and the same picture is always the same
///         bytes. An unpainted channel is a header. Each version before this stays readable, and the
///         first save rewrites the file.
///     </para>
///     <para>
///         ⚠ <b>Tiles do not buy an incremental save here</b>, and #850 was right that nothing is
///         bounded by the whole-file write once it is 4 MB. What they buy is what the image holds in
///         memory; a save still writes every tile that holds anything.
///     </para>
/// </remarks>
public sealed class PaintCanvas {
    /// <summary>What the format's first eight bytes say.</summary>
    public static readonly byte[] Magic = [.."VXPAINT\0"u8];

    /// <summary>What this build writes.</summary>
    public const int CurrentVersion = 3;

    /// <summary>The oldest version this build can still open.</summary>
    /// <remarks>
    ///     ⚠ <b>Version 1 stays readable rather than being migrated or refused.</b> The channel
    ///     framing is the same in both — usage, then that channel's bytes — so the reader differs by
    ///     one <c>if</c>, and an artist who saves once rewrites the file at the current version
    ///     anyway. Refusing would turn an editor upgrade into a lost painting.
    /// </remarks>
    public const int OldestVersion = 1;

    /// <summary>Where the channel bytes stop being raw texels.</summary>
    /// <remarks>The version that introduced Deflate, so the reader's branch is named rather than 2.</remarks>
    const int CompressedFrom = 2;

    /// <summary>Where a channel stops being one image and becomes a pack of tiles.</summary>
    const int TiledFrom = 3;

    readonly Dictionary<string, PaintImage> channels = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> order = [];

    /// <summary>An empty canvas at a resolution.</summary>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public PaintCanvas(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
    }

    /// <summary>Its width in texels.</summary>
    public int Width { get; }

    /// <summary>Its height in texels.</summary>
    public int Height { get; }

    /// <summary>The channels it holds, in the order they were added.</summary>
    public IReadOnlyList<string> Channels => order;

    /// <summary>The image for a channel, adding an empty one the first time it is asked for.</summary>
    /// <param name="usage">The channel's usage — <c>baseColor</c>, <c>roughness</c>, <c>mask</c>.</param>
    /// <returns>The image.</returns>
    /// <exception cref="ArgumentException">The usage is blank.</exception>
    public PaintImage Channel(string usage) {
        ArgumentNullException.ThrowIfNull(usage);

        var name = usage.Trim();

        if (name.Length == 0) {
            throw new ArgumentException("A paint channel is named by its usage.", nameof(usage));
        }

        if (channels.TryGetValue(name, out var existing)) {
            return existing;
        }

        PaintImage image = new(Width, Height);

        channels[name] = image;
        order.Add(name);

        return image;
    }

    /// <summary>Whether a channel has been painted on.</summary>
    /// <param name="usage">The channel's usage.</param>
    /// <returns>Whether it is present.</returns>
    public bool Has(string usage) => channels.ContainsKey(usage);

    /// <summary>This canvas at a larger resolution, each texel repeated over the texels it now covers.</summary>
    /// <param name="width">The new width, at least this one's.</param>
    /// <param name="height">The new height, at least this one's.</param>
    /// <returns>A new canvas with every channel this one has.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is smaller than this one's.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Nearest and never filtered, because the point is to lose nothing.</b> Each new texel takes the value
    ///         of the old texel it lies in, so no value appears that was not painted, and at a whole factor (every
    ///         change between the power-of-two sides a texture set offers) averaging each block of the new canvas gives
    ///         the old one back exactly. A filtered enlargement would blur what was painted on the way up, and that
    ///         blur would stay after the set came back down.
    ///     </para>
    ///     <para>
    ///         Only larger: a canvas painted at a higher resolution than its set keeps it, and the evaluator samples
    ///         it at the set's size, so making a set smaller never rewrites a texel.
    ///     </para>
    /// </remarks>
    public PaintCanvas Enlarged(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, Width);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, Height);

        PaintCanvas enlarged = new(width, height);

        foreach (var usage in order) {
            var from = channels[usage];
            var into = enlarged.Channel(usage);

            // What was never painted stays a background rather than becoming tiles of it.
            into.Fill(from.Background);

            for (var y = 0; y < height; y++) {
                var row = (int)((long)y * Height / height);

                for (var x = 0; x < width; x++) {
                    var value = from.At((int)((long)x * Width / width), row);

                    if (value != from.Background) {
                        into.Put(x, y, value);
                    }
                }
            }
        }

        return enlarged;
    }

    /// <summary>This canvas at a smaller resolution, each texel the average of the texels it now covers.</summary>
    /// <param name="width">The new width, at most this one's and at least 1.</param>
    /// <param name="height">The new height, at most this one's and at least 1.</param>
    /// <returns>A new canvas with every channel this one has.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is larger than this one's, or below 1.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>A box average, weighted by coverage, which is the inverse of <see cref="Enlarged" /></b> (Patina #106):
    ///         at a whole factor each new texel is the block it covers, so a canvas enlarged and brought back down is the
    ///         canvas it was, to the byte. Colour is averaged under its alpha, so a transparent texel's colour, which
    ///         nothing shows, does not bleed into a painted neighbour; a block that is transparent throughout keeps the
    ///         plain average.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Detail goes, which is why nothing does this on its own.</b> A set made smaller samples a larger canvas
    ///         at its size and keeps it; this is for a copy taken deliberately into a smaller set, where the canvas has
    ///         to be painted at that set's size.
    ///     </para>
    /// </remarks>
    public PaintCanvas Reduced(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, Height);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        PaintCanvas reduced = new(width, height);

        foreach (var usage in order) {
            var from = channels[usage];
            var into = reduced.Channel(usage);

            into.Fill(from.Background);

            for (var y = 0; y < height; y++) {
                var top = (int)((long)y * Height / height);
                var bottom = Math.Max(top + 1, (int)((long)(y + 1) * Height / height));

                for (var x = 0; x < width; x++) {
                    var left = (int)((long)x * Width / width);
                    var right = Math.Max(left + 1, (int)((long)(x + 1) * Width / width));
                    var value = Average(from, left, top, right, bottom);

                    if (value != from.Background) {
                        into.Put(x, y, value);
                    }
                }
            }
        }

        return reduced;
    }

    /// <summary>
    ///     This canvas at another resolution: <see cref="Enlarged" /> where it grows, <see cref="Reduced" /> where it
    ///     shrinks.
    /// </summary>
    /// <param name="width">The new width.</param>
    /// <param name="height">The new height.</param>
    /// <returns>A new canvas with every channel this one has.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is below 1.</exception>
    public PaintCanvas Resampled(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var grown = Enlarged(Math.Max(width, Width), Math.Max(height, Height));

        return grown.Width == width && grown.Height == height ? grown : grown.Reduced(width, height);
    }

    /// <summary>The coverage-weighted average of a block of texels.</summary>
    static uint Average(PaintImage from, int left, int top, int right, int bottom) {
        double red = 0, green = 0, blue = 0, alpha = 0, plainRed = 0, plainGreen = 0, plainBlue = 0;
        var count = 0;

        for (var y = top; y < bottom; y++) {
            for (var x = left; x < right; x++) {
                var texel = from.At(x, y);
                var a = (texel >> 24) & 0xFF;
                var r = texel & 0xFF;
                var g = (texel >> 8) & 0xFF;
                var b = (texel >> 16) & 0xFF;

                red += r * a;
                green += g * a;
                blue += b * a;
                alpha += a;
                plainRed += r;
                plainGreen += g;
                plainBlue += b;
                count++;
            }
        }

        return alpha > 0
            ? Packed(red / alpha, green / alpha, blue / alpha, alpha / count)
            : Packed(plainRed / count, plainGreen / count, plainBlue / count, 0);
    }

    static uint Packed(double red, double green, double blue, double alpha) =>
        Byte(red) | (Byte(green) << 8) | (Byte(blue) << 16) | (Byte(alpha) << 24);

    static uint Byte(double value) => (uint)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>Writes the canvas.</summary>
    /// <param name="stream">Where to.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Little-endian and explicit about it.</b> <see cref="BinaryWriter" /> is
    ///         little-endian on every platform .NET runs on, which is a fact about the class and not
    ///         about the machine — an asset written on one architecture is opened on another and a
    ///         format that inherited the host's endianness would be a file that reads as noise rather
    ///         than as an error.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Each tile's compressed length is written before its bytes, and the compression
    ///             therefore happens into memory first.
    ///         </b>
    ///         A <c>DeflateStream</c> straight onto the
    ///         output would need the length patched afterwards, which needs a seekable stream — and
    ///         <see cref="PaintSurface.Save" /> writes through a temporary file precisely because the
    ///         destination is not always one. The buffer is the <em>compressed</em> size, which is the
    ///         4 MB the measurement is about rather than the 64 MB it replaces.
    ///     </para>
    /// </remarks>
    public void Write(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);

        using BinaryWriter writer = new(stream, Encoding.UTF8, true);

        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(order.Count);

        foreach (var usage in order) {
            WriteChannel(writer, usage, channels[usage]);
        }
    }

    /// <summary>The bytes this canvas holds in memory, which is what a store's budget has to count.</summary>
    public long ResidentBytes {
        get {
            var total = 0L;

            foreach (var image in channels.Values) {
                total += image.ResidentBytes;
            }

            return total;
        }
    }

    static void WriteChannel(BinaryWriter writer, string usage, PaintImage image) {
        writer.Write(usage);
        writer.Write(image.Background);

        List<(int X, int Y)> held = [];

        for (var y = 0; y < image.TilesHigh; y++) {
            for (var x = 0; x < image.TilesWide; x++) {
                if (!image.IsBackground(x, y)) {
                    held.Add((x, y));
                }
            }
        }

        var packed = new byte[held.Count][];

        Parallel.For(
            0,
            held.Count,
            index => packed[index] = Deflated(image.TileTexels(held[index].X, held[index].Y).Span)
        );

        writer.Write(held.Count);

        for (var index = 0; index < held.Count; index++) {
            writer.Write(held[index].Y * image.TilesWide + held[index].X);
            writer.Write(packed[index].Length);
            writer.Write(packed[index]);
        }
    }

    static byte[] Deflated(ReadOnlySpan<byte> texels) {
        using MemoryStream packed = new();

        using (DeflateStream deflate = new(packed, CompressionLevel.Fastest, true)) {
            deflate.Write(texels);
        }

        return packed.ToArray();
    }

    /// <summary>Reads a canvas.</summary>
    /// <param name="stream">Where from.</param>
    /// <returns>The canvas.</returns>
    /// <exception cref="InvalidDataException">It is not a <c>.vxpaint</c> this build can read.</exception>
    public static PaintCanvas Read(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);

        using BinaryReader reader = new(stream, Encoding.UTF8, true);

        var magic = reader.ReadBytes(Magic.Length);

        if (!magic.AsSpan().SequenceEqual(Magic)) {
            throw new InvalidDataException(
                "This is not a .vxpaint: the first eight bytes are not the format's magic. A paint file "
                + "is raw texels, so a reader that guessed would produce a plausible picture of nothing."
            );
        }

        // ⚠ Everything after the magic, because a compressed file is small enough that a cut lands in
        // a header as readily as in the texels. `BinaryReader` reports that as an
        // `EndOfStreamException` and a short channel as a count mismatch; both are the same fact
        // about the file, and a caller that had to catch two types would catch one.
        try {
            var version = reader.ReadInt32();

            if (version is < OldestVersion or > CurrentVersion) {
                throw new InvalidDataException(
                    $"This .vxpaint is version {version} and this build reads {OldestVersion}…{CurrentVersion}."
                );
            }

            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var count = reader.ReadInt32();

            if (width <= 0 || height <= 0 || count < 0) {
                throw new InvalidDataException(
                    $"This .vxpaint declares {width}×{height} and {count} channel(s), which is not a canvas."
                );
            }

            PaintCanvas canvas = new(width, height);

            for (var channel = 0; channel < count; channel++) {
                var usage = reader.ReadString();
                var image = canvas.Channel(usage);

                if (version >= TiledFrom) {
                    ReadTiles(reader, usage, image);

                    continue;
                }

                var texels = new byte[(long)width * height * PaintImage.BytesPerTexel];
                var read = version >= CompressedFrom ? Inflate(reader, texels) : Fill(reader, texels);

                if (read != texels.Length) {
                    throw new InvalidDataException(
                        $"This .vxpaint's '{usage}' channel is {read} bytes and its header says "
                        + $"{texels.Length}. A truncated paint file reads as a half-painted layer."
                    );
                }

                image.CopyFrom(texels);
            }

            return canvas;
        } catch (EndOfStreamException ended) {
            throw new InvalidDataException(
                "This .vxpaint ends before the canvas its header describes. A truncated paint file "
                + "reads as a half-painted layer.",
                ended
            );
        }
    }

    /// <summary>Reads a version-3 channel: the background, then each tile that differs from it.</summary>
    /// <remarks>
    ///     ⚠ <b>In strictly rising order or refused.</b> A writer has one order, so a file with a tile
    ///     twice or out of place did not come from one, and taking the last of two would be a picture
    ///     nobody can explain.
    /// </remarks>
    static void ReadTiles(BinaryReader reader, string usage, PaintImage image) {
        image.Reset(reader.ReadUInt32());

        var count = reader.ReadInt32();
        var slots = image.TilesWide * image.TilesHigh;

        if (count < 0 || count > slots) {
            throw new InvalidDataException($"This .vxpaint's '{usage}' channel declares {count} tiles of {slots}.");
        }

        var previous = -1;

        for (var index = 0; index < count; index++) {
            var slot = reader.ReadInt32();

            if (slot <= previous || slot >= slots) {
                throw new InvalidDataException(
                    $"This .vxpaint's '{usage}' channel has tile {slot} after tile {previous}, of {slots}."
                );
            }

            previous = slot;

            var rect = image.TileRect(slot % image.TilesWide, slot / image.TilesWide);
            var texels = new byte[rect.Area * PaintImage.BytesPerTexel];
            var read = Inflate(reader, texels);

            if (read != texels.Length) {
                throw new InvalidDataException(
                    $"Tile {slot} of this .vxpaint's '{usage}' channel is {read} bytes and should be "
                    + $"{texels.Length}. A truncated paint file reads as a half-painted layer."
                );
            }

            image.Adopt(slot % image.TilesWide, slot / image.TilesWide, texels);
        }
    }

    /// <summary>Reads a version-1 channel: the texels, raw.</summary>
    /// <returns>How many bytes were read.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A loop, because <c>BinaryReader.Read(byte[], int, int)</c> forwards to a single
    ///         <c>Stream.Read</c> and a stream is entitled to return fewer bytes than asked for.
    ///     </b>
    ///     A 4K
    ///     channel is 67 MB, and over a network stream the first read is a chunk — so the single-read
    ///     form refuses a complete file as a truncated one.
    /// </remarks>
    static int Fill(BinaryReader reader, byte[] texels) {
        var read = 0;

        while (read < texels.Length) {
            var got = reader.Read(texels, read, texels.Length - read);

            if (got <= 0) {
                break;
            }

            read += got;
        }

        return read;
    }

    /// <summary>Reads a version-2 channel: a length, then that many Deflate bytes.</summary>
    /// <returns>How many texel bytes came out.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The claim that made this format's short-read loop load-bearing is false, and it was
    ///         tested rather than assumed.
    ///     </b>
    ///     <a href="https://github.com/Rikarin/Vixen/issues/850">#850</a>
    ///     says "over a <c>DeflateStream</c> a short read stops being an edge case and becomes the
    ///     norm — the loop is what makes version 2 possible at all". It is not: .NET's
    ///     <see cref="DeflateStream" /> keeps inflating until the caller's buffer is full or the data
    ///     runs out, so replacing the loop with one <c>Read</c> left every test in this file green.
    ///     What the loop is really for is <see cref="Fill" />'s case, a raw channel off a stream that
    ///     answers in chunks. So this asks the framework for the loop —
    ///     <see cref="Stream.ReadAtLeast(Span{byte}, int, bool)" /> — rather than keeping a hand-written
    ///     one no test can distinguish from its own sabotage.
    /// </remarks>
    static int Inflate(BinaryReader reader, byte[] texels) {
        var packed = reader.ReadInt32();

        if (packed < 0) {
            throw new InvalidDataException($"This .vxpaint declares a channel of {packed} compressed bytes.");
        }

        // ⚠ The compressed block is taken whole rather than inflated straight off the source, and
        // that is what keeps one channel's framing from bleeding into the next: `DeflateStream` reads
        // ahead past the end of its own data, so a decompressor left on the file would swallow the
        // usage string after it. `ReadBytes` loops over short reads of its own.
        var block = reader.ReadBytes(packed);

        if (block.Length != packed) {
            throw new EndOfStreamException(
                $"This .vxpaint declares {packed} compressed bytes for a channel and holds {block.Length}."
            );
        }

        using MemoryStream source = new(block, false);
        using DeflateStream inflate = new(source, CompressionMode.Decompress);

        return inflate.ReadAtLeast(texels, texels.Length, false);
    }
}
