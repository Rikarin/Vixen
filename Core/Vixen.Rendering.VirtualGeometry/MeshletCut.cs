// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Which clusters of a DAG to draw — the CPU's answer, against which the device's is checked.</summary>
/// <remarks>
///     <para>
///         Improvement 4 of <c>docs/virtualized-geometry.md</c>: the parts of this system that fail
///         silently get a CPU reference. A cut chosen with an error metric that is non-monotonic at
///         one edge of one mesh produces a crack at one distance, and finding that in a frame capture
///         is a bad afternoon. Finding it here is a unit test.
///     </para>
///     <para>
///         <b>A cut is chosen per cluster, with no traversal and no agreement between neighbours.</b>
///         <see cref="SelectByError" /> is a linear scan asking each cluster one question about
///         itself, which is exactly what the traversal shader of phase 3 will ask — it will simply
///         ask it hierarchically, so that a rejected subtree costs one test rather than one per
///         cluster. That the two produce the same set is the property worth testing, and it holds
///         because a cluster's own error and its parent's bracket a half-open interval of thresholds,
///         and the intervals along any path through the DAG tile the number line exactly once.
///     </para>
/// </remarks>
public static class MeshletCut {
    /// <summary>The clusters to draw at a given object-space error threshold.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="threshold">How far the drawn surface may deviate from the original, in object space.</param>
    /// <returns>The cluster indices, ascending.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    /// <remarks>
    ///     Half-open on purpose — <c>Error ≤ t &lt; ParentError</c>. Closed at both ends would draw
    ///     two levels at the threshold where they meet, and open at both would draw neither, which is
    ///     the hole the build's strict monotonicity exists to make impossible.
    /// </remarks>
    public static int[] SelectByError(MeshletMesh mesh, float threshold) {
        ArgumentNullException.ThrowIfNull(mesh);

        var cut = new List<int>();

        for (var index = 0; index < mesh.Meshlets.Length; index++) {
            var meshlet = mesh.Meshlets[index];

            if (meshlet.Error <= threshold && threshold < meshlet.ParentError) {
                cut.Add(index);
            }
        }

        return [.. cut];
    }

    /// <summary>The finest cut that fits inside a triangle budget.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="triangleBudget">How many triangles it may draw.</param>
    /// <returns>The cluster indices, ascending.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         What the fallback mesh is cut with. It starts at the roots and refines the group with
    ///         the largest error that still fits, which is a greedy answer rather than an optimal one:
    ///         the optimal cut under a budget is a knapsack, and the difference is not worth the
    ///         difference on a mesh that will be drawn by the virtualized path anyway.
    ///     </para>
    ///     <para>
    ///         <b>A group refines whole or not at all.</b> Taking some of a group's children and
    ///         leaving the rest is exactly the cut that cracks — the boundary between the two was
    ///         locked at one level and simplified at the other.
    ///     </para>
    ///     <para>
    ///         A budget below what the roots alone cost returns the roots. There is nothing coarser
    ///         to fall back to, and answering with a hole would be worse than answering with more
    ///         triangles than were asked for.
    ///     </para>
    /// </remarks>
    public static int[] SelectByBudget(MeshletMesh mesh, int triangleBudget) {
        ArgumentNullException.ThrowIfNull(mesh);

        var cut = new HashSet<int>(mesh.Roots);
        var triangles = 0;

        foreach (var root in mesh.Roots) {
            triangles += mesh.Meshlets[root].TriangleCount;
        }

        while (true) {
            var chosen = -1;
            var chosenError = float.NegativeInfinity;
            var chosenDelta = 0;

            for (var index = 0; index < mesh.Groups.Length; index++) {
                var group = mesh.Groups[index];

                if (group.Parents.Length == 0 || group.Error <= chosenError) {
                    continue;
                }

                if (!group.Parents.All(cut.Contains)) {
                    continue;
                }

                var delta = Count(mesh, group.Children) - Count(mesh, group.Parents);

                if (triangles + delta > triangleBudget) {
                    continue;
                }

                chosen = index;
                chosenError = group.Error;
                chosenDelta = delta;
            }

            if (chosen < 0) {
                break;
            }

            cut.ExceptWith(mesh.Groups[chosen].Parents);
            cut.UnionWith(mesh.Groups[chosen].Children);
            triangles += chosenDelta;
        }

        var ordered = cut.ToArray();
        Array.Sort(ordered);

        return ordered;
    }

