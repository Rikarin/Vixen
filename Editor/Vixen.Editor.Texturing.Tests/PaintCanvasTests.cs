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

    /// <summary>A channel is created on first use and kept in the order it was asked for.</summary>
    [Fact]
    public void A_canvas_holds_only_the_channels_the_layer_writes() {
        PaintCanvas canvas = new(16, 16);

        canvas.Channel("roughness");

        Assert.True(canvas.Has("roughness"));
        Assert.False(canvas.Has("baseColor"));
        Assert.Single(canvas.Channels);
    }
}
