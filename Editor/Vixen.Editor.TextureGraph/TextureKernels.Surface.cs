// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>Doc 48 § 4.6's surface kernels, by the name a <see cref="TextureOp" /> gives.</summary>
/// <remarks>
///     <para>
///         <b>Five names for six nodes.</b> § 4.6's <c>Normal → Height</c> has no kernel here and
///         cannot have one: it is a Poisson solve, the plan document names
///         <c>Core/Vixen.Geometry.Uv/Solving/ConjugateGradient.cs</c> as what should run it, and it
///         says in as many words that it runs on the <b>CPU</b> as a deliberate exception to
///         doc 48 § D3. ⚠ <b><see cref="TexturePlan" /> cannot express that.</b> Every
///         <see cref="TextureOp" /> goes through <c>TexturePlanEvaluator.VariantFor</c>, which
///         compiles the op's <c>Kernel</c> as Raven and builds a compute pipeline from it — there is
///         no op kind that is not a dispatch, and no way for one to be. That is a finding about the
///         plan rather than a reason to write a GPU Poisson nobody asked for, and it is reported as
///         one.
///     </para>
///     <para>
///         ⚠ <b>Everything here shares one convention and it is derived, not chosen.</b>
///         <c>Shaders/HeightToNormal.rvn</c> works out from
///         <c>Raven/Library/Material/MaterialSurface.rvn</c>, <c>Normals.Frame</c> and
///         <c>MapBaker.Frame</c> that green is <c>−∂h/∂v</c> with <c>v</c> pointing <em>down</em> the
///         image — so a height that rises downwards is green below a half. Every other kernel in this
///         file reads that convention rather than restating it, and
///         <c>TextureSurfaceDeviceTests</c> asserts it against a ramp because a flipped green is the
///         defect that survives every review: the lighting stays plausible.
///     </para>
/// </remarks>
internal static class TextureSurfaceKernels {
    /// <summary>A Sobel gradient turned into a tangent-space normal.</summary>
    public const string HeightToNormal = "HeightToNormal";

    /// <summary>Reoriented normal mapping — ⚠ not whiteout, which agrees with it on the flat case.</summary>
    public const string NormalCombine = "NormalCombine";

    /// <summary>Flip green, turn the frame, renormalise.</summary>
    public const string NormalTransform = "NormalTransform";

    /// <summary>The divergence of a normal field, centred on a half. ⚠ Not § D12's mesh bake.</summary>
    public const string Curvature = "Curvature";

    /// <summary>A horizon search over a height field. ⚠ Not § D12's mesh bake either.</summary>
    public const string AmbientOcclusion = "AmbientOcclusion";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [
        AmbientOcclusion,
        Curvature,
        HeightToNormal,
        NormalCombine,
        NormalTransform
    ];
}

/// <summary>The ops doc 48 § 4.6's surface nodes are.</summary>
/// <remarks>
///     <b>Builders rather than an op at each call site</b>, for <see cref="TextureSources" />'s
///     reason: <c>TexturePlanEvaluator.Uniforms</c> refuses an op that leaves out one of the
///     parameters its kernel declares, so writing one out by hand is a chance to produce an exception
///     at bake time and — worse — a chance to name the wrong one and get a plausible picture.
/// </remarks>
internal static class TextureSurfaces {
    /// <summary>Doc 48 § 4.6's <c>Height → Normal</c>.</summary>
    /// <param name="output">The normal map to write.</param>
    /// <param name="height">The height field, read from its red channel.</param>
    /// <param name="intensity">How far the normal is bent. 1 is the height field's true slope.</param>
    /// <param name="width">The Sobel's tap spacing, in texels at the plan's base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp HeightToNormal(int output, int height, float intensity = 1f, float width = 1f) =>
        new() {
            Kernel = TextureSurfaceKernels.HeightToNormal,
            Output = output,
            Inputs = [height],
            Parameters = [
                new("intensity", intensity),
                new("width", width, TextureParameterUnit.TexelsAtBase)
            ]
        };

