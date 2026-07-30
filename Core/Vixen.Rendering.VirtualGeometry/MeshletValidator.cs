// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Checks that a finished DAG cannot crack, and says exactly where if it can.</summary>
/// <remarks>
///     <para>
///         Improvement 5 of <c>docs/plan/22-virtualized-geometry.md</c>: DAG validity as a build error. Nanite's
///         crack-freedom is a property its builder is careful to maintain; making it a checked
///         invariant costs one pass over a structure that is already in memory, and converts the
///         engine's most notorious artefact class into a build that fails with a group index in the
///         message.
///     </para>
///     <para>
///         <b>Two invariants matter and the rest is book-keeping.</b> Errors must increase strictly
///         along every DAG edge, or a threshold exists at which a cut draws neither a cluster nor its
///         parent. And a group's outer boundary must be <em>bit-identical</em> between its children
///         and the clusters that replaced them, or a cut that takes one on each side of it meets two
///         surfaces that do not join. Everything else here — ranges, levels, membership — exists so
///         that a failure of those two is reported as itself rather than as an exception somewhere
///         downstream.
///     </para>
///     <para>
///         The checks recompute rather than re-read: the boundary comparison walks the clusters'
///         triangles out of the packed arrays and rebuilds the edge sets from the positions. A check
///         that asked the builder what it had done would pass whenever the builder was
///         self-consistently wrong, which is the only interesting case.
///     </para>
/// </remarks>
public static class MeshletValidator {
    /// <summary>Everything wrong with a DAG.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="source">The mesh it was built from.</param>
    /// <returns>One message per problem, empty if there are none.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyList<string> Validate(MeshletMesh mesh, MeshletBuildInput source) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();

        if (mesh.Meshlets.Length == 0) {
            if (source.TriangleCount != 0) {
                problems.Add($"The mesh has {source.TriangleCount} triangles and the DAG has no clusters.");
            }

            return problems;
        }

        CheckRanges(mesh, source, problems);

        if (problems.Count > 0) {
            // Everything below indexes through the arrays this just checked. Reporting a broken
            // offset and then crashing on it would be worse than reporting it and stopping.
            return problems;
        }

        CheckMembership(mesh, problems);
        CheckErrors(mesh, problems);
        CheckCoverage(mesh, source, problems);
        CheckBoundaries(mesh, source, problems);

