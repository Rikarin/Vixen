// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;

namespace Vixen.TextureGraph;

/// <summary>Which of doc 48 § D10's projections a <c>Triplanar</c> op performs.</summary>
/// <remarks>
///     ⚠
///     <b>
///         The numbers are the contract with <c>Shaders/Triplanar.rvn</c>'s <c>axis</c>, which
///         compares against them literally.
///     </b>
///     Nothing in the compilation would notice a renumbering —
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
    Z = 3,

    /// <summary>Around a sphere at the middle of the bounds, with its poles on y.</summary>
    Spherical = 4,

    /// <summary>Around a cylinder along x.</summary>
    CylinderX = 5,

    /// <summary>Around a cylinder along y.</summary>
    CylinderY = 6,

    /// <summary>Around a cylinder along z.</summary>
    CylinderZ = 7
}

/// <summary>Doc 48 § D10's projection kernel, by the name a <see cref="TextureOp" /> gives.</summary>
/// <remarks>
///     ⚠
///     <b>
///         One kernel for both of <c>LayerProjection</c>'s unbuilt members, and that is the
///         finding <a href="https://github.com/Rikarin/Vixen/issues/815">#815</a> rests on.
///     </b>
///     A planar
///     projection is a triplanar one with the blend replaced by a choice, so the sampling, the wrap
///     and the scale are the same arithmetic three times; a second <c>.rvn</c> would be that
///     arithmetic written twice with nothing comparing the copies.
/// </remarks>
[TextureKernelSurface]
internal static class TextureProjectionKernels {
    /// <summary>A picture sampled by a world position, blended between three planes by the world normal.</summary>
    public const string Triplanar = "Triplanar";

    /// <summary>A picture projected through a box placed on the model, perhaps warped: Patina #133 and #134.</summary>
    public const string Decal = "Decal";

    /// <summary>A stroke or a ribbon along a polyline on the model: Patina #130 and #131.</summary>
    public const string SurfacePath = "SurfacePath";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [Decal, SurfacePath, Triplanar];
}

/// <summary>A box on the model a decal is projected through, in the model's units, its axes square.</summary>
/// <param name="Centre">The box's middle.</param>
/// <param name="Right">The picture's right, a unit vector.</param>
/// <param name="Up">The picture's up, a unit vector square to <paramref name="Right" />.</param>
/// <param name="Forward">Out of the surface the decal faces, a unit vector square to both.</param>
/// <param name="Size">Its width, height and depth.</param>
internal readonly record struct TextureDecalFrame(
    Vector3 Centre,
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    Vector3 Size
);

