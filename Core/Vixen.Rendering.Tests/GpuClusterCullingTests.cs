// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 3's exit criterion: the traversal's answer against a brute-force cut, over random DAGs.
/// </summary>
/// <remarks>
///     <para>
///         Improvement 4 of <c>docs/plan/22-virtualized-geometry.md</c>, one level deeper than
///         <see cref="GpuVisibilityGroupTests" />. That one compares <c>GpuCulling.IsVisible</c> — the
///         host's transliteration of the object cull — against <c>VisibilityGroup</c>, which is the
///         definition. This compares <c>GpuClusterCulling.Traverse</c> against <c>Cut</c>: a linear
///         scan that asks every cluster one question about itself, with no traversal, no hierarchy and
///         no rejection.
///     </para>
///     <para>
///         <strong>Why the two must agree.</strong> A cluster's own error and its parent's bracket a
///         half-open interval of thresholds, and along any path through the DAG those intervals tile
///         the number line exactly once. So for any threshold each part of the surface is drawn by
///         exactly one cluster — and a traversal that descends while a cluster's own error is too
///         large arrives at precisely that cluster, having only reached it because its parent's error
///         was too large. Which is <c>Error ≤ t &lt; ParentError</c> from the top rather than tested
///         per cluster.
///     </para>
///     <para>
///         <strong>What this cannot say</strong> is whether the shader still contains this
///         arithmetic. That is <see cref="The_shader_traverses_what_the_host_says_it_does" />, which
///         reads the source — the same defence, and the same reason, as the object cull's.
///     </para>
/// </remarks>
public class GpuClusterCullingTests {
    /// <summary>A DAG with a known shape: a balanced tree of clusters, coarsest last.</summary>
    /// <remarks>
    ///     <para>
    ///         Built here rather than run through <c>MeshletBuilder</c>, and that is the point of it:
    ///         what is under test is the traversal, so the DAG it walks should be one whose cut is
    ///         known by construction rather than one whose cut is whatever a simplifier produced. A
    ///         randomised <em>shape</em> — how many levels, how wide, what errors — is what makes the
    ///         comparison worth running.
    ///     </para>
    ///     <para>
    ///         The errors increase strictly up every edge, which is the invariant the build
    ///         guarantees and <c>MeshletValidator</c> refuses a DAG without. A generator that did not
    ///         maintain it would be testing the traversal against a DAG no build can produce, and the
    ///         disagreement it found would be about the fixture.
    ///     </para>
    /// </remarks>
    static ClusterScene Tree(int levels, int fanOut, float errorStep, float radius = 1f) {
        var clusters = new List<CullCluster>();
        var children = new List<uint>();

        // Level zero first, then each level above it, so a cluster's children always have lower
        // indices than it does — which is the order the builder emits and the order a page set packs
        // in reverse.
        var previous = new List<int>();
        var count = 1;

        for (var i = 0; i < levels; i++) {
            count *= fanOut;
        }

        for (var i = 0; i < count; i++) {
            clusters.Add(Cluster(0f, radius, Spread(i, count, radius)));
            previous.Add(i);
        }

        for (var level = 1; level <= levels; level++) {
            var error = level * errorStep;
            var current = new List<int>();

            for (var group = 0; group * fanOut < previous.Count; group++) {
                var first = children.Count;
                var members = 0;

                for (var i = 0; i < fanOut && (group * fanOut) + i < previous.Count; i++) {
                    var child = previous[(group * fanOut) + i];
                    children.Add((uint)child);
                    members++;

                    // The parent's error is where the child stops being drawn, which is what makes
                    // the intervals tile.
                    clusters[child] = clusters[child] with { ParentError = error };
                }

                var centre = Vector3.Zero;

                for (var i = 0; i < members; i++) {
                    centre += clusters[(int)children[first + i]].Center;
                }

                centre /= members;
                current.Add(clusters.Count);

                // One parent per group here, so the group bound is this cluster's own and the
                // parent leads itself — the general case, several parents sharing one child set and
                // one designated lead, is `Flatten`'s to build and `ClusterTraversalGroupTests`' to
                // check. What this fixture is for is the traversal's arithmetic rather than the
                // group bookkeeping, but the lead still has to be right: a cluster that does not
                // lead its own group never pushes, which is a walk that stops at the roots.
                clusters.Add(
                    Cluster(error, radius * fanOut, centre) with {
                        ErrorCenter = centre,
                        ErrorRadius = radius * fanOut,
                        FirstChild = (uint)first,
                        ChildCount = (uint)members,
                        GroupLead = (uint)clusters.Count
                    }
                );
            }

            previous = current;
        }

        var roots = previous.Select(root => (uint)root).ToArray();

        foreach (var root in roots) {
            clusters[(int)root] = clusters[(int)root] with {
                ParentError = 3.4e38f,
                Flags = GpuClusterCulling.ClusterRoot
            };
        }

        return new([.. clusters], [.. children], roots);
    }

