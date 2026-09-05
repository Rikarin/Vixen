// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Two claims a § 4.3 header makes that no test in the branch put a number to.</summary>
/// <remarks>
///     <para>
///         <b>A closed form written in a comment and never asserted is a claim, not a property</b> —
///         and both of these turned out to be worth checking. <c>Mirror</c>'s header said a flip
///         "applied twice with the same axis and offset is the identity, bit for bit"; that is false
///         for every offset but 0.5, and the file has been corrected rather than the kernel.
///         <c>Tile</c>'s header calls the per-tile shift "what makes this a brick node rather than a
///         <c>frac</c>" — and no test ever gave <c>offsetX</c> or <c>offsetY</c> a value other than
///         zero, so the node's whole point was uncovered.
///     </para>
/// </remarks>
public class TextureSpaceClaimDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    static TextureOp Op(string kernel, int output, int input, TextureParameter[] parameters) =>
        new() { Kernel = kernel, Output = output, Inputs = [input], Parameters = [.. parameters] };

    static TextureParameter[] Flip(float offset) => [
        new("axis", 0f),
        new("mode", 1f),
        new("offset", offset)
    ];

    /// <summary>One op over one uploaded picture.</summary>
    static Bitmap OneOp(VulkanDevice device, byte[] source, TextureOp op) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
                Ops = [op],
                Outputs = [1]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(1);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>The same op twice in one plan, the second reading the first's image.</summary>
    static Bitmap Twice(VulkanDevice device, byte[] source, TextureParameter[] parameters) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [Op("Mirror", 1, 0, parameters), Op("Mirror", 2, 1, parameters)],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(2);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>How many texels of two pictures disagree in any of three channels.</summary>
    static int Differing(Bitmap expected, Bitmap actual) {
        var count = 0;

        for (var y = 0; y < expected.Height; y++) {
            for (var x = 0; x < expected.Width; x++) {
                for (var channel = 0; channel < 3; channel++) {
                    if (TextureKernelHarness.At(expected, x, y, channel)
                        != TextureKernelHarness.At(actual, x, y, channel)) {
                        count++;

                        break;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    ///     ⚠ A flip is its own inverse about the middle of the image, and about no other line.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The claim, and the correction.</b> <c>Mirror.rvn</c> said a flip applied twice with
    ///         the same axis and offset is the identity, bit for bit. The arithmetic is
    ///         <c>x ↦ round(2ℓ − x)</c> with <c>ℓ = offset·extent − 0.5</c>, which is an involution
    ///         wherever it lands inside the image — but the store clamps, and <b>a clamp is not
    ///         invertible</b>. At <c>offset</c> 0.5 the map is <c>x ↦ extent − 1 − x</c>, a
    ///         permutation of the texels, and nothing is ever clamped; at any other offset part of
    ///         the image mirrors to a coordinate outside it, every one of those texels collapses onto
    ///         the same edge column, and the second application cannot tell them apart.
    ///     </para>
    ///     <para>
    ///         <b>The second half names what the clamp actually does</b>, so the corrected sentence
    ///         is a property rather than a hedge: at <c>offset</c> 0.25 on a 64-wide image the line
    ///         sits at 15.5 and <c>x ↦ 31 − x</c>, so <em>every</em> column from 32 rightwards is
    ///         column 0 of the source, repeated.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_flip_is_its_own_inverse_about_the_middle_and_not_about_another_line() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Unique(Side);
        var picture = new Bitmap(Side, Side, source);

        var middle = Twice(device, source, Flip(0.5f));
        var quarter = Twice(device, source, Flip(0.25f));

        var lost = Differing(picture, quarter);

        output.WriteLine(
            $"adapter: {TextureKernelHarness.Adapter(device)}; twice about the middle differs in "
            + $"{Differing(picture, middle)} texels, twice about a quarter in {lost}"
        );

        TextureKernelHarness.AssertSame(
            picture,
            middle,
            3,
            $"a flip about the middle applied twice on {TextureKernelHarness.Adapter(device)}"
        );

        // ⚠ And the header's claim, refuted with a number. Half the image is unrecoverable, so this
        // is not a rounding difference that a tolerance would hide.
        Assert.True(
            lost > Side * Side / 4,
            $"a flip about 0.25 applied twice left {lost} texels of {Side * Side} different from the "
            + $"source, and the file used to claim it was the identity ({TextureKernelHarness.Adapter(device)})"
        );

        // What the clamp does, stated exactly: the line is at 15.5, so x ↦ 31 − x and every column
        // from 32 on is column 0.
        //
        // ⚠ Read in the red channel, which is the only one of `Unique`'s four that varies along x.
        // Written against green — which is a function of y alone — this assertion stayed green under
        // a sabotage that replaced the clamp with a wrap, because every column it could have named
        // has the same green. A probe has to be read in a channel that separates the candidates.
        var once = OneOp(device, source, Op("Mirror", 1, 0, Flip(0.25f)));

        for (var y = 0; y < Side; y += 7) {
            for (var x = 32; x < Side; x += 5) {
                Assert.Equal(TextureKernelHarness.At(picture, 0, y, 0), TextureKernelHarness.At(once, x, y, 0));
            }
        }
    }

    /// <summary>
    ///     ⚠ <c>Tile</c>'s per-tile shift is a running bond: every second tile row is offset half a
    ///     tile.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The node's whole point, and the branch gave it no value but zero.</b> A repeat with
    ///         both offsets at zero is a plain grid, which is what every existing tile test asserts;
    ///         <c>offsetX</c> at 0.5 with a repeat of two shifts the lower tile row half a tile along
    ///         x, which is the brick pattern § 4.9's list starts from.
    ///     </para>
    ///     <para>
    ///         <b>The closed form is exact, and it is a shift and not a resemblance.</b> Half a tile
    ///         at a repeat of two is a quarter of the image — sixteen texels on a 64 — so the bottom
    ///         half of the output is the top half moved sixteen columns left:
    ///         <c>out(x, y + 32) = out(x + 16, y)</c>, byte for byte, for every x it can be read at.
    ///         A source with no vertical variation is what makes the two halves comparable at all,
    ///         and a ramp is also what makes the shift visible in the numbers rather than only in the
    ///         equality.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_tile_row_offset_shifts_every_second_row_by_that_fraction_of_a_tile() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Ramp(Side);

        var plain = OneOp(
            device,
            source,
            Op(
                "Tile",
                1,
                0,
                [new("repeatX", 2f), new("repeatY", 2f), new("offsetX", 0f), new("offsetY", 0f)]
            )
        );

        var bonded = OneOp(
            device,
            source,
            Op(
                "Tile",
                1,
                0,
                [new("repeatX", 2f), new("repeatY", 2f), new("offsetX", 0.5f), new("offsetY", 0f)]
            )
        );

        output.WriteLine(
            $"adapter: {TextureKernelHarness.Adapter(device)}; at column 8 the grid reads "
            + $"{TextureKernelHarness.At(plain, 8, 8, 0)} above and {TextureKernelHarness.At(plain, 8, 40, 0)} "
            + $"below, the bond {TextureKernelHarness.At(bonded, 8, 8, 0)} and "
            + $"{TextureKernelHarness.At(bonded, 8, 40, 0)}"
        );

        // Without the offset the two tile rows are the same row, which is the grid the existing
        // tests assert — and the premise that makes the shift below a claim about the parameter.
        for (var y = 0; y < Side / 2; y += 3) {
            for (var x = 0; x < Side; x += 5) {
                Assert.Equal(
                    TextureKernelHarness.At(plain, x, y, 0),
                    TextureKernelHarness.At(plain, x, y + (Side / 2), 0)
                );
            }
        }

        // With it, the lower row is the upper one shifted by half a tile: a quarter of the image.
        for (var y = 0; y < Side / 2; y += 3) {
            for (var x = 0; x + (Side / 4) < Side; x += 5) {
                Assert.Equal(
                    TextureKernelHarness.At(bonded, x + (Side / 4), y, 0),
                    TextureKernelHarness.At(bonded, x, y + (Side / 2), 0)
                );
            }
        }
    }
}
