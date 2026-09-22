// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>CSS Compositing 1 § 5's sixteen blend functions, against the specification's own arithmetic.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracle the device transcription has to agree with, and the reason it is written
///         here rather than with the fragment.</b>
///         <a href="https://github.com/Rikarin/Vixen/issues/783">#783</a> is the same sixteen
///         functions written twice more — once in GLSL for the golden suite and once in Raven for
///         `Ui.rvn` — and a transcription checked only against the C# it was transcribed from is this
///         repository's named anti-pattern. These numbers are independent of all three: each is
///         § 5.1's or § 5.3's formula evaluated by hand on the stated operands.
///     </para>
///     <para>
///         ⚠ <b>Nine of these sixteen were covered by nothing before this file.</b>
///         <c>MixBlendModeTests</c> drives four of them end to end through
///         <c>SoftwareUiRasterizer</c> — multiply, screen, difference and luminosity — and
///         <c>MixBlendModeLuminanceTests</c> asserts a homogeneity <i>property</i> that every mode
///         satisfies whatever its arithmetic is. So <c>soft-light</c>'s knee, <c>color-dodge</c>'s
///         three-case order, <c>color-burn</c>'s, <c>overlay</c>'s argument swap and § 5.3's
///         <c>ClipColor</c> could all have been wrong and no test in the repository would have moved.
///     </para>
///     <para>
///         <b>Two operand triples rather than one</b>, chosen so that no two modes agree on either.
///         A single pair with <c>Sat(Cb) == Sat(Cs)</c> — which is easy to pick by accident — makes
///         <c>hue</c> and <c>color</c> produce identical colours and <c>saturation</c> return the
///         backdrop unchanged, so three of the four non-separable modes become untestable at once.
///     </para>
/// </remarks>
public class UiBlendTests {
    /// <summary>What a float of this magnitude is worth comparing to.</summary>
    /// <remarks>
    ///     The references are computed in double and the implementation in single, and
    ///     <c>soft-light</c>'s square root and § 5.3's two divisions are where the difference shows.
    ///     Tight enough that a wrong formula cannot hide: the closest pair of answers in the table
    ///     below differ by 0.02.
    /// </remarks>
    const float Tolerance = 1e-5f;

    static readonly Vector3 BackdropA = new(0.25f, 0.5f, 0.75f);
    static readonly Vector3 SourceA = new(0.6f, 0.4f, 0.3f);

    static readonly Vector3 BackdropB = new(0.9f, 0.6f, 0.1f);
    static readonly Vector3 SourceB = new(0.2f, 0.75f, 0.5f);

    static void Same(Vector3 expected, Vector3 actual, string what) {
        Assert.True(
            MathF.Abs(expected.X - actual.X) < Tolerance
            && MathF.Abs(expected.Y - actual.Y) < Tolerance
            && MathF.Abs(expected.Z - actual.Z) < Tolerance,
            $"{what}: expected ({expected.X}, {expected.Y}, {expected.Z}) and got ({actual.X}, {actual.Y}, {actual.Z})"
        );
    }

