// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Layers;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     Paint wider than eight bits a lane (Patina #19): a channel held as half or single floats keeps what eight bits
///     round away, through a stroke, a file, a change of size and an undo, and an eight-bit channel is as it was.
/// </summary>
public class PaintTexelFormatTests {
    const int Side = 64;

    /// <summary>A value between two eight-bit levels: 76.5 of 255.</summary>
    static readonly Vector4 Between = new(0.3f, 0.3f, 0.3f, 1f);

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 12f, Flow = 1f, Falloff = 0f };

    static PaintImage Stroked(PaintTexelFormat format) {
        PaintImage image = new(Side, Side, format);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(Side, Side), Hard, 0u, 0) { Colour = Between };

        stroke.MoveTo(new(20f, 32f));
        stroke.MoveTo(new(44f, 32f));

        return image;
    }

    [Theory]
    [InlineData(PaintTexelFormat.Rgba16Float, 1e-3f)]
    [InlineData(PaintTexelFormat.Rgba32Float, 1e-6f)]
    public void A_stroke_on_a_wide_channel_lays_what_eight_bits_would_round(PaintTexelFormat format, float within) {
        var painted = Stroked(format).Read(32, 32);
        var rounded = Stroked(PaintTexelFormat.Rgba8).Read(32, 32);

        Assert.InRange(painted.X, Between.X - within, Between.X + within);
        Assert.True(MathF.Abs(rounded.X - Between.X) > 1e-3f, $"Eight bits laid {rounded.X}.");
    }

    /// <summary>A channel's width and a set's description survive the stack file, and a default writes neither.</summary>
    [Fact]
    public void A_channel_width_and_a_set_description_are_written_only_when_chosen() {
        LayerStackAsset stack = new() {
            Name = "Crate",
            Sets = [
                new() {
                    Name = "Body",
                    Description = "Painted metal: rust in the seams.\nKeep the decals sharp.",
                    Channels = [
                        new() { Usage = "baseColor" },
                        new() { Usage = "height", Format = PaintTexelFormat.Rgba32Float }
                    ]
                },
                new() { Name = "Lid", Channels = [new() { Usage = "baseColor" }] }
            ]
        };

        var text = LayerStackYaml.Write(stack);
        var read = LayerStackYaml.Read(text);

        Assert.Equal(stack.Sets[0].Description, read.Sets[0].Description);
        Assert.Equal(PaintTexelFormat.Rgba32Float, read.Sets[0].Channels[1].Format);
        Assert.Equal(PaintTexelFormat.Rgba8, read.Sets[0].Channels[0].Format);
        Assert.Equal(string.Empty, read.Sets[1].Description);
        Assert.Equal(2, text.Split("format:").Length);
        Assert.Equal(2, text.Split("description:").Length);
    }

    [Fact]
    public void A_canvas_with_a_wide_channel_reads_back_as_it_was_written() {
        PaintCanvas canvas = new(Side, Side);
        canvas.Channel("baseColor").Put(3, 4, 0x80402010u);
        var height = canvas.Channel("height", PaintTexelFormat.Rgba16Float);
        height.Fill(new Vector4(0.25f, 0.25f, 0.25f, 1f));
        height.Write(40, 50, Between);

        using MemoryStream written = new();
        canvas.Write(written);
        written.Position = 0;
        var read = PaintCanvas.Read(written);

        Assert.Equal(PaintTexelFormat.Rgba8, read.Channel("baseColor").Format);
        Assert.Equal(0x80402010u, read.Channel("baseColor").At(3, 4));
        Assert.Equal(PaintTexelFormat.Rgba16Float, read.Channel("height").Format);
        Assert.Equal(height.Read(40, 50), read.Channel("height").Read(40, 50));
        Assert.Equal(new Vector4(0.25f, 0.25f, 0.25f, 1f), read.Channel("height").Read(0, 0));
        Assert.Equal(height.Digest, read.Channel("height").Digest);
    }

    /// <summary>A canvas of eight-bit channels is still version 3, which a build that predates widths reads.</summary>
    [Fact]
    public void A_canvas_of_eight_bit_channels_is_written_as_before() {
        PaintCanvas canvas = new(Side, Side);
        canvas.Channel("baseColor").Put(3, 4, 0x80402010u);

        using MemoryStream written = new();
        canvas.Write(written);

        Assert.Equal(3, BitConverter.ToInt32(written.ToArray(), PaintCanvas.Magic.Length));
    }

    [Fact]
    public void A_channel_widened_holds_every_texel_it_held_and_shares_the_rest() {
        PaintCanvas canvas = new(Side, Side);
        var colour = canvas.Channel("baseColor");
        var height = canvas.Channel("height");
        height.Fill(0xFF808080u);
        for (var x = 0; x < Side; x++) {
            height.Put(x, 7, PaintImage.Pack(x / (float)Side, 0.1f, 0.9f, 1f));
        }

        var widened = canvas.Formatted("height", PaintTexelFormat.Rgba16Float);

        Assert.Same(colour, widened.Channel("baseColor"));
        Assert.NotSame(height, widened.Channel("height"));
        Assert.Equal(PaintTexelFormat.Rgba8, height.Format);
        for (var x = 0; x < Side; x++) {
            Assert.Equal(height.At(x, 7), widened.Channel("height").At(x, 7));
            Assert.Equal(height.Read(x, 7), widened.Channel("height").Read(x, 7), new HalfOfEightBits());
        }

        Assert.Equal(height.Background, widened.Channel("height").Background);
    }

    /// <summary>
    ///     A set with a channel painted at 32-bit float computes at 32 bits, so its canvas is not rounded to a half on
    ///     its way through the composite, and a set of narrower channels at the graph's default of sixteen.
    /// </summary>
    [Theory]
    [InlineData(PaintTexelFormat.Rgba32Float, TextureGraph.TextureFormat.Rgba32Float)]
    [InlineData(PaintTexelFormat.Rgba16Float, TextureGraph.TextureFormat.Rgba16Float)]
    [InlineData(PaintTexelFormat.Rgba8, TextureGraph.TextureFormat.Rgba16Float)]
    public void A_set_computes_at_the_depth_its_widest_channel_paints(
        PaintTexelFormat painted,
        TextureGraph.TextureFormat computed
    ) {
        TextureSetAsset set = new() {
            Name = "Body", Channels = [new() { Usage = "baseColor" }, new() { Usage = "height", Format = painted }]
        };

        var compilation = LayerStackCompiler.Compile(new() { Name = "Crate", Sets = [set] }, set);

        Assert.True(compilation.Succeeded, string.Join("; ", compilation.Diagnostics.Select(static d => d.Message)));
        Assert.All(
            compilation.Outputs,
            output => Assert.Equal(computed, compilation.Plan!.Images[output.Image].Format)
        );
    }

    [Fact]
    public void A_wide_channel_enlarged_and_reduced_is_what_it_was() {
        PaintCanvas canvas = new(Side, Side);
        var height = canvas.Channel("height", PaintTexelFormat.Rgba32Float);
        for (var x = 0; x < Side; x++) {
            height.Write(x, 9, new(x / 1000f, 0.123456f, 0.5f, 1f));
        }

        var back = canvas.Enlarged(Side * 2, Side * 4).Reduced(Side, Side).Channel("height");

        Assert.Equal(PaintTexelFormat.Rgba32Float, back.Format);
        for (var x = 0; x < Side; x++) {
            Assert.Equal(height.Read(x, 9), back.Read(x, 9));
        }
    }

    [Fact]
    public void A_stroke_on_a_wide_channel_undoes_to_what_it_held() {
        PaintImage image = new(Side, Side, PaintTexelFormat.Rgba16Float);
        image.Fill(new Vector4(0.1f, 0.2f, 0.3f, 0.4f));
        var digest = image.Digest;
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(Side, Side), Hard, 0u, 0) { Colour = Between };

        stroke.MoveTo(new(20f, 32f));
        Assert.NotEqual(digest, image.Digest);
        stroke.Undo();

        Assert.Equal(digest, image.Digest);
        Assert.Equal(image.BackgroundValue, image.Read(20, 32));
    }

    /// <summary>Two values a half float holds apart by less than one eight-bit level count as one.</summary>
    sealed class HalfOfEightBits : IEqualityComparer<Vector4> {
        public bool Equals(Vector4 x, Vector4 y) => Vector4.Distance(x, y) < 1e-3f;

        public int GetHashCode(Vector4 obj) => 0;
    }
}
