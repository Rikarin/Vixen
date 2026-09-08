// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Xunit;

namespace Tests;

/// <summary><c>Colour/Mix</c>'s kernel on a real device: per texel, and the same sixteen operators.</summary>
/// <remarks>
///     <para>
///         <b>Two questions, and neither answers the other.</b> The first is whether the mask is read
///         <em>per texel</em> at all — a kernel that took the mask's first texel, or ignored it, is a
///         perfectly plausible picture. The second is whether the sixteen operators
///         <a href="https://github.com/Rikarin/Vixen/issues/1059">#1059</a> transcribed still
///         composite what <c>Blend</c>'s do; <c>TextureMixParityTests</c> holds the two files to the
///         same text and this holds them to the same picture, which is the half a text comparison
///         cannot make and the half that survives somebody reformatting a file.
///     </para>
///     <para>
///         ⚠ <b>The mask is a ramp and not a constant, which is the whole of the first test.</b> A
///         flat mask is the assertion a kernel reading <c>mask.Load(int3(0, 0, 0))</c> passes, and a
///         flat mask at 1 is the assertion a kernel with no mask at all passes. Sixty-four distinct
///         values across the image mean the answer has to move with x, and the three columns read off
///         below are the two ends and the middle of a closed form.
///     </para>
///     <para>
///         ⚠ <b>No CPU twin</b> — doc 48 § D3. <c>Copy</c> under the over rule against an opaque
///         backdrop is <c>lerp(background, foreground, mask)</c> by hand, so the expected numbers are
///         arithmetic anybody can check rather than a second implementation looped over the image.
///     </para>
/// </remarks>
public class TextureMixDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>Every mode, so the roll call is the table rather than one row of it.</summary>
    /// <remarks>
    ///     ⚠ <b>All sixteen and not a sample, because the failure this is for is one operator.</b> A
    ///     transcription loses a <c>1 −</c> or a swapped selector in one case out of sixteen, and
    ///     fifteen identical pictures are what that looks like from any test that checks a few.
    ///     <c>Copy</c> is included: it has no neutral, so it is the one mode <c>Blend</c>'s own
    ///     neutral roll call cannot carry, and here it needs nothing special because the comparison
    ///     is against the other kernel rather than against the backdrop.
    /// </remarks>
    public static TheoryData<int> Modes {
        get {
            TheoryData<int> rows = [];

            foreach (var mode in Enum.GetValues<TextureBlendMode>()) {
                rows.Add((int)mode);
            }

            return rows;
        }
    }

    /// <summary>A varying mask composites per texel, along a closed form read at three columns.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>Copy</c> over an opaque backdrop is <c>lerp(¼, ¾, m)</c>, so the answer runs from ¼
    ///         at the black end of the mask to ¾ at the white end and is exactly ½ where the mask is.
    ///         ⚠ The ramp's own quantisation is what the tolerance is for: column <c>x</c> carries
    ///         <c>x · 255 / 63</c>, which is a whole 8-bit step and lands on the halfway column at
    ///         <c>32 · 255 / 63 = 129.5</c> rather than at 128.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two ends are what a uniform read cannot produce.</b> A kernel that sampled
    ///         the mask once would answer one number for the whole image, and every arrangement of
    ///         one number fails at least one of the three columns — 0.25 fails the right, 0.75 fails
    ///         the left, and anything between fails both.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_varying_mask_composites_per_texel() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var ramp = TextureKernelHarness.Ramp(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, ramp, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [
                    TextureSources.Uniform(1, 0.25f),
                    TextureSources.Uniform(2, 0.75f),
                    TextureBlend.Masked(3, 1, 2, 0)
                ],
                Outputs = [3]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var picture = bake.Read(3);

            // The mask's own value at each column, so the expectation is the closed form rather than
            // three numbers somebody wrote down.
            foreach (var x in new[] { 0, Side / 2, Side - 1 }) {
                var mask = x * 255f / (Side - 1) / 255f;
                var expected = (0.25f + (0.5f * mask)) * 255f;

                Assert.InRange((float)TextureKernelHarness.At(picture, x, Side / 2, 0), expected - 2f, expected + 2f);
            }
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>With a white mask, <c>Mix</c> is <c>Blend</c> — texel for texel, in every mode.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what makes a second copy of the operator table safe to have.</b> Both
    ///         kernels run in one bake over the same two images, so nothing about the device, the
    ///         adapter or the format can differ between the two answers — the only thing that can is
    ///         the arithmetic, which is the thing being compared.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The backdrop is a ramp and the foreground is a constant that is nobody's
    ///         neutral.</b> Against a flat backdrop several of the sixteen agree with each other, and
    ///         against a neutral foreground five of them are a <c>Copy</c> — either would make a
    ///         picture two different operators could produce, and the comparison would then be
    ///         between two kernels that were both wrong in the same direction.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the mask is a genuine white image rather than an unbound input</b>, because
    ///         <c>Mix</c>'s contract is that the mask <em>multiplies</em> the opacity. A test that
    ///         left the port to whatever the pool held would be asserting about a black mask, under
    ///         which every mode composites nothing and all sixteen rows agree.
    ///     </para>
    /// </remarks>
    /// <param name="number">The mode, as <c>TextureBlendMode</c>'s number.</param>
    [Theory]
    [MemberData(nameof(Modes))]
    public void A_white_mask_composites_exactly_what_the_blend_kernel_does(int number) {
        var mode = (TextureBlendMode)number;

        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var ramp = TextureKernelHarness.Ramp(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, ramp, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba8),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [
                    TextureSources.Uniform(1, 0.6f),
                    TextureSources.Uniform(2, 1f),
                    TextureBlend.Mix(3, 0, 1, mode),
                    TextureBlend.Masked(4, 0, 1, 2, mode)
                ],
                Outputs = [3, 4]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            TextureKernelHarness.AssertSame(
                bake.Read(3),
                bake.Read(4),
                4,
                $"{mode} through a white mask on {TextureKernelHarness.Adapter(device)}"
            );
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>⚠ The mask multiplies the opacity rather than replacing it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half a white mask cannot see.</b> Every assertion above runs at the default
    ///         opacity of 1, under which "the mask replaces the opacity" and "the mask multiplies it"
    ///         are the same kernel. A node whose <c>Opacity</c> port stopped reaching the uniform
    ///         would pass all seventeen of them — and it is the port an author reaches for first,
    ///         because it is the one <c>Colour/Blend</c> taught them.
    ///     </para>
    ///     <para>
    ///         The two masks are a half-grey image and a white one, at opacities of 1 and ½, so the
    ///         product is ½ both ways and the two composites must be the same picture. ⚠ Read as an
    ///         equality between two bakes rather than against a written number, so the 8-bit
    ///         quantisation of ½ is the same on both sides and no tolerance has to absorb it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_opacity_scales_the_mask_rather_than_being_replaced_by_it() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var ramp = TextureKernelHarness.Ramp(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, ramp, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba8),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [
                    TextureSources.Uniform(1, 0.6f),
                    TextureSources.Uniform(2, 0.5f),
                    TextureSources.Uniform(3, 1f),
                    TextureBlend.Masked(4, 0, 1, 2, TextureBlendMode.Copy),
                    TextureBlend.Masked(5, 0, 1, 3, TextureBlendMode.Copy, 0.5f)
                ],
                Outputs = [4, 5]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var half = bake.Read(4);

            // The instrument for the equality below: half of a ramp under a constant is not the ramp,
            // so a bake in which neither the mask nor the opacity reached the kernel — both composites
            // being a plain `Copy` of the foreground, or of the backdrop — cannot satisfy it.
            Assert.InRange((float)TextureKernelHarness.At(half, 0, Side / 2, 0), 74f, 79f);
            Assert.InRange((float)TextureKernelHarness.At(half, Side - 1, Side / 2, 0), 202f, 207f);

            TextureKernelHarness.AssertSame(
                half,
                bake.Read(5),
                4,
                $"a half mask against a white mask at half opacity on {TextureKernelHarness.Adapter(device)}"
            );
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }
}