        return problems;
    }

    /// <summary>Checks that every offset, count and index addresses something that exists.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="source">The mesh it was built from.</param>
    /// <param name="problems">Where to report.</param>
    static void CheckRanges(MeshletMesh mesh, MeshletBuildInput source, List<string> problems) {
        for (var index = 0; index < mesh.Meshlets.Length; index++) {
            var meshlet = mesh.Meshlets[index];

            if (meshlet.VertexCount is < 1 or > 256) {
                problems.Add($"Cluster {index} references {meshlet.VertexCount} vertices, which a byte cannot index.");

                continue;
            }

            if (meshlet.VertexOffset < 0 || meshlet.VertexOffset + meshlet.VertexCount > mesh.Vertices.Length) {
                problems.Add($"Cluster {index}'s vertices run past the end of the shared vertex list.");

                continue;
            }

            if (meshlet.TriangleOffset < 0
                || (meshlet.TriangleOffset + meshlet.TriangleCount) * 3 > mesh.Triangles.Length) {
                problems.Add($"Cluster {index}'s triangles run past the end of the shared triangle list.");

                continue;
            }

            for (var entry = 0; entry < meshlet.VertexCount; entry++) {
                var vertex = mesh.Vertices[meshlet.VertexOffset + entry];

                if (vertex < 0 || vertex >= source.VertexCount) {
                    problems.Add($"Cluster {index} references vertex {vertex}, which the mesh does not have.");
                }
            }

            for (var corner = 0; corner < meshlet.TriangleCount * 3; corner++) {
                if (mesh.Triangles[(meshlet.TriangleOffset * 3) + corner] >= meshlet.VertexCount) {
                    problems.Add($"Cluster {index} has a corner outside its own vertex list.");

                    break;
                }
            }
        }
    }

    /// <summary>Checks that the DAG's edges agree with the clusters that sit on them.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="problems">Where to report.</param>
    static void CheckMembership(MeshletMesh mesh, List<string> problems) {
        for (var index = 0; index < mesh.Meshlets.Length; index++) {
            var meshlet = mesh.Meshlets[index];

            if (meshlet.Group >= mesh.Groups.Length || meshlet.Source >= mesh.Groups.Length) {
                problems.Add($"Cluster {index} names a group that does not exist.");

                continue;
            }

            if (meshlet.Group >= 0 && !mesh.Groups[meshlet.Group].Children.Contains(index)) {
                problems.Add($"Cluster {index} says it is in group {meshlet.Group}, which does not list it.");
            }

            if (meshlet.Source >= 0 && !mesh.Groups[meshlet.Source].Parents.Contains(index)) {
                problems.Add($"Cluster {index} says group {meshlet.Source} produced it, which does not list it.");
            }

            if (meshlet.Group < 0 && !float.IsPositiveInfinity(meshlet.ParentError)) {
                problems.Add($"Cluster {index} is a root and its parent error is {meshlet.ParentError}, not infinite.");
            }

            if (meshlet.Level == 0 != (meshlet.Source < 0)) {
                problems.Add($"Cluster {index} is at level {meshlet.Level} and names source group {meshlet.Source}.");
            }

            if (meshlet.Level == 0 && meshlet.Error != 0) {
                problems.Add($"Cluster {index} is the original mesh and claims an error of {meshlet.Error}.");
            }
        }

        var roots = new HashSet<int>(mesh.Roots);

        for (var index = 0; index < mesh.Meshlets.Length; index++) {
            if (mesh.Meshlets[index].Group < 0 != roots.Contains(index)) {
                problems.Add($"Cluster {index} disagrees with the root list about whether it is a root.");
            }
        }

        for (var index = 0; index < mesh.Groups.Length; index++) {
            var group = mesh.Groups[index];

            foreach (var child in group.Children) {
                foreach (var parent in group.Parents) {
                    if (mesh.Meshlets[parent].Level <= mesh.Meshlets[child].Level) {
                        problems.Add(
                            $"Group {index} produced cluster {parent} at level {mesh.Meshlets[parent].Level} "
                            + $"from cluster {child} at level {mesh.Meshlets[child].Level}."
                        );

                        return;
                    }
                }
            }
        }
    }

    /// <summary>Checks that error increases strictly along every edge of the DAG.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="problems">Where to report.</param>
    /// <remarks>
    ///     Strictly, not merely monotonically. Equal errors either side of a group leave the interval
    ///     <c>[child, parent)</c> empty, and a cluster whose interval is empty is never drawn at any
    ///     threshold — which is a hole rather than a crack, and appears at one distance on one mesh.
    /// </remarks>
    static void CheckErrors(MeshletMesh mesh, List<string> problems) {
        for (var index = 0; index < mesh.Groups.Length; index++) {
            var group = mesh.Groups[index];

            foreach (var child in group.Children) {
                if (mesh.Meshlets[child].Error >= group.Error) {
                    problems.Add(
                        $"Group {index} has an error of {group.Error} and its child cluster {child} "
                        + $"has {mesh.Meshlets[child].Error}, which is not below it."
                    );
                }

                if (mesh.Meshlets[child].ParentError != group.Error) {
                    problems.Add(
                        $"Cluster {child} carries a parent error of {mesh.Meshlets[child].ParentError} "
                        + $"and its group's is {group.Error}."
                    );
                }
            }

            foreach (var parent in group.Parents) {
                if (mesh.Meshlets[parent].Error != group.Error) {
                    problems.Add(
                        $"Group {index} produced cluster {parent} with an error of "
                        + $"{mesh.Meshlets[parent].Error} rather than the group's {group.Error}."
                    );
                }
            }
        }
    }

    /// <summary>Checks that level zero is the original mesh, triangle for triangle.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="source">The mesh it was built from.</param>
    /// <param name="problems">Where to report.</param>
    /// <remarks>
    ///     A partition, not a subset: every source triangle in exactly one cluster. A dropped triangle
    ///     is a hole in the finest level of detail, which is the one seen from close up, and a
    ///     duplicated one is z-fighting on a surface that has none.
    /// </remarks>
    static void CheckCoverage(MeshletMesh mesh, MeshletBuildInput source, List<string> problems) {
        var counts = new Dictionary<(int, int, int), int>();

        for (var triangle = 0; triangle < source.TriangleCount; triangle++) {
            var key = (source.Indices[triangle * 3], source.Indices[(triangle * 3) + 1], source.Indices[(triangle * 3) + 2]);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var corners = new int[3];
        var extra = 0;

        foreach (var meshlet in mesh.Meshlets) {
            if (meshlet.Level != 0) {
                continue;
            }

            for (var triangle = 0; triangle < meshlet.TriangleCount; triangle++) {
                for (var corner = 0; corner < 3; corner++) {
                    corners[corner] = mesh.Vertices[
                        meshlet.VertexOffset + mesh.Triangles[((meshlet.TriangleOffset + triangle) * 3) + corner]
                    ];
                }

                var key = (corners[0], corners[1], corners[2]);

                if (counts.TryGetValue(key, out var remaining) && remaining > 0) {
                    counts[key] = remaining - 1;
                } else {
                    extra++;
                }
            }
        }

        var missing = counts.Values.Sum();

        if (missing > 0) {
            problems.Add($"Level zero is missing {missing} of the mesh's {source.TriangleCount} triangles.");
        }

        if (extra > 0) {
            problems.Add($"Level zero holds {extra} triangles the mesh does not have.");
        }
    }

    /// <summary>Checks that no group's outer boundary moved when it was simplified.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="source">The mesh it was built from.</param>
    /// <param name="problems">Where to report.</param>
    /// <remarks>
    ///     The check the whole scheme stands on, and the one that fails when the group boundary is
    ///     not locked. The two sides of a group boundary are drawn at different levels of detail
    ///     whenever a cut passes through it, so an edge that exists on one side and not the other is a
    ///     visible slit — and it is invisible in the build, in the asset and in every test that draws
    ///     one level at a time.
    /// </remarks>
    static void CheckBoundaries(MeshletMesh mesh, MeshletBuildInput source, List<string> problems) {
        var welded = Topology.Weld(source.Positions);

        for (var index = 0; index < mesh.Groups.Length; index++) {
            var group = mesh.Groups[index];

            if (group.Parents.Length == 0) {
                continue;
            }

            var before = Boundary(mesh, welded, group.Children);
            var after = Boundary(mesh, welded, group.Parents);

            if (before.SetEquals(after)) {
                continue;
            }

            var lost = before.Except(after).Count();
            var gained = after.Except(before).Count();

            problems.Add(
                $"Group {index}'s boundary moved: {lost} edges lost and {gained} gained by the simplification."
            );
        }
    }

    /// <summary>The outer boundary of a set of clusters.</summary>
    /// <param name="mesh">The DAG.</param>
    /// <param name="welded">What <see cref="Topology.Weld" /> produced.</param>
    /// <param name="clusters">The clusters.</param>
    /// <returns>The keys of the edges used by exactly one of their triangles.</returns>
    static HashSet<long> Boundary(MeshletMesh mesh, int[] welded, int[] clusters) {
        var corners = MeshletCut.Flatten(mesh, clusters);
        var all = new int[corners.Length / 3];

        for (var triangle = 0; triangle < all.Length; triangle++) {
            all[triangle] = triangle;
        }

        return Topology.BoundaryEdges(corners, welded, all);
    }
}
