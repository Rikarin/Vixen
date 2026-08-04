// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 6's routing: which raster an accepted cluster goes to, and the property that it is one.
/// </summary>
/// <remarks>
///     <para>
///         <b>The failure this exists to catch is a cluster in neither list.</b> A cut that goes to the
///         hardware raster is drawn; a cut that goes to the software one is drawn; a cluster the routing
///         dropped is a hole, and a cluster in both is a surface resolved by whichever raster's depth
///         won — which for two rasters computing the same depth is a coin toss between two identities
///         naming the same triangle, and therefore invisible until something else changes.
///     </para>
///     <para>
///         So the assertion swept below is not "the threshold does something" but that the two lists
///         <em>partition</em> the cut at every threshold, which is the same shape as phase 3's "every
///         path from a root to a leaf holds exactly one visible cluster".
///     </para>
/// </remarks>
public class ClusterRoutingTests {
    /// <summary>A view that rejects nothing, so a cut is decided by error and routing alone.</summary>
    /// <remarks>
    ///     <c>GpuClusterCullingTests.Everywhere</c>'s shape. The near plane is the one that has to be
    ///     real rather than permissive-by-accident, because the routing consults it — so it is stated as
    ///     a plane a bound at the origin clears by a mile rather than as a plane pointing nowhere.
    /// </remarks>
    static CullView Everywhere(float errorThreshold, float softwareThreshold, float errorScale = 1000f) {
        var view = new CullView {
            Position = new(0f, 0f, -1000f),
            MaximumDistance = 0f,
            StagesLow = 1,
            StagesHigh = 0,
            ErrorScale = errorScale,
            ErrorThreshold = errorThreshold,
            SoftwareThreshold = softwareThreshold
        };

        for (var i = 0; i < BoundingFrustum.PlaneCount; i++) {
            view.Planes[i] = new(0f, 0f, 1f, 1e9f);
        }

        return view;
    }

    // --- The predicate -------------------------------------------------------

    /// <summary>Zero is off, and off is what a view carries unless a host measured otherwise.</summary>
    /// <remarks>
    ///     <c>docs/plan/22-virtualized-geometry.md</c> phase 6: "gated on a measurement, not a plan". A
    ///     default that guessed where the crossover falls would be a frame that is slower for a reason
    ///     nothing reports.
    /// </remarks>
    [Fact]
    public void A_threshold_of_zero_routes_nothing_to_software() {
        Assert.False(GpuClusterCulling.IsSoftware(Vector3.Zero, 0.001f, Everywhere(1f, 0f)));
        Assert.Equal(0f, VirtualGeometryRenderFeature.DefaultSoftwareThreshold);
    }

    /// <summary>A view that does no screen-size work does no routing either.</summary>
    /// <remarks>
    ///     <see cref="RenderView.ScreenHeightScale" /> is zero for a shadow cascade and a probe face on
    ///     purpose, and <see cref="GpuClusterCulling.ErrorScaleFor(float, int)" /> propagates that as a
    ///     zero error scale. Without this clause a zero scale would make every cluster read as
    ///     <em>infinitely small</em> and route the entire scene to a raster meant for specks.
    /// </remarks>
    [Fact]
    public void A_view_with_no_error_scale_routes_nothing_to_software() {
        Assert.False(GpuClusterCulling.IsSoftware(Vector3.Zero, 1f, Everywhere(1f, 1e6f, errorScale: 0f)));
    }

    /// <summary>A large cluster stays with the hardware and a small one does not.</summary>
    [Fact]
    public void The_threshold_is_the_clusters_screen_diameter() {
        // errorScale 1000, eye 1000 away: a radius of r is 2 * r * 1000 / (1000 - r) pixels across, so
        // a radius of 0.01 is about 0.02 pixels and a radius of 5 is about ten.
        var view = Everywhere(1f, softwareThreshold: 1f);

        Assert.True(GpuClusterCulling.IsSoftware(Vector3.Zero, 0.01f, view));
        Assert.False(GpuClusterCulling.IsSoftware(Vector3.Zero, 5f, view));
    }