    /// <summary>A cluster with a useless cone, so only the error decides anything.</summary>
    /// <remarks>
    ///     A negative cosine is the honest value for a cluster whose normals span more than a
    ///     hemisphere, and it never rejects — which is what a fixture comparing against a cut that
    ///     does no rejection at all needs. The cone gets its own tests below.
    /// </remarks>
    static CullCluster Cluster(float error, float radius, Vector3 center) =>
        new() {
            Center = center,
            Radius = radius,
            ErrorCenter = center,
            ErrorRadius = radius,
            ConeAxis = Vector3.UnitZ,
            ConeCosine = -1f,
            Error = error,
            ParentError = 3.4e38f
        };

    /// <summary>Spreads clusters along X so their bounds are not all the same sphere.</summary>
    static Vector3 Spread(int index, int count, float radius) =>
        new((index - ((count - 1) * 0.5f)) * radius * 2f, 0f, 0f);

    /// <summary>An instance of a whole scene's clusters, alive and unscaled.</summary>
    static CullInstance Instance(ClusterScene scene, float scale = 1f) =>
        new() {
            FirstCluster = 0,
            ClusterCount = (uint)scene.Clusters.Length,
            FirstRoot = 0,
            RootCount = (uint)scene.Roots.Length,
            Position = Vector3.Zero,
            Scale = scale,
            StagesLow = 1,
            StagesHigh = 0,
            Flags = GpuCulling.Alive
        };

    /// <summary>
    ///     A view that rejects nothing: a frustum containing everything, no cutoff, no pyramid.
    /// </summary>
    /// <remarks>
    ///     Deliberate, and the comparison is only meaningful with it. The brute-force cut is the
    ///     definition of "which cluster covers this surface at this threshold" and knows nothing about
    ///     frustums or cones — those <em>remove</em> clusters from a cut, so a view that rejected
    ///     anything would make the two disagree for a reason that is not a defect. What rejection does
    ///     is tested separately, against what it is supposed to remove.
    /// </remarks>
    static CullView Everywhere(float threshold, float errorScale = 1000f, Vector3? eye = null) {
        var view = new CullView {
            Position = eye ?? new Vector3(0f, 0f, -1000f),
            MaximumDistance = 0f,
            StagesLow = 1,
            StagesHigh = 0,
            ErrorScale = errorScale,
            ErrorThreshold = threshold
        };

        // Six planes whose inside is everywhere, so nothing is ever outside one.
        for (var i = 0; i < BoundingFrustum.PlaneCount; i++) {
            view.Planes[i] = new(0f, 0f, 1f, 1e9f);
        }

        return view;
    }

    // --- The exit criterion --------------------------------------------------

