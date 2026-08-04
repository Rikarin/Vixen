// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>One tonal range's five grading operations.</summary>
/// <remarks>
///     <para>
///         The American Society of Cinematographers' colour decision list, which every grading suite
///         reads and writes. Saturation and contrast, then the slope-offset-power triple that the
///         standard calls gain, offset and gamma — ⚠ <b>applied in that order</b>, because two tools
///         applying the same numbers in different orders produce different pictures, which is the one
///         thing an interchange format exists to prevent.
///     </para>
///     <para>
///         ⚠ <b>Scene-referred.</b> These run before the tonemap on unbounded light, so
///         <see cref="Gamma" /> is a power on radiance and 1 means "leave it alone" rather than
///         "display gamma", and <see cref="Contrast" /> pivots around 0.18 in log space rather than
///         around 0.5. A range copied from a display-referred tool will be roughly right and not
///         exactly right, which is the honest trade for grading where both SDR and HDR output agree.
///     </para>
/// </remarks>
[DataContract("ColorGradingRange")]
public readonly record struct ColorGradingRange {
    /// <summary>How much colour survives. 0 is greyscale, 1 leaves it alone.</summary>
    public float Saturation { get; init; }

    /// <summary>Contrast about middle grey. 1 leaves it alone.</summary>
    public float Contrast { get; init; }

    /// <summary>The power, per channel. The standard's <em>power</em>.</summary>
    public Vector3 Gamma { get; init; }

    /// <summary>The multiplier, per channel. The standard's <em>slope</em>.</summary>
    public Vector3 Gain { get; init; }

    /// <summary>What is added, per channel. The standard's <em>offset</em>.</summary>
    public Vector3 Offset { get; init; }

    /// <summary>The range that changes nothing.</summary>
    /// <remarks>
    ///     A property and not <c>default</c>, for the reason every other one in the engine is: a zeroed
    ///     struct here is a saturation of zero and a gain of zero, which is a black greyscale image.
    /// </remarks>
    public static ColorGradingRange Neutral => new() {
        Saturation = 1f,
        Contrast = 1f,
        Gamma = Vector3.One,
        Gain = Vector3.One,
        Offset = Vector3.Zero
    };

    /// <summary>The range part-way between two, component by component.</summary>
    /// <param name="from">Where the blend starts.</param>
    /// <param name="to">And where it lands.</param>
    /// <param name="amount">How far along, 0 to 1.</param>
    /// <remarks>
    ///     Every component here is a value whose zero is a value — a black gain, a flat offset — so a
    ///     plain lerp per component is well defined at every weight, unlike the tonemap's kelvin. What
    ///     it feeds is <see cref="BlendedGrading" />: a volume or a look profile fading in wants part
    ///     of its grade, and "part of a grade" is this.
    /// </remarks>
    public static ColorGradingRange Lerp(in ColorGradingRange from, in ColorGradingRange to, float amount) => new() {
        Saturation = MathUtil.Lerp(from.Saturation, to.Saturation, amount),
        Contrast = MathUtil.Lerp(from.Contrast, to.Contrast, amount),
        Gamma = Vector3.Lerp(from.Gamma, to.Gamma, amount),
        Gain = Vector3.Lerp(from.Gain, to.Gain, amount),
        Offset = Vector3.Lerp(from.Offset, to.Offset, amount)
    };
}

/// <summary>A whole grade: one global range and three weighted by luminance.</summary>
/// <remarks>
///     <para>
///         Unreal's decomposition, and it replaces four of Unity's separate effects — Lift/Gamma/Gain,
///         Shadows/Midtones/Highlights, Channel Mixer and Colour Curves are all ways of authoring a
///         colour transform, and adopting four overlapping models because one engine ships four is how
///         a grading stack becomes unexplainable. See <c>docs/plan/30</c>.
///     </para>
///     <para>
///         <b>The three ranges are a partition.</b> Their weights sum to one at every luminance, so
///         setting all three to the same values gives the same picture as setting
///         <see cref="Global" /> to them. That property is what makes the decomposition usable rather
///         than four controls fighting over the same pixels.
///     </para>
///     <para>
///         ⚠ What this cannot express is the one real loss against Unity: hue-versus-hue and
///         hue-versus-saturation curves are not a CDL, and no combination of these five operations is
///         one. That is what a grading LUT is for.
///     </para>
///     <para>
///         <b>It lives beside the volume settings rather than beside the tonemap that consumes it</b>,
///         and it moved when the look profile landed: <see cref="Ecs.PostProcessSettings" /> carries a
///         whole grade as one opinion, and the settings type is upstream of the effect set — the same
///         direction <c>IPostProcessTarget</c> already sends the knowledge. Nothing about the grade
///         itself changed, including its <c>ColorGrading</c> document name.
///     </para>
/// </remarks>
[DataContract("ColorGrading")]
public readonly record struct ColorGrading {
    /// <summary>Applied everywhere, before the three ranges.</summary>
    public ColorGradingRange Global { get; init; }

    /// <summary>Applied where the image is dark.</summary>
    public ColorGradingRange Shadows { get; init; }

    /// <summary>Applied in between.</summary>
    public ColorGradingRange Midtones { get; init; }

    /// <summary>Applied where the image is bright.</summary>
    public ColorGradingRange Highlights { get; init; }

    /// <summary>Where the shadow range stops contributing, in exposure-normalised luminance.</summary>
    public float ShadowsMax { get; init; }

    /// <summary>Where the highlight range starts contributing.</summary>
    public float HighlightsMin { get; init; }

    /// <summary>A grade that changes nothing, and the base to write one from.</summary>
    public static ColorGrading Neutral => new() {
        Global = ColorGradingRange.Neutral,
        Shadows = ColorGradingRange.Neutral,
        Midtones = ColorGradingRange.Neutral,
        Highlights = ColorGradingRange.Neutral,
        ShadowsMax = 0.09f,
        HighlightsMin = 0.5f
    };

    /// <summary>The grade part-way between two, range by range.</summary>
    /// <param name="from">Where the blend starts.</param>
    /// <param name="to">And where it lands.</param>
    /// <param name="amount">How far along, 0 to 1.</param>
    /// <remarks>
    ///     The two crossover luminances lerp with everything else: they are plain thresholds whose
    ///     zero is a threshold, not sentinels, so the blend passes through numbers that mean what
    ///     they say at every weight.
    /// </remarks>
    public static ColorGrading Lerp(in ColorGrading from, in ColorGrading to, float amount) => new() {
        Global = ColorGradingRange.Lerp(from.Global, to.Global, amount),
        Shadows = ColorGradingRange.Lerp(from.Shadows, to.Shadows, amount),
        Midtones = ColorGradingRange.Lerp(from.Midtones, to.Midtones, amount),
        Highlights = ColorGradingRange.Lerp(from.Highlights, to.Highlights, amount),
        ShadowsMax = MathUtil.Lerp(from.ShadowsMax, to.ShadowsMax, amount),
        HighlightsMin = MathUtil.Lerp(from.HighlightsMin, to.HighlightsMin, amount)
    };
}
