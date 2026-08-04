// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>
///     The CPU reference rasterizer: <c>ClusterSoftwareRaster.rvn</c>'s coverage and depth, on the host.
/// </summary>
/// <remarks>
///     <para>
///         <b>Improvement 4 of <c>docs/plan/22-virtualized-geometry.md</c>, applied to the raster.</b>
///         <see cref="GpuCulling.IsVisible" /> transliterates the object cull and
///         <see cref="GpuClusterCulling.Traverse" /> transliterates the traversal, for one reason each
///         time: these are the decisions that fail <em>silently</em>. A rasterizer that covers one pixel
///         too many along a shared edge draws a seam that blends; one that covers one too few draws a
///         crack; and both look like a driver, a mesh or a camera until somebody can compare the
///         arithmetic against its definition.
///     </para>
///     <para>
///         So the coverage rule here is the same one the shader runs, and the tests compare it against
///         the <em>definition</em> — a pixel centre is inside a triangle, two triangles sharing an edge
///         cover it exactly once — rather than against a second implementation.
///     </para>
///     <para>
///         <b>What it deliberately does not mirror is the schedule.</b> The shader deals triangles out
///         to sixty-four lanes and resolves them with an atomic; the answer does not depend on which
///         lane got which, so this walks them in order and resolves them with a comparison.
///         <see cref="GpuClusterSoftwareRaster.Key(float, uint)" /> is the packing both use.
///     </para>
/// </remarks>
public static class SoftwareRaster {
    /// <summary>One edge function of a triangle, evaluated at a point.</summary>
    /// <param name="a">The edge's start.</param>
    /// <param name="b">Its end.</param>
    /// <param name="point">Where to evaluate it.</param>
    /// <remarks>
    ///     Twice the signed area of <c>(a, b, point)</c>, positive when the point is to the left of
    ///     <c>a → b</c>. The three of them at a pixel centre are its unnormalised barycentrics, which is
    ///     why interpolating depth is these over their sum and nothing else.
    /// </remarks>
    public static float Edge(Vector2 a, Vector2 b, Vector2 point) =>
        ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));

    /// <summary>Whether an edge's zero line belongs to the triangle on its left — the top-left rule.</summary>
    /// <param name="a">The edge's start.</param>
    /// <param name="b">Its end.</param>
    /// <remarks>
    ///     <b>Two triangles sharing an edge must cover the pixels exactly on it exactly once</b>, or the
    ///     seam is drawn twice — which for a visibility buffer means an identity decided by whichever
    ///     atomic won — or not at all, which is a line of background through a solid surface. For a
    ///     counter-clockwise triangle, an edge going strictly left (a <em>top</em> edge) or downward (a
    ///     <em>left</em> one) owns them and the other two do not.
    /// </remarks>
    public static bool IsTopLeft(Vector2 a, Vector2 b) {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;

        return dy < 0f || (dy == 0f && dx < 0f);
    }

    /// <summary>Whether a point is on the inner side of one edge, ties included.</summary>
    /// <param name="edge">What <see cref="Edge" /> answered.</param>
    /// <param name="a">The edge's start.</param>
    /// <param name="b">Its end.</param>
    public static bool Inside(float edge, Vector2 a, Vector2 b) =>
        edge > 0f || (edge == 0f && IsTopLeft(a, b));

    /// <summary>Where a clip-space position lands on the screen, in pixels.</summary>
    /// <param name="clip">The position, after the view-projection.</param>
    /// <param name="screen">The screen, in pixels.</param>
    /// <remarks>
    ///     <c>Transform.NdcToUv</c> times the size, which is the engine's one convention about which way
    ///     up a clip space is — see <c>Transform.rvn</c>. <c>VisibilityResolve</c> inverts exactly this
    ///     to find the pixel centre it reconstructs barycentrics at, so the two have to be the same
    ///     mapping or a resolved attribute belongs to a neighbouring pixel.
    /// </remarks>
    public static Vector2 Project(Vector4 clip, Int2 screen) =>
        new(
            ((clip.X / clip.W * 0.5f) + 0.5f) * screen.X,
            ((-clip.Y / clip.W * 0.5f) + 0.5f) * screen.Y
        );

    /// <summary>
    ///     Rasterizes one triangle into a packed depth-and-identity buffer.
    /// </summary>
    /// <param name="c0">Its first corner, in clip space.</param>
    /// <param name="c1">Its second.</param>
    /// <param name="c2">Its third.</param>
    /// <param name="screen">The screen, in pixels.</param>
    /// <param name="identity">What <see cref="GpuClusterRaster.Pack" /> produced for it.</param>
    /// <param name="depths">
    ///     One <see cref="GpuClusterSoftwareRaster.Key(float, uint)" /> per pixel, row-major. Resolved by taking the
    ///     larger, which is the nearer under reverse-Z.
    /// </param>
    /// <returns>How many pixels it covered.</returns>
    /// <exception cref="ArgumentException"><paramref name="depths" /> is smaller than the screen.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Nothing is clipped, and nothing has to be.</b> The traversal only routes a cluster to
    ///         the software raster when its whole bound is in front of the near plane — see
    ///         <see cref="GpuClusterCulling.IsSoftware" /> — so a corner behind the eye is not a case,
    ///         it is a contract violation, and it is answered by drawing nothing rather than by
    ///         projecting a position that means nothing.
    ///     </para>
    ///     <para>
    ///         <b>Wound to face the same way rather than culled</b>, because the hardware raster's
    ///         pipeline is two-sided: the normal cone has already rejected the clusters that face away
    ///         as a whole, and a back-facing triangle inside a surviving one is drawn on both paths or on
    ///         neither.
    ///     </para>
    /// </remarks>
    public static int Rasterize(
        Vector4 c0,
        Vector4 c1,
        Vector4 c2,
        Int2 screen,
        uint identity,
        Span<ulong> depths
    ) {
        if (screen.X <= 0 || screen.Y <= 0) {
            return 0;
        }

        if (depths.Length < screen.X * screen.Y) {
            throw new ArgumentException(
                $"A screen of {screen.X}×{screen.Y} needs {screen.X * screen.Y} words and was given "
                + $"{depths.Length}.",
                nameof(depths)
            );
        }

        if (c0.W <= GpuCulling.ClipEpsilon || c1.W <= GpuCulling.ClipEpsilon || c2.W <= GpuCulling.ClipEpsilon) {
            return 0;
        }

        var p0 = Project(c0, screen);
        var p1 = Project(c1, screen);
        var p2 = Project(c2, screen);

        var z0 = c0.Z / c0.W;
        var z1 = c1.Z / c1.W;
        var z2 = c2.Z / c2.W;

        var area = Edge(p0, p1, p2);

        if (area == 0f) {
            return 0;
        }

        if (area < 0f) {
            (p1, p2) = (p2, p1);
            (z1, z2) = (z2, z1);
            area = -area;
        }

        var x0 = Math.Max((int)MathF.Floor(MathF.Min(MathF.Min(p0.X, p1.X), p2.X)), 0);
        var y0 = Math.Max((int)MathF.Floor(MathF.Min(MathF.Min(p0.Y, p1.Y), p2.Y)), 0);
        var x1 = Math.Min((int)MathF.Ceiling(MathF.Max(MathF.Max(p0.X, p1.X), p2.X)), screen.X - 1);
        var y1 = Math.Min((int)MathF.Ceiling(MathF.Max(MathF.Max(p0.Y, p1.Y), p2.Y)), screen.Y - 1);

        var covered = 0;

        for (var y = y0; y <= y1; y++) {
            for (var x = x0; x <= x1; x++) {
                var point = new Vector2(x + 0.5f, y + 0.5f);

                var e0 = Edge(p1, p2, point);
                var e1 = Edge(p2, p0, point);
                var e2 = Edge(p0, p1, point);

                if (!Inside(e0, p1, p2) || !Inside(e1, p2, p0) || !Inside(e2, p0, p1)) {
                    continue;
                }

                // z/w is affine in screen space, which is the one attribute a rasterizer may interpolate
                // without the perspective divide the resolve does for everything else.
                var depth = ((e0 * z0) + (e1 * z1) + (e2 * z2)) / area;
                var key = GpuClusterSoftwareRaster.Key(depth, identity);
                var slot = (y * screen.X) + x;

                covered++;

                if (key > depths[slot]) {
                    depths[slot] = key;
                }
            }
        }

        return covered;
    }
}
