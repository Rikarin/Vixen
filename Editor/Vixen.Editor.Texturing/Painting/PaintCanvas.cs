// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;

namespace Vixen.Editor.Texturing.Painting;

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
///         ⚠ <b>Deflated per channel since version 2, and the reason the first version was not is
///         wrong by more than an order of magnitude</b> —
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
///         ⚠ <b>The block format with per-tile storage this issue reached for is a different
///         problem.</b> Tiles buy an <em>incremental</em> save — writing only the tiles a session
///         touched — and nothing here is bounded by the whole-file write once it is 4 MB. It is worth
///         doing when a stroke has to be saved during the drag rather than at the end of it.
///     </para>
/// </remarks>
sealed class PaintCanvas {
    /// <summary>What the format's first eight bytes say.</summary>
    public static readonly byte[] Magic = "VXPAINT\0"u8.ToArray();

    /// <summary>What this build writes.</summary>
    public const int CurrentVersion = 2;

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
    ///         ⚠ <b>Each channel's compressed length is written before its bytes, and the compression
    ///         therefore happens into memory first.</b> A <c>DeflateStream</c> straight onto the
    ///         output would need the length patched afterwards, which needs a seekable stream — and
    ///         <see cref="PaintSurface.Save" /> writes through a temporary file precisely because the
    ///         destination is not always one. The buffer is the <em>compressed</em> size, which is the
    ///         4 MB the measurement is about rather than the 64 MB it replaces.
    ///     </para>
    /// </remarks>
    public void Write(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);

        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(order.Count);

        foreach (var usage in order) {
            writer.Write(usage);

            using MemoryStream packed = new();

            using (DeflateStream deflate = new(packed, CompressionLevel.Fastest, leaveOpen: true)) {
                deflate.Write(channels[usage].Texels);
            }

            writer.Write((int)packed.Length);
            writer.Write(packed.GetBuffer(), 0, (int)packed.Length);
        }
    }

    /// <summary>Reads a canvas.</summary>
    /// <param name="stream">Where from.</param>
    /// <returns>The canvas.</returns>
    /// <exception cref="InvalidDataException">It is not a <c>.vxpaint</c> this build can read.</exception>
    public static PaintCanvas Read(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);

        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

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

                var read = version >= CompressedFrom
                    ? Inflate(reader, image.Texels)
                    : Fill(reader, image.Texels);

                if (read != image.Texels.Length) {
                    throw new InvalidDataException(
                        $"This .vxpaint's '{usage}' channel is {read} bytes and its header says "
                        + $"{image.Texels.Length}. A truncated paint file reads as a half-painted layer."
                    );
                }
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

    /// <summary>Reads a version-1 channel: the texels, raw.</summary>
    /// <returns>How many bytes were read.</returns>
    /// <remarks>
    ///     ⚠ <b>A loop, because <c>BinaryReader.Read(byte[], int, int)</c> forwards to a single
    ///     <c>Stream.Read</c> and a stream is entitled to return fewer bytes than asked for.</b> A 4K
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
    ///     ⚠ <b>The claim that made this format's short-read loop load-bearing is false, and it was
    ///     tested rather than assumed.</b> <a href="https://github.com/Rikarin/Vixen/issues/850">#850</a>
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

        using MemoryStream source = new(block, writable: false);
        using DeflateStream inflate = new(source, CompressionMode.Decompress);

        return inflate.ReadAtLeast(texels, texels.Length, throwOnEndOfStream: false);
    }
}
