// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         ⚠ <b>Uncompressed, and that is a decision rather than an omission.</b> The file is written
///         on every save of a document whose whole point is that the artist is dragging on it; a
///         4K RGBA channel is 67 MB, which is a second of Deflate for a compression ratio that a
///         painted layer — mostly one flat colour under a coverage ramp — would answer very well.
///         The right answer is a block format, the wrong answer is whichever one is quickest to add,
///         and the honest answer today is that nothing here is measured. #850.
///     </para>
/// </remarks>
sealed class PaintCanvas {
    /// <summary>What the format's first eight bytes say.</summary>
    public static readonly byte[] Magic = "VXPAINT\0"u8.ToArray();

    /// <summary>What this build writes.</summary>
    public const int CurrentVersion = 1;

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
    ///     ⚠ <b>Little-endian and explicit about it.</b> <see cref="BinaryWriter" /> is
    ///     little-endian on every platform .NET runs on, which is a fact about the class and not
    ///     about the machine — an asset written on one architecture is opened on another and a
    ///     format that inherited the host's endianness would be a file that reads as noise rather
    ///     than as an error.
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
            writer.Write(channels[usage].Texels);
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

        var version = reader.ReadInt32();

        if (version != CurrentVersion) {
            throw new InvalidDataException(
                $"This .vxpaint is version {version} and this build writes {CurrentVersion}."
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
            // ⚠ A loop, because `BinaryReader.Read(byte[], int, int)` forwards to a single
            // `Stream.Read` and a stream is entitled to return fewer bytes than asked for. A 4K
            // channel is 67 MB, and over a decompressing or a network stream the first read is a
            // chunk — so the single-read form refuses a complete file as a truncated one.
            var read = 0;

            while (read < image.Texels.Length) {
                var got = reader.Read(image.Texels, read, image.Texels.Length - read);

                if (got <= 0) {
                    break;
                }

                read += got;
            }

            if (read != image.Texels.Length) {
                throw new InvalidDataException(
                    $"This .vxpaint's '{usage}' channel is {read} bytes and its header says "
                    + $"{image.Texels.Length}. A truncated paint file reads as a half-painted layer."
                );
            }
        }

        return canvas;
    }
}