    /// <summary>
    ///     Cb = (0.25, 0.5, 0.75) over Cs = (0.6, 0.4, 0.3). ⚠ <c>Sat(Cb)</c> is 0.5 and
    ///     <c>Sat(Cs)</c> is 0.3, which is what keeps the four non-separable modes apart.
    /// </summary>
    [Theory]
    [InlineData(UiBlendMode.Multiply, 0.150000f, 0.200000f, 0.225000f)]
    [InlineData(UiBlendMode.Screen, 0.700000f, 0.700000f, 0.825000f)]
    [InlineData(UiBlendMode.Overlay, 0.300000f, 0.400000f, 0.650000f)]
    [InlineData(UiBlendMode.Darken, 0.250000f, 0.400000f, 0.300000f)]
    [InlineData(UiBlendMode.Lighten, 0.600000f, 0.500000f, 0.750000f)]
    [InlineData(UiBlendMode.ColorDodge, 0.625000f, 0.833333f, 1.000000f)]
    [InlineData(UiBlendMode.ColorBurn, 0.000000f, 0.000000f, 0.166667f)]
    [InlineData(UiBlendMode.HardLight, 0.400000f, 0.400000f, 0.450000f)]
    [InlineData(UiBlendMode.SoftLight, 0.300000f, 0.450000f, 0.675000f)]
    [InlineData(UiBlendMode.Difference, 0.350000f, 0.100000f, 0.450000f)]
    [InlineData(UiBlendMode.Exclusion, 0.550000f, 0.500000f, 0.600000f)]
    [InlineData(UiBlendMode.Hue, 0.704167f, 0.370833f, 0.204167f)]
    [InlineData(UiBlendMode.Saturation, 0.331000f, 0.481000f, 0.631000f)]
    [InlineData(UiBlendMode.Color, 0.603500f, 0.403500f, 0.303500f)]
    [InlineData(UiBlendMode.Luminosity, 0.246500f, 0.496500f, 0.746500f)]
    public void Each_mode_is_the_specifications_own_function(UiBlendMode mode, float r, float g, float b) =>
        Same(new Vector3(r, g, b), UiBlend.Blend(mode, BackdropA, SourceA), mode.ToString());

    /// <summary>
    ///     Cb = (0.9, 0.6, 0.1) over Cs = (0.2, 0.75, 0.5). The second triple exists because the
    ///     first one clamps <c>color-burn</c> in two channels of three and <c>color-dodge</c> in one;
    ///     this one clamps the other way round, so between them every branch of both is taken.
    /// </summary>
    [Theory]
    [InlineData(UiBlendMode.Multiply, 0.180000f, 0.450000f, 0.050000f)]
    [InlineData(UiBlendMode.Screen, 0.920000f, 0.900000f, 0.550000f)]
    [InlineData(UiBlendMode.Overlay, 0.840000f, 0.800000f, 0.100000f)]
    [InlineData(UiBlendMode.Darken, 0.200000f, 0.600000f, 0.100000f)]
    [InlineData(UiBlendMode.Lighten, 0.900000f, 0.750000f, 0.500000f)]
    [InlineData(UiBlendMode.ColorDodge, 1.000000f, 1.000000f, 0.200000f)]
    [InlineData(UiBlendMode.ColorBurn, 0.500000f, 0.466667f, 0.000000f)]
    [InlineData(UiBlendMode.HardLight, 0.360000f, 0.800000f, 0.100000f)]
    [InlineData(UiBlendMode.SoftLight, 0.846000f, 0.687298f, 0.100000f)]
    [InlineData(UiBlendMode.Difference, 0.700000f, 0.150000f, 0.400000f)]
    [InlineData(UiBlendMode.Exclusion, 0.740000f, 0.450000f, 0.500000f)]
    [InlineData(UiBlendMode.Hue, 0.115000f, 0.915000f, 0.551364f)]
    [InlineData(UiBlendMode.Saturation, 0.817188f, 0.610938f, 0.267188f)]
    [InlineData(UiBlendMode.Color, 0.277500f, 0.827500f, 0.577500f)]
    [InlineData(UiBlendMode.Luminosity, 0.822500f, 0.522500f, 0.022500f)]
    public void Each_mode_is_the_specifications_own_function_on_a_second_pair(
        UiBlendMode mode,
        float r,
        float g,
        float b
    ) =>
        Same(new Vector3(r, g, b), UiBlend.Blend(mode, BackdropB, SourceB), mode.ToString());

    /// <summary>
    ///     ⚠ <b>The three cases of <c>color-dodge</c> are ordered and are not interchangeable
    ///     guards.</b> A backdrop of zero stays zero even where the source is one — which is what
    ///     makes the mode leave black alone instead of blowing it to white — and a test that only
    ///     drove the general case would pass against an implementation that asked about the source
    ///     first.
    /// </summary>
    [Fact]
    public void Color_dodge_asks_about_the_backdrop_before_the_source() {
        Same(
            Vector3.Zero,
            UiBlend.Blend(UiBlendMode.ColorDodge, Vector3.Zero, Vector3.One),
            "dodge of black by white"
        );

        // And a source of one anywhere else is the saturating case, which is the one the order is
        // usually written for.
        Same(
            Vector3.One,
            UiBlend.Blend(UiBlendMode.ColorDodge, new Vector3(0.001f), Vector3.One),
            "dodge of nearly black by white"
        );
    }

