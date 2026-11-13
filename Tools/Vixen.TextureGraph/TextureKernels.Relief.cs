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

    /// <summary>A height field pushed out along one direction, by a bounded walk keeping the highest — Patina #168.</summary>
    public const string HeightExtrude = "HeightExtrude";

    /// <summary>
    ///     Rays cast from a height field into the sky: what share got out, the direction they got out in, and the
    ///     light they brought back — Patina #176's traced occlusion, bent normal and irradiance.
    /// </summary>
    public const string HeightRays = "HeightRays";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [HeightExtrude, HeightRays, HeightShadow, NormalBlend];
}

/// <summary>What <see cref="TextureRelief.HeightRays" /> stores, by its kernel's <c>mode</c>.</summary>
enum TextureHeightRays {
    /// <summary>The share of rays that reached the sky.</summary>
    Occlusion = 0,

    /// <summary>The mean direction of those that did, as a tangent-space normal.</summary>
    BentNormal = 1,

    /// <summary>The light they brought back: the sky's, or the light where a blocked one struck.</summary>
    Irradiance = 2
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
    public static TextureOp NormalBlend(
        int output,
        int background,
        int foreground,
        int mask = -1,
        float opacity = 1f
    ) =>
        new() {
            Kernel = TextureReliefKernels.NormalBlend,
            Output = output,
            Inputs = [background, foreground, mask >= 0 ? mask : foreground],
            Parameters = [new("opacity", opacity), new("masked", mask >= 0 ? 1f : 0f)]
        };

    /// <summary>Patina #168's height extrude.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="height">The height field, read from its red channel.</param>
    /// <param name="angle">The direction the shapes are pushed, in radians: 0 toward +x, π/2 toward +y.</param>
    /// <param name="length">How far, as a fraction of the image's longer side.</param>
    /// <param name="falloff">How far the side falls over the length, 0–1.</param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     ⚠ <b>The length is a fraction of the image and not texels at the base</b>, as a distance's reach is, so it
    ///     is the same extrusion at every bake without § D8's scaling.
    /// </remarks>
    public static TextureOp HeightExtrude(
        int output,
        int height,
        float angle = 0.785398f,
        float length = 0.05f,
        float falloff = 0f
    ) =>
        new() {
            Kernel = TextureReliefKernels.HeightExtrude,
            Output = output,
            Inputs = [height],
            Parameters = [
                new("angle", angle),
                new("length", length),
                new("falloff", falloff)
            ]
        };

    /// <summary>Patina #176's traced occlusion, bent normal and irradiance: one march, three readings of it.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="height">The height field, read from its red channel.</param>
    /// <param name="reading">What the rays are asked.</param>
    /// <param name="light">What a blocked ray brings back in irradiance, or −1 for black.</param>
    /// <param name="radius">How far a ray is followed, in texels at the plan's base resolution.</param>
    /// <param name="samples">How many rays leave each texel, rounded up to a pair; at most 64.</param>
    /// <param name="height01">How tall a height of one is, as a fraction of the image's width.</param>
    /// <param name="sky">The colour a ray that gets out brings back in irradiance; white when left out.</param>
    /// <returns>The op.</returns>
    public static TextureOp HeightRays(
        int output,
        int height,
        TextureHeightRays reading = TextureHeightRays.Occlusion,
        int light = -1,
        float radius = 32f,
        int samples = 32,
        float height01 = 0.1f,
        (float Red, float Green, float Blue)? sky = null
    ) {
        var (red, green, blue) = sky ?? (1f, 1f, 1f);

        return new() {
            Kernel = TextureReliefKernels.HeightRays,
            Output = output,
            Inputs = [height, light >= 0 ? light : height],
            Parameters = [
                new("radius", radius, TextureParameterUnit.TexelsAtBase),
                new("samples", samples),
                new("height", height01),
                new("mode", (float)reading),
                new("lit", light >= 0 ? 1f : 0f),
                new("skyRed", red),
                new("skyGreen", green),
                new("skyBlue", blue)
            ]
        };
    }

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    public static ImmutableArray<TextureOp> All { get; } =
        [HeightExtrude(0, 1), HeightRays(0, 1), HeightShadow(0, 1), NormalBlend(0, 1, 2)];
}
