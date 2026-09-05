// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Xunit;

namespace Tests;

/// <summary>What a kernel's own constants do to doc 48 § D8 when a bake scales a length past them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A ceiling inside a kernel is a second place a resolution can be decided, and § D8's
///         whole point is that there is only one.</b>
///         <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a>: <c>Blur</c> clamped the
///         radius it was handed to a constant 64 — after <see cref="TexturePlan.Resolve" /> had
///         already scaled it into the written image's texels. An authored radius of 20 resolves to 20
///         at <c>BakeLevelOffset</c> 0 and to 80 at −2, so the 4× bake was a *narrower* filter than
///         the 1× one, with no message anywhere. That is the two-year fuse
///         <a href="https://github.com/Rikarin/Vixen/issues/619">#619</a> was opened to remove,
///         reintroduced one layer below the fix.
///     </para>
///     <para>
///         ⚠ <b>The existing § D8 blur test escapes it by the radius it happened to pick.</b>
///         <c>TexturePlanDeviceTests</c> authors 12, whose 4× value is 48 and sits just under the
///         cliff. Anything above 16 falls off it at a 4× bake and anything above 32 at a 2× one — so
///         the next case with a larger radius would have gone red *for this reason* while the
///         branch's own risk note blamed the <c>2r+1</c> slope residual. This file is that case,
///         written down.
///     </para>
///     <para>
///         <b>What the fix is, and what it is not.</b> The constant survives, because a loop bound
///         that a NaN or a four-zero radius can run away with is a device loss rather than a slow
///         bake. What changed is what it bounds: the number of <em>taps</em>, not the width. Past the
///         budget the same width is covered by taps spaced further apart, so the filter a plan
///         authored is the filter every bake of it applies and only the sampling inside it thins.
///     </para>
/// </remarks>
public class TextureBudgetDeviceTests(ITestOutputHelper output) {
    /// <summary>The authoring resolution, standing in for § D8's 1K.</summary>
    const int Authored = 64;

    /// <summary>The bake resolution. Four times the authoring one, which is the 1K-to-4K ratio.</summary>
    const int Large = 256;

    /// <summary>
    ///     ⚠ Above 16, which is what makes this test the one the escaped case was not.
    /// </summary>
    /// <remarks>
    ///     20 at the base resolves to 80 at a 4× bake, and <c>Blur</c>'s tap budget is 64. The old
    ///     clamp turned that 80 into 64 — an effective authored radius of 16 in the fine bake against
    ///     20 in the coarse one.
    /// </remarks>
    const float RadiusAtBase = 20f;

    static TextureOp Blur(int output, int input, float radiusAtBase, int stepX, int stepY) =>
        new() {
            Kernel = "Blur",
            Output = output,
            Inputs = [input],
            Parameters = [
                new("radius", radiusAtBase, TextureParameterUnit.TexelsAtBase),
                new("stepX", stepX),
                new("stepY", stepY)
            ]
        };

