// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.2's sixteen blend modes, on a real device, two closed forms each.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every mode owes its own case, and one closed form per mode is not enough.</b> The
///         standing example is a blend test written against a white foreground: a multiply against
///         white is the backdrop, so the assertion is "the ramp comes back" and it holds for multiply,
///         divide, darken, colour burn and a `Copy` — five modes and a missing kernel. So each mode is
///         read off twice here, and the two are chosen to fail differently.
///     </para>
///     <para>
///         <b>The neutral</b> — <see cref="A_mode_leaves_the_backdrop_alone_at_its_own_neutral" /> —
///         is the foreground value at which the operator is the identity, asserted as an
///         <em>equality</em> over every texel of a ramp. It catches a factor, a missing <c>1 −</c> and
///         a clamp on the wrong side.
///     </para>
///     <para>
///         <b>The distinguishing value</b> —
///         <see cref="A_mode_takes_two_known_operands_to_one_known_answer" /> — is what one backdrop
///         and one foreground produce, and no two of the sixteen agree on it. That is what catches an
///         operand swap, which usually preserves the neutral: hard light copied from overlay without
///         moving the selector <em>is</em> overlay, on every image there is.
///     </para>
///     <para>
///         <b>No CPU twin</b> — doc 48 § D3. The numbers below are written out as constants, arrived
///         at by hand, not by a C# re-implementation looped over the image: a parity test proves two
///         transcriptions agree and not that either is right.
///     </para>
/// </remarks>
public class TextureBlendDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>Each mode with the foreground value at which it does nothing at all.</summary>
    /// <remarks>
    ///     ⚠ <c>Copy</c> is not here and cannot be: it has no neutral by construction, which is also
    ///     why an <em>unimplemented</em> mode is invisible — <c>Combine</c> falls through to the
    ///     foreground, so a missing case is a `Copy`, and a `Copy` is a picture.
    ///     <c>TexturePlacementKernelTests</c> counts the cases instead.
    /// </remarks>
    public static TheoryData<int, float> Neutrals =>
        new() {
            { (int)TextureBlendMode.Multiply, 1f },
            { (int)TextureBlendMode.Screen, 0f },
            { (int)TextureBlendMode.Overlay, 0.5f },
            { (int)TextureBlendMode.Add, 0f },
            { (int)TextureBlendMode.Subtract, 0f },
            { (int)TextureBlendMode.Darken, 1f },
            { (int)TextureBlendMode.Lighten, 0f },
            { (int)TextureBlendMode.Divide, 1f },
            { (int)TextureBlendMode.HardLight, 0.5f },
            { (int)TextureBlendMode.SoftLight, 0.5f },
            { (int)TextureBlendMode.Difference, 0f },
            { (int)TextureBlendMode.Exclusion, 0f },
            { (int)TextureBlendMode.ColourDodge, 0f },
            { (int)TextureBlendMode.ColourBurn, 1f },
            { (int)TextureBlendMode.SignedAdd, 0.5f }
        };

    /// <summary>
    ///     Each of the eight modes this slice added, against a backdrop of ¼ and of ¾ under a
    ///     foreground of 0.6.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No two rows agree in the first column</b>, which is the property that makes this a
    ///     test of <em>which</em> operator ran rather than of whether one did. Worked by hand:
    ///     divide is <c>¼ / 0.6</c>; hard light takes the screen half because the *foreground* is
    ///     above a half, <c>1 − 2·0.75·0.4</c>; soft light is <c>¼ + 0.2·(√¼ − ¼)</c>; difference is
    ///     <c>|¼ − 0.6|</c>; exclusion is <c>0.6 − 0.2·¼</c>; colour dodge is <c>¼ / 0.4</c>; colour
    ///     burn is <c>1 − 0.75/0.6</c>, which clips to black; signed add is <c>¼ + 0.2</c>.
    /// </remarks>
    public static TheoryData<int, int, int> Distinguishing =>
        new() {
            { (int)TextureBlendMode.Divide, 106, 255 },
            { (int)TextureBlendMode.HardLight, 102, 204 },
            { (int)TextureBlendMode.SoftLight, 76, 197 },
            { (int)TextureBlendMode.Difference, 89, 38 },
            { (int)TextureBlendMode.Exclusion, 140, 115 },
            { (int)TextureBlendMode.ColourDodge, 159, 255 },
            { (int)TextureBlendMode.ColourBurn, 0, 149 },
            { (int)TextureBlendMode.SignedAdd, 115, 242 }
        };

    /// <summary>At its neutral a mode reproduces the backdrop, texel for texel and channel for channel.</summary>
    /// <remarks>
    ///     ⚠ <b>An equality over a ramp and not a tolerance over a flat fill.</b> A flat backdrop is
    ///     the assertion every broken operator passes; sixty-four distinct values across the image
    ///     mean a mode that is the identity only near one end of the range fails here.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Neutrals))]
    public void A_mode_leaves_the_backdrop_alone_at_its_own_neutral(int number, float neutral) {
        var mode = (TextureBlendMode)number;

        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Ramp(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    // ⚠ The foreground is a half-float image so that the neutral is *exact*. Written
                    // through an Rgba8 intermediate, 0.5 becomes 128/255 and every mid-grey neutral
                    // is off by a step — which is a real difference of one in the answer and would
                    // have to be absorbed by a tolerance this assertion is stronger without.
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [TextureSources.Uniform(1, neutral), TextureBlend.Mix(2, 0, 1, mode)],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            TextureKernelHarness.AssertSame(
                new(Side, Side, source),
                bake.Read(2),
                4,
                $"{mode} at its neutral {neutral} on {TextureKernelHarness.Adapter(device)}"
            );
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>Two known operands give one known answer, and no two modes give the same one.</summary>
    [Theory]
    [MemberData(nameof(Distinguishing))]
    public void A_mode_takes_two_known_operands_to_one_known_answer(
        int number,
        int overQuarter,
        int overThreeQuarters
    ) {
        var mode = (TextureBlendMode)number;

        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // ⚠ Both operands are computed rather than uploaded, so neither is quantised on the way in.
        // An 8-bit ¼ is 64/255 = 0.2510, and four of these eight answers move by more than a step
        // under that — colour burn's clip is at exactly ¾, so it would be the difference between 0
        // and 3.
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                TextureSources.Uniform(0, 0.25f),
                TextureSources.Uniform(1, 0.75f),
                TextureSources.Uniform(2, 0.6f),
                TextureBlend.Mix(3, 0, 2, mode),
                TextureBlend.Mix(4, 1, 2, mode)
            ],
            Outputs = [3, 4]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        Near(bake.Read(3), overQuarter, $"{mode} of ¼ under 0.6", device);
        Near(bake.Read(4), overThreeQuarters, $"{mode} of ¾ under 0.6", device);
    }

    /// <summary>
    ///     ⚠ Atop over a backdrop that covers nothing leaves that backdrop alone — its colour as well
    ///     as its coverage.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/899">#899</a>: the kernel's own
    ///         comment said this and the arithmetic said the opposite.</b>
    ///         <c>saturate(amount / max(backdrop, 1e-6))</c> at <c>backdrop == 0</c> is
    ///         <c>amount · 10⁶</c>, which saturates to 1 for any foreground that covers anything — so
    ///         the branch whose contract is "atop covers exactly what the backdrop covered" wrote the
    ///         <em>blended</em> colour into every texel the backdrop does not reach.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The stored alpha was already right, which is exactly why this survived.</b> A
    ///         colour at zero coverage is invisible to a compositor and perfectly visible here: every
    ///         image in this library carries a <em>straight</em> colour, so the next adjustment,
    ///         resample or output reads that channel and never asks what its alpha was. A filter layer
    ///         under an empty region of a stack turned it into the filter's own output.
    ///     </para>
    ///     <para>
    ///         <b>The second bake is the instrument.</b> The same two images under <c>over</c> must
    ///         come out the foreground's own grey — a multiply against a backdrop that is not there is
    ///         the thing that was multiplied — so a plan whose backdrop was accidentally opaque, or a
    ///         kernel ignoring the <c>atop</c> uniform altogether, cannot make the first assertion
    ///         pass.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Atop_a_backdrop_that_covers_nothing_keeps_the_backdrop_colour() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                // Three unequal channels at zero coverage, so a result that has been through the
                // operator is a different number in every one of them.
                TextureSources.Uniform(0, 0.8f, 0.2f, 0.4f, 0f),
                TextureSources.Uniform(1, 0.5f),
                TextureBlend.Mix(2, 0, 1, TextureBlendMode.Multiply, coverage: TextureBlendCoverage.Atop),
                TextureBlend.Mix(3, 0, 1, TextureBlendMode.Multiply)
            ],
            Outputs = [2, 3]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        var kept = bake.Read(2);

        Near(kept, 204, "atop over a transparent 0.8 backdrop, red", device);
        NearChannel(kept, 1, 51, "atop over a transparent 0.2 backdrop, green", device);
        NearChannel(kept, 2, 102, "atop over a transparent 0.4 backdrop, blue", device);

        // And the coverage that leaves is the coverage that arrived, which is none.
        NearChannel(kept, 3, 0, "atop over a transparent backdrop, coverage", device);

        // The instrument: over is a different rule at the same texel, and this is the number the
        // atop branch used to give.
        Near(bake.Read(3), 128, "over a transparent 0.8 backdrop, red", device);
    }

    static void Near(Bitmap picture, int expected, string what, VulkanDevice device) =>
        NearChannel(picture, 0, expected, what, device);

    static void NearChannel(Bitmap picture, int channel, int expected, string what, VulkanDevice device) {
        var actual = TextureKernelHarness.At(picture, 8, 8, channel);

        Assert.True(
            Math.Abs(actual - expected) <= 2,
            $"{what} is {actual} and the closed form is {expected} ({TextureKernelHarness.Adapter(device)})"
        );
    }
}