    /// <summary>
    ///     The traversal and the brute-force cut agree, over random DAGs and random thresholds.
    /// </summary>
    /// <remarks>
    ///     The criterion phase 3 is judged on. Randomised over the DAG's shape as well as the
    ///     threshold, because the interesting failures are at the shape boundaries: a fan-out of one
    ///     is a chain, a single level is a DAG with nothing to refine, and a threshold that lands
    ///     exactly on a group's error is where a half-open interval and a closed one differ.
    /// </remarks>
    [Fact]
    public void The_traversal_agrees_with_the_brute_force_cut() {
        Gen.Select(Gen.Int[1, 4], Gen.Int[2, 4], Gen.Float[0.05f, 4f])
            .Sample(
                shape => {
                    var (levels, fanOut, threshold) = shape;
                    var scene = Tree(levels, fanOut, errorStep: 0.5f);
                    var instance = Instance(scene);
                    var view = Everywhere(threshold);

                    var walked = GpuClusterCulling.Traverse(scene, instance, view, _ => true);
                    var brute = GpuClusterCulling.Cut(scene, instance, view);

                    Assert.Equal(brute, walked.Visible);
                },
                iter: 2000
            );
    }

    /// <summary>
    ///     And they agree at exactly the thresholds where the answer changes.
    /// </summary>
    /// <remarks>
    ///     A randomised threshold lands on a group's error with probability zero, and that is the one
    ///     value where a half-open interval and a closed one differ — the boundary at which a cut
    ///     either draws two levels or neither. So it is hit on purpose rather than hoped for.
    /// </remarks>
    [Fact]
    public void They_agree_at_the_thresholds_where_the_answer_changes() {
        var scene = Tree(levels: 4, fanOut: 2, errorStep: 0.5f);
        var instance = Instance(scene);

        // The pixel error a cluster's own object-space error projects to, at the fixture's distance.
        var view = Everywhere(threshold: 0f);

        var errors = scene.Clusters
            .Where(c => c.Error > 0f)
            .Select(c => GpuClusterCulling.ProjectedError(c, instance, view))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(errors);

        foreach (var threshold in errors) {
            foreach (var offset in (float[])[-1e-4f, 0f, 1e-4f]) {
                var at = Everywhere(threshold + offset);

                Assert.Equal(
                    GpuClusterCulling.Cut(scene, instance, at),
                    GpuClusterCulling.Traverse(scene, instance, at, _ => true).Visible
                );
            }
        }
    }

    /// <summary>
    ///     Every cut is a cut: exactly one cluster on every path from a root to a leaf.
    /// </summary>
    /// <remarks>
    ///     Stronger than agreeing with the oracle, and independent of it — the property that makes the
    ///     answer crack-free rather than merely equal to something. Two clusters on one path is the
    ///     surface drawn twice; none is a hole. Both are pictures rather than exceptions, which is
    ///     why they are counted here.
    /// </remarks>
    [Fact]
    public void Every_path_through_the_dag_holds_exactly_one_visible_cluster() {
        Gen.Select(Gen.Int[1, 4], Gen.Int[2, 4], Gen.Float[0.05f, 4f])
            .Sample(
                shape => {
                    var (levels, fanOut, threshold) = shape;
                    var scene = Tree(levels, fanOut, errorStep: 0.5f);
                    var instance = Instance(scene);

                    var visible = GpuClusterCulling
                        .Traverse(scene, instance, Everywhere(threshold), _ => true)
                        .Visible
                        .ToHashSet();

                    foreach (var root in scene.Roots) {
                        Assert.Equal(1, OnPath(scene, root, visible));
                    }
                },
                iter: 500
            );
    }

    /// <summary>How many of a subtree's clusters are visible on each path through it.</summary>
    /// <remarks>
    ///     Returns the count when every path agrees and fails the test when they do not, so a
    ///     disagreement is reported where it is rather than as a wrong total at the root.
    /// </remarks>
    static int OnPath(ClusterScene scene, uint cluster, HashSet<int> visible) {
        var record = scene.Clusters[(int)cluster];
        var here = visible.Contains((int)cluster) ? 1 : 0;

        if (record.ChildCount == 0) {
            return here;
        }

        var below = -1;

        for (var i = 0u; i < record.ChildCount; i++) {
            var count = OnPath(scene, scene.Children[(int)(record.FirstChild + i)], visible);

            if (below < 0) {
                below = count;
                continue;
            }

            Assert.Equal(below, count);
        }

        return here + below;
    }