/// <summary>The box a <c>position</c> bake is normalised in, in the model's units.</summary>
/// <param name="Least">Its least corner.</param>
/// <param name="Size">How far it reaches along each axis.</param>
internal readonly record struct TextureModelBox(Vector3 Least, Vector3 Size) {
    /// <summary>The unit cube, which is the bake's own space.</summary>
    public static TextureModelBox Unit { get; } = new(default, new(1f, 1f, 1f));
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
    /// <param name="rotation">How far the projected coordinate turns, in radians.</param>
    /// <param name="offsetX">How far it moves along its first axis, in repeats.</param>
    /// <param name="offsetY">How far it moves along its second axis, in repeats.</param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         <paramref name="scale" /> is not a <see cref="TextureParameterUnit.TexelsAtBase" />,
    ///         and asking why is doc 48 § D8 answered rather than skipped.
    ///     </b>
    ///     It is a repeat count over
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
        TextureProjectionAxis axis = TextureProjectionAxis.Triplanar,
        float rotation = 0f,
        float offsetX = 0f,
        float offsetY = 0f
    ) =>
        new() {
            Kernel = TextureProjectionKernels.Triplanar,
            Output = output,
            Inputs = [source, position, world],
            Parameters = [
                new("scale", scale),
                new("sharpness", sharpness),
                new("axis", (float)axis),
                new("rotation", rotation),
                new("offsetX", offsetX),
                new("offsetY", offsetY)
            ]
        };

    /// <summary>The grid a decal's warp leaves alone: each point of a 3 by 3 grid where it already is.</summary>
    public static ImmutableArray<float> Unwarped { get; } = [
        0f, 0f, 0.5f, 0f, 1f, 0f,
        0f, 0.5f, 0.5f, 0.5f, 1f, 0.5f,
        0f, 1f, 0.5f, 1f, 1f, 1f
    ];

    /// <summary>Patina #133's decal: a picture projected through a box on the model, and #134's warp.</summary>
    /// <param name="output">The image to write, in the atlas.</param>
    /// <param name="source">The picture to project.</param>
    /// <param name="position">§ D12's <c>position</c> bake. Unsigned, 0..1 over <paramref name="box" />.</param>
    /// <param name="world">§ D12's <c>world</c> bake. Signed, <c>0.5 + 0.5·n</c>.</param>
    /// <param name="box">The box the position bake is normalised in.</param>
    /// <param name="frame">The decal's box.</param>
    /// <param name="facing">The cosine of the widest angle a face may turn from the decal and still take it.</param>
    /// <param name="fade">How much of the half depth the picture fades over, 0 to 1.</param>
    /// <param name="tiled">Whether the picture repeats past its edges.</param>
    /// <param name="nearest">Whether the picture is read at its nearest pixel rather than mixed.</param>
    /// <param name="warp">Eighteen numbers, where the picture's 3 by 3 grid lands on the decal's square, or empty.</param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     ⚠ <b>Every length is in the model's units and none is a <see cref="TextureParameterUnit.TexelsAtBase" /></b>,
    ///     for the triplanar scale's reason: a decal is a place on the model, so the same decal at four times the bake
    ///     covers the same part of it.
    /// </remarks>
    public static TextureOp Decal(
        int output,
        int source,
        int position,
        int world,
        TextureModelBox box,
        TextureDecalFrame frame,
        float facing = 0.2674989f,
        float fade = 0.25f,
        bool tiled = false,
        bool nearest = false,
        ReadOnlySpan<float> warp = default
    ) {
        var grid = warp.Length == Unwarped.Length ? warp : Unwarped.AsSpan();
        List<TextureParameter> parameters = [
            new("boxMinX", box.Least.X), new("boxMinY", box.Least.Y), new("boxMinZ", box.Least.Z),
            new("boxSizeX", box.Size.X), new("boxSizeY", box.Size.Y), new("boxSizeZ", box.Size.Z),
            new("centreX", frame.Centre.X), new("centreY", frame.Centre.Y), new("centreZ", frame.Centre.Z),
            new("rightX", frame.Right.X), new("rightY", frame.Right.Y), new("rightZ", frame.Right.Z),
            new("upX", frame.Up.X), new("upY", frame.Up.Y), new("upZ", frame.Up.Z),
            new("forwardX", frame.Forward.X), new("forwardY", frame.Forward.Y), new("forwardZ", frame.Forward.Z),
            new("width", frame.Size.X), new("height", frame.Size.Y), new("depth", frame.Size.Z),
            new("facing", facing),
            new("fade", fade),
            new("tiled", tiled ? 1f : 0f),
            new("nearest", nearest ? 1f : 0f),
            new("warp", warp.Length == Unwarped.Length ? 1f : 0f)
        ];

        for (var point = 0; point < 9; point++) {
            var name = point.ToString(System.Globalization.CultureInfo.InvariantCulture);

            parameters.Add(new($"warp{name}X", grid[point * 2]));
            parameters.Add(new($"warp{name}Y", grid[point * 2 + 1]));
        }

        return new() {
            Kernel = TextureProjectionKernels.Decal,
            Output = output,
            Inputs = [source, position, world],
            Parameters = [..parameters]
        };
    }

    /// <summary>Patina #130's stroke and #131's ribbon: a picture along a polyline on the model.</summary>
    /// <param name="output">The image to write, in the atlas.</param>
    /// <param name="source">The picture: read through the path, or along it for a ribbon.</param>
    /// <param name="position">§ D12's <c>position</c> bake. Unsigned, 0..1 over <paramref name="box" />.</param>
    /// <param name="world">§ D12's <c>world</c> bake. Signed, <c>0.5 + 0.5·n</c>.</param>
    /// <param name="points">The polyline's table, as <see cref="TextureSurfacePaths.Table" /> writes it.</param>
    /// <param name="count">How many points the table holds.</param>
    /// <param name="box">The box the position bake is normalised in.</param>
    /// <param name="least">The least corner of the box a texel must be in to be near the path at all.</param>
    /// <param name="most">Its greatest corner.</param>
    /// <param name="radius">Half the path's width at full pressure, in the model's units.</param>
    /// <param name="hardness">How much of the width is at full strength, 0 to 1.</param>
    /// <param name="facing">The cosine of the widest angle a face may turn from the path and still take it.</param>
    /// <param name="ribbon">Whether the picture is read along the path rather than where the texel is.</param>
    /// <param name="repeat">How far along the path a ribbon's picture repeats, or zero to keep its shape.</param>
    /// <returns>The op.</returns>
    public static TextureOp SurfacePath(
        int output,
        int source,
        int position,
        int world,
        int points,
        int count,
        TextureModelBox box,
        Vector3 least,
        Vector3 most,
        float radius = 0.01f,
        float hardness = 0.5f,
        float facing = 0.2674989f,
        bool ribbon = false,
        float repeat = 0f
    ) =>
        new() {
            Kernel = TextureProjectionKernels.SurfacePath,
            Output = output,
            Inputs = [source, position, world, points],
            Parameters = [
                new("count", count),
                new("boxMinX", box.Least.X), new("boxMinY", box.Least.Y), new("boxMinZ", box.Least.Z),
                new("boxSizeX", box.Size.X), new("boxSizeY", box.Size.Y), new("boxSizeZ", box.Size.Z),
                new("reachMinX", least.X), new("reachMinY", least.Y), new("reachMinZ", least.Z),
                new("reachMaxX", most.X), new("reachMaxY", most.Y), new("reachMaxZ", most.Z),
                new("radius", radius),
                new("hardness", hardness),
                new("facing", facing),
                new("ribbon", ribbon ? 1f : 0f),
                new("period", repeat)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what a test over the builders prints on the day one of them is forgotten.</b> A
    ///     theory with an <c>InlineData</c> per builder passes silently when a second is added and
    ///     not listed; this list is what the parameter-agreement test walks, so a builder reaches it
    ///     by existing.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [
        Triplanar(0, 1, 2, 3),
        Decal(
            0,
            1,
            2,
            3,
            TextureModelBox.Unit,
            new(default, new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f), new(1f, 1f, 1f))
        ),
        SurfacePath(0, 1, 2, 3, 4, 1, TextureModelBox.Unit, default, new(1f, 1f, 1f))
    ];
}

