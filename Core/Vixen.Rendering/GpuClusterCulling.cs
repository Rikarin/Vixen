// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Rendering.VirtualGeometry;

namespace Vixen.Rendering;

/// <summary>One cluster as <c>CullCluster</c> in the shader declares it.</summary>
/// <remarks>
///     The runtime half of <see cref="Meshlet" />, and nothing that is only needed once a cluster has
///     been accepted. Where its geometry is lives in the page placement, which the traversal reads
///     only to ask whether it has arrived — a cluster has to be tested before anybody can know
///     whether its bytes are wanted, which is why the records are resident and the geometry is paged.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct CullCluster {
    /// <summary>The bind-pose bound's centre, in the mesh's own space.</summary>
    public Vector3 Center;

    /// <summary>Its radius.</summary>
    public float Radius;

    /// <summary>The axis of the cone every triangle's normal lies inside.</summary>
    public Vector3 ConeAxis;

    /// <summary>
    ///     The cosine of the cone's half-angle. Negative means the cone can never reject.
    /// </summary>
    /// <remarks>
    ///     Carried as it was built rather than clamped to something usable. A cluster whose normals
    ///     span more than a hemisphere has some triangle facing every direction, so no eye position
    ///     makes all of them backfacing — and a clamp would cull geometry that is facing the camera.
    /// </remarks>
    public float ConeCosine;

    /// <summary>
    ///     The bound of the <em>group</em> whose simplification produced this cluster, which is where
    ///     <see cref="Error" /> is projected from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Not the cluster's own bound, and the difference is the whole of why a cut does
    ///         not tear.</strong> A group's simplification produces several parents, every one of
    ///         which replaces <em>all</em> of the group's children — so all of them have to reach the
    ///         same refinement decision. If one refines while its sibling does not, the sibling is
    ///         drawn beside the children of its own group and the surface is covered twice in one
    ///         place and once in another.
    ///     </para>
    ///     <para>
    ///         They share an error already, because phase 1 gives a group one. What they also have to
    ///         share is the distance it is projected at, and their own bounds are in different places
    ///         — so a per-cluster distance makes the same error project to different pixel counts and
    ///         the parents disagree. Found by the randomised comparison against the brute-force cut,
    ///         which is exactly the class of defect it exists for.
    ///     </para>
    /// </remarks>
    public Vector3 ErrorCenter;

    /// <summary>The group bound's radius.</summary>
    public float ErrorRadius;

    /// <summary>How far this cluster's surface may deviate from the original mesh, in object space.</summary>
    public float Error;

    /// <summary>The error of the group this cluster is simplified into, or a very large number.</summary>
    /// <remarks>
    ///     The traversal never reads it — it descends when a cluster's own error is too large, and it
    ///     only reached the cluster because its parent's was too — but the brute-force oracle the
    ///     mirror is checked against does, and the two have to be reading one record.
    /// </remarks>
    public float ParentError;

    /// <summary>Where this cluster's children start in the child array.</summary>
    public uint FirstChild;

    /// <summary>How many it has. Zero is a leaf: level zero, the original mesh.</summary>
    public uint ChildCount;

    /// <summary>Which page holds its geometry.</summary>
    public uint Page;

    /// <summary><see cref="GpuClusterCulling.ClusterRoot" /> when nothing simplifies it further.</summary>
    public uint Flags;

    /// <summary>Eight bytes of tail padding the shader declares and never reads.</summary>
    public ulong Padding;
}

/// <summary>One instance of a cluster DAG as <c>CullInstance</c> in the shader declares it.</summary>
/// <remarks>
///     A position and a scale rather than a matrix, and not for the sake of the bytes: what every test
///     in the traversal wants is a world-space sphere and the factor an object-space length is
///     multiplied by, and a record carrying a matrix would be four times the size for a transform the
///     pass would only use to derive those two. A rotation does not enter into it — the bound is a
///     sphere.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct CullInstance {
    /// <summary>Where this instance's clusters start in the cluster buffer.</summary>
    public uint FirstCluster;

    /// <summary>How many it has, so a traversal cannot walk into another instance's DAG.</summary>
    public uint ClusterCount;

    /// <summary>Where its roots start in the root buffer.</summary>
    public uint FirstRoot;

    /// <summary>How many roots it has.</summary>
    public uint RootCount;

    /// <summary>Where the instance is.</summary>
    public Vector3 Position;

    /// <summary>The largest scale factor any axis has.</summary>
    public float Scale;

    /// <summary>The low half of the instance's <see cref="RenderStageMask.Bits" />.</summary>
    public uint StagesLow;

    /// <summary>The high half of it.</summary>
    public uint StagesHigh;

    /// <summary><see cref="GpuCulling.Alive" /> when the slot holds a live instance.</summary>
    public uint Flags;

    /// <summary>Which registered mesh's quantization grid this instance's geometry decodes against.</summary>
    /// <remarks>
    ///     The word that used to be padding, and phase 4 is what earned it a name. A page holds positions
    ///     as integers on a grid the <em>mesh</em> owns, so a raster that has a cluster still needs the
    ///     grid to turn them back into object space. The traversal never reads it — deciding whether to
    ///     draw a cluster is about bounds and error, not about vertices — which is why it rides here
    ///     rather than in <see cref="CullCluster" />: one word per instance against one per cluster.
    /// </remarks>
    public uint Mesh;
}