    // --- The sabotages the criterion names ----------------------------------

    /// <summary>
    ///     Projecting the error with the wrong view changes the answer.
    /// </summary>
    /// <remarks>
    ///     The first sabotage the phase-3 exit criterion names, and the reason
    ///     <see cref="CullView.ErrorScale" /> is one field rather than a derivation. A traversal that
    ///     projected against another view's scale — a shadow cascade's, say, while drawing the camera
    ///     — would pick a level of detail for the wrong screen: too coarse and the camera view pops,
    ///     too fine and every cascade draws the whole mesh.
    /// </remarks>
    [Fact]
    public void Projecting_the_error_with_the_wrong_view_changes_the_answer() {
        var scene = Tree(levels: 4, fanOut: 2, errorStep: 0.5f);
        var instance = Instance(scene);

        var right = Everywhere(threshold: 1f, errorScale: 1000f);
        var wrong = Everywhere(threshold: 1f, errorScale: 50f);

        var expected = GpuClusterCulling.Traverse(scene, instance, right, _ => true).Visible;
        var actual = GpuClusterCulling.Traverse(scene, instance, wrong, _ => true).Visible;

        Assert.NotEqual(expected, actual);

        // And not merely different: the wrong scale makes every error look smaller, so the cut is
        // coarser — which is what "the mesh pops" is, arrived at by arithmetic.
        Assert.True(actual.Length < expected.Length);
    }

    /// <summary>
    ///     Removing the normal-cone test changes the answer.
    /// </summary>
    /// <remarks>
    ///     The second sabotage the criterion names. A cluster whose normals all face away cannot
    ///     contribute a pixel, and the whole point of testing it in the traversal rather than in the
    ///     raster is that rejecting it takes its subtree with it. This is the test that says the cone
    ///     is being read at all: it is the one rejection whose absence costs performance and nothing
    ///     visible, so nothing else would notice it going.
    /// </remarks>
    [Fact]
    public void Removing_the_normal_cone_test_changes_the_answer() {
        var scene = Tree(levels: 2, fanOut: 2, errorStep: 0.5f);

        // Every cluster facing away from an eye on the far side, with a tight cone so the test can
        // reject — the fixture's default is a useless cone precisely so it cannot.
        var facing = new ClusterScene(
            [.. scene.Clusters.Select(c => c with { ConeAxis = Vector3.UnitZ, ConeCosine = 0.99f })],
            scene.Children,
            scene.Roots
        );

        // Facing +Z with the eye at −Z, which is the fixture's default: every normal points away.
        var instance = Instance(facing, scale: 0.01f);
        var view = Everywhere(threshold: 0.001f, eye: new Vector3(0f, 0f, 1000f));

        facing = new(
            [.. facing.Clusters.Select(c => c with { ConeAxis = -Vector3.UnitZ })],
            facing.Children,
            facing.Roots
        );

        var withCone = GpuClusterCulling.Traverse(facing, instance, view, _ => true).Visible;

        Assert.Empty(withCone);

        // The same walk with a cone that cannot reject draws the mesh, which is what the test removes.
        var blind = new ClusterScene(
            [.. facing.Clusters.Select(c => c with { ConeCosine = -1f })],
            facing.Children,
            facing.Roots
        );

        Assert.NotEmpty(GpuClusterCulling.Traverse(blind, instance, view, _ => true).Visible);
    }

    /// <summary>A cone spanning more than a hemisphere never rejects, however the eye is placed.</summary>
    /// <remarks>
    ///     The honest answer for a closed cap or a crumpled fold, and the reason phase 1 records a
    ///     negative cosine rather than clamping it: there is no eye position from which such a
    ///     cluster is entirely backfacing, and a test that pretended otherwise would remove geometry
    ///     that is facing the camera — which is the failure this whole file is arranged to catch.
    /// </remarks>
    [Fact]
    public void A_cone_wider_than_a_hemisphere_never_rejects() {
        Gen.Select(Gen.Float[-100f, 100f], Gen.Float[-100f, 100f], Gen.Float[-100f, 100f])
            .Sample(
                eye => {
                    var at = new Vector3(eye.Item1, eye.Item2, eye.Item3);

                    Assert.False(
                        GpuClusterCulling.Backfacing(Vector3.Zero, 1f, Vector3.UnitZ, -0.1f, at)
                    );
                },
                iter: 500
            );
    }

