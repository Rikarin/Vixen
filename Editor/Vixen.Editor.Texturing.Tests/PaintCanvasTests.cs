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

    /// <summary>A file cut off in its header is refused rather than read as a half-painted layer.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the <em>header</em> case and it is now named as one</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/959">#959</a>. It cut a constant 64 bytes
    ///     and was written when a channel was raw texels, so the cut landed in 4 KiB of them. Version
    ///     2 deflates, and a flat 32² mask compresses to 44 bytes in a 77-byte file — so the same cut
    ///     started landing thirteen bytes in, before the width field, and the two truncation paths
    ///     compression introduced went unexercised while this stayed green. The measurement is in the
    ///     assertion below rather than in a comment, so the day the framing changes it says so.
    /// </remarks>
    [Fact]
    public void A_paint_file_cut_off_in_its_header_is_refused() {
        PaintCanvas canvas = new(32, 32);

        canvas.Channel("mask").Fill(0xFFFFFFFFu);

        using MemoryStream stream = new();

        canvas.Write(stream);

        var bytes = stream.ToArray();

        // The instrument. `Read` walks magic · version · width, so a cut that left more than sixteen
        // bytes would be refused somewhere else and this test would be about somewhere else.
        Assert.InRange(bytes.Length - 64, 9, 16);

        using MemoryStream cut = new(bytes[..(bytes.Length - 64)]);

        var refused = Assert.Throws<InvalidDataException>(() => PaintCanvas.Read(cut));

        Assert.Contains("ends before the canvas its header describes", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠ And a file cut off inside a channel's compressed block is refused too.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The path <a href="https://github.com/Rikarin/Vixen/issues/959">#959</a> found
    ///         unreachable.</b> <c>PaintCanvas.Inflate</c> takes the block whole and refuses one
    ///         shorter than its own declared length — a guard whose message names two byte counts and
    ///         which nothing had ever produced, because the only truncation test cut a file so small
    ///         that the cut never reached a block.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A fraction rather than a constant, and a canvas that does not compress to
    ///         nothing.</b> Those are the same defect twice: a constant cut is a cut whose meaning
    ///         changes when the format's density changes, and a flat canvas is one whose body is
    ///         smaller than any interesting constant. The stroke is what <c>PaintStroke</c> writes,
    ///         which is the least compressible content this format holds.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two byte counts are on the <em>inner</em> exception and that is a finding
    ///         rather than a detail of this test.</b> <c>Read</c> wraps every
    ///         <see cref="EndOfStreamException" /> in one sentence about a truncated file — which is
    ///         the right thing for a caller, since both are the same fact — so the guard's own message
    ///         reaches a log and never a person. Asserting on the inner is what makes this test about
    ///         the block path rather than about the wrapper, which a header cut produces too.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_paint_file_cut_off_inside_a_compressed_channel_is_refused() {
        const int Side = 128;

        PaintCanvas canvas = new(Side, Side);
        var colour = canvas.Channel("baseColor");

        PaintStroke stroke = new(
            colour,
            PaintCoverage.Everywhere(Side, Side),
            PaintStrokeTests.Hard(Side / 8f) with { Spacing = 0.5f },
            0xFF3366CCu
        );

        stroke.MoveTo(new(16f, 16f));
        stroke.MoveTo(new(Side - 16f, Side - 16f));

        using MemoryStream stream = new();

        canvas.Write(stream);

        var bytes = stream.ToArray();

        // The instrument, and it is the whole point of this test. 33 bytes of magic, version, extent,
        // channel count, the channel's name and its declared compressed length come before the block;
        // half the file has to be past that or the cut is the header case one test up.
        Assert.True(bytes.Length / 2 > 64, $"a stroked {Side}² channel wrote {bytes.Length} bytes, which is "
            + "small enough that half of it is still the header — so this cut lands where the test above "
            + "already cuts.");

        using MemoryStream cut = new(bytes[..(bytes.Length / 2)]);

        var refused = Assert.Throws<InvalidDataException>(() => PaintCanvas.Read(cut));
        var inner = Assert.IsType<EndOfStreamException>(refused.InnerException);

        Assert.Contains("compressed bytes for a channel and holds", inner.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠ A channel declaring a negative compressed length is refused, not allocated for.</summary>
    /// <remarks>
    ///     The third path compression introduced, and the one a corrupt or hostile file reaches first:
    ///     <c>BinaryReader.ReadBytes</c> takes an <see langword="int" /> and a negative one throws an
    ///     <see cref="ArgumentOutOfRangeException" />, which is not what a caller of a file reader
    ///     catches. The guard turns it into the same <see cref="InvalidDataException" /> every other
    ///     malformed <c>.vxpaint</c> produces.
    /// </remarks>
    [Fact]
    public void A_channel_declaring_a_negative_compressed_length_is_refused() {
        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(PaintCanvas.Magic);
            writer.Write(PaintCanvas.CurrentVersion);
            writer.Write(8);
            writer.Write(4);
            writer.Write(1);
            writer.Write("baseColor");
            writer.Write(-1);
        }

        stream.Position = 0;

        var refused = Assert.Throws<InvalidDataException>(() => PaintCanvas.Read(stream));

        Assert.Contains("-1 compressed bytes", refused.Message, StringComparison.Ordinal);
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

    /// <summary>⚠ The file is a fraction of the texels it holds, which is the whole of version 2.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/850">#850</a>, and the round trip
    ///         above cannot see it.</b> A writer that reverted to raw texels round-trips perfectly, so
    ///         the only assertion that can tell the two apart is the size of what was written.
    ///     </para>
    ///     <para>
    ///         <b>The content is a stroke over transparency</b> — the case the type's remarks describe
    ///         as "mostly one flat colour under a coverage ramp", and the least compressible of the
    ///         three that were measured. An empty or flat canvas reaches 1%, which would make the
    ///         bound below true of a format that only happened to store zeroes well.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A quarter, not the 6.4% that was measured.</b> The measurement is a fact about one
    ///         stroke at 4K; the assertion is the property — a paint canvas is <em>compressible and is
    ///         compressed</em> — and a bound tuned to the sample would go red on a brush change that
    ///         is nobody's defect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_written_canvas_is_a_fraction_of_the_texels_it_holds() {
        const int Side = 256;

        PaintCanvas canvas = new(Side, Side);
        var colour = canvas.Channel("baseColor");

        PaintStroke stroke = new(
            colour,
            PaintCoverage.Everywhere(Side, Side),
            PaintStrokeTests.Hard(Side / 24f) with { Spacing = 0.5f },
            0xFF3366CCu
        );

        for (var step = 0; step <= 40; step++) {
            var t = step / 40f;

            stroke.MoveTo(new((t * (Side - 32)) + 16f, (MathF.Sin(t * MathF.Tau) * (Side / 4f)) + (Side / 2f)));
        }

        Assert.True(stroke.StampCount > 20, $"only {stroke.StampCount} stamps, so the canvas is nearly empty");

        using MemoryStream stream = new();

        canvas.Write(stream);

        var raw = (long)colour.Texels.Length;

        Assert.True(
            stream.Length < raw / 4,
            $"a {Side}² stroked channel wrote {stream.Length} bytes against {raw} of texels. A .vxpaint "
            + "is storing raw texels again, which at 4K is 64 MiB a channel and 192 for a layer that "
            + "paints three."
        );

        // And it is still the picture: a size assertion on its own is satisfied by a writer that
        // dropped the channel.
        stream.Position = 0;

        Assert.Equal(colour.Texels, PaintCanvas.Read(stream).Channel("baseColor").Texels);
    }

    /// <summary>⚠ A version-1 file — raw texels — still opens, one byte at a time.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An artist's existing paintings are the reason version 1 is read rather than
    ///         refused.</b> The bytes are laid out here by hand rather than by an old build, which is
    ///         the only way this can be a test at all once the writer has moved on: a fixture written
    ///         by <see cref="PaintCanvas.Write" /> would be version 2 and would prove nothing about
    ///         the branch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Through <see cref="Trickle" />, and that is not decoration — it is the raw path's
    ///         short-read loop, which version 2 quietly took the last caller away from.</b> The
    ///         drip-feed test above used to be what proved that loop, and it now writes a compressed
    ///         file: with the loop replaced by a single <c>Read</c> every test in this file stayed
    ///         green, which was checked. This is the one that goes red.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_version_one_canvas_of_raw_texels_still_opens() {
        PaintImage expected = new(8, 4, 0xFF3366CCu);

        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(PaintCanvas.Magic);
            writer.Write(PaintCanvas.OldestVersion);
            writer.Write(8);
            writer.Write(4);
            writer.Write(1);
            writer.Write("baseColor");
            writer.Write(expected.Texels);
        }

        using Trickle trickle = new(stream.ToArray());

        var read = PaintCanvas.Read(trickle);

        Assert.Equal(["baseColor"], read.Channels);
        Assert.Equal(expected.Texels, read.Channel("baseColor").Texels);
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