/// <summary>A polyline on the model as the <c>SurfacePath</c> kernel reads it: a table two rows high.</summary>
/// <remarks>
///     Row 0 holds each point and its pressure, row 1 its normal and how far along the path it is, as 32-bit floats:
///     a half would put a point on a model a metre across a millimetre out, which a path a few millimetres wide shows.
/// </remarks>
internal static class TextureSurfacePaths {
    /// <summary>How many points a table holds at most, which is the kernel's loop.</summary>
    public const int MaximumPoints = 1024;

    /// <summary>A polyline as the kernel's table, RGBA32F texels, row-major.</summary>
    /// <param name="points">The points, in the model's units.</param>
    /// <param name="normals">The surface's normal at each.</param>
    /// <param name="pressures">How much of the width and strength each has.</param>
    /// <returns>Two rows of <c>points.Count</c> texels.</returns>
    /// <exception cref="ArgumentException">
    ///     The lists differ in length, are empty, or hold more than
    ///     <see cref="MaximumPoints" />.
    /// </exception>
    public static byte[] Table(
        IReadOnlyList<Vector3> points,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<float> pressures
    ) {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(pressures);

        if (points.Count == 0
            || points.Count > MaximumPoints
            || normals.Count != points.Count
            || pressures.Count != points.Count) {
            throw new ArgumentException(
                $"A path's table holds 1 to {MaximumPoints} points, each with a normal and a pressure.",
                nameof(points)
            );
        }

        var count = points.Count;
        var floats = new float[count * 8];
        var along = 0f;

        for (var index = 0; index < count; index++) {
            if (index > 0) {
                along += Vector3.Distance(points[index - 1], points[index]);
            }

            var top = index * 4;
            var bottom = (count + index) * 4;

            floats[top] = points[index].X;
            floats[top + 1] = points[index].Y;
            floats[top + 2] = points[index].Z;
            floats[top + 3] = pressures[index];
            floats[bottom] = normals[index].X;
            floats[bottom + 1] = normals[index].Y;
            floats[bottom + 2] = normals[index].Z;
            floats[bottom + 3] = along;
        }

        var bytes = new byte[floats.Length * sizeof(float)];

        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);

        return bytes;
    }
}