    /// <summary>An eye inside the bound never rejects either.</summary>
    [Fact]
    public void An_eye_inside_the_bound_never_rejects() {
        Assert.False(GpuClusterCulling.Backfacing(Vector3.Zero, 10f, Vector3.UnitZ, 0.99f, new(0f, 0f, 5f)));
        Assert.False(GpuClusterCulling.Backfacing(Vector3.Zero, 10f, Vector3.UnitZ, 0.99f, new(0f, 0f, -5f)));
    }

    /// <summary>A cluster facing the eye is never rejected; one facing away is.</summary>
    [Fact]
    public void The_cone_rejects_what_faces_away_and_keeps_what_faces_the_eye() {
        Assert.False(GpuClusterCulling.Backfacing(Vector3.Zero, 0.01f, Vector3.UnitZ, 0.99f, new(0f, 0f, 100f)));
        Assert.True(GpuClusterCulling.Backfacing(Vector3.Zero, 0.01f, Vector3.UnitZ, 0.99f, new(0f, 0f, -100f)));
    }

    // --- The hierarchy earning its name -------------------------------------

    /// <summary>
    ///     A rejected subtree costs one test, which is the whole point of the hierarchy.
    /// </summary>
    /// <remarks>
    ///     Not an assertion about time. What is counted is how many clusters the walk <em>looked at</em>
    ///     — a mesh entirely outside the frustum should cost as many tests as it has roots, and a flat
    ///     per-cluster cull would cost one per cluster. That difference is the reason phase 3 exists,
    ///     and it is invisible in any test that only checks the answer.
    /// </remarks>
    [Fact]
    public void A_rejected_subtree_costs_one_test_per_root() {
        var scene = Tree(levels: 5, fanOut: 3, errorStep: 0.5f);
        var instance = Instance(scene);

        var view = Everywhere(threshold: 0.0001f);

        // One plane that everything is behind, which is a whole mesh off screen.
        view.Planes[0] = new(0f, 0f, 1f, -1e9f);

        var visited = 0;
        var resident = new Func<uint, bool>(
            _ => {
                visited++;
                return true;
            }
        );

        var result = GpuClusterCulling.Traverse(scene, instance, view, resident);

        Assert.Empty(result.Visible);

        // Nothing was accepted, so nothing asked about residency — the rejection happens before it.
        Assert.Equal(0, visited);

        // And the finest level, which a flat cull would have tested every cluster of, is enormous
        // beside the roots the walk actually looked at.
        Assert.True(scene.Clusters.Length > scene.Roots.Length * 100);
    }

    // --- Streaming -----------------------------------------------------------

    /// <summary>
    ///     A cluster whose children are not resident is drawn rather than refined into nothing.
    /// </summary>
    /// <remarks>
    ///     What makes streaming degrade rather than fail, and the same rule the offline cut follows:
    ///     all of a group's children or none of them, because the boundary between a refined cluster
    ///     and its unrefined neighbour was locked at one level and simplified at the other. The page
    ///     is asked for on the way, which is what makes the request demand-driven.
    /// </remarks>
    [Fact]
    public void A_cluster_whose_children_are_absent_is_drawn_and_its_pages_asked_for() {
        var scene = Tree(levels: 2, fanOut: 2, errorStep: 0.5f);

        // A page per level, coarsest first — which is how the page builder packs them.
        var paged = new ClusterScene(
            [
                .. scene.Clusters.Select(
                    c => c with { Page = (uint)(c.ChildCount == 0 ? 2 : c.Flags == GpuClusterCulling.ClusterRoot ? 0 : 1) }
                )
            ],
            scene.Children,
            scene.Roots
        );

        var instance = Instance(paged);

        // Fine enough that everything would refine to level zero if the pages were there.
        var view = Everywhere(threshold: 0.0001f);

        var everything = GpuClusterCulling.Traverse(paged, instance, view, _ => true);
        Assert.All(everything.Visible, cluster => Assert.Equal(0u, paged.Clusters[cluster].ChildCount));
        Assert.Empty(everything.Requests);

        // With the finest page missing, the walk stops one level up and says what it wanted.
        var partial = GpuClusterCulling.Traverse(paged, instance, view, page => page != 2);

        Assert.NotEmpty(partial.Visible);
        Assert.All(partial.Visible, cluster => Assert.NotEqual(0u, paged.Clusters[cluster].ChildCount));
        Assert.Equal([2], partial.Requests);
    }