    /// <summary>Doc 48 § 4.6's <c>Normal Combine</c>, reoriented.</summary>
    /// <param name="output">The combined map.</param>
    /// <param name="baseMap">The map whose orientation is kept.</param>
    /// <param name="detailMap">The map rotated into the base's frame.</param>
    /// <param name="opacity">How much of the detail is applied.</param>
    /// <returns>The op.</returns>
    public static TextureOp NormalCombine(int output, int baseMap, int detailMap, float opacity = 1f) =>
        new() {
            Kernel = TextureSurfaceKernels.NormalCombine,
            Output = output,
            Inputs = [baseMap, detailMap],
            Parameters = [new("opacity", opacity)]
        };

    /// <summary>Doc 48 § 4.6's <c>Normal Transform</c>.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The normal map to transform.</param>
    /// <param name="flipGreen">Whether to negate green about a half.</param>
    /// <param name="rotation">How far the frame turns, in radians, clockwise on screen.</param>
    /// <param name="renormalise">Whether to return a unit vector.</param>
    /// <returns>The op.</returns>
    public static TextureOp NormalTransform(
        int output,
        int source,
        bool flipGreen = false,
        float rotation = 0f,
        bool renormalise = true
    ) =>
        new() {
            Kernel = TextureSurfaceKernels.NormalTransform,
            Output = output,
            Inputs = [source],
            Parameters = [
                new("flipGreen", flipGreen ? 1f : 0f),
                new("rotation", rotation),
                new("renormalise", renormalise ? 1f : 0f)
            ]
        };

    /// <summary>Doc 48 § 4.6's <c>Curvature from Normal</c>.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="normal">A tangent-space normal map.</param>
    /// <param name="radius">The central difference's half-width, in texels at the base resolution.</param>
    /// <param name="intensity">What the divergence is multiplied by before it is centred.</param>
    /// <returns>The op. ⚠ Not § D12's mesh curvature — see the kernel.</returns>
    public static TextureOp Curvature(int output, int normal, float radius = 1f, float intensity = 0.25f) =>
        new() {
            Kernel = TextureSurfaceKernels.Curvature,
            Output = output,
            Inputs = [normal],
            Parameters = [
                new("radius", radius, TextureParameterUnit.TexelsAtBase),
                new("intensity", intensity)
            ]
        };

    /// <summary>Doc 48 § 4.6's <c>Ambient Occlusion from Height</c>.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="height">The height field, read from its red channel.</param>
    /// <param name="radius">How far each ray marches, in texels at the base resolution.</param>
    /// <param name="samples">How many directions are searched. Capped by the kernel at sixteen.</param>
    /// <param name="height01">
    ///     How tall a height of one is, as a fraction of the image's width. ⚠ Zero is a flat surface
    ///     and an answer of one everywhere, which is a plausible picture from a node that never ran.
    /// </param>
    /// <returns>The op. ⚠ Not § D12's mesh bake — see the kernel.</returns>
    public static TextureOp AmbientOcclusion(
        int output,
        int height,
        float radius = 16f,
        int samples = 8,
        float height01 = 0.1f
    ) =>
        new() {
            Kernel = TextureSurfaceKernels.AmbientOcclusion,
            Output = output,
            Inputs = [height],
            Parameters = [
                new("radius", radius, TextureParameterUnit.TexelsAtBase),
                new("samples", samples),
                new("height", height01)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what a test over the builders prints on the day one of them is forgotten.</b> A
    ///     theory with an <c>InlineData</c> per builder passes silently when a sixth is added and not
    ///     listed; this list is what the parameter-agreement test walks, so a builder reaches it by
    ///     existing.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [
        HeightToNormal(0, 1),
        NormalCombine(0, 1, 2),
        NormalTransform(0, 1),
        Curvature(0, 1),
        AmbientOcclusion(0, 1)
    ];
}