/// <summary>
///     The cluster DAG as one flat set of buffers, plus the traversal's answer.
/// </summary>
/// <remarks>
///     Flat because a device reads it: the DAG's edges are a run of child indices per cluster and the
///     roots are a run per instance, both concatenated across every instance in the scene. The offline
///     form — <see cref="MeshletMesh" />'s groups — is a better shape to build and a worse one to
///     traverse, and <see cref="GpuClusterCulling.Flatten" /> is the one place the two are related.
/// </remarks>
/// <param name="Clusters">Every cluster of every DAG, concatenated.</param>
/// <param name="Children">Every cluster's children, concatenated.</param>
/// <param name="Roots">Every instance's roots, concatenated.</param>
public sealed record ClusterScene(CullCluster[] Clusters, uint[] Children, uint[] Roots);

/// <summary>
///     The records, the dispatch shape and the tests that the cluster traversal in
///     <c>Raven/Library/Pipeline/Culling.rvn</c> and the host have to agree about.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="GpuCulling" />'s sibling, and deliberately not a second copy of it: the frustum
///         test, the rounding slack, the stage-mask intersection and the whole occlusion test are
///         that one's, called from here. What is new is a normal cone, a screen-space error and a
///         walk — which is exactly the difference improvement 3 of
///         <c>docs/plan/22-virtualized-geometry.md</c> says should be the whole difference.
///     </para>
///     <para>
///         <strong><see cref="Traverse" /> is the mirror, and it is what phase 3 is judged on.</strong>
///         It is a transliteration of the shader's <c>Traverse</c>, so a randomised comparison
///         against the brute-force cut over the same DAG — which is the definition — says whether the
///         arithmetic the device will run is the arithmetic the engine means. What it cannot say is
///         whether the shader still contains that arithmetic; that is what the source assertion in
///         the tests is for, the same defence <see cref="GpuCulling.IsVisible" /> has.
///     </para>
/// </remarks>
public static class GpuClusterCulling {
    /// <summary>The permutation key selecting the traversal rather than the per-object cull.</summary>
    public static string ClustersKey => "Clusters";

    /// <summary>The bit of <see cref="CullCluster.Flags" /> that says nothing simplifies it further.</summary>
    public const uint ClusterRoot = 1u;

    /// <summary>How many clusters one workgroup's shared queue holds.</summary>
    /// <remarks>
    ///     Four kilobytes of the sixteen a device has to offer, which leaves room for a second
    ///     consumer in the same shader. Overflow is not a dropped node: the group stops refining and
    ///     draws what it has, which is a coarser cut rather than a hole.
    /// </remarks>
    public const int QueueCapacity = 1024;

    /// <summary>
    ///     The screen-space scale an object-space error is projected by, before the distance divide.
    /// </summary>
    /// <param name="verticalFieldOfView">The camera's vertical field of view, in radians.</param>
    /// <param name="screenHeight">How many pixels tall the view is.</param>
    /// <remarks>
    ///     <c>MeshletCut.PixelError</c>'s constant factor, pulled out so it is computed once per view
    ///     per frame instead of once per cluster per view. The offline cut and the runtime traversal
    ///     therefore project error by the same arithmetic, which is what lets a mesh authored against
    ///     a pixel budget be drawn against one.
    /// </remarks>
    public static float ErrorScaleFor(float verticalFieldOfView, float screenHeight) =>
        screenHeight / (2f * MathF.Tan(verticalFieldOfView * 0.5f));