    /// <summary>The same ordering, mirrored: a white backdrop stays white however dark the source.</summary>
    [Fact]
    public void Color_burn_asks_about_the_backdrop_before_the_source() {
        Same(Vector3.One, UiBlend.Blend(UiBlendMode.ColorBurn, Vector3.One, Vector3.Zero), "burn of white by black");

        Same(
            Vector3.Zero,
            UiBlend.Blend(UiBlendMode.ColorBurn, new Vector3(0.999f), Vector3.Zero),
            "burn of nearly white by black"
        );
    }

    /// <summary>
    ///     ⚠ <b><c>soft-light</c>'s two halves meet at a quarter with matching value <i>and</i>
    ///     slope</b>, which is the whole reason § 5.1 writes <c>D(Cb)</c> as a cubic below the knee
    ///     rather than clamping. An implementation that used the square root everywhere is continuous
    ///     too and is a different picture; this is the assertion that tells them apart, because the
    ///     cubic and the root agree at 0.25 and diverge immediately below it.
    /// </summary>
    [Fact]
    public void Soft_lights_knee_is_continuous_and_is_not_the_square_root_below_it() {
        // At the knee the two branches of D agree exactly: ((16·0.25 − 12)·0.25 + 4)·0.25 = 0.5, and
        // sqrt(0.25) = 0.5.
        var atKnee = UiBlend.Blend(UiBlendMode.SoftLight, new Vector3(0.25f), new Vector3(0.75f));
        var justAbove = UiBlend.Blend(UiBlendMode.SoftLight, new Vector3(0.2500001f), new Vector3(0.75f));

        Assert.True(
            MathF.Abs(atKnee.X - justAbove.X) < 1e-4f,
            $"the knee is discontinuous: {atKnee.X} against {justAbove.X}"
        );

        // Below it the cubic is *not* the root. At Cb = 0.04 the cubic gives 0.141824 and the root
        // gives 0.2, so a soft light with Cs = 1 lands on 0.141824 rather than on 0.2.
        var below = UiBlend.Blend(UiBlendMode.SoftLight, new Vector3(0.04f), Vector3.One);

        Assert.True(
            MathF.Abs(below.X - 0.141824f) < Tolerance,
            $"D(0.04) is the cubic and not the root, so this should be 0.141824 and is {below.X}"
        );
    }

    /// <summary>
    ///     ⚠ <b>§ 5.3's <c>ClipColor</c> pulls an out-of-range colour towards its own luma and not
    ///     towards the faces of the unit cube</b>, and the difference is the one thing the four
    ///     non-separable modes promise: <c>mix-blend-color</c> takes the source's hue and the
    ///     backdrop's brightness, so the brightness must survive the clip exactly.
    /// </summary>
    /// <remarks>
    ///     Both directions, because they are two branches. A saturated red taken to a near-white
    ///     backdrop's luma overshoots one; the same red taken to a near-black backdrop's undershoots
    ///     zero. A per-channel clamp would give (1, 0.65, 0.65) for the first, whose luma is 0.755
    ///     rather than the 0.95 the mode was asked for.
    /// </remarks>
    [Fact]
    public void Clip_color_preserves_the_luma_it_was_given() {
        const float LumR = 0.3f;
        const float LumG = 0.59f;
        const float LumB = 0.11f;

        static float Luma(Vector3 c) => (LumR * c.X) + (LumG * c.Y) + (LumB * c.Z);

        var bright = UiBlend.Blend(UiBlendMode.Color, new Vector3(0.95f), new Vector3(1f, 0f, 0f));

        Same(new Vector3(1f, 0.928571f, 0.928571f), bright, "color over a near-white backdrop");
        Assert.True(MathF.Abs(Luma(bright) - 0.95f) < Tolerance, $"the luma moved to {Luma(bright)}");

        var dark = UiBlend.Blend(UiBlendMode.Color, new Vector3(0.05f), new Vector3(0f, 1f, 0f));

        Same(new Vector3(0f, 0.0847458f, 0f), dark, "color over a near-black backdrop");
        Assert.True(MathF.Abs(Luma(dark) - 0.05f) < Tolerance, $"the luma moved to {Luma(dark)}");
    }

