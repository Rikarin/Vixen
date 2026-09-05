// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.4's eleven filters, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>Closed forms, not goldens and not a CPU twin</b> — doc 48 § D3. Most of § 4.4 has one:
///         a blur of a constant is that constant, a warp by a zero field is a copy, a directional
///         blur leaves the stripes it runs along alone, a sharpen of amount zero is a copy, a slope
///         blur of zero steps is a copy. <c>BlurHq</c> has the strongest of them all — its impulse
///         response <em>is</em> the gaussian, so the picture can be compared against a series
///         evaluated in C# rather than against another picture.
///     </para>
///     <para>
///         ⚠ <b>Every parameter these kernels declare is asserted by something that would notice its
///         absence, and that is a deliberate answer to what batch 2's review found.</b> A kernel with
///         a declared <c>angle</c>, <c>centre</c> or <c>elevation</c> that simply ignored it passes
///         every "is it a copy" test ever written. So each of those has an assertion built to fail
///         without it: an angle is pinned by a 2×2 of stripe patterns, a centre by which texel is a
///         fixed point, an elevation by the quarter turn that flattens the relief, a sample count by
///         the field whose curvature makes it matter.
///     </para>
///     <para>
///         ⚠ <b>Without a real adapter a headless run falls back to the Null device on every
///         platform and prints identical healthy counters</b>, so a test that passed there would have
///         proved that a black image equals a black image. <c>TextureKernelHarness.Open</c> names the
///         adapter into every failure and skips loudly; <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip
///         into a failure.
///     </para>
/// </remarks>
public class TextureFilterDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>π/2 as a float, for the angle assertions that turn a filter onto the other axis.</summary>
    const float Quarter = 1.5707964f;

    /// <summary>π as a float, for the assertions that reflect an angle.</summary>
    const float Half = 3.14159265f;

    // --- Blur HQ --------------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ A gaussian's impulse response is the gaussian — asserted against the series, not against
    ///     another picture.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the one kernel in § 4.4 whose answer is arithmetic, and the test was written
    ///         before it.</b> One lit texel in a black field, blurred along x by σ, must come back as
    ///         <c>exp(−d²/2σ²)</c> divided by the sum of the weights actually taken — a number per
    ///         distance, computed here in C# from σ alone. A box blur of any radius is flat and fails
    ///         at d = 0 by 30 counts; a gaussian of the wrong σ fails in the tail. Neither can be
    ///         made to pass by a tolerance.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The series is truncated at 3σ and renormalised, exactly as the kernel is.</b>
    ///         Normalising by the analytic <c>σ√(2π)</c> instead would be asserting a slightly
    ///         different filter, and the disagreement would grow as the truncation tightened —
    ///         a tolerance would hide it and then hide a real defect later.
    ///     </para>
    ///     <para>
    ///         Two sigmas, because one would be satisfied by a kernel that ignored the parameter and
    ///         happened to be tuned to it.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(2f)]
    [InlineData(4f)]
    public void A_gaussians_impulse_response_is_its_analytic_profile(float sigma) {
        using var device = Device();

        var picture = Run(
            device,
            [Impulse(Side, Side / 2, Side / 2)],
            [TextureFormat.Rgba8],
            [TextureFilters.BlurHqOp(1, 0, sigma)],
            1
        );

        var reach = (int)MathF.Ceiling(sigma * 3f);
        var total = 1f;

        for (var d = 1; d <= reach; d++) {
            total += 2f * MathF.Exp(-(d * d) / (2f * sigma * sigma));
        }

        for (var d = 0; d <= reach + 1; d++) {
            var weight = d > reach ? 0f : MathF.Exp(-(d * d) / (2f * sigma * sigma));
            var expected = (int)MathF.Round(weight / total * 255f);
            var actual = TextureKernelHarness.At(picture, (Side / 2) + d, Side / 2, 0);

            Assert.True(
                Math.Abs(actual - expected) <= 1,
                $"σ = {sigma} at d = {d}: the picture says {actual} and exp(−d²/2σ²)/Σ says {expected}, "
                + $"on {TextureKernelHarness.Adapter(device)}."
            );
        }
    }

    /// <summary>A gaussian blur of a constant is that constant, exactly.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion the renormalisation exists for.</b> A kernel that divided by
    ///     the analytic <c>σ√(2π)</c> rather than by the weights it actually took would darken a flat
    ///     fill by the weight of the tail it dropped — a couple of counts at 3σ, which reads as a
    ///     blur that dims and is blamed on the levels node downstream.
    /// </remarks>
    [Fact]
    public void A_gaussian_blur_of_a_constant_is_that_constant() {
        using var device = Device();

        var source = TextureKernelHarness.Solid(Side, 100, 150, 200, 255);

        var picture = Run(
            device,
            [source],
            [TextureFormat.Rgba8],
            [TextureFilters.BlurHqOp(1, 0, 5f)],
            1
        );

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            picture,
            4,
            $"a gaussian of a flat fill on {TextureKernelHarness.Adapter(device)}"
        );
    }

    /// <summary>The blur runs along the axis its step names, and only that one.</summary>
    /// <remarks>
    ///     ⚠ <b>The plan is what separates a separable blur, so a kernel that ignored
    ///     <c>stepX</c>/<c>stepY</c> and always ran horizontally would produce a perfectly plausible
    ///     picture from a two-op plan — blurred twice across and not at all down.</b> An impulse says
    ///     so in one texel: the vertical variant leaves its own row black either side of the centre.
    /// </remarks>
    [Fact]
    public void A_gaussian_blurs_the_axis_its_step_names() {
        using var device = Device();

        var picture = Run(
            device,
            [Impulse(Side, Side / 2, Side / 2)],
            [TextureFormat.Rgba8],
            [TextureFilters.BlurHqOp(1, 0, 2f, vertical: true)],
            1
        );

        // Down the column the profile is there; across the row there is nothing but the centre.
        Assert.True(TextureKernelHarness.At(picture, Side / 2, (Side / 2) + 2, 0) > 20);
        Assert.Equal(0, TextureKernelHarness.At(picture, (Side / 2) + 2, Side / 2, 0));
    }

    // --- Directional Blur -----------------------------------------------------------------------

    /// <summary>
    ///     ⚠ A directional blur leaves the stripes it runs along alone and flattens the ones it
    ///     crosses — and the 2×2 of that is what pins <c>angle</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Either half alone is passed by a kernel that ignores the angle.</b> A horizontal
    ///         smear over horizontal stripes is a copy; so is a vertical smear over vertical ones.
    ///         Asserting both, with the two angles swapped, is the only arrangement that can tell a
    ///         working <c>angle</c> from a hard-coded axis.
    ///     </para>
    ///     <para>
    ///         The copies are <b>exact</b>: at integer offsets along an axis every tap lands on a
    ///         texel centre and the bilinear weights collapse, so this is an equality over 4 096
    ///         texels rather than a tolerance.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_directional_blur_leaves_the_stripes_it_runs_along_alone() {
        using var device = Device();

        var rows = Rows(Side);
        var columns = TextureKernelHarness.Columns(Side);

        var alongRows = Run(device, [rows], [TextureFormat.Rgba8], [Filter(0f)], 1);
        var acrossColumns = Run(device, [columns], [TextureFormat.Rgba8], [Filter(0f)], 1);
        var alongColumns = Run(device, [columns], [TextureFormat.Rgba8], [Filter(Quarter)], 1);
        var acrossRows = Run(device, [rows], [TextureFormat.Rgba8], [Filter(Quarter)], 1);

        var adapter = TextureKernelHarness.Adapter(device);

        TextureKernelHarness.AssertSame(new(Side, Side, rows), alongRows, 3, $"rows smeared across, on {adapter}");

        // A one-texel checkerboard averaged over 17 taps is its own mean, which is one half.
        Assert.InRange(TextureKernelHarness.At(acrossColumns, 32, 32, 0), 118, 138);

        for (var x = 0; x < Side; x++) {
            Assert.True(
                Math.Abs(TextureKernelHarness.At(alongColumns, x, 32, 0) - (x % 2 == 0 ? 0 : 255)) <= 1,
                $"columns smeared down should be untouched at x = {x}, on {adapter}."
            );
        }

        Assert.InRange(TextureKernelHarness.At(acrossRows, 32, 32, 0), 118, 138);

        static TextureOp Filter(float angle) => TextureFilters.DirectionalBlurOp(1, 0, angle, 8f);
    }

    /// <summary>A smear of no length is a copy, exactly.</summary>
    [Fact]
    public void A_directional_blur_of_no_length_is_a_copy() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);

        var picture = Run(
            device,
            [source],
            [TextureFormat.Rgba8],
            [TextureFilters.DirectionalBlurOp(1, 0, 0.7f, 0f)],
            1
        );

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            picture,
            4,
            $"a zero-length smear on {TextureKernelHarness.Adapter(device)}"
        );
    }

    // --- Radial Blur ----------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ The centre is a fixed point, and moving the centre moves which texel is fixed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the assertion <c>centreX</c>/<c>centreY</c> needs, and nothing weaker will
    ///         do.</b> At the centre the ray has no length, so every sample lands on the same texel —
    ///         the answer there is the source, exactly. A kernel that ignored the parameters would
    ///         have a fixed point in the middle of the image whatever the plan said, so each centre is
    ///         checked twice: the texel it names is untouched, and the texel the <em>other</em> centre
    ///         names is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source is a checkerboard and not <c>TextureKernelHarness.Unique</c>, and the
    ///         first version of this test used <c>Unique</c> and passed for no reason at all.</b> A
    ///         zoom blur averages a span symmetric about the texel's own radius, and the mean of a
    ///         <em>linear</em> function over a symmetric interval is its value at the middle — so any
    ///         image whose channels are linear in x and y, which <c>Unique</c>'s four are by
    ///         construction, is <b>invariant under a radial blur of any amount</b>. The "this texel is
    ///         no longer fixed" half was unfalsifiable and the "this one is" half was asserting a
    ///         property of the pattern rather than of the kernel. Worth knowing before reaching for
    ///         <c>Unique</c> in any future averaging filter.
    ///     </para>
    ///     <para>
    ///         The centres are written as <c>(k + 0.5) / 64</c> because a texel's centre is a
    ///         half-texel in, and a parameter read as a texel index rather than a fraction fails here
    ///         too.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radial_blur_leaves_its_own_centre_alone_and_moving_it_moves_which_texel() {
        using var device = Device();

        var source = TextureKernelHarness.Columns(Side);
        var picture = new Bitmap(Side, Side, source);

        var middle = Run(device, [source], [TextureFormat.Rgba8], [At(32, 32)], 1);
        var offset = Run(device, [source], [TextureFormat.Rgba8], [At(56, 32)], 1);

        var adapter = TextureKernelHarness.Adapter(device);

        Assert.Equal(TextureKernelHarness.At(picture, 32, 32, 0), TextureKernelHarness.At(middle, 32, 32, 0));
        Assert.Equal(TextureKernelHarness.At(picture, 56, 32, 0), TextureKernelHarness.At(offset, 56, 32, 0));

        // And each blur has moved the texel the other one holds still, by most of the range a
        // one-texel checkerboard has.
        Assert.True(
            Math.Abs(TextureKernelHarness.At(middle, 56, 32, 0) - TextureKernelHarness.At(picture, 56, 32, 0)) > 90,
            $"a blur about (32, 32) must not leave (56, 32) alone, on {adapter}."
        );

        Assert.True(
            Math.Abs(TextureKernelHarness.At(offset, 32, 32, 0) - TextureKernelHarness.At(picture, 32, 32, 0)) > 90,
            $"a blur about (56, 32) must not leave (32, 32) alone, on {adapter}."
        );

        static TextureOp At(int x, int y) =>
            TextureFilters.RadialBlurOp(1, 0, 0.6f, (x + 0.5f) / Side, (y + 0.5f) / Side);
    }

    /// <summary>
    ///     ⚠ A field constant along every ray from the centre is unchanged, and about a different
    ///     centre it is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The invariant that says this is a zoom along the ray rather than a spin about the
    ///         centre.</b> Every sample of a zoom lies on the ray through the texel and the centre, so
    ///         a field constant along every such ray comes back untouched — while a zoom about a
    ///         different point walks off the ray immediately and crosses into the neighbouring spokes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The field is a wheel of hard-edged spokes rather than a smooth angular sweep, and
    ///         the smooth one is what this test had first.</b> It was green and proved nothing: a
    ///         smooth sweep is very nearly <em>linear</em> along the short arc a zoom reaches, and the
    ///         mean of a linear function over a symmetric span is its middle — so a zoom about
    ///         <em>any</em> centre left it within three counts of itself, and the half meant to catch
    ///         a broken centre could not fail. Hard edges cannot be averaged back into themselves.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The invariance is exact for the field and only approximate for the picture of
    ///         it</b>, so the window matters and is chosen rather than guessed. A tap at radius
    ///         <c>r</c> along the ray reads texels whose own centres are up to half a texel off it,
    ///         which is an angular error of about <c>0.5 / r</c>; inside a spoke that costs nothing
    ///         and within about a twelfth of a radian of its edge it costs everything. So the window
    ///         is an annulus with the taps' whole reach inside it and a margin of 0.15 rad from any
    ///         edge — comfortably more than the 0.04 rad the innermost tap can be wrong by. The first
    ///         wheel here had sixteen spokes and no angular margin at all, and moved a texel by 80.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radial_blur_leaves_a_ray_constant_field_about_its_own_centre_alone() {
        using var device = Device();

        const int Count = 8;

        var spokes = Spokes(Side, 32, 32, Count);

        var same = Run(device, [spokes], [TextureFormat.Rgba8], [About(32, 32)], 1);
        var elsewhere = Run(device, [spokes], [TextureFormat.Rgba8], [About(56, 32)], 1);

        var adapter = TextureKernelHarness.Adapter(device);
        var picture = new Bitmap(Side, Side, spokes);
        var worst = 0;
        var moved = 0;
        var counted = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (!Interior(x, y)) {
                    continue;
                }

                counted++;

                worst = Math.Max(
                    worst,
                    Math.Abs(TextureKernelHarness.At(same, x, y, 0) - TextureKernelHarness.At(picture, x, y, 0))
                );

                moved = Math.Max(
                    moved,
                    Math.Abs(TextureKernelHarness.At(elsewhere, x, y, 0) - TextureKernelHarness.At(picture, x, y, 0))
                );
            }
        }

        // ⚠ Ask what this prints on the day the window rejects every texel: it would pass, having
        // compared nothing. So the sample is counted and required to be a picture.
        Assert.True(counted > 200, $"only {counted} texels were well inside a spoke, which is not a picture.");

        // ⚠ The two thresholds are an order of magnitude apart, which is what makes the pair mean
        // something: inside its own centre the wheel survives to within a couple of counts, and about
        // a centre 24 texels away a spoke has been averaged most of the way to grey.
        Assert.True(worst <= 6, $"a zoom about the wheel's own centre moved a texel by {worst}, on {adapter}.");
        Assert.True(moved > 60, $"a zoom about a different centre only moved a texel by {moved}, on {adapter}.");

        static bool Interior(int x, int y) {
            var radius = MathF.Sqrt(((x - 32) * (x - 32)) + ((y - 32) * (y - 32)));

            if (radius is < 16f or > 30f) {
                return false;
            }

            var wedges = (MathF.Atan2(y - 32, x - 32) + MathF.PI) / (2f * MathF.PI) * Count;

            return MathF.Abs(wedges - MathF.Round(wedges)) * 2f * MathF.PI / Count > 0.15f;
        }

        static TextureOp About(int x, int y) =>
            TextureFilters.RadialBlurOp(1, 0, 0.5f, (x + 0.5f) / Side, (y + 0.5f) / Side);
    }

    /// <summary>An amount of zero, and a single sample, are both copies — exactly.</summary>
    /// <remarks>
    ///     <para>
    ///         Two parameters, two ways to collapse the span, and each is a copy for a different
    ///         reason: the scale is one at every sample, or there is only one sample. A kernel that
    ///         had lost either would still pass the other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source is a checkerboard, and the third assertion is why.</b> A copy test is
    ///         only worth running over a pattern the filter would otherwise have changed — and a
    ///         radial blur leaves every image linear in x and y alone whatever its amount, so over
    ///         <c>TextureKernelHarness.Unique</c> all three of these would be copies and the file
    ///         would have said nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radial_blur_collapses_to_a_copy_at_zero_amount_and_at_one_sample() {
        using var device = Device();

        var source = TextureKernelHarness.Columns(Side);
        var expected = new Bitmap(Side, Side, source);
        var adapter = TextureKernelHarness.Adapter(device);

        var flat = Run(device, [source], [TextureFormat.Rgba8], [TextureFilters.RadialBlurOp(1, 0, 0f)], 1);

        var single = Run(
            device,
            [source],
            [TextureFormat.Rgba8],
            [TextureFilters.RadialBlurOp(1, 0, 0.8f, samples: 1)],
            1
        );

        var blurred = Run(
            device,
            [source],
            [TextureFormat.Rgba8],
            [TextureFilters.RadialBlurOp(1, 0, 0.8f)],
            1
        );

        TextureKernelHarness.AssertSame(expected, flat, 3, $"a radial blur of amount 0 on {adapter}");
        TextureKernelHarness.AssertSame(expected, single, 3, $"a radial blur of one sample on {adapter}");

        // Verify the instrument: over this pattern the filter does something, so the two copies above
        // are claims about the parameters rather than about the picture.
        Assert.True(
            Math.Abs(TextureKernelHarness.At(blurred, 56, 32, 0) - TextureKernelHarness.At(expected, 56, 32, 0)) > 90,
            $"the same op with an amount and its samples changes the picture, on {adapter}."
        );
    }

    // --- Non-Uniform Blur -----------------------------------------------------------------------

    /// <summary>
    ///     ⚠ An impulse spreads into the <em>diagonals</em>, which is what says this is not separable.
    /// </summary>
    /// <remarks>
    ///     <b>The one assertion that would go red if a future reader split this kernel into two
    ///     passes.</b> With a radius of one texel everywhere, a lit texel must come back as nine
    ///     texels of 255/9 — the four sides <b>and the four corners</b>. One separable pass leaves
    ///     the corners black, and the picture that results looks like a blur that is slightly wrong
    ///     in the direction the radius map varies, which an artist blames on the map.
    /// </remarks>
    [Fact]
    public void A_non_uniform_blur_spreads_an_impulse_into_the_diagonals() {
        using var device = Device();

        var picture = Run(
            device,
            [Impulse(Side, 32, 32), TextureKernelHarness.Solid(Side, 255, 255, 255, 255)],
            [TextureFormat.Rgba8],
            [TextureFilters.NonUniformBlurOp(2, 0, 1, 1f)],
            2
        );

        var adapter = TextureKernelHarness.Adapter(device);
        var expected = (int)MathF.Round(255f / 9f);

        for (var dy = -1; dy <= 1; dy++) {
            for (var dx = -1; dx <= 1; dx++) {
                Assert.True(
                    Math.Abs(TextureKernelHarness.At(picture, 32 + dx, 32 + dy, 0) - expected) <= 1,
                    $"a 3×3 box must put {expected} at ({dx}, {dy}) from the impulse and put "
                    + $"{TextureKernelHarness.At(picture, 32 + dx, 32 + dy, 0)}, on {adapter}."
                );
            }
        }

        Assert.Equal(0, TextureKernelHarness.At(picture, 34, 34, 0));
    }

    /// <summary>
    ///     ⚠ The radius is read per texel: half the image is untouched and half is flattened, from
    ///     one dispatch.
    /// </summary>
    /// <remarks>
    ///     <b>This is the node, and it is what a constant radius could not do.</b> The left half of
    ///     the map is black, so the left half of a one-texel checkerboard comes back <em>exactly</em>
    ///     as it went in; the right half is white, and a radius of 3.5 texels — whose fractional rim
    ///     is weighted a half — sums to eight in each axis with four of that on the lit columns, so
    ///     the answer there is 127.5. A kernel that read the map once, or read it at the wrong texel,
    ///     produces one of those everywhere.
    /// </remarks>
    [Fact]
    public void A_non_uniform_blur_reads_its_radius_at_every_texel() {
        using var device = Device();

        var picture = Run(
            device,
            [TextureKernelHarness.Columns(Side), HalfAndHalf(Side)],
            [TextureFormat.Rgba8],
            [TextureFilters.NonUniformBlurOp(2, 0, 1, 3.5f)],
            2
        );

        var adapter = TextureKernelHarness.Adapter(device);

        for (var x = 0; x < 24; x++) {
            Assert.True(
                TextureKernelHarness.At(picture, x, 32, 0) == (x % 2 == 0 ? 0 : 255),
                $"a radius of zero must leave x = {x} untouched, on {adapter}."
            );
        }

        for (var x = 40; x < 56; x++) {
            Assert.InRange(TextureKernelHarness.At(picture, x, 32, 0), 126, 129);
        }
    }

    /// <summary>A radius map of black is a copy, exactly.</summary>
    [Fact]
    public void A_non_uniform_blur_with_a_black_radius_map_is_a_copy() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);

        var picture = Run(
            device,
            [source, TextureKernelHarness.Solid(Side, 0, 0, 0, 255)],
            [TextureFormat.Rgba8],
            [TextureFilters.NonUniformBlurOp(2, 0, 1, 8f)],
            2
        );

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            picture,
            4,
            $"a black radius map on {TextureKernelHarness.Adapter(device)}"
        );
    }

    // --- Sharpen --------------------------------------------------------------------------------

    /// <summary>A sharpen of amount zero is a copy, exactly, whatever the radius.</summary>
    [Fact]
    public void A_sharpen_of_no_amount_is_a_copy() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);

        var picture = Run(
            device,
            [source],
            [TextureFormat.Rgba8],
            [TextureFilters.SharpenOp(1, 0, 0f, 6f)],
            1
        );

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            picture,
            4,
            $"a sharpen of amount 0 on {TextureKernelHarness.Adapter(device)}"
        );
    }

    /// <summary>
    ///     ⚠ A sharpen overshoots above the source on the light side of an edge and undershoots below
    ///     it on the dark side — which is the sign of <c>amount</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Nothing else in this file says which way the correction goes.</b> An unsharp mask with
    ///     the sign inverted is a blur of a peculiar shape, and it is a picture: softer, not obviously
    ///     wrong, and the artist raises the amount. The two halves are asserted together because a
    ///     kernel that clamped one of them would still show the other.
    /// </remarks>
    [Fact]
    public void A_sharpen_overshoots_on_the_light_side_of_an_edge() {
        using var device = Device();

        var picture = Run(
            device,
            [Step(Side, 32, 64, 192)],
            [TextureFormat.Rgba8],
            [TextureFilters.SharpenOp(1, 0, 1f)],
            1
        );

        var adapter = TextureKernelHarness.Adapter(device);

        Assert.True(
            TextureKernelHarness.At(picture, 32, 32, 0) > 210,
            $"the light side of the edge reads {TextureKernelHarness.At(picture, 32, 32, 0)} against a source "
            + $"of 192, on {adapter}."
        );

        Assert.True(
            TextureKernelHarness.At(picture, 31, 32, 0) < 45,
            $"the dark side of the edge reads {TextureKernelHarness.At(picture, 31, 32, 0)} against a source "
            + $"of 64, on {adapter}."
        );

        // Far from the edge the correction is nothing, because the box and the texel agree.
        Assert.Equal(192, TextureKernelHarness.At(picture, 50, 32, 0));
        Assert.Equal(64, TextureKernelHarness.At(picture, 10, 32, 0));
    }

    /// <summary>⚠ The radius is how far the overshoot reaches, and that is what asserts it.</summary>
    /// <remarks>
    ///     <b>A kernel that ignored <c>radius</c> entirely would pass every other test in this
    ///     file</b> — amount zero would still be a copy, a constant would still be untouched, and the
    ///     edge would still ring. Four texels from the edge is the place the two radii disagree: a
    ///     one-texel box sees nothing but the light side and leaves the texel alone <em>exactly</em>,
    ///     and a six-texel box has two dark columns in it and cannot.
    /// </remarks>
    [Fact]
    public void A_sharpens_radius_is_how_far_the_overshoot_reaches() {
        using var device = Device();

        var edge = Step(Side, 32, 64, 192);

        var narrow = Run(device, [edge], [TextureFormat.Rgba8], [TextureFilters.SharpenOp(1, 0, 1f, 1f)], 1);
        var wide = Run(device, [edge], [TextureFormat.Rgba8], [TextureFilters.SharpenOp(1, 0, 1f, 6f)], 1);

        var adapter = TextureKernelHarness.Adapter(device);

        Assert.Equal(192, TextureKernelHarness.At(narrow, 36, 32, 0));

        Assert.True(
            TextureKernelHarness.At(wide, 36, 32, 0) > 200,
            $"a six-texel radius must still ring four texels from the edge and reads "
            + $"{TextureKernelHarness.At(wide, 36, 32, 0)}, on {adapter}."
        );
    }

    // --- Emboss ---------------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ An emboss of a constant is a mid grey, and so is an emboss of no intensity.
    /// </summary>
    /// <remarks>
    ///     <b>The assertion that an emboss carries no offset of its own.</b> A relief kernel that
    ///     added a bias produces a picture an artist corrects with a levels node two steps later and
    ///     never traces back — and a flat input is the one place the bias is visible on its own,
    ///     because the gradient it would otherwise be mixed with is exactly zero.
    /// </remarks>
    [Fact]
    public void An_emboss_of_a_flat_field_is_a_mid_grey() {
        using var device = Device();

        var flat = Run(
            device,
            [TextureKernelHarness.Solid(Side, 90, 90, 90, 255)],
            [TextureFormat.Rgba8],
            [TextureFilters.EmbossOp(1, 0, 0f, 0f, 0.25f)],
            1
        );

        var quiet = Run(
            device,
            [Ramp4(Side)],
            [TextureFormat.Rgba8],
            [TextureFilters.EmbossOp(1, 0, 0f, 0f, 0f)],
            1
        );

        Assert.InRange(TextureKernelHarness.At(flat, 32, 32, 0), 127, 128);
        Assert.InRange(TextureKernelHarness.At(quiet, 32, 32, 0), 127, 128);
    }

    /// <summary>
    ///     ⚠ Turning the angle half way round reflects the relief about the mid tone, texel for
    ///     texel.
    /// </summary>
    /// <remarks>
    ///     <b>An equality rather than an inequality, and that is what makes it strong.</b> Adding π
    ///     negates the dot product exactly, so the two pictures must sum to 255 everywhere the relief
    ///     has not clipped. A kernel that ignored <c>angle</c> would give the same picture twice and
    ///     sum to twice it; a kernel that used the angle for something else would sum to something
    ///     that is not a constant.
    /// </remarks>
    [Fact]
    public void An_emboss_reflects_about_the_mid_tone_when_the_angle_turns_half_way() {
        using var device = Device();

        var ramp = Ramp4(Side);

        var forwards = Run(device, [ramp], [TextureFormat.Rgba8], [Lit(0f)], 1);
        var backwards = Run(device, [ramp], [TextureFormat.Rgba8], [Lit(Half)], 1);

        var adapter = TextureKernelHarness.Adapter(device);

        for (var x = 2; x < Side - 2; x++) {
            var sum = TextureKernelHarness.At(forwards, x, 32, 0) + TextureKernelHarness.At(backwards, x, 32, 0);

            Assert.True(
                Math.Abs(sum - 255) <= 2,
                $"at x = {x} the two lightings sum to {sum} rather than 255, on {adapter}."
            );
        }

        // And they are not the same picture, which is what says the angle reached the kernel.
        Assert.True(
            Math.Abs(TextureKernelHarness.At(forwards, 32, 32, 0) - TextureKernelHarness.At(backwards, 32, 32, 0))
            > 40,
            $"the two lightings are the same picture, on {adapter}."
        );

        static TextureOp Lit(float angle) => TextureFilters.EmbossOp(1, 0, angle, 0f, 0.25f);
    }

    /// <summary>⚠ At a quarter turn of elevation the relief is flat, and nothing else asserts it.</summary>
    /// <remarks>
    ///     <b>Light straight down the surface normal casts no directional relief</b>, and
    ///     <c>cos(π/2)</c> is what says so. A kernel that dropped <c>elevation</c> entirely would pass
    ///     every other emboss test here — the flat field would still be grey, the angle would still
    ///     reflect — so this is the one assertion the parameter has.
    /// </remarks>
    [Fact]
    public void An_emboss_at_a_quarter_turn_of_elevation_is_flat() {
        using var device = Device();

        var picture = Run(
            device,
            [Ramp4(Side)],
            [TextureFormat.Rgba8],
            [TextureFilters.EmbossOp(1, 0, 0f, Quarter, 0.25f)],
            1
        );

        for (var x = 2; x < Side - 2; x++) {
            Assert.InRange(TextureKernelHarness.At(picture, x, 32, 0), 126, 129);
        }
    }

    // --- Warp -----------------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ A warp by an exactly linear ramp is a shift of exactly four texels, in the named
    ///     direction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the whole node in one equality</b>, and it pins three separate things at
    ///         once: the <b>sign</b> (up the gradient, so the picture slides towards −x), the
    ///         <b>magnitude</b>, and the fact that the gradient is taken <b>per unit of image
    ///         width</b> rather than per texel.
    ///     </para>
    ///     <para>
    ///         The arithmetic is exact and worth writing out. The ramp is <c>4x</c> in bytes, so its
    ///         height rises by <c>4/255</c> per texel; a central difference halves the two-texel span
    ///         and the scale by the image's 64 texels gives a slope of <c>256/255</c> per unit of
    ///         width. An intensity of <c>255/64</c> — which is exact in binary — therefore displaces
    ///         by <c>4.0</c> texels and not by 3.98, so the bilinear tap lands on a texel centre and
    ///         the assertion is an <b>equality over every channel of every interior texel</b> rather
    ///         than a tolerance.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_warp_by_a_linear_ramp_is_a_shift_of_exactly_four_texels() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);

        var picture = Run(
            device,
            [source, Ramp4(Side)],
            [TextureFormat.Rgba8],
            [TextureFilters.WarpOp(2, 0, 1, 255f / 64f)],
            2
        );

        var expected = new Bitmap(Side, Side, source);
        var adapter = TextureKernelHarness.Adapter(device);

        for (var y = 8; y < Side - 8; y++) {
            for (var x = 8; x < Side - 12; x++) {
                for (var channel = 0; channel < 4; channel++) {
                    Assert.True(
                        TextureKernelHarness.At(picture, x, y, channel)
                        == TextureKernelHarness.At(expected, x + 4, y, channel),
                        $"at ({x}, {y}) channel {channel} the warp reads "
                        + $"{TextureKernelHarness.At(picture, x, y, channel)} and the source four texels right is "
                        + $"{TextureKernelHarness.At(expected, x + 4, y, channel)}, on {adapter}."
                    );
                }
            }
        }
    }

    /// <summary>A constant field has no gradient, and no intensity is no displacement — both copies.</summary>
    [Fact]
    public void A_warp_by_a_flat_field_and_a_warp_of_no_intensity_are_both_copies() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);
        var expected = new Bitmap(Side, Side, source);
        var adapter = TextureKernelHarness.Adapter(device);

        var flat = Run(
            device,
            [source, TextureKernelHarness.Solid(Side, 200, 200, 200, 255)],
            [TextureFormat.Rgba8],
            [TextureFilters.WarpOp(2, 0, 1, 12f)],
            2
        );

        var quiet = Run(
            device,
            [source, Ramp4(Side)],
            [TextureFormat.Rgba8],
            [TextureFilters.WarpOp(2, 0, 1, 0f)],
            2
        );

        TextureKernelHarness.AssertSame(expected, flat, 4, $"a warp by a flat field on {adapter}");
        TextureKernelHarness.AssertSame(expected, quiet, 4, $"a warp of zero intensity on {adapter}");
    }

    // --- Directional Warp ---------------------------------------------------------------------

    /// <summary>
    ///     ⚠ A constant field shifts by exactly the intensity, along the angle — and the angle is
    ///     what the second half pins.
    /// </summary>
    /// <remarks>
    ///     <b>The map is read raw and not centred</b>, so a fully lit field displaces by the whole
    ///     intensity; that is the difference from <c>VectorWarp</c> and it is asserted rather than
    ///     described. Turning the angle a quarter turn must move the same distance down the image
    ///     instead of across it — which is the only assertion <c>angle</c> has here, and a kernel
    ///     with a hard-coded axis fails exactly one half of this test.
    /// </remarks>
    [Fact]
    public void A_directional_warp_by_a_lit_field_shifts_by_the_intensity_along_its_angle() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);
        var lit = TextureKernelHarness.Solid(Side, 255, 255, 255, 255);
        var expected = new Bitmap(Side, Side, source);
        var adapter = TextureKernelHarness.Adapter(device);

        var across = Run(device, [source, lit], [TextureFormat.Rgba8], [Push(0f)], 2);
        var down = Run(device, [source, lit], [TextureFormat.Rgba8], [Push(Quarter)], 2);

        for (var y = 8; y < Side - 12; y++) {
            for (var x = 8; x < Side - 12; x++) {
                Assert.True(
                    TextureKernelHarness.At(across, x, y, 0) == TextureKernelHarness.At(expected, x + 5, y, 0),
                    $"at ({x}, {y}) an angle of 0 must read five texels right, on {adapter}."
                );

                Assert.True(
                    TextureKernelHarness.At(down, x, y, 1) == TextureKernelHarness.At(expected, x, y + 5, 1),
                    $"at ({x}, {y}) a quarter turn must read five texels down, on {adapter}."
                );
            }
        }

        static TextureOp Push(float angle) => TextureFilters.DirectionalWarpOp(2, 0, 1, angle, 5f);
    }

    /// <summary>
    ///     ⚠ A black field is a copy — which is the assertion that the map is one-sided.
    /// </summary>
    /// <remarks>
    ///     <b>A directional warp reads its map raw, so black is rest.</b> If it centred the value the
    ///     way <c>VectorWarp</c> does, a black field would displace by the whole intensity in the
    ///     opposite direction — a picture, and a plausible one. The two nodes differ in exactly this
    ///     and each asserts its own half.
    /// </remarks>
    [Fact]
    public void A_directional_warp_by_a_black_field_is_a_copy() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);

        var picture = Run(
            device,
            [source, TextureKernelHarness.Solid(Side, 0, 0, 0, 255)],
            [TextureFormat.Rgba8],
            [TextureFilters.DirectionalWarpOp(2, 0, 1, 0.7f, 12f)],
            2
        );

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            picture,
            4,
            $"a directional warp by a black field on {TextureKernelHarness.Adapter(device)}"
        );
    }

    // --- Vector Warp ----------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ The map is a <em>signed</em> displacement biased into an unorm, and the two conventions
    ///     differ by a factor of two and a sign.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three maps, and the third is the one the one-sided reading cannot survive.</b> A
    ///         red of 255 displaces by the whole intensity; a red of 0 displaces by the same distance
    ///         the <em>other way</em>; and a red of 128 is rest. Under the reading that the channels
    ///         are already the displacement, 0 would be a copy and 255 would be half as far — and
    ///         everything would drift one way at half the amplitude, which is a picture an artist
    ///         fixes by doubling the intensity and never reports.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rest is asserted to within one count rather than exactly, and the reason is
    ///         arithmetic rather than sloppiness</b>: 128 decodes to 0.50196 and not to a half, so the
    ///         residual displacement is a fiftieth of a texel. There is no byte that decodes to
    ///         exactly 0.5, which is a property of the encoding and worth knowing before someone
    ///         tries to assert an equality here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_vector_warp_decodes_a_signed_displacement_out_of_an_unorm_map() {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);
        var expected = new Bitmap(Side, Side, source);
        var adapter = TextureKernelHarness.Adapter(device);

        var right = Run(device, [source, TextureKernelHarness.Solid(Side, 255, 128, 0, 255)], [TextureFormat.Rgba8], [Push()], 2);
        var left = Run(device, [source, TextureKernelHarness.Solid(Side, 0, 128, 0, 255)], [TextureFormat.Rgba8], [Push()], 2);
        var rest = Run(device, [source, TextureKernelHarness.Solid(Side, 128, 128, 0, 255)], [TextureFormat.Rgba8], [Push()], 2);
        var downwards = Run(device, [source, TextureKernelHarness.Solid(Side, 128, 255, 0, 255)], [TextureFormat.Rgba8], [Push()], 2);

        for (var y = 8; y < Side - 12; y++) {
            for (var x = 8; x < Side - 12; x++) {
                Assert.True(
                    TextureKernelHarness.At(right, x, y, 0) == TextureKernelHarness.At(expected, x + 5, y, 0),
                    $"a red of 255 must read five texels right at ({x}, {y}), on {adapter}."
                );

                Assert.True(
                    TextureKernelHarness.At(left, x, y, 0) == TextureKernelHarness.At(expected, x - 5, y, 0),
                    $"a red of 0 must read five texels left at ({x}, {y}), on {adapter}."
                );

                Assert.True(
                    Math.Abs(TextureKernelHarness.At(rest, x, y, 0) - TextureKernelHarness.At(expected, x, y, 0)) <= 1,
                    $"a red of 128 is rest at ({x}, {y}), on {adapter}."
                );

                Assert.True(
                    TextureKernelHarness.At(downwards, x, y, 1) == TextureKernelHarness.At(expected, x, y + 5, 1),
                    $"a green of 255 must read five texels down at ({x}, {y}), on {adapter}."
                );
            }
        }

        static TextureOp Push() => TextureFilters.VectorWarpOp(2, 0, 1, 5f);
    }

    // --- Slope Blur -----------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ The sample count changes the answer exactly where the field curves — which is what says
    ///     the walk is iterative.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is doc 48 § 4.4's "the one everybody gets subtly wrong", asserted as a pair,
    ///         because either half alone is satisfied by the wrong implementation.</b>
    ///     </para>
    ///     <para>
    ///         Over the <b>down-ramp</b> field the gradient is the same everywhere, so the walk is a
    ///         straight line and its far end is <c>x + intensity</c> whether it got there in one step
    ///         or sixteen. In <c>Max</c> mode over a source that rises with x, that far end
    ///         <em>is</em> the answer — and the two counts agree. A single-pass approximation passes
    ///         this, and so does the real thing.
    ///     </para>
    ///     <para>
    ///         Over the <b>valley</b> field the gradient reverses at the bottom, so a walk of sixteen
    ///         small steps arrives at the floor and stays there while one large step sails over it and
    ///         lands forty texels away. The answers are 132 and 192 — sixty counts apart — and
    ///         <b>nothing that reads the gradient once can produce both</b>.
    ///     </para>
    ///     <para>
    ///         ⚠ Together they say the count matters <em>when and only when</em> the field curves,
    ///         which is the definition of an iterative walk. Hoisting the gradient read out of the
    ///         kernel's loop leaves the first half green and turns the second red, which is exactly
    ///         the sabotage this pair was built for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_sample_count_changes_the_answer_exactly_where_the_field_curves() {
        using var device = Device();

        var source = Ramp4(Side);
        var adapter = TextureKernelHarness.Adapter(device);

        // 40 × 255/256, so the walk travels exactly forty texels however it is cut up.
        const float Distance = 39.84375f;

        var straightOnce = Walk(device, source, DownRamp(Side), Distance, 1);
        var straightMany = Walk(device, source, DownRamp(Side), Distance, 16);
        var curvedOnce = Walk(device, source, Valley(Side), Distance, 1);
        var curvedMany = Walk(device, source, Valley(Side), Distance, 16);

        var straight = Math.Abs(straightOnce - straightMany);
        var curved = Math.Abs(curvedOnce - curvedMany);

        Assert.True(
            straight <= 2,
            $"over a field with a constant gradient the count must not matter, and one step gave "
            + $"{straightOnce} against sixteen steps' {straightMany}, on {adapter}."
        );

        Assert.True(
            curved >= 40,
            $"over a curving field the count is the node: one step gave {curvedOnce} and sixteen gave "
            + $"{curvedMany}, which is {curved} apart, on {adapter}."
        );

        static int Walk(VulkanDevice device, byte[] source, byte[] field, float distance, int samples) {
            var picture = Run(
                device,
                [source, field],
                [TextureFormat.Rgba8],
                [TextureFilters.SlopeBlurOp(2, 0, 1, distance, samples, TextureSlopeMode.Max)],
                2
            );

            return TextureKernelHarness.At(picture, 8, 32, 0);
        }
    }

    /// <summary>
    ///     ⚠ <c>intensity</c> is a <em>gain on the slope</em> and not a distance: halving the field's
    ///     slope halves the walk.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/706">#706</a>, written down so the
    ///         wording cannot drift back.</b> <c>SlopeBlur.rvn</c> and its builder both called
    ///         <c>intensity</c> "the whole distance walked", and the loop steps by
    ///         <c>−∇h · intensity / N</c> — so the path is <c>|∇h| · intensity</c> and the two agree
    ///         only over a field of unit slope. ⚠ <b>Every other assertion in this file uses exactly
    ///         such a field</b>, which is why the confusion survived a suite that measures the walk
    ///         to the texel: <see cref="DownRamp" /> is <c>4x</c> in bytes, whose gradient per unit of
    ///         image width is <c>256/255</c>, and the distance constant above is authored as
    ///         <c>40 × 255/256</c> to cancel it.
    ///     </para>
    ///     <para>
    ///         So the instrument is a <em>second</em> field, <c>2x</c> in bytes, at the same
    ///         intensity. Its slope is half, so the walk is twenty texels rather than forty and lands
    ///         on 112 rather than 192 — eighty counts apart over a source that rises by four a texel.
    ///         A kernel that normalised <c>∇h</c> to walk the documented distance reads 192 in both
    ///         rows, which is the sabotage this pins, and it is also the change #706 was resolved
    ///         <em>against</em>: the gain is the node, because a slope blur is supposed to stand still
    ///         on a flat.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(4, 192)]
    [InlineData(2, 112)]
    public void A_slope_blurs_intensity_is_a_gain_on_the_slope_and_not_a_distance(int rise, int expected) {
        using var device = Device();

        // The same 40 × 255/256 the pair above walks, so the only thing that changes between the two
        // rows is the field's own slope.
        const float Distance = 39.84375f;

        var field = Along(Side, x => (byte)((Side - 1 - x) * rise));

        var picture = Run(
            device,
            [Ramp4(Side), field],
            [TextureFormat.Rgba8],
            [TextureFilters.SlopeBlurOp(2, 0, 1, Distance, 16, TextureSlopeMode.Max)],
            2
        );

        var landed = TextureKernelHarness.At(picture, 8, 32, 0);

        Assert.True(
            Math.Abs(landed - expected) <= 4,
            $"over a field of {rise}/255 per texel the walk ended on {landed} and a gain on the slope owes "
            + $"{expected}; a distance would owe 192 whatever the field ({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ The walk runs <em>down</em> the field, and the three modes are min, blend and max in
    ///     that order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The sign first.</b> Over a field that falls towards +x, a walk starting at x = 8
    ///         with a distance of forty texels ends at x = 48, where a source of <c>4x</c> reads 192.
    ///         Uphill it would run into the clamped edge and read 32 — so the two directions are as
    ///         far apart as this image gets, and neither is a plausible version of the other.
    ///     </para>
    ///     <para>
    ///         <b>Then the modes.</b> Along that straight walk the source rises monotonically, so
    ///         <c>Min</c> is the texel it started from, <c>Max</c> is the one it ended on, and
    ///         <c>Blend</c> is the mean of an evenly spaced run between them — 112, which is neither.
    ///         A renumbering of <see cref="TextureSlopeMode" /> swaps two of those, and an erosion
    ///         where a dilation was meant is a picture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_slope_blur_walks_down_the_field_and_its_three_modes_are_min_blend_and_max() {
        using var device = Device();

        var source = Ramp4(Side);
        var field = DownRamp(Side);
        var adapter = TextureKernelHarness.Adapter(device);

        const float Distance = 39.84375f;

        var lowest = Mode(TextureSlopeMode.Min);
        var mean = Mode(TextureSlopeMode.Blend);
        var highest = Mode(TextureSlopeMode.Max);

        Assert.True(
            Math.Abs(highest - 192) <= 2,
            $"the far end of the walk is x = 48, which the source reads as 192, and Max gave {highest} on {adapter}."
        );

        Assert.True(
            Math.Abs(lowest - 32) <= 2,
            $"the walk starts at x = 8, which the source reads as 32, and Min gave {lowest} on {adapter}."
        );

        Assert.True(
            Math.Abs(mean - 112) <= 3,
            $"the mean of an even run from 32 to 192 is 112, and Blend gave {mean} on {adapter}."
        );

        int Mode(TextureSlopeMode mode) {
            var picture = Run(
                device,
                [source, field],
                [TextureFormat.Rgba8],
                [TextureFilters.SlopeBlurOp(2, 0, 1, Distance, 16, mode)],
                2
            );

            return TextureKernelHarness.At(picture, 8, 32, 0);
        }
    }

    /// <summary>Zero steps, zero distance and a flat field are all copies — exactly, in every mode.</summary>
    /// <remarks>
    ///     ⚠ <b>Three different ways for the walk to go nowhere, and each would survive the loss of
    ///     the other two.</b> Zero steps never enters the loop; zero distance enters it and stands
    ///     still; a flat field enters it, computes a gradient and finds it is zero. A kernel that
    ///     stepped before it looked, or that divided by the count before checking it, fails the first
    ///     alone.
    /// </remarks>
    [Theory]
    [InlineData(0, 12f, false)]
    [InlineData(8, 0f, false)]
    [InlineData(8, 12f, true)]
    public void A_slope_blur_that_goes_nowhere_is_a_copy(int samples, float intensity, bool flat) {
        using var device = Device();

        var source = TextureKernelHarness.Unique(Side);
        var field = flat ? TextureKernelHarness.Solid(Side, 180, 180, 180, 255) : Valley(Side);

        var picture = Run(
            device,
            [source, field],
            [TextureFormat.Rgba8],
            [TextureFilters.SlopeBlurOp(2, 0, 1, intensity, samples, TextureSlopeMode.Blend)],
            2
        );

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            picture,
            4,
            $"a walk of {samples} steps over {intensity} texels of a {(flat ? "flat" : "curving")} field, "
            + $"on {TextureKernelHarness.Adapter(device)}"
        );
    }

    // --- The bench ------------------------------------------------------------------------------

    /// <summary>A device, named into the test's output so no number here is anonymous.</summary>
    VulkanDevice Device() {
        var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        return device;
    }

    /// <summary>Uploads some pictures, runs some ops over them and reads one image back.</summary>
    /// <remarks>
    ///     The externals come first in the image table, so an op's indices are
    ///     <c>0 … sources.Length − 1</c> for the inputs and <c>sources.Length …</c> for what the plan
    ///     makes. Every source is <c>Rgba8</c>, which is what <c>TextureKernelHarness.Upload</c>
    ///     writes.
    /// </remarks>
    static Bitmap Run(
        VulkanDevice device,
        byte[][] sources,
        TextureFormat[] made,
        TextureOp[] ops,
        int read
    ) {
        var uploaded = new (TextureHandle Texture, BufferHandle Staging)[sources.Length];

        for (var index = 0; index < sources.Length; index++) {
            uploaded[index] = TextureKernelHarness.Upload(device, sources[index], Side, Side);
        }

        try {
            List<TextureImage> images = [];

            foreach (var _ in sources) {
                images.Add(new(TextureFormat.Rgba8, External: true));
            }

            foreach (var format in made) {
                images.Add(new(format));
            }

            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [.. images],
                Ops = [.. ops],
                Outputs = [read]
            };

            Assert.Empty(plan.Validate());
            Assert.Empty(TextureFilters.Verify(plan));

            var bound = new Dictionary<int, TextureHandle>();

            for (var index = 0; index < sources.Length; index++) {
                bound[index] = uploaded[index].Texture;
            }

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, bound);

            return bake.Read(read);
        } finally {
            foreach (var (texture, staging) in uploaded) {
                device.Destroy(staging);
                device.Destroy(texture);
            }
        }
    }

    /// <summary>One lit texel in a black field — the pattern a filter's impulse response is read off.</summary>
    static byte[] Impulse(int side, int x, int y) {
        var pixels = new byte[side * side * 4];
        var at = ((y * side) + x) * 4;

        pixels[at] = 255;
        pixels[at + 1] = 255;
        pixels[at + 2] = 255;
        pixels[at + 3] = 255;

        return pixels;
    }

    /// <summary>A one-texel-high <em>row</em> checkerboard — constant along x, alternating down y.</summary>
    /// <remarks>
    ///     The transpose of <c>TextureKernelHarness.Columns</c>, and it exists so that a directional
    ///     filter can be asserted against both axes. One of the two is a copy and the other is a mean
    ///     for any angle, which is what makes the 2×2 a claim about the angle.
    /// </remarks>
    static byte[] Rows(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            var value = (byte)(y % 2 == 0 ? 0 : 255);

            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>An <b>exactly</b> linear ramp: <c>4x</c> in bytes, rising with x.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TextureKernelHarness.Ramp</c> is <c>x·255/63</c> and is not exactly linear</b> —
    ///     integer division leaves a step of 4 in most places and 5 in some, so its slope is not a
    ///     constant and a gradient read off it is not either. Every closed form in this file that
    ///     needs a known slope uses this one instead, where the step is 4 everywhere and the
    ///     arithmetic works out to a displacement of exactly four texels.
    /// </remarks>
    static byte[] Ramp4(int side) => Along(side, x => (byte)(x * 4));

    /// <summary>The same ramp falling instead of rising, so the walk down it runs towards +x.</summary>
    static byte[] DownRamp(int side) => Along(side, x => (byte)((side - 1 - x) * 4));

    /// <summary>A V, with its floor at the middle of the image and an exactly constant slope either side.</summary>
    /// <remarks>
    ///     ⚠ <b>The field a slope blur's iteration count can be read off.</b> Its gradient reverses at
    ///     the floor, so a walk of many small steps settles there while one large step sails over —
    ///     which is a difference no amount of denser sampling along a straight line can produce.
    /// </remarks>
    static byte[] Valley(int side) => Along(side, x => (byte)(Math.Abs(x - (side / 2)) * 4));

    /// <summary>A step edge at <paramref name="at" />.</summary>
    static byte[] Step(int side, int at, byte low, byte high) => Along(side, x => x < at ? low : high);

    /// <summary>Black to the left of the middle and white to the right of it.</summary>
    static byte[] HalfAndHalf(int side) => Along(side, x => (byte)(x < side / 2 ? 0 : 255));

    /// <summary>A wheel of hard-edged spokes about one texel — constant along every ray from it.</summary>
    /// <remarks>
    ///     ⚠ <b>Hard edges rather than a smooth sweep, and that is the whole point of the pattern.</b>
    ///     Both are constant along every ray, so both are invariant under a zoom about their own
    ///     centre; but a smooth sweep is very nearly <em>linear</em> along the arc a zoom about a
    ///     different centre reaches, and the mean of a linear function over a symmetric span is its
    ///     middle — so the smooth version is invariant under a zoom about any centre at all, and the
    ///     assertion meant to catch a broken centre could not fail. Spokes cannot be averaged back
    ///     into themselves.
    /// </remarks>
    static byte[] Spokes(int side, int centreX, int centreY, int count) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var angle = MathF.Atan2(y - centreY, x - centreX) + MathF.PI;
                var wedge = (int)MathF.Floor(angle / (2f * MathF.PI) * count);
                var value = (byte)(wedge % 2 == 0 ? 0 : 255);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A pattern that depends on x alone, in every colour channel.</summary>
    static byte[] Along(int side, Func<int, byte> value) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var v = value(x);

                pixels[at] = v;
                pixels[at + 1] = v;
                pixels[at + 2] = v;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }
}