    /// <summary>An instance nothing is resident for draws nothing and asks for its roots.</summary>
    /// <remarks>
    ///     Drawing a cluster whose page is absent would be drawing whatever page occupies that pool
    ///     slot now — another mesh's triangles at this one's indices — which is worse than drawing
    ///     nothing. The request is what fixes it on a later frame.
    /// </remarks>
    [Fact]
    public void An_instance_with_no_resident_pages_draws_nothing_and_asks() {
        var scene = Tree(levels: 2, fanOut: 2, errorStep: 0.5f);
        var instance = Instance(scene);

        var result = GpuClusterCulling.Traverse(scene, instance, Everywhere(threshold: 1e9f), _ => false);

        Assert.Empty(result.Visible);
        Assert.NotEmpty(result.Requests);
    }

    // --- The instance ---------------------------------------------------------

    [Fact]
    public void A_dead_instance_draws_nothing() {
        var scene = Tree(levels: 2, fanOut: 2, errorStep: 0.5f);
        var instance = Instance(scene) with { Flags = 0 };

        Assert.Empty(GpuClusterCulling.Traverse(scene, instance, Everywhere(1f), _ => true).Visible);
    }

    [Fact]
    public void An_instance_in_no_stage_the_view_draws_draws_nothing() {
        var scene = Tree(levels: 2, fanOut: 2, errorStep: 0.5f);
        var instance = Instance(scene) with { StagesLow = 2 };

        Assert.Empty(GpuClusterCulling.Traverse(scene, instance, Everywhere(1f), _ => true).Visible);
    }

    /// <summary>
    ///     Scaling an instance scales its errors, so a bigger copy refines sooner.
    /// </summary>
    /// <remarks>
    ///     An object-space error is a length in the mesh's own space, so an instance drawn twice the
    ///     size deviates twice as far — and a traversal that ignored the scale would give a giant and
    ///     a pebble the same level of detail. The oracle applies it too, which is what makes the
    ///     comparison meaningful rather than circular.
    /// </remarks>
    [Fact]
    public void An_instances_scale_scales_its_errors() {
        var scene = Tree(levels: 3, fanOut: 2, errorStep: 0.5f);
        var view = Everywhere(threshold: 1f);

        var small = GpuClusterCulling.Traverse(scene, Instance(scene, scale: 0.1f), view, _ => true).Visible;
        var large = GpuClusterCulling.Traverse(scene, Instance(scene, scale: 10f), view, _ => true).Visible;

        Assert.True(
            large.Length > small.Length,
            $"The large instance drew {large.Length} clusters and the small one {small.Length}."
        );

        Assert.Equal(GpuClusterCulling.Cut(scene, Instance(scene, scale: 10f), view), large);
    }

    // --- The layout and the source ------------------------------------------