    /// <summary>
    ///     ⚠ <b><c>overlay</c> is <c>hard-light</c> with the operands the other way round</b>, which
    ///     is easy to transcribe as "the same function" and is a visibly different picture. Asserted
    ///     as the swap itself rather than as two more tables, so it cannot be satisfied by two
    ///     tables that were both written from the same wrong reading.
    /// </summary>
    [Fact]
    public void Overlay_is_hard_light_with_its_operands_exchanged() {
        Same(
            UiBlend.Blend(UiBlendMode.HardLight, SourceA, BackdropA),
            UiBlend.Blend(UiBlendMode.Overlay, BackdropA, SourceA),
            "overlay against the swapped hard light"
        );

        // And they are genuinely different on this pair, so the assertion above is not two equal
        // things being compared.
        Assert.NotEqual(
            UiBlend.Blend(UiBlendMode.HardLight, BackdropA, SourceA),
            UiBlend.Blend(UiBlendMode.Overlay, BackdropA, SourceA)
        );
    }

    /// <summary>
    ///     § 5.1 gives <c>normal</c> as <c>B(Cb, Cs) = Cs</c> outright, and so is anything a later
    ///     specification adds — which is what keeps an unknown mode a source-over rather than a
    ///     black rectangle.
    /// </summary>
    [Fact]
    public void Normal_and_an_unknown_mode_are_the_source() {
        Same(SourceA, UiBlend.Blend(UiBlendMode.Normal, BackdropA, SourceA), "normal");
        Same(SourceA, UiBlend.Blend((UiBlendMode)99, BackdropA, SourceA), "an unknown mode");
    }

    /// <summary>
    ///     Each mode's own identity, which is the property a reader checks a transcription against
    ///     before reading a table: white multiplies to nothing, black screens to nothing, and a
    ///     difference with itself is black.
    /// </summary>
    [Fact]
    public void The_identities_each_mode_is_named_for_hold() {
        Same(BackdropA, UiBlend.Blend(UiBlendMode.Multiply, BackdropA, Vector3.One), "multiply by white");
        Same(BackdropA, UiBlend.Blend(UiBlendMode.Screen, BackdropA, Vector3.Zero), "screen by black");
        Same(Vector3.Zero, UiBlend.Blend(UiBlendMode.Difference, BackdropA, BackdropA), "difference with itself");
        Same(BackdropA, UiBlend.Blend(UiBlendMode.Darken, BackdropA, Vector3.One), "darken against white");
        Same(BackdropA, UiBlend.Blend(UiBlendMode.Lighten, BackdropA, Vector3.Zero), "lighten against black");

        // ⚠ And the one that is not an identity and looks like one: soft-light at a source of a half
        // leaves the backdrop alone, because both branches reduce to Cb there. A transcription that
        // got the knee wrong still passes this, which is why the table above exists.
        Same(BackdropA, UiBlend.Blend(UiBlendMode.SoftLight, BackdropA, new Vector3(0.5f)), "soft light at a half");
    }

    /// <summary>
    ///     A grey has no saturation to stretch, so § 5.3's <c>SetSat</c> divides by zero on it — the
    ///     degenerate case every implementation has to spell out, and the one a table of general
    ///     values never reaches.
    /// </summary>
    [Fact]
    public void A_grey_source_has_no_saturation_to_take() {
        // `saturation` takes the source's saturation onto the backdrop; a grey source has none, so
        // the result is the backdrop's luma as a grey rather than a NaN.
        var result = UiBlend.Blend(UiBlendMode.Saturation, BackdropA, new Vector3(0.4f));

        Assert.True(float.IsFinite(result.X) && float.IsFinite(result.Y) && float.IsFinite(result.Z));
        Same(new Vector3(0.4525f), result, "saturation from a grey source");
    }
}