    /// <summary>
    ///     A cluster reaching the near plane stays with the hardware however small it is.
    /// </summary>
    /// <remarks>
    ///     <b>Not a safety check — the contract.</b> <c>ClusterSoftwareRaster.rvn</c> does no clipping
    ///     at all, so a corner behind the eye projects to a position that means nothing. This is what
    ///     guarantees every corner of every triangle it will ever see has a positive <c>w</c>, and
    ///     removing it does not fail here: it fails as geometry smeared across the screen at the one
    ///     camera angle where a cluster straddles the plane.
    /// </remarks>
    [Fact]
    public void A_cluster_reaching_the_near_plane_stays_with_the_hardware() {
        var view = Everywhere(1f, softwareThreshold: 1e6f);

        // A near plane through the origin, inward-facing along +z: a bound at the origin straddles it.
        view.Planes[0] = new(0f, 0f, 1f, 0f);

        Assert.False(GpuClusterCulling.IsSoftware(Vector3.Zero, 0.001f, view));

        // The same bound a metre in front of it clears it, and routes.
        Assert.True(GpuClusterCulling.IsSoftware(new(0f, 0f, 1f), 0.001f, view));
    }

    // --- The partition -------------------------------------------------------

    /// <summary>
    ///     At every threshold, the two rasters' lists partition the cut exactly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The sweep phase 6's exit criterion asks for, in the form the host can assert without a
    ///         device: from a threshold that routes nothing through one that routes everything, every
    ///         accepted cluster appears in exactly one of the two — and the union is the same cut the
    ///         traversal produced with routing switched off entirely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cut itself must not move</b>, and that is the second assertion rather than an
    ///         assumption: routing is a decision about <em>how</em> to draw a cluster taken after it has
    ///         been accepted, so a threshold that changed which clusters were accepted would be a
    ///         threshold that changes the picture's level of detail — which is exactly the kind of
    ///         "it looks fine" defect the sweep exists to rule out.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_accepted_cluster_goes_to_exactly_one_raster() {
        var scene = Chain(levels: 6);

        var instance = new CullInstance {
            FirstCluster = 0,
            ClusterCount = (uint)scene.Clusters.Length,
            FirstRoot = 0,
            RootCount = 1,
            Position = Vector3.Zero,
            Scale = 1f,
            StagesLow = 1,
            Flags = GpuCulling.Alive
        };

        var baseline = GpuClusterCulling.Traverse(scene, instance, Everywhere(2f, 0f), _ => true);

        Assert.NotEmpty(baseline.Visible);
        Assert.Empty(baseline.Software);

        var routed = 0;

        foreach (var threshold in (float[])[0f, 0.5f, 2f, 8f, 32f, 128f, 1e6f]) {
            var result = GpuClusterCulling.Traverse(scene, instance, Everywhere(2f, threshold), _ => true);

            // The cut is the cut, whatever the routing said about it.
            Assert.Equal(baseline.Visible, result.Visible);

            // Every software cluster is an accepted one, and none is counted twice.
            Assert.Equal(result.Software.Distinct().Count(), result.Software.Length);
            Assert.All(result.Software, cluster => Assert.Contains(cluster, result.Visible));

            routed += result.Software.Length;
        }

        // And the sweep actually reached both ends, or the assertions above are statements about a
        // routing that never fired — which is how this test would pass with the feature removed.
        Assert.True(routed > 0, "No threshold in the sweep routed anything to the software raster.");

        var everything = GpuClusterCulling.Traverse(scene, instance, Everywhere(2f, 1e6f), _ => true);

        Assert.Equal(everything.Visible, everything.Software);
    }

    /// <summary>A chain of clusters, each simplifying the one below it.</summary>
    /// <remarks>
    ///     One parent per level and one child each, which is the smallest DAG with a cut worth taking:
    ///     the errors halve going down, so a threshold picks a level and the bounds are small enough
    ///     that a large software threshold takes all of them.
    /// </remarks>
    static ClusterScene Chain(int levels) {
        var clusters = new CullCluster[levels];
        var children = new List<uint>();

        for (var i = 0; i < levels; i++) {
            var error = MathF.Pow(0.5f, i);
            var first = (uint)children.Count;

            if (i + 1 < levels) {
                children.Add((uint)(i + 1));
            }

            clusters[i] = new() {
                Center = new(i * 0.001f, 0f, 0f),
                Radius = 0.01f,
                ErrorCenter = new(i * 0.001f, 0f, 0f),
                ErrorRadius = 0.01f,
                ConeAxis = new(0f, 0f, -1f),

                // Negative: a cone that can never reject, so the routing is what this test is about.
                ConeCosine = -1f,
                Error = error,
                ParentError = i == 0 ? 3.4e38f : MathF.Pow(0.5f, i - 1),
                FirstChild = first,
                ChildCount = (uint)(i + 1 < levels ? 1 : 0),
                Page = 0,
                Flags = i == 0 ? GpuClusterCulling.ClusterRoot : 0u,
                GroupLead = (uint)i
            };
        }

        return new(clusters, [.. children], [0u]);
    }
}
