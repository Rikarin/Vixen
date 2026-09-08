// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>Which of doc 48 § D10's projections a <c>Triplanar</c> op performs.</summary>
/// <remarks>
///     ⚠ <b>The numbers are the contract with <c>Shaders/Triplanar.rvn</c>'s <c>axis</c>, which
///     compares against them literally.</b> Nothing in the compilation would notice a renumbering —
///     a planar fill along the wrong axis is a perfectly plausible picture, and on a box it is the
///     same picture rotated. <c>TextureProjectionDeviceTests</c> pins each one by driving the world
///     normal down that axis and requiring exactly the plane it names.
/// </remarks>
enum TextureProjectionAxis {
    /// <summary>All three planes, blended by the world normal. § D10's <c>Triplanar</c>.</summary>
    Triplanar = 0,

    /// <summary>The plane facing x alone, sampled by <c>(z, y)</c>.</summary>
    X = 1,

    /// <summary>The plane facing y alone, sampled by <c>(x, z)</c>.</summary>
    Y = 2,

    /// <summary>The plane facing z alone, sampled by <c>(x, y)</c>.</summary>
    Z = 3
}

/// <summary>Doc 48 § D10's projection kernel, by the name a <see cref="TextureOp" /> gives.</summary>
/// <remarks>
///     ⚠ <b>One kernel for both of <c>LayerProjection</c>'s unbuilt members, and that is the
///     finding <a href="https://github.com/Rikarin/Vixen/issues/815">#815</a> rests on.</b> A planar
///     projection is a triplanar one with the blend replaced by a choice, so the sampling, the wrap
///     and the scale are the same arithmetic three times; a second <c>.rvn</c> would be that
///     arithmetic written twice with nothing comparing the copies.
/// </remarks>
[TextureKernelSurface]
internal static class TextureProjectionKernels {
    /// <summary>A picture sampled by a world position, blended between three planes by the world normal.</summary>
    public const string Triplanar = "Triplanar";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [Triplanar];
}

/// <summary>The ops doc 48 § D10's projections are.</summary>
/// <remarks>
///     <b>A builder rather than an op at the call site</b>, for <see cref="TextureSurfaces" />'s
///     reason: <c>TexturePlanEvaluator.Uniforms</c> refuses an op that leaves out one of the
///     parameters its kernel declares, so writing one out by hand is a chance to produce an
///     exception at bake time and — worse — a chance to name the wrong one and get a plausible
///     picture.
/// </remarks>
[TextureKernelSurface]
internal static class TextureProjections {
    /// <summary>Doc 48 § D10's triplanar and planar fill.</summary>
    /// <param name="output">The image to write, in the atlas.</param>
    /// <param name="source">The picture to project.</param>
    /// <param name="position">§ D12's <c>position</c> bake. ⚠ Unsigned — already 0..1 over the bounds.</param>
    /// <param name="world">§ D12's <c>world</c> bake. ⚠ Signed — <c>0.5 + 0.5·n</c>, decoded by the kernel.</param>
    /// <param name="scale">How many times the picture repeats across the position range.</param>
    /// <param name="sharpness">The exponent the blend weights are raised to. Ignored by a planar axis.</param>
    /// <param name="axis">Which projection.</param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="scale" /> is not a <see cref="TextureParameterUnit.TexelsAtBase" />,
    ///     and asking why is doc 48 § D8 answered rather than skipped.</b> It is a repeat count over
    ///     a <em>world</em> range, so it has no length in the image at all: the same graph at four
    ///     times the bake covers the mesh in the same number of tiles, which is exactly the invariant
    ///     § D8 is about. A projection that scaled with the resolution would be the § D8 bug written
    ///     the other way up.
    /// </remarks>
    public static TextureOp Triplanar(
        int output,
        int source,
        int position,
        int world,
        float scale = 1f,
        float sharpness = 4f,
        TextureProjectionAxis axis = TextureProjectionAxis.Triplanar
    ) =>
        new() {
            Kernel = TextureProjectionKernels.Triplanar,
            Output = output,
            Inputs = [source, position, world],
            Parameters = [
                new("scale", scale),
                new("sharpness", sharpness),
                new("axis", (float)axis)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what a test over the builders prints on the day one of them is forgotten.</b> A
    ///     theory with an <c>InlineData</c> per builder passes silently when a second is added and
    ///     not listed; this list is what the parameter-agreement test walks, so a builder reaches it
    ///     by existing.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [Triplanar(0, 1, 2, 3)];
}
