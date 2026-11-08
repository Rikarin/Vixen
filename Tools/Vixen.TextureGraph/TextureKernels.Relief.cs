// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.TextureGraph;

/// <summary>Patina #176's surface and lighting kernels, by the name a <see cref="TextureOp" /> gives.</summary>
/// <remarks>
///     Beside <see cref="TextureSurfaceKernels" /> rather than in it, so that § 4.6's five keep the count
///     their roll call states. Both read the green convention <c>HeightToNormal.rvn</c> derives.
/// </remarks>
[TextureKernelSurface]
internal static class TextureReliefKernels {
    /// <summary>Where a height field shadows itself under one light: a march toward it, keeping the steepest rise.</summary>
    public const string HeightShadow = "HeightShadow";

    /// <summary>One normal map faded into another as unit vectors, by an opacity and a mask.</summary>
    public const string NormalBlend = "NormalBlend";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [HeightShadow, NormalBlend];
}

/// <summary>The ops Patina #176's surface and lighting nodes are.</summary>
/// <remarks>
///     Every builder emits the complete parameter set its kernel declares, for <see cref="TextureSources" />'s
///     reason: <c>TexturePlanEvaluator.Uniforms</c> refuses an op that leaves one out.
/// </remarks>
[TextureKernelSurface]
internal static class TextureRelief {
    /// <summary>Patina #176's height shadow.</summary>
    /// <param name="output">The image to write: one where lit, nought where shadowed.</param>
    /// <param name="height">The height field, read from its red channel.</param>
    /// <param name="angle">The direction the light is in, in radians: 0 toward +x, π/2 toward +y.</param>
    /// <param name="elevation">How high the light stands, in radians above the horizon.</param>
    /// <param name="radius">How far a shadow can be cast, in texels at the plan's base resolution.</param>
    /// <param name="height01">How tall a height of one is, as a fraction of the image's width.</param>
    /// <param name="softness">The light's angular size, in radians.</param>
    /// <returns>The op.</returns>
    public static TextureOp HeightShadow(
        int output,
        int height,
        float angle = -2.3561945f,
        float elevation = 0.7853982f,
        float radius = 64f,
        float height01 = 0.1f,
        float softness = 0f
    ) =>
        new() {
            Kernel = TextureReliefKernels.HeightShadow,
            Output = output,
            Inputs = [height],
            Parameters = [
                new("angle", angle),
                new("elevation", elevation),
                new("radius", radius, TextureParameterUnit.TexelsAtBase),
                new("height", height01),
                new("softness", softness)
            ]
        };

    /// <summary>Patina #176's normal mix.</summary>
    /// <param name="output">The normal map to write.</param>
    /// <param name="background">The map where the mask is nought.</param>
    /// <param name="foreground">The map where the mask is one.</param>
    /// <param name="mask">A mask scaling the opacity, or −1 for none.</param>
    /// <param name="opacity">How much of the foreground reaches the result.</param>
    /// <returns>The op.</returns>
    public static TextureOp NormalBlend(int output, int background, int foreground, int mask = -1, float opacity = 1f) =>
        new() {
            Kernel = TextureReliefKernels.NormalBlend,
            Output = output,
            Inputs = [background, foreground, mask >= 0 ? mask : foreground],
            Parameters = [new("opacity", opacity), new("masked", mask >= 0 ? 1f : 0f)]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    public static ImmutableArray<TextureOp> All { get; } = [HeightShadow(0, 1), NormalBlend(0, 1, 2)];
}