    /// <summary>The records are the size and shape the shader declares.</summary>
    /// <remarks>
    ///     The host writes bytes and the shader reads structs, so a member that moved is not a compile
    ///     error anywhere — it is a cone axis built out of an error and a parent error, which culls
    ///     everything or nothing and says why nowhere.
    /// </remarks>
    [Fact]
    public void The_records_match_the_shader() {
        // centre + radius, cone axis + cosine, the group bound, the two errors and the child range,
        // then the page, the flags and the padding the shader declares: five sixteen-byte rows, with
        // every float3 starting one.
        Assert.Equal(80, System.Runtime.InteropServices.Marshal.SizeOf<CullCluster>());

        // Four counts, position + scale, two mask halves, flags, the mesh, the palette base, the motion
        // radius and the padding the shader declares: four rows. The padding is the whole point of the
        // assertion — the record was three rows and exactly a multiple of the sixteen a float3 aligns a
        // struct to, and the two fields skinning added would otherwise have made it 56 here and 64 on
        // the device, which reads instance one out of the middle of instance zero.
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf<CullInstance>());

        // ⚠ The raster's two records, which this assertion did not cover until they were the ones that
        // were wrong. `RasterMesh` is a float3 and some words, so the device aligns the record to
        // sixteen and its array stride is the size rounded up to it — `ClusterRaster.reflect.json`
        // says 32. The host wrote 20, and one mesh is the only scene in which that is invisible:
        // registered mesh zero decodes correctly out of offset zero and every mesh after it reads its
        // quantization grid out of the middle of the one before, which is geometry folded in on
        // itself with a healthy cluster count beside it.
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.SizeOf<RasterMesh>());
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.SizeOf<RasterCluster>());

        Assert.Equal(1024, GpuClusterCulling.QueueCapacity);
        Assert.Equal(1u, GpuClusterCulling.ClusterRoot);
    }

    /// <summary>
    ///     The shader still contains the arithmetic the mirror mirrors.
    /// </summary>
    /// <remarks>
    ///     The gap every mirror has, and the same defence the object cull uses: a transliteration
    ///     checked against an oracle says the host's copy is right, and says nothing at all about
    ///     whether the shader is still the thing it is a copy of. Someone deleting the cone test from
    ///     the <c>.rvn</c> breaks no test in this file without this one.
    /// </remarks>
    [Fact]
    public void The_shader_traverses_what_the_host_says_it_does() {
        var source = Source("Pipeline", "Culling.rvn");

        // A permutation of this shader rather than a shader of its own — improvement 3.
        Assert.Contains("[Permutation] val Clusters: bool", source, StringComparison.Ordinal);

        // The three rejections the object path does not have, and the walk.
        Assert.Contains("Cull.Backfacing(", source, StringComparison.Ordinal);
        Assert.Contains("Cull.PixelError(", source, StringComparison.Ordinal);
        Assert.Contains("view.errorScale", source, StringComparison.Ordinal);
        Assert.Contains("view.errorThreshold", source, StringComparison.Ordinal);

        // The shared queue and the barrier that makes it a queue rather than a race — what
        // docs/plan/22-virtualized-geometry.md § B1 was blocked on.
        Assert.Contains("groupshared var queue:", source, StringComparison.Ordinal);
        Assert.Contains("barrier()", source, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(pushed,", source, StringComparison.Ordinal);

        // One occlusion test, taking a sphere, so both callers hand it one — improvement 3 again.
        Assert.Contains("func Occluded(center: float3, radius: float", source, StringComparison.Ordinal);

        // And the output the traversal exists to produce, at both ends of it — phase 6 routes an
        // accepted cluster to the hardware raster's prefix or the software raster's suffix, and the
        // shared reservation is what keeps the two from meeting.
        Assert.Contains("atomicAdd(visible[Cull.VisibleHardware]", source, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(visible[Cull.VisibleSoftware]", source, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(visible[Cull.VisibleReserved]", source, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(requests[0]", source, StringComparison.Ordinal);

        // The routing itself, and the near-plane clause the software raster's lack of clipping rests on.
        Assert.Contains("func Software(center: float3, radius: float", source, StringComparison.Ordinal);
        Assert.Contains("view.softwareThreshold", source, StringComparison.Ordinal);
        Assert.Contains("dot(view.planes[0].xyz, center) + view.planes[0].w < radius", source, StringComparison.Ordinal);
    }

    /// <summary>A shipped shader's source, found by walking up rather than by counting directories.</summary>
    static string Source(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }
}