    /// <summary>
    ///     The same scale, from what a <see cref="RenderView" /> already carries.
    /// </summary>
    /// <param name="screenHeightScale">
    ///     <see cref="RenderView.ScreenHeightScale" />: <c>1 / tan(fov / 2)</c>, or zero for a view that
    ///     does no screen-size work at all.
    /// </param>
    /// <param name="screenHeight">How many pixels tall the view is.</param>
    /// <returns>The scale, or zero for a view that opted out.</returns>
    /// <remarks>
    ///     <para>
    ///         Two entry points to one number, and this is the one a frame uses. A view does not carry a
    ///         field of view — it carries the fraction-of-height factor every LOD consumer wants, which
    ///         is that projection with the pixels divided out — so the conversion is half of it times the
    ///         height, and it is written here rather than at the caller because a factor of two in the
    ///         wrong place is a threshold that means twice what it says.
    ///     </para>
    ///     <para>
    ///         <b>Zero propagates, and that is the useful part.</b>
    ///         <see cref="RenderView.ScreenHeightScale" /> is zero for a shadow cascade and a probe face
    ///         on purpose — choosing a different mesh for a shadow than for its caster makes the shadow
    ///         stop matching it — and a zero scale projects every error to zero, so such a view accepts
    ///         every cluster at its root. <see cref="Features.LodRenderFeature" /> reads the same field
    ///         the same way, which is what keeps the two paths agreeing about which views do this.
    ///     </para>
    /// </remarks>
    public static float ErrorScaleFor(float screenHeightScale, int screenHeight) =>
        screenHeightScale <= 0f || screenHeight <= 0 ? 0f : screenHeightScale * screenHeight * 0.5f;

    /// <summary>How many pixels of screen an object-space deviation covers, at a distance.</summary>
    /// <param name="error">The deviation, in object space, already scaled by the instance.</param>
    /// <param name="distance">How far the cluster's nearest point is from the eye.</param>
    /// <param name="errorScale">The view's <see cref="CullView.ErrorScale" />.</param>
    /// <returns>The deviation in pixels; effectively infinite at or behind the eye.</returns>
    public static float PixelError(float error, float distance, float errorScale) =>
        distance <= 0f ? 3.4e38f : error * errorScale / distance;

    /// <summary>
    ///     Whether every triangle of a cluster faces away from a point.
    /// </summary>
    /// <param name="center">The cluster's world-space bound centre.</param>
    /// <param name="radius">Its world-space radius.</param>
    /// <param name="axis">The normal cone's axis.</param>
    /// <param name="cosine">The cosine of its half-angle.</param>
    /// <param name="eye">Where the view is.</param>
    /// <remarks>
    ///     <para>
    ///         A cone <c>{n : dot(n, axis) ≥ cosine}</c> is entirely backfacing from an eye when the
    ///         angle between the axis and the direction <em>to</em> the eye exceeds ninety degrees
    ///         plus the half-angle — which is <c>dot(axis, toEye) &lt; -sin(halfAngle)</c>. Widened by
    ///         the cluster's own extent, because the direction is measured from the centre and a
    ///         cluster is not a point.
    ///     </para>
    ///     <para>
    ///         <strong>A negative cosine never rejects.</strong> That is the honest answer for a
    ///         cluster whose normals span more than a hemisphere, and the reason phase 1 records the
    ///         cosine rather than clamping it: there is no eye position from which a closed cap is
    ///         entirely backfacing, and a test that pretended otherwise would remove geometry that is
    ///         facing the camera. An eye inside the bound never rejects either, for the same reason.
    ///     </para>
    /// </remarks>
    public static bool Backfacing(Vector3 center, float radius, Vector3 axis, float cosine, Vector3 eye) {
        if (cosine < 0f) {
            return false;
        }

        var offset = eye - center;
        var distance = offset.Length();

        if (distance <= radius) {
            return false;
        }

        var sine = MathF.Sqrt(MathF.Max(1f - (cosine * cosine), 0f));

        return Vector3.Dot(axis, offset / distance) < -sine - (radius / distance);
    }