    /// <summary>A vertical edge down the middle: the one shape that is the same picture at both sizes.</summary>
    static byte[] Step(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var value = (byte)(x < side / 2 ? 0 : 255);
                var at = ((y * side) + x) * 4;

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>How wide the blurred edge is, in texels of the picture it is measured in.</summary>
    /// <remarks>
    ///     <para>
    ///         A box of radius <c>r</c> over a step covers <c>2r + 1</c> texels, so this is the
    ///         radius read straight off the picture — which is what makes a failure legible in the
    ///         output rather than only in a difference.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it counts is <c>2r</c>, not <c>2r + 1</c>, and the missing one is real.</b>
    ///         The texel <c>r</c> away on the dark side has every tap but one on the dark side of the
    ///         edge, so its value is <c>0/(2r+1)</c> — indistinguishable from the flat it came out
    ///         of. Counting the texels that are neither black nor white therefore finds one fewer at
    ///         each radius, at every radius, which is a constant and not a tolerance.
    ///     </para>
    /// </remarks>
    static int RampWidth(Bitmap picture, int row) {
        var first = -1;
        var last = -1;

        for (var x = 0; x < picture.Width; x++) {
            var value = TextureKernelHarness.At(picture, x, row, 0);

            if (value is > 3 and < 252) {
                if (first < 0) {
                    first = x;
                }

                last = x;
            }
        }

        return first < 0 ? 0 : last - first + 1;
    }

    /// <summary>
    ///     ⚠ A radius whose 4× value is past the kernel's tap budget still bakes the same material.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The § D8 criterion, at a radius that crosses the cliff: one plan authored at 64,
    ///         baked once at <c>BakeLevelOffset</c> 0 and once at −2, the larger box-downsampled 4:1
    ///         and compared with the smaller texel for texel.
    ///     </para>
    ///     <para>
    ///         <b>The ramp widths in the output are the diagnosis.</b> A box of radius <c>r</c>
    ///         spreads a step over <c>2r</c> texels that are neither black nor white, so the two
    ///         bakes owe 40 and 160. With the radius clipped at 64 the fine bake's is 126 — an
    ///         authored 16 where 20 was written — and the two profiles then part by 26/255 against a
    ///         tolerance of 8. Spaced taps measure 158, and part by 2.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radius_past_the_tap_budget_is_still_the_radius_the_plan_resolved() {
        using var device = TextureKernelHarness.Open();

        const int Factor = Large / Authored;

        var (small, smallStaging) = TextureKernelHarness.Upload(device, Step(Authored), Authored, Authored);
        var (large, largeStaging) = TextureKernelHarness.Upload(device, Step(Large), Large, Large);

        using var evaluator = new TexturePlanEvaluator(device);

        var at1x = Bake(small, 0);
        var at4x = Bake(large, TexturePlan.BakeLevelFor(Authored, Large));

        Assert.Equal(Authored, at1x.Width);
        Assert.Equal(Large, at4x.Width);

        var coarseRamp = RampWidth(at1x, Authored / 2);
        var fineRamp = RampWidth(at4x, Large / 2);

        var reduced = Downsample(at4x, Factor);
        var worst = 0;
        var worstAt = 0;

        for (var x = 0; x < Authored; x++) {
            var difference = Math.Abs(TextureKernelHarness.At(at1x, x, Authored / 2, 0) - reduced[x]);

            if (difference > worst) {
                worst = difference;
                worstAt = x;
            }
        }

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"adapter: {TextureKernelHarness.Adapter(device)}; the 1× ramp is {coarseRamp} texels wide "
                + $"(2r says {2 * RadiusAtBase}), the 4× ramp is {fineRamp} "
                + $"(2r says {2 * RadiusAtBase * Factor}); worst {worst}/255 at column {worstAt}"
            )
        );

        // Read off the fine bake alone, so the failure names the radius rather than a difference: a
        // ramp of about 126 is a radius of 64, which is the clamp, and 158 is the 80 the plan
        // resolved. The threshold sits between them with room on both sides — the two candidates are
        // thirty texels apart, and neither is near it.
        Assert.True(
            fineRamp > 145,
            $"the 4× bake's blurred edge is {fineRamp} texels wide, so its radius is about "
            + $"{fineRamp / 2}; the plan resolved {RadiusAtBase * Factor} and a ramp of 126 is the "
            + $"kernel's own tap budget ({TextureKernelHarness.Adapter(device)})"
        );

        Assert.True(
            worst <= 8,
            $"the 4× bake downsampled differs from the 1× bake by {worst}/255 at column {worstAt} on "
            + $"{TextureKernelHarness.Adapter(device)}, and § D8 says the two are the same material"
        );

        device.Destroy(smallStaging);
        device.Destroy(small);
        device.Destroy(largeStaging);
        device.Destroy(large);

        return;

        Bitmap Bake(TextureHandle source, int bake) {
            var plan = new TexturePlan {
                BaseWidth = Authored,
                BaseHeight = Authored,
                BakeLevelOffset = bake,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba16Float)],
                Ops = [Blur(1, 0, RadiusAtBase, 1, 0)],
                Outputs = [1]
            };

            Assert.Empty(plan.Validate());

            using var baked = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

            return baked.Read(1);
        }

        int[] Downsample(Bitmap picture, int factor) {
            var row = new int[picture.Width / factor];

            for (var x = 0; x < row.Length; x++) {
                var sum = 0;

                for (var dy = 0; dy < factor; dy++) {
                    for (var dx = 0; dx < factor; dx++) {
                        sum += TextureKernelHarness.At(
                            picture,
                            (x * factor) + dx,
                            ((picture.Height / 2 / factor) * factor) + dy,
                            0
                        );
                    }
                }

                row[x] = sum / (factor * factor);
            }

            return row;
        }
    }

    /// <summary>
    ///     Under the budget nothing changed: the taps are still one texel apart, so a step comes out
    ///     as a <b>straight line</b> of exactly <c>2r + 1</c> equal steps.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half of the fix that is worth a test of its own.</b> Spacing the taps only
    ///         past the budget means every radius an artist is likely to author takes exactly the path
    ///         it took before, and the closed form for that is the box's own step response: with 25
    ///         unit-weighted taps, the texel <c>d</c> to the right of the edge has <c>d + 13</c> of
    ///         them on the white side, so the ramp climbs by <c>255 / 25</c> per texel and by nothing
    ///         else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measuring the ramp's <em>width</em> instead proves nothing, and that is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/707">#707</a>.</b> The obvious
    ///         sabotage — <c>spacing = wide / MaxTaps</c>, dropping the <c>max(1f, …)</c> that
    ///         confines the spreading to radii past the budget — leaves the width at exactly 24 and
    ///         the whole file green. At a radius of 12 that spacing is 0.1875, the loop runs all 64
    ///         iterations, and <c>round(i · 0.1875)</c> still tops out at 12: the reach is unchanged
    ///         and only the <em>weights</em> move, three taps piling onto the outermost offset and two
    ///         onto the centre. ⚠ <b>So the taps bunch together rather than spreading apart</b> — the
    ///         superseded remark here said "would round the taps apart and widen it", and both halves
    ///         of that were false. What the bunching does is bend the straight line into an S, which
    ///         is what this now reads: 5.9/255 where the box owes 10.2 at the ramp's foot.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radius_inside_the_budget_is_a_box_of_unit_spaced_taps() {
        using var device = TextureKernelHarness.Open();

        var (source, staging) = TextureKernelHarness.Upload(device, Step(Authored), Authored, Authored);

        using var evaluator = new TexturePlanEvaluator(device);

        const int Radius = 12;
        const int Taps = (2 * Radius) + 1;
        const int Edge = Authored / 2;

        var plan = new TexturePlan {
            BaseWidth = Authored,
            BaseHeight = Authored,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba16Float)],
            Ops = [Blur(1, 0, Radius, 1, 0)],
            Outputs = [1]
        };

        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });
        var picture = bake.Read(1);
        var width = RampWidth(picture, Edge);

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}; a radius of {Radius} ramps over {width} texels");

        Assert.Equal(2 * Radius, width);

        // The whole profile, texel by texel, against the box's own step response. A tap at offset
        // `d` from texel `x` lands on the white side when `x + d >= Edge`, so the count is
        // `x - Edge + Radius + 1` clamped into 0..Taps — one equal step per texel and no curve
        // anywhere in it.
        for (var x = Edge - Radius - 2; x <= Edge + Radius + 2; x++) {
            var lit = Math.Clamp(x - Edge + Radius + 1, 0, Taps);
            var expected = (int)MathF.Round(255f * lit / Taps);
            var measured = TextureKernelHarness.At(picture, x, Edge, 0);

            Assert.True(
                Math.Abs(measured - expected) <= 2,
                $"texel {x} reads {measured} and a {Taps}-tap box owes {expected} ({lit} of {Taps} taps on the "
                + $"white side) on {TextureKernelHarness.Adapter(device)} — the profile is not a straight ramp, "
                + "so the taps are not one texel apart"
            );
        }

        device.Destroy(staging);
        device.Destroy(source);
    }
}
