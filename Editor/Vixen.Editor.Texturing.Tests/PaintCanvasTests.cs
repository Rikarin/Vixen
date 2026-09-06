// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The <c>.vxpaint</c> beside the stack: the pixels the stack deliberately does not hold.</summary>
public class PaintCanvasTests {
    /// <summary>A painted canvas survives a round trip, to the byte.</summary>
    [Fact]
    public void A_canvas_round_trips() {
        PaintCanvas canvas = new(48, 32);
        var colour = canvas.Channel("baseColor");
        var rough = canvas.Channel("roughness");

        PaintStroke stroke = new(colour, PaintCoverage.Everywhere(48, 32), PaintStrokeTests.Hard(8f), 0xFF3366CCu);

        stroke.MoveTo(new(12f, 16f));
        stroke.MoveTo(new(36f, 16f));

        rough.Fill(0xFF808080u);

        using MemoryStream stream = new();

        canvas.Write(stream);
        stream.Position = 0;

        var read = PaintCanvas.Read(stream);

        Assert.Equal(48, read.Width);
        Assert.Equal(32, read.Height);
        Assert.Equal(["baseColor", "roughness"], read.Channels);
        Assert.Equal(colour.Texels, read.Channel("baseColor").Texels);
        Assert.Equal(rough.Texels, read.Channel("roughness").Texels);

        // The instrument: an all-zero canvas would round-trip through a reader that returned an
        // empty image, so the picture has to be a picture.
        Assert.Contains(colour.Texels, texel => texel != 0);
    }

    /// <summary>A file that is not a <c>.vxpaint</c> is refused rather than read as one.</summary>
    /// <remarks>
    ///     ⚠ Raw texels have no self-describing structure, so a reader that skipped the magic would
    ///     turn any file of the right length into a plausible picture of nothing.
    /// </remarks>
    [Fact]
    public void Something_that_is_not_a_paint_file_is_refused() {
        using MemoryStream stream = new([.. "NOTPAINT"u8.ToArray(), 1, 0, 0, 0]);

        Assert.Throws<InvalidDataException>(() => PaintCanvas.Read(stream));
    }

    /// <summary>A truncated file is refused rather than read as a half-painted layer.</summary>
    [Fact]
    public void A_truncated_paint_file_is_refused() {
        PaintCanvas canvas = new(32, 32);

        canvas.Channel("mask").Fill(0xFFFFFFFFu);

        using MemoryStream stream = new();

        canvas.Write(stream);

        var bytes = stream.ToArray();

        using MemoryStream cut = new(bytes[..(bytes.Length - 64)]);

        Assert.Throws<InvalidDataException>(() => PaintCanvas.Read(cut));
    }

    /// <summary>⚠ A stream that hands back one byte at a time still reads a whole canvas.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The canary for a compressed <c>.vxpaint</c>, written before there is one —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/850">#850</a>.</b> A
    ///         <c>MemoryStream</c> answers every read in full, so every test above it is blind to
    ///         the difference between a loop and a single <c>Read</c>. The moment the format gains a
    ///         <c>DeflateStream</c> — or is read off a network — the first read is a chunk, and a
    ///         reader that took it for the whole channel would refuse a complete file as a truncated
    ///         one, with a message pointing at the artist's disk.
    ///     </para>
    ///     <para>
    ///         The one-byte stream is the strongest form of the same thing: it is legal, it is what
    ///         <c>Stream.Read</c>'s contract permits, and it fails everything that assumes otherwise.
    ///         The refusal for a genuinely short file is asserted one test up, so this cannot be
    ///         satisfied by a reader that stopped refusing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stream_that_returns_one_byte_at_a_time_still_reads_a_whole_canvas() {
        PaintCanvas canvas = new(32, 24);

        canvas.Channel("baseColor").Fill(0xFF3366CCu);
        canvas.Channel("height").Fill(0xFF204080u);

        using MemoryStream stream = new();

        canvas.Write(stream);

        using Trickle trickle = new(stream.ToArray());

        var read = PaintCanvas.Read(trickle);

        Assert.Equal(["baseColor", "height"], read.Channels);
        Assert.Equal(canvas.Channel("baseColor").Texels, read.Channel("baseColor").Texels);
        Assert.Equal(canvas.Channel("height").Texels, read.Channel("height").Texels);
    }

    /// <summary>A channel is created on first use and kept in the order it was asked for.</summary>
    [Fact]
    public void A_canvas_holds_only_the_channels_the_layer_writes() {
        PaintCanvas canvas = new(16, 16);

        canvas.Channel("roughness");

        Assert.True(canvas.Has("roughness"));
        Assert.False(canvas.Has("baseColor"));
        Assert.Single(canvas.Channels);
    }

    /// <summary>A readable stream that never returns more than one byte per call.</summary>
    /// <remarks>
    ///     Not a <c>MemoryStream</c> subclass: <c>MemoryStream</c>'s <c>Read(Span&lt;byte&gt;)</c> is
    ///     its own override, so a subclass that narrowed only the array form would be bypassed by
    ///     whichever one the reader happens to call. Both are narrowed here.
    /// </remarks>
    sealed class Trickle(byte[] bytes) : Stream {
        int position;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => bytes.Length;

        /// <inheritdoc />
        public override long Position {
            get => position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override int Read(Span<byte> buffer) {
            if (buffer.Length == 0 || position >= bytes.Length) {
                return 0;
            }

            buffer[0] = bytes[position++];

            return 1;
        }

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
