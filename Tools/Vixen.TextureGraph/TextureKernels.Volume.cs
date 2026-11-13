// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.TextureGraph;

/// <summary>Which lattice a <c>Volume Noise</c> sums.</summary>
/// <remarks>
///     The numbers are the contract with <c>Shaders/VolumeNoise.rvn</c>'s <c>basis</c>, which compares against them
///     literally.
/// </remarks>
internal enum TextureVolumeBasis {
    /// <summary>A value per lattice point, blended trilinearly: soft and blobby.</summary>
    Value = 0,

    /// <summary>Perlin's gradient noise over the twelve cube-edge gradients: the usual one.</summary>
    Gradient = 1,

    /// <summary>The distance to the nearest of one jittered point per cell: pits, cells and pores.</summary>
    Worley = 2,

    /// <summary>Gradient noise over a tetrahedral lattice: cheaper and less aligned with the axes.</summary>
    Simplex = 3
}

/// <summary>Which shape a <c>Volume Mask</c> is.</summary>
/// <remarks>The numbers are the contract with <c>Shaders/VolumeShape.rvn</c>'s <c>kind</c>.</remarks>
internal enum TextureVolumeShape {
    /// <summary>A box of the given extents.</summary>
    Box = 0,

    /// <summary>An ellipsoid of the given diameters.</summary>
    Ellipsoid = 1
}

/// <summary>Patina #179's kernels driven by a position map, by the name a <see cref="TextureOp" /> gives.</summary>
[TextureKernelSurface]
internal static class TextureVolumeKernels {
    /// <summary>A 3D noise evaluated at each texel's position on the model.</summary>
    public const string VolumeNoise = "VolumeNoise";

    /// <summary>A box, an ellipsoid or a two-point ramp in the model's space.</summary>
    public const string VolumeShape = "VolumeShape";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [VolumeNoise, VolumeShape];
}

/// <summary>The ops Patina #179's nodes are.</summary>
/// <remarks>
///     <para>
///         <b>Every one reads § D12's <c>position</c> bake</b> — 0..1 over the mesh's bounds — so its
///         numbers are fractions of the model's bounding box and its picture continues across UV seams.
///     </para>
///     <para>
///         Every builder emits the complete parameter set its kernel declares, for
///         <see cref="TextureSources" />'s reason.
///     </para>
/// </remarks>
[TextureKernelSurface]
internal static class TextureVolumes {
    /// <summary>The kind <c>VolumeShape.rvn</c> draws a two-point ramp under.</summary>
    const int GradientKind = 2;

    /// <summary>A 3D noise read through a position map.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="position">The position map, 0..1 over the mesh's bounds.</param>
    /// <param name="basis">Which lattice.</param>
    /// <param name="profile">Which fold, per octave.</param>
    /// <param name="scale">Cells across the bounding box at the first octave.</param>
    /// <param name="octaves">How many octaves.</param>
    /// <param name="lacunarity">How much finer each octave is.</param>
    /// <param name="gain">How much weaker each octave is.</param>
    /// <param name="offset">A shift of the lattice, in cells of the first octave.</param>
    /// <param name="variation">Another draw of the same noise; nought is the op's own seed.</param>
    /// <returns>The op.</returns>
    public static TextureOp Noise(
        int output,
        int position,
        TextureVolumeBasis basis = TextureVolumeBasis.Gradient,
        TextureNoiseProfile profile = TextureNoiseProfile.Plain,
        float scale = 4f,
        int octaves = 4,
        float lacunarity = 2f,
        float gain = 0.5f,
        (float X, float Y, float Z) offset = default,
        int variation = 0
    ) =>
        new() {
            Kernel = TextureVolumeKernels.VolumeNoise,
            Output = output,
            Inputs = [position],

            // Patina #179: an unwired node reads the bake's position map as it is, whatever its size.
            ReadsOtherExtents = true,
            Parameters = [
                new("basis", (float)basis),
                new("profile", (float)profile),
                new("scale", scale),
                new("octaves", octaves),
                new("lacunarity", lacunarity),
                new("gain", gain),
                new("offsetX", offset.X),
                new("offsetY", offset.Y),
                new("offsetZ", offset.Z),
                new("variation", variation)
            ]
        };

    /// <summary>A box or an ellipsoid in the model's space.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="position">The position map, 0..1 over the mesh's bounds.</param>
    /// <param name="shape">Which shape.</param>
    /// <param name="centre">Its centre.</param>
    /// <param name="size">Its extents, or diameters.</param>
    /// <param name="falloff">How far outside it fades to nought.</param>
    /// <returns>The op.</returns>
    public static TextureOp Mask(
        int output,
        int position,
        TextureVolumeShape shape,
        (float X, float Y, float Z) centre,
        (float X, float Y, float Z) size,
        float falloff
    ) =>
        Shape(output, position, (int)shape, centre, size, falloff);

    /// <summary>A ramp from nought at one point to one at another, in the model's space.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="position">The position map, 0..1 over the mesh's bounds.</param>
    /// <param name="start">Where it is nought.</param>
    /// <param name="end">Where it is one.</param>
    /// <returns>The op.</returns>
    public static TextureOp Gradient(
        int output,
        int position,
        (float X, float Y, float Z) start,
        (float X, float Y, float Z) end
    ) =>
        Shape(output, position, GradientKind, start, end, 0f);

    static TextureOp Shape(
        int output,
        int position,
        int kind,
        (float X, float Y, float Z) a,
        (float X, float Y, float Z) b,
        float falloff
    ) =>
        new() {
            Kernel = TextureVolumeKernels.VolumeShape,
            Output = output,
            Inputs = [position],

            // Patina #179: an unwired node reads the bake's position map as it is, whatever its size.
            ReadsOtherExtents = true,
            Parameters = [
                new("kind", kind),
                new("ax", a.X),
                new("ay", a.Y),
                new("az", a.Z),
                new("bx", b.X),
                new("by", b.Y),
                new("bz", b.Z),
                new("falloff", falloff)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    public static ImmutableArray<TextureOp> All { get; } = [
        Noise(0, 1),
        Mask(0, 1, TextureVolumeShape.Box, (0.5f, 0.5f, 0.5f), (0.5f, 0.5f, 0.5f), 0.1f),
        Gradient(0, 1, (0.5f, 0f, 0.5f), (0.5f, 1f, 0.5f))
    ];
}