    /// <summary>A cut's triangles, as an ordinary index buffer over the source mesh's vertices.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="cut">The cluster indices.</param>
    /// <returns>Three source-vertex indices per triangle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public static int[] Flatten(MeshletMesh mesh, ReadOnlySpan<int> cut) {
        ArgumentNullException.ThrowIfNull(mesh);

        var total = 0;

        foreach (var index in cut) {
            total += mesh.Meshlets[index].TriangleCount;
        }

        var indices = new int[total * 3];
        var cursor = 0;

        foreach (var index in cut) {
            var meshlet = mesh.Meshlets[index];
            mesh.GetTriangles(meshlet, indices.AsSpan(cursor));
            cursor += meshlet.TriangleCount * 3;
        }

        return indices;
    }

    /// <summary>How many pixels of screen an object-space deviation covers.</summary>
    /// <param name="error">The deviation, in object space.</param>
    /// <param name="distance">How far the cluster is from the eye, in the same units.</param>
    /// <param name="verticalFieldOfView">The camera's vertical field of view, in radians.</param>
    /// <param name="screenHeight">How many pixels tall the view is.</param>
    /// <returns>The deviation in pixels. Infinite at or behind the eye.</returns>
    /// <remarks>
    ///     <para>
    ///         The projection a traversal will do per cluster per view, written once here so that the
    ///         shader can be checked against it rather than against a second derivation. It is the
    ///         perspective divide and nothing else: a length perpendicular to the view direction
    ///         subtends <c>error / distance</c> radians, and the view is
    ///         <c>2 tan(fov / 2)</c> radians of screen across <paramref name="screenHeight" /> pixels.
    ///     </para>
    ///     <para>
    ///         Perpendicular is the worst case and is the case taken. A deviation along the view
    ///         direction covers no pixels at all, and a metric that assumed the average orientation
    ///         would be right on average and visibly wrong on the silhouette — which is the only
    ///         place anybody looks.
    ///     </para>
    /// </remarks>
    public static float PixelError(float error, float distance, float verticalFieldOfView, float screenHeight) {
        if (distance <= 0) {
            return float.PositiveInfinity;
        }

        return error * screenHeight / (2f * distance * MathF.Tan(verticalFieldOfView * 0.5f));
    }

    /// <summary>The object-space error that projects to a given number of pixels.</summary>
    /// <param name="pixels">How many pixels of deviation is acceptable.</param>
    /// <param name="distance">How far the cluster is from the eye.</param>
    /// <param name="verticalFieldOfView">The camera's vertical field of view, in radians.</param>
    /// <param name="screenHeight">How many pixels tall the view is.</param>
    /// <returns>The threshold to hand <see cref="SelectByError" />.</returns>
    /// <remarks>
    ///     <see cref="PixelError" /> turned round. Both exist because a cut is chosen against a
    ///     threshold and reported against a pixel count, and deriving one from the other at each call
    ///     site is how the two stop agreeing.
    /// </remarks>
    public static float ErrorForPixels(float pixels, float distance, float verticalFieldOfView, float screenHeight) {
        if (screenHeight <= 0) {
            return float.PositiveInfinity;
        }

        return pixels * 2f * distance * MathF.Tan(verticalFieldOfView * 0.5f) / screenHeight;
    }

    /// <summary>How many triangles a set of clusters holds.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="clusters">The cluster indices.</param>
    /// <returns>The total.</returns>
    static int Count(MeshletMesh mesh, int[] clusters) {
        var total = 0;

        foreach (var index in clusters) {
            total += mesh.Meshlets[index].TriangleCount;
        }

        return total;
    }
}
