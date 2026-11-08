// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph;

/// <summary>The smoothing filters Patina's audit found missing, by kernel name, with their op builders — Patina #173.</summary>
/// <remarks>
///     <para>
///         <b>Three of the five take a length and every one of those spends a budget in taps</b>, which is
///         <c>Blur</c>'s rule since <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a>: past the
///         budget the taps spread out, so a 4× bake is the same width at the same cost rather than a
///         narrower filter or a refusal. Nothing here is in <c>TextureKernelCeilings</c>, and nothing
///         should be.
///     </para>
///     <para>
///         <b>Every read is <c>Math.Address</c></b>, so each of them keeps a tiling input tiling except
///         <see cref="SpinBlur" />, whose arcs about one centre run opposite ways at opposite edges.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureSmoothingFilters {
    /// <summary>The per-channel median of a square window.</summary>
    public const string Median = "Median";

    /// <summary>A bilateral filter: a gaussian that does not cross a colour edge.</summary>
    public const string Bilateral = "Bilateral";

    /// <summary>A Kuwahara filter: each texel takes the mean of its least varied quadrant.</summary>
    public const string Kuwahara = "Kuwahara";

    /// <summary>A smear along the circle about a centre.</summary>
    public const string SpinBlur = "SpinBlur";

    /// <summary>A staircase edge smoothed along its own direction.</summary>
    public const string AntiAlias = "AntiAlias";

    /// <summary>Every one of them.</summary>
    public static IReadOnlyList<string> All { get; } = [AntiAlias, Bilateral, Kuwahara, Median, SpinBlur];

    /// <summary>A median of a square window.</summary>
    /// <param name="output">The image written.</param>
    /// <param name="source">The image read.</param>
    /// <param name="radius">The window's half-width, in texels at the base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp MedianOp(int output, int source, float radius) =>
        new() {
            Kernel = Median,
            Output = output,
            Inputs = [source],
            Parameters = [new("radius", radius, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>A bilateral blur.</summary>
    /// <param name="output">The image written.</param>
    /// <param name="source">The image read.</param>
    /// <param name="sigma">The spatial sigma, in texels at the base resolution.</param>
    /// <param name="rangeSigma">The colour difference at which a tap's weight falls to e^-½. Not a length.</param>
    /// <returns>The op.</returns>
    public static TextureOp BilateralOp(int output, int source, float sigma, float rangeSigma) =>
        new() {
            Kernel = Bilateral,
            Output = output,
            Inputs = [source],
            Parameters = [new("sigma", sigma, TextureParameterUnit.TexelsAtBase), new("rangeSigma", rangeSigma)]
        };

    /// <summary>A Kuwahara filter.</summary>
    /// <param name="output">The image written.</param>
    /// <param name="source">The image read.</param>
    /// <param name="radius">The side of each quadrant, in texels at the base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp KuwaharaOp(int output, int source, float radius) =>
        new() {
            Kernel = Kuwahara,
            Output = output,
            Inputs = [source],
            Parameters = [new("radius", radius, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>A spin blur.</summary>
    /// <param name="output">The image written.</param>
    /// <param name="source">The image read.</param>
    /// <param name="angle">The whole arc, in radians. Not a length.</param>
    /// <param name="centreX">The centre, as a fraction of the width.</param>
    /// <param name="centreY">The centre, as a fraction of the height.</param>
    /// <param name="samples">How many reads along the arc.</param>
    /// <returns>The op.</returns>
    public static TextureOp SpinBlurOp(
        int output,
        int source,
        float angle,
        float centreX = 0.5f,
        float centreY = 0.5f,
        int samples = 16
    ) =>
        new() {
            Kernel = SpinBlur,
            Output = output,
            Inputs = [source],
            Parameters = [
                new("centreX", centreX),
                new("centreY", centreY),
                new("angle", angle),
                new("samples", samples)
            ]
        };

    /// <summary>An anti-alias.</summary>
    /// <param name="output">The image written.</param>
    /// <param name="source">The image read.</param>
    /// <param name="threshold">The edge contrast below which a texel is left alone, from nought to one.</param>
    /// <returns>The op.</returns>
    public static TextureOp AntiAliasOp(int output, int source, float threshold) =>
        new() {
            Kernel = AntiAlias,
            Output = output,
            Inputs = [source],
            Parameters = [new("threshold", threshold)]
        };
}