    /// <summary>
    ///     Flattens an offline DAG into the buffers a traversal reads.
    /// </summary>
    /// <param name="mesh">The DAG, from <c>MeshletBuilder</c>.</param>
    /// <param name="pages">Where each cluster's geometry is, from <c>MeshletPageBuilder</c>.</param>
    /// <returns>The cluster records, the child runs and the roots.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         The one place the offline shape and the runtime shape are related, which is what keeps
    ///         the relation checkable. Offline, a DAG edge is a <c>MeshletGroup</c> — a set of children
    ///         and a set of parents — because that is what a build produces and validates. At runtime
    ///         a traversal only ever asks one question of one cluster: what do I refine into? So the
    ///         edge is inverted here into a run of children per cluster.
    ///     </para>
    ///     <para>
    ///         A cluster's children are the children of the group that <em>produced</em> it, and every
    ///         parent of a group carries the whole set. That is not redundancy to be factored out: it
    ///         is what makes refinement a decision one cluster can take alone, which is the property
    ///         the whole traversal is built on.
    ///     </para>
    /// </remarks>
    public static ClusterScene Flatten(MeshletMesh mesh, MeshletPageSet pages) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pages);

        var clusters = new CullCluster[mesh.Meshlets.Length];
        var children = new List<uint>();

        // Which group produced each cluster, so its children can be found without searching.
        var producedBy = new int[mesh.Meshlets.Length];
        Array.Fill(producedBy, -1);

        for (var group = 0; group < mesh.Groups.Length; group++) {
            foreach (var parent in mesh.Groups[group].Parents) {
                producedBy[parent] = group;
            }
        }

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            var meshlet = mesh.Meshlets[i];
            var first = children.Count;
            var count = 0;

            if (producedBy[i] >= 0) {
                foreach (var child in mesh.Groups[producedBy[i]].Children) {
                    children.Add((uint)child);
                    count++;
                }
            }

            var bounds = meshlet.Bounds;
            var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;

            // The group bound: the sphere enclosing every parent of the group that produced this
            // cluster, which is what all of them project their shared error at. A leaf has no such
            // group and an error of zero, so its own bound stands in and nothing reads it.
            var (groupCentre, groupRadius) = producedBy[i] < 0
                ? (centre, (bounds.Maximum - centre).Length())
                : GroupBound(mesh, mesh.Groups[producedBy[i]].Parents);

            clusters[i] = new() {
                Center = centre,
                Radius = (bounds.Maximum - centre).Length(),
                ErrorCenter = groupCentre,
                ErrorRadius = groupRadius,
                ConeAxis = meshlet.ConeAxis,
                ConeCosine = meshlet.ConeCosine,
                Error = meshlet.Error,

                // Infinity does not survive a buffer a shader reads as a float it compares — a NaN
                // out of any arithmetic on it would compare false both ways — so a root's parent
                // error is the largest finite float instead. Nothing is above it either.
                ParentError = float.IsInfinity(meshlet.ParentError) ? 3.4e38f : meshlet.ParentError,
                FirstChild = (uint)first,
                ChildCount = (uint)count,
                Page = (uint)pages.Clusters[i].Page,
                Flags = float.IsInfinity(meshlet.ParentError) ? ClusterRoot : 0u
            };
        }

        return new(clusters, [.. children], [.. mesh.Roots.Select(root => (uint)root)]);
    }

    /// <summary>The sphere enclosing a run of clusters' bounds.</summary>
    /// <remarks>
    ///     Axis-aligned union then a sphere around it, which is looser than the smallest enclosing
    ///     sphere and is the right kind of loose: an error projected from a bound that is too large is
    ///     projected from too far away, which refines sooner than it had to. The other direction pops.
    /// </remarks>
    static (Vector3 Center, float Radius) GroupBound(MeshletMesh mesh, int[] clusters) {
        if (clusters.Length == 0) {
            return (Vector3.Zero, 0f);
        }

        var minimum = mesh.Meshlets[clusters[0]].Bounds.Minimum;
        var maximum = mesh.Meshlets[clusters[0]].Bounds.Maximum;

        foreach (var cluster in clusters) {
            minimum = Vector3.Min(minimum, mesh.Meshlets[cluster].Bounds.Minimum);
            maximum = Vector3.Max(maximum, mesh.Meshlets[cluster].Bounds.Maximum);
        }

        var centre = (minimum + maximum) * 0.5f;

        return (centre, (maximum - centre).Length());
    }

    /// <summary>What one instance's traversal decided.</summary>
    /// <param name="Visible">The clusters to draw, ascending.</param>
    /// <param name="Requests">The pages it wanted and did not have, ascending and distinct.</param>
    public readonly record struct TraversalResult(int[] Visible, int[] Requests);

    /// <summary>
    ///     Whether the shader's traversal would draw this cluster, and what it would ask for — the
    ///     whole of the hierarchical culling decision.
    /// </summary>
    /// <param name="scene">The flattened DAG.</param>
    /// <param name="instance">The instance being traversed.</param>
    /// <param name="view">The view being traversed for.</param>
    /// <param name="isResident">Whether a page's bytes are in the pool.</param>
    /// <param name="occluder">The depth pyramid, or null for a view with none.</param>
    /// <param name="occluderSize">Its level-0 size in texels.</param>
    /// <returns>The visible cluster list and the page requests.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         A transliteration of the shader, sequential where it is parallel. What is deliberately
    ///         <em>not</em> mirrored is the schedule: the shader's queue, its barriers and the order
    ///         its lanes happen to visit a round in are how sixty-four invocations agree, and the
    ///         answer does not depend on them. What has to match is the set of clusters accepted and
    ///         the set of pages asked for, which is what the tests compare.
    ///     </para>
    ///     <para>
    ///         <strong>A rejected subtree costs one test.</strong> A cluster that fails the frustum,
    ///         the cone or the pyramid takes its children with it — which is the whole point of the
    ///         hierarchy and the whole difference from the per-object cull: a mesh of a hundred
    ///         thousand clusters behind the camera costs as many tests as it has roots.
    ///     </para>
    ///     <para>
    ///         <strong>Accept or refine, never both.</strong> A cluster is drawn when its own error is
    ///         under the threshold, and the walk only reached it because its parent's was not — which
    ///         is <c>Error ≤ t &lt; ParentError</c> arrived at from the top rather than tested per
    ///         cluster. That is what makes the answer a cut: along any path through the DAG exactly
    ///         one cluster satisfies it.
    ///     </para>
    /// </remarks>
    public static TraversalResult Traverse(
        ClusterScene scene,
        in CullInstance instance,
        in CullView view,
        Func<uint, bool> isResident,
        GpuCulling.OccluderDepth? occluder = null,
        Int2 occluderSize = default
    ) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(isResident);

        var visible = new List<int>();
        var requests = new HashSet<uint>();

        // Copied out of the `in` parameter, because the local functions below close over it and a
        // by-reference parameter cannot be captured. A record of eleven words copied once per
        // instance per view is not the cost worth restructuring the walk to avoid.
        var owner = instance;

        if ((instance.Flags & GpuCulling.Alive) == 0) {
            return new([], []);
        }

        if ((instance.StagesLow & view.StagesLow) == 0 && (instance.StagesHigh & view.StagesHigh) == 0) {
            return new([], []);
        }

        // A queue rather than a stack, matching the shader's breadth-first front — and bounded the
        // same way, because the overflow behaviour is part of the answer rather than an implementation
        // detail: a group whose front does not fit stops refining and draws what it has.
        var queue = new Queue<uint>();
        var overflowed = false;

        for (var i = 0u; i < instance.RootCount; i++) {
            queue.Enqueue(scene.Roots[(int)(instance.FirstRoot + i)]);
        }

        while (queue.Count > 0) {
            var cluster = queue.Dequeue();
            var record = scene.Clusters[(int)(instance.FirstCluster + cluster)];

            var centre = instance.Position + (record.Center * instance.Scale);
            var radius = record.Radius * instance.Scale;

            if (view.MaximumDistance > 0f) {
                var reach = view.MaximumDistance + radius;
                var offset = centre - view.Position;

                if (Vector3.Dot(offset, offset) > reach * reach) {
                    continue;
                }
            }

            var outside = false;

            for (var i = 0; i < BoundingFrustum.PlaneCount; i++) {
                if (GpuCulling.Outside(view.Planes[i], centre, radius)) {
                    outside = true;
                    break;
                }
            }

            if (outside) {
                continue;
            }

            if (Backfacing(centre, radius, record.ConeAxis, record.ConeCosine, view.Position)) {
                continue;
            }

            if (occluder is not null
                && (view.Flags & GpuCulling.Occluders) != 0
                && GpuCulling.IsOccluded(centre, radius, view, occluderSize, occluder)) {
                continue;
            }

            // The error is projected at the *group's* bound and not at this cluster's, so every parent
            // of the group decides identically — see CullCluster.ErrorCenter. Measured to the surface
            // rather than to the centre: at the centre a large group's error reads smaller than it is
            // where anybody is looking, which is the silhouette.
            var pixels = ProjectedError(record, instance, view);

            if (pixels <= view.ErrorThreshold || record.ChildCount == 0 || overflowed) {
                Accept(cluster, record);
                continue;
            }

            if (!Refine(record)) {
                Accept(cluster, record);
            }
        }

        var accepted = visible.ToArray();
        Array.Sort(accepted);

        var wanted = requests.Select(page => (int)page).ToArray();
        Array.Sort(wanted);

        return new(accepted, wanted);

        void Accept(uint cluster, in CullCluster record) {
            if (!isResident(record.Page)) {
                requests.Add(record.Page);
                return;
            }

            visible.Add((int)cluster);
        }

        // All or none, which is the same rule the offline cut follows and for the same reason: the
        // boundary between a refined cluster and its unrefined neighbour was locked at one level and
        // simplified at the other, so refining half a group is a crack.
        bool Refine(in CullCluster record) {
            var ready = true;

            for (var i = 0u; i < record.ChildCount; i++) {
                var child = scene.Children[(int)(record.FirstChild + i)];
                var page = scene.Clusters[(int)(owner.FirstCluster + child)].Page;

                if (!isResident(page)) {
                    requests.Add(page);
                    ready = false;
                }
            }

            if (!ready) {
                return false;
            }

            if (queue.Count + record.ChildCount > QueueCapacity) {
                overflowed = true;
                return false;
            }

            for (var i = 0u; i < record.ChildCount; i++) {
                queue.Enqueue(scene.Children[(int)(record.FirstChild + i)]);
            }

            return true;
        }
    }

    /// <summary>
    ///     The cut a threshold names, found by asking every cluster about itself — the oracle the
    ///     traversal is checked against.
    /// </summary>
    /// <param name="scene">The flattened DAG.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="view">The view.</param>
    /// <returns>The clusters whose own error is under the threshold and whose parent's is not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scene" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         Brute force on purpose: a linear scan over every cluster, with no traversal, no
    ///         hierarchy and no rejection. That the two agree is the property phase 3 is judged on,
    ///         and it holds because a cluster's own error and its parent's bracket a half-open
    ///         interval of thresholds and the intervals along any path through the DAG tile the
    ///         number line exactly once.
    ///     </para>
    ///     <para>
    ///         It is the definition rather than a second implementation, which is why it does not
    ///         test the frustum, the cone or the pyramid: those <em>remove</em> clusters from the cut,
    ///         so a comparison against this one is only meaningful for a view that rejects nothing.
    ///         The tests arrange that, and then arrange the opposite separately.
    ///     </para>
    /// </remarks>
    public static int[] Cut(ClusterScene scene, in CullInstance instance, in CullView view) {
        ArgumentNullException.ThrowIfNull(scene);

        // One parent per cluster — any of them, because every parent of a group carries the group's
        // bound identically, which is the invariant that makes the whole scheme work. What the oracle
        // needs it for is the *parent's* error at the *parent's* group bound: that is the number the
        // parent itself would have compared, and comparing it at the child's bound instead is exactly
        // the mistake that made the two disagree the first time this was written.
        var parentOf = new int[scene.Clusters.Length];
        Array.Fill(parentOf, -1);

        for (var i = 0; i < scene.Clusters.Length; i++) {
            var record = scene.Clusters[i];

            for (var c = 0u; c < record.ChildCount; c++) {
                parentOf[scene.Children[(int)(record.FirstChild + c)]] = i;
            }
        }

        var cut = new List<int>();

        for (var i = 0u; i < instance.ClusterCount; i++) {
            var index = (int)(instance.FirstCluster + i);
            var record = scene.Clusters[index];

            var own = ProjectedError(record, instance, view);

            var parent = parentOf[index] < 0
                ? 3.4e38f
                : ProjectedError(
                    scene.Clusters[parentOf[index]] with { Error = record.ParentError },
                    instance,
                    view
                );

            if (own <= view.ErrorThreshold && view.ErrorThreshold < parent) {
                cut.Add((int)i);
            }
        }

        return [.. cut];
    }

    /// <summary>One cluster's own error in pixels, projected at its group's bound.</summary>
    /// <param name="record">The cluster.</param>
    /// <param name="instance">The instance it belongs to.</param>
    /// <param name="view">The view it is being projected for.</param>
    /// <remarks>
    ///     One function, called by the traversal and by the oracle, for the reason every shared
    ///     predicate in this file exists: the two are meant to agree, and two copies of the projection
    ///     is the one place they could differ for a reason that is not a defect.
    /// </remarks>
    public static float ProjectedError(in CullCluster record, in CullInstance instance, in CullView view) {
        var centre = instance.Position + (record.ErrorCenter * instance.Scale);
        var radius = record.ErrorRadius * instance.Scale;
        var distance = MathF.Max((centre - view.Position).Length() - radius, GpuCulling.ClipEpsilon);

        return PixelError(record.Error * instance.Scale, distance, view.ErrorScale);
    }
}
