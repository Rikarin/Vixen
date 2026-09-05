// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The atlas a bake is writing into: every channel, and the counters it keeps.</summary>
/// <remarks>
///     <para>
///         <b>One object rather than fifteen parameters.</b> The rasterizer and the dilation each
///         touch every channel, and threading them through as arguments is how a channel gets added
///         to one and forgotten in the other — which for the gutter is not a compile error, it is a
///         map with an undilated border that only shows up at mip 3.
///     </para>
///     <para>
///         ⚠ <b>A channel that was not asked for is <c>null</c>, not empty.</b> Ambient occlusion and
///         thickness are the only expensive measurements in this bake — they are the only ones that
///         cast more rays than the one the normal map already casts — so a caller that wants a
///         normal map must not pay for them. Null says "not asked for" where an all-zero array would
///         say "asked for, and every texel is fully occluded".
///     </para>
/// </remarks>
sealed class BakeBuffers {
    /// <summary>The struck source normal per texel, in the requested space.</summary>
    public Vector3[] Normals { get; }

    /// <summary>The signed distance to the source per texel.</summary>
    public float[] Displacement { get; }

    /// <summary>Whether a texel is chart content rather than gutter or background.</summary>
    public bool[] Coverage { get; }

    /// <summary>The unoccluded fraction of the hemisphere, when it was asked for.</summary>
    public float[]? Occlusion { get; }

    /// <summary>The average unoccluded direction, when it was asked for.</summary>
    public Vector3[]? Bent { get; }

    /// <summary>The interpolated mean curvature, when it was asked for.</summary>
    public float[]? Curvature { get; }

    /// <summary>The occluded fraction of the inverted hemisphere, when it was asked for.</summary>
    public float[]? Thickness { get; }

    /// <summary>The surface point normalised into the source's box, when it was asked for.</summary>
    public Vector3[]? Position { get; }

    /// <summary>The source normal unrotated, when it was asked for.</summary>
    public Vector3[]? WorldNormal { get; }

    /// <summary>The source's face group per texel, or <c>-1</c> where there is none.</summary>
    public int[]? Ids { get; }

    /// <summary>How many texels the charts claimed.</summary>
    public int Covered { get; set; }

    /// <summary>How many of those found no source along the normal.</summary>
    public int Missed { get; set; }

    /// <summary>The largest absolute displacement seen.</summary>
    public float DisplacementRange { get; set; }

    /// <summary>The largest absolute curvature seen.</summary>
    public float CurvatureRange { get; set; }

    /// <summary>Allocates exactly the channels the settings asked for.</summary>
    /// <param name="settings">What to bake, and how big.</param>
    public BakeBuffers(BakeSettings settings) {
        var texels = settings.Resolution * settings.Resolution;
        var maps = settings.Maps;

        Normals = new Vector3[texels];
        Displacement = new float[texels];
        Coverage = new bool[texels];

        Occlusion = maps.HasFlag(MeshMaps.AmbientOcclusion) ? new float[texels] : null;
        Bent = maps.HasFlag(MeshMaps.BentNormal) ? new Vector3[texels] : null;
        Curvature = maps.HasFlag(MeshMaps.Curvature) ? new float[texels] : null;
        Thickness = maps.HasFlag(MeshMaps.Thickness) ? new float[texels] : null;
        Position = maps.HasFlag(MeshMaps.Position) ? new Vector3[texels] : null;
        WorldNormal = maps.HasFlag(MeshMaps.WorldNormal) ? new Vector3[texels] : null;

        if (maps.HasFlag(MeshMaps.Id)) {
            // ⚠ Filled with −1 rather than left at zero. Zero is a real face group — the one every
            // mesh with no groups at all carries — so a background texel left at the default would
            // read as material zero, and a generator masking on it would paint the whole gutter.
            Ids = new int[texels];
            Array.Fill(Ids, -1);
        }
    }

    /// <summary>Whether any measurement needs the hemisphere cast at all.</summary>
    /// <param name="maps">What was asked for.</param>
    /// <returns>Whether to spend the rays.</returns>
    public static bool NeedsRays(MeshMaps maps) =>
        maps.HasFlag(MeshMaps.AmbientOcclusion)
        || maps.HasFlag(MeshMaps.BentNormal)
        || maps.HasFlag(MeshMaps.Thickness);
}
