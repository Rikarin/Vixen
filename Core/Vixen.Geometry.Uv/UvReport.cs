// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv;

/// <summary>Which of the three stages a timing or a refusal belongs to.</summary>
/// <remarks>
///     docs/plan/42 § Part 0: charting, flattening and packing have different literatures, different
///     failure modes and different runtimes, and a tool that exposes only the fused verb cannot be
///     debugged. Naming the stage is what makes "which one was slow" and "which one refused"
///     answerable.
/// </remarks>
public enum UvStage : byte {
    /// <summary>Where to cut.</summary>
    Chart,

    /// <summary>How a chart becomes flat.</summary>
    Flatten,

    /// <summary>Where each island sits.</summary>
    Pack
}

/// <summary>How long a stage took and how much it handled.</summary>
/// <param name="Stage">Which stage.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Elements">What it handled — charts, triangles or islands, depending on the stage.</param>
public readonly record struct UvStageTiming(UvStage Stage, TimeSpan Elapsed, int Elements);

/// <summary>How distorted a mapping is, measured four ways because the failures are different.</summary>
/// <param name="Angular">Conformal distortion: how far angles moved.</param>
/// <param name="Area">Authalic distortion: how far the area ratio moved from uniform.</param>
/// <param name="StretchL2">Sander's L² stretch, averaged over area.</param>
/// <param name="StretchLInf">Sander's L^∞ stretch — the worst triangle, not the average.</param>
/// <param name="Flipped">Triangles whose winding reversed under the mapping.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Reporting one number hides the failure that matters.</b> docs/plan/42 § D6: a
///         conformal map can have perfect angles and a fortyfold area ratio between two ends of one
///         chart, which shows up as a texture that is sharp on the shoulder and mush on the hand —
///         and an angle-only metric calls that excellent.
///     </para>
///     <para>
///         ⚠ <b><see cref="Flipped" /> is a correctness field wearing a metric's clothes and must be
///         zero.</b> A flipped triangle is a region of the atlas where the mapping is not invertible:
///         a bake writes to the wrong texel and sampling reads from it. A chart that cannot reach
///         zero is subdivided rather than shipped.
///     </para>
/// </remarks>
public readonly record struct UvDistortion(
    float Angular,
    float Area,
    float StretchL2,
    float StretchLInf,
    int Flipped
);

/// <summary>What the achieved texel density turned out to be, across every chart.</summary>
/// <param name="Minimum">The sparsest chart, in texels per world unit.</param>
/// <param name="Mean">The average.</param>
/// <param name="Maximum">The densest.</param>
/// <param name="Variance">How far from uniform the set is.</param>
/// <remarks>
///     docs/plan/42 § D9. Density is a constraint rather than an observation, so this is the field
///     that answers <i>"did the packer quietly rescale something"</i> — which, without it, is a
///     question you can only answer by looking at the game.
/// </remarks>
public readonly record struct UvTexelDensity(float Minimum, float Mean, float Maximum, float Variance);

/// <summary>Everything measured about an unwrap, which is the answer to <i>"is this good?"</i>.</summary>
/// <param name="ChartCount">The headline. Fewer is better at equal distortion.</param>
/// <param name="Compactness">4πA/P² per island, averaged. Round islands pack and filter better than tendrils.</param>
/// <param name="Convexity">A/A(hull) per island, averaged — the other half of shape quality.</param>
/// <param name="SeamLength">Total seam length, in world units.</param>
/// <param name="SeamLengthNormalized">Seam length over the square root of surface area, so it is scale-free.</param>
/// <param name="BoundaryJaggedness">Discrete curvature over resampled boundary loops.</param>
/// <param name="Distortion">The four measures.</param>
/// <param name="PackingEfficiency">Island area over atlas area, before margin.</param>
/// <param name="EffectiveEfficiency">The same after margin — what the atlas actually costs.</param>
/// <param name="TexelDensity">Achieved density across every chart.</param>
/// <param name="Stages">Time and element counts, per stage.</param>
/// <param name="Warnings">What the caller should know: a chart that had to be split, a rescale, a spill into a second tile.</param>
/// <remarks>
///     <para>
///         <b>Five of these are MeshTailor's and the rest are what a renderer needs.</b>
///         docs/plan/42 § Part 4. The paper's metric set is the best-specified one in the literature
///         — and <see cref="BoundaryJaggedness" /> in particular is what separates "low distortion"
///         from "an artist would accept this", which nothing else in the field writes down.
///     </para>
///     <para>
///         ⚠ <b><see cref="SeamLengthNormalized" /> divides by the square root of the area, not by
///         the area.</b> The paper defines it over area, which is not dimensionless: halve a model's
///         scale and the figure doubles. Both are reported so the number compares across models and
///         the published one is still recoverable.
///     </para>
///     <para>
///         ⚠ <b><see cref="PackingEfficiency" /> and <see cref="EffectiveEfficiency" /> are both
///         here because the first one flatters.</b> Raw used-area-over-atlas-area rewards a packer
///         that leaves no room to bleed into, and the gap between the two is exactly what a margin
///         setting costs.
///     </para>
/// </remarks>
public readonly record struct UvReport(
    int ChartCount,
    float Compactness,
    float Convexity,
    float SeamLength,
    float SeamLengthNormalized,
    float BoundaryJaggedness,
    UvDistortion Distortion,
    float PackingEfficiency,
    float EffectiveEfficiency,
    UvTexelDensity TexelDensity,
    IReadOnlyList<UvStageTiming> Stages,
    IReadOnlyList<string> Warnings
) {
    /// <summary>Whether the mapping is invertible everywhere, which is the one field that is pass or fail.</summary>
    public bool IsInjective => Distortion.Flipped == 0;
}
