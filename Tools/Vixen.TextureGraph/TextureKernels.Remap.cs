// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph;

/// <summary>The transform nodes Patina's audit found missing, by kernel name — Patina #175.</summary>
/// <remarks>
///     <para>
///         <b>Every one is a remap: an output texel asks where in the source it came from.</b> None takes
///         a length — a centre or a radius is a fraction of the image and an angle is radians — so each is
///         resolution-independent by construction rather than by <c>TexturePlan.Resolve</c>.
///     </para>
///     <para>
///         ⚠ <b>The turns are taken in texels and not in the unit square</b>, so a circle stays a circle
///         on an image that is not square. <c>Space/Transform 2D</c> was the one that did not, and now
///         does the same.
///     </para>
///     <para>
///         <see cref="BoundsReduce" /> is a rung of the ladder <c>Space/Trim to Content</c> climbs before
///         <see cref="Trim" /> can crop, so that pair reads every texel of its input before writing one.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureRemapKernels {
    /// <summary>Square to polar coordinates, or back.</summary>
    public const string Polar = "Polar";

    /// <summary>The image's four corners moved to four points, by an inverse bilinear map.</summary>
    public const string CornerWarp = "CornerWarp";

    /// <summary>The image turned about a centre by an angle a map scales per texel.</summary>
    public const string RotateByMap = "RotateByMap";

    /// <summary>The image twisted about a centre, falling to nothing at a radius.</summary>
    public const string Swirl = "Swirl";

    /// <summary>One wedge of the image repeated round a centre, mirrored every other time.</summary>
    public const string WedgeSymmetry = "WedgeSymmetry";

    /// <summary>One rung of the box that holds an image's content.</summary>
    public const string BoundsReduce = "BoundsReduce";

    /// <summary>The image cropped to the box the ladder found, and scaled to fill.</summary>
    public const string Trim = "Trim";

    /// <summary>Every one of them.</summary>
    public static IReadOnlyList<string> All { get; } = [
        BoundsReduce,
        CornerWarp,
        Polar,
        RotateByMap,
        Swirl,
        Trim,
        WedgeSymmetry
    ];
}
