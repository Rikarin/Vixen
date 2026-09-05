// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Ui.Text;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.1's <c>Text</c> from one end to the other on a real device: a string, shaped and
///     filled on the CPU, uploaded as an external image, and read by a kernel.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this closes is the seam, not the rasteriser.</b>
///         <see cref="TextureTextTests" /> says the coverage is right and needs no adapter;
///         everything that can still be wrong is between that array and a texel a kernel samples —
///         the format, the row order, the quantisation, the queue the copy went to and the state the
///         texture is left in. Every one of those produces a picture rather than an error.
///     </para>
///     <para>
///         ⚠ <b>The closed form is equality, in eight bits, texel for texel.</b> The kernel is an
///         <c>Invert</c> with every amount at zero, which is a copy — so what comes back is
///         <see cref="TextureUploads.Quantize" /> of the coverage and nothing else. A flipped row
///         order gives the string upside down and is a *different* exact answer; a truncating
///         quantisation is out by one nearly everywhere; an <c>R8</c> read as two channels halves the
///         width. None of them is near, and none of them is blank.
///     </para>
///     <para>
///         ⚠ <b>And <c>R8</c> is the point of the format here.</b>
///         <see cref="TextureFormats.IsStorable" /> is false for it — no conformant device promises a
///         storage image of one <c>unorm</c> channel — and uploading one is fine, which is what
///         <see cref="TextureUploads.Add" />'s own remarks say and what a mask costs a quarter of RGBA
///         for. A plan that made this <c>Rgba8</c> would pass and would be paying four times over for
///         a glyph.
///     </para>
/// </remarks>
public class TextureTextDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>A rasterised string reaches a kernel as the mask it was.</summary>
    [Fact]
    public void A_rasterised_string_reaches_a_kernel_as_the_mask_it_was() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var coverage = TextureText.Rasterize(Font(), "Vixen", Side, Side, 28f, TextureTextAlignment.Centre);

        // The instrument first: an all-zero field would make every comparison below a claim that
        // black equals black, on a path where black is exactly what a broken upload produces.
        Assert.Contains(coverage, value => value > 0.5f);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.R8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Invert",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [new("invertR", 0f), new("invertG", 0f), new("invertB", 0f), new("invertA", 0f)]
                }
            ],
            Outputs = [1]
        };

        Assert.Empty(plan.Check());

        using var uploads = new TextureUploads(device);

        uploads.AddCoverage(plan, 0, Side, Side, coverage);

        Assert.Equal(new(Side, Side), uploads.SizeOf(0));

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(1);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(
                    TextureUploads.Quantize(coverage[(y * Side) + x]),
                    TextureKernelHarness.At(picture, x, y, 0)
                );
            }
        }

        // ⚠ And the other three channels are what a single-channel sampled read gives: (r, 0, 0, 1).
        // Asserting only red would leave a plan that uploaded the mask into all four channels
        // indistinguishable from this one, and that plan costs four times the memory.
        Assert.Equal(0, TextureKernelHarness.At(picture, Side / 2, Side / 2, 1));
        Assert.Equal(0, TextureKernelHarness.At(picture, Side / 2, Side / 2, 2));
        Assert.Equal(255, TextureKernelHarness.At(picture, Side / 2, Side / 2, 3));
    }

    /// <summary>An upload of a coverage field into anything but an <c>R8</c> is refused by name.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is what stops the quarter-cost decision being made by accident.</b> A
    ///     coverage field written into an <c>Rgba8</c> would upload perfectly well — one channel of
    ///     four, three of them zero — and would draw the same picture, which is why nothing downstream
    ///     would ever say so.
    /// </remarks>
    [Fact]
    public void A_coverage_field_goes_into_a_single_channel_image() {
        using var device = TextureKernelHarness.Open();

        var plan = new TexturePlan {
            BaseWidth = 8,
            BaseHeight = 8,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Invert",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [new("invertR", 0f), new("invertG", 0f), new("invertB", 0f), new("invertA", 0f)]
                }
            ],
            Outputs = [1]
        };

        using var uploads = new TextureUploads(device);

        var failure = Assert.Throws<ArgumentException>(
            () => uploads.AddCoverage(plan, 0, 8, 8, new float[64])
        );

        Assert.Contains("R8", failure.Message, StringComparison.Ordinal);
    }

    static FontFace Font() {
        using var stream = typeof(TextureTextDeviceTests).Assembly.GetManifestResourceStream(
            "Fonts.OpenSans-Regular.ttf"
        );

        Assert.NotNull(stream);

        using var bytes = new MemoryStream();

        stream.CopyTo(bytes);

        return FontFace.Load(bytes.ToArray());
    }
}
