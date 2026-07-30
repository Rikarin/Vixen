// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     A group with several parents refines once, not once per parent.
/// </summary>
/// <remarks>
///     <para>
///         <b>The case every other traversal fixture deliberately left out.</b>
///         <c>GpuClusterCullingTests.Tree</c> builds one parent per group — its own comment says so —
///         and a real DAG does not: a group's simplification produces several parents, every one of
///         which carries the whole child set and, by the shared <c>ErrorCenter</c>, decides to refine
///         at the same moment. Before <see cref="CullCluster.GroupLead" />, each of them pushed that
///         same child set, and the duplication compounded per level: this fixture's 87-cluster sphere
///         produced a visible list of 1 016 entries, 34 of them distinct.
///     </para>
///     <para>
///         <b>No property test could see it.</b> "Every path from a root to a leaf holds exactly one
///         visible cluster" is true of a list with duplicates — each path still crosses the cut once —
///         and the golden image compares coverage, which a cluster drawn thirty times covers exactly
///         as well as one drawn once. What gave it away was <c>Samples/12-VirtualGeometry</c> logging
///         more accepted clusters than its mesh has. The failure it risks is real: a visible list
///         thirty times the cut overflows <see cref="GpuClusterVisibility.VisiblePerInstance" /> on a
///         mesh that should fit with room to spare, and an overflowed list is a hole.
///     </para>
/// </remarks>
public sealed class ClusterTraversalGroupTests {
    /// <summary>Each visible cluster appears once, over a DAG whose groups have several parents.</summary>
    [Fact]
    public void A_multi_parent_group_is_refined_once() {
        var scene = Scene(out var instance);

        // The fixture only means something while the builder produces shared parenthood — if a
        // future builder ever emits one parent per group, this test must fail loudly rather than
        // keep passing about nothing.
        var parents = new int[scene.Clusters.Length];

        foreach (var record in scene.Clusters) {
            for (var i = 0u; i < record.ChildCount; i++) {
                parents[scene.Children[(int)(record.FirstChild + i)]]++;
            }
        }

        Assert.True(
            parents.Any(count => count > 1),
            "The DAG has no cluster with more than one parent, so this fixture no longer exercises "
            + "what it exists for."
        );

        var result = GpuClusterCulling.Traverse(scene, instance, View(), _ => true);

        Assert.NotEmpty(result.Visible);

        Assert.True(
            result.Visible.Length == result.Visible.Distinct().Count(),
            $"The traversal accepted {result.Visible.Length} clusters, "
            + $"{result.Visible.Length - result.Visible.Distinct().Count()} of them duplicates."
        );

        // And therefore no more clusters than exist — the sample's symptom, as an assertion.
        Assert.True(result.Visible.Length <= scene.Clusters.Length);
    }

    /// <summary>What the walk accepts is drawn from the cut the threshold defines.</summary>
    /// <remarks>
    ///     A subset and not equality, because the walk also rejects by the normal cone and the view
    ///     is outside a closed sphere — half of it faces away. What this holds is that de-duplication
    ///     did not change <em>which</em> clusters are accepted, only how many times.
    /// </remarks>
    [Fact]
    public void The_accepted_clusters_are_a_subset_of_the_cut() {
        var scene = Scene(out var instance);
        var view = View();

        var cut = GpuClusterCulling.Cut(scene, instance, view).ToHashSet();
        var visible = GpuClusterCulling.Traverse(scene, instance, view, _ => true).Visible;

        Assert.NotEmpty(visible);
        Assert.All(visible, cluster => Assert.Contains(cluster, cut));
    }

    /// <summary>A real DAG from a real build, flattened the way a registration flattens one.</summary>
    static ClusterScene Scene(out CullInstance instance) {
        var (positions, indices) = Icosphere(subdivisions: 4);

        var input = new MeshletBuildInput { Positions = positions, Indices = indices };
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, positions, [], new());

        var scene = GpuClusterCulling.Flatten(mesh, pages);

        instance = new() {
            FirstCluster = 0,
            ClusterCount = (uint)scene.Clusters.Length,
            FirstRoot = 0,
            RootCount = (uint)scene.Roots.Length,
            Position = new(0f, 0f, -4f),
            Scale = 1f,
            StagesLow = 1u,
            Flags = GpuCulling.Alive,
            FirstBone = GpuCulling.NoBones
        };

        return scene;
    }

    /// <summary>A camera close enough that the walk genuinely refines through the levels.</summary>
    static CullView View() {
        const float FieldOfView = MathF.PI / 3f;

        var render = new RenderView("camera") {
            Position = Vector3.Zero,
            ViewProjection = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f))
                * Matrix4x4.PerspectiveFieldOfView(FieldOfView, 1f, 0.1f, 1000f),
            ScreenHeightScale = 1f / MathF.Tan(FieldOfView * 0.5f),
            Stages = new(1u)
        };

        var view = GpuCulling.Pack(render, 0, 0, default, 0);

        view.ErrorScale = GpuClusterCulling.ErrorScaleFor(render.ScreenHeightScale, 720);
        view.ErrorThreshold = 1f;

        return view;
    }

    /// <summary>A subdivided icosahedron: uniform triangles, and a DAG with shared parenthood.</summary>
    static (Vector3[] Positions, int[] Indices) Icosphere(int subdivisions) {
        var t = (1f + MathF.Sqrt(5f)) * 0.5f;

        var positions = new List<Vector3> {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1)
        };

        for (var i = 0; i < positions.Count; i++) {
            positions[i] = Vector3.Normalize(positions[i]);
        }

        List<(int A, int B, int C)> faces = [
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
            (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
            (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)
        ];

        var midpoints = new Dictionary<(int, int), int>();

        for (var level = 0; level < subdivisions; level++) {
            List<(int, int, int)> split = [];

            foreach (var (a, b, c) in faces) {
                var ab = Midpoint(a, b);
                var bc = Midpoint(b, c);
                var ca = Midpoint(c, a);

                split.Add((a, ab, ca));
                split.Add((b, bc, ab));
                split.Add((c, ca, bc));
                split.Add((ab, bc, ca));
            }

            faces = split;
        }

        var indices = new int[faces.Count * 3];

        for (var i = 0; i < faces.Count; i++) {
            var (a, b, c) = faces[i];

            // Wound outward, checked rather than trusted: the traversal's cone culls a mesh whose
            // back is turned, and both device fixtures made that mistake once each.
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);

            if (Vector3.Dot(normal, positions[a] + positions[b] + positions[c]) < 0f) {
                (b, c) = (c, b);
            }

            indices[(i * 3) + 0] = a;
            indices[(i * 3) + 1] = b;
            indices[(i * 3) + 2] = c;
        }

        return ([.. positions], indices);

        int Midpoint(int a, int b) {
            var key = a < b ? (a, b) : (b, a);

            if (midpoints.TryGetValue(key, out var existing)) {
                return existing;
            }

            positions.Add(Vector3.Normalize((positions[a] + positions[b]) * 0.5f));
            midpoints[key] = positions.Count - 1;

            return positions.Count - 1;
        }
    }
}
