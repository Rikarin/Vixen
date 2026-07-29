// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>One cluster: about a hundred and twenty-eight triangles, culled and streamed as a unit.</summary>
/// <remarks>
///     <para>
///         A range rather than its own arrays. The triangles of every cluster at every level live in
///         one pair of arrays on <see cref="MeshletMesh" />, because the thing that consumes them is
///         a page pool with one buffer per vertex layout — and a cluster that owned its arrays would
///         be a thousand allocations per mesh describing what two offsets describe.
///     </para>
///     <para>
///         <b><see cref="Error" /> and <see cref="ParentError" /> are what make a cut crack-free.</b>
///         A cluster is drawn when its own error is under the threshold and its parent's is not —
///         <c>Error ≤ t &lt; ParentError</c> — which is a decision each cluster takes alone, with no
///         knowledge of what its neighbours decided. That only works because the build guarantees a
///         parent's error is at least every child's: two clusters either side of a group boundary
///         cross the threshold at the same value, so a cut never takes the parent on one side and the
///         child on the other.
///     </para>
///     <para>
///         Both are object-space lengths, not pixels. The projection is the runtime's, because it
///         depends on the view — see <see cref="MeshletCut.PixelError" />, which is the arithmetic a
///         traversal shader will mirror.
///     </para>
/// </remarks>
[DataContract("Meshlet")]
public readonly record struct Meshlet {
    /// <summary>An empty cluster, for the serializer.</summary>
    public Meshlet() { }

    /// <summary>Where this cluster's entries start in <see cref="MeshletMesh.Vertices" />.</summary>
    public int VertexOffset { get; init; }

    /// <summary>How many distinct vertices it references.</summary>
    public int VertexCount { get; init; }

    /// <summary>Which triangle of <see cref="MeshletMesh.Triangles" /> is its first.</summary>
    /// <remarks>In triangles, not in bytes: the byte offset is three times this.</remarks>
    public int TriangleOffset { get; init; }

    /// <summary>How many triangles it holds.</summary>
    public int TriangleCount { get; init; }

    /// <summary>Everything it occupies, in the mesh's own space.</summary>
    /// <remarks>
    ///     The bind-pose bound for a skinned cluster, which is not the bound it occupies while it is
    ///     animating. Expanding it is the traversal's job and <see cref="FirstBone" /> is what it
    ///     expands by — see the remarks there.
    /// </remarks>
    public BoundingBox Bounds { get; init; }

    /// <summary>The axis of the cone every triangle's normal lies inside.</summary>
    /// <remarks>
    ///     Area-weighted, so a cluster with one large triangle and twenty slivers points where the
    ///     surface is rather than where the tessellation is.
    /// </remarks>
    public Vector3 ConeAxis { get; init; }

    /// <summary>The cosine of the cone's half-angle.</summary>
    /// <remarks>
    ///     <b>Negative means the cone is useless</b>, which is the honest answer for a cluster whose
    ///     normals span more than a hemisphere — a closed cap, a crumpled fold. A backface test
    ///     against such a cone can never reject, and a build that clamped this to something usable
    ///     would be a build that culls geometry that is facing the camera.
    /// </remarks>
    public float ConeCosine { get; init; }

    /// <summary>How far this cluster's surface may deviate from the original mesh, in object space.</summary>
    /// <remarks>
    ///     Zero at level zero, because level zero <em>is</em> the original mesh. Above that it is the
    ///     deviation the simplification that produced this cluster introduced, taken as the maximum
    ///     against every error already carried by the clusters it replaced.
    /// </remarks>
    public float Error { get; init; }

    /// <summary>The error of the group this cluster is simplified into, or infinity for a root.</summary>
    /// <remarks>
    ///     Infinity rather than a large number, and rather than a flag: a root is drawn whenever the
    ///     threshold is below its parent's error, and there is no threshold above infinity. The
    ///     comparison then needs no special case, which is one branch fewer in a traversal that runs
    ///     per cluster per view per frame.
    /// </remarks>
    public float ParentError { get; init; } = float.PositiveInfinity;

    /// <summary>How many simplifications lie between this cluster and the original mesh.</summary>
    public int Level { get; init; }

    /// <summary>The group this cluster is a member of — the one that simplified it away — or −1.</summary>
    public int Group { get; init; } = -1;

    /// <summary>The group whose simplification produced this cluster, or −1 at level zero.</summary>
    public int Source { get; init; } = -1;

    /// <summary>Which of the model's materials it is drawn with.</summary>
    public int MaterialIndex { get; init; }

    /// <summary>The lowest bone index any of its vertices is weighted to, or −1 if it is not skinned.</summary>
    /// <remarks>
    ///     <para>
    ///         Improvement 1 of <c>docs/virtualized-geometry.md</c>: skinning is designed in rather
    ///         than retrofitted, and this is the whole of what the cluster record has to carry for
    ///         it. A traversal expands <see cref="Bounds" /> by the motion of the bones in
    ///         <c>[FirstBone, FirstBone + BoneCount)</c> and everything downstream is unchanged.
    ///     </para>
    ///     <para>
    ///         A range and not a set, because a set is unbounded and a range is two integers. It is
    ///         only tight if bone indices are locality-ordered, which is a reordering the build does
    ///         not yet do — so today this is a correct bound that is looser than it will be, and
    ///         nothing that reads it has to change when that lands.
    ///     </para>
    /// </remarks>
    public int FirstBone { get; init; } = -1;

    /// <summary>How many consecutive bones the range covers, or zero if it is not skinned.</summary>
    public int BoneCount { get; init; }
}

/// <summary>The clusters that were simplified together, and what came out.</summary>
/// <remarks>
///     <para>
///         The edge of the DAG, and the reason the DAG is a DAG rather than a tree: a group's
///         simplification produces several clusters, and every one of them replaces <em>all</em> of
///         the group's children rather than some of them. A cut therefore refines a group at a time —
///         take all the parents out, put all the children in — which is what keeps it valid.
///     </para>
///     <para>
///         <see cref="Error" /> is one number for the whole group for the same reason. If two parents
///         out of one group carried different errors, a threshold between them would draw one parent
///         beside its own siblings' children, and the boundary those two share is exactly the one
///         that was locked to make them meet.
///     </para>
/// </remarks>
[DataContract("MeshletGroup")]
public sealed record MeshletGroup {
    /// <summary>The clusters simplified together.</summary>
    public int[] Children { get; init; } = [];

    /// <summary>The clusters the simplification produced. Empty if it failed to reduce anything.</summary>
    public int[] Parents { get; init; } = [];

    /// <summary>The deviation from the original mesh that drawing the parents rather than the children costs.</summary>
    public float Error { get; init; }

    /// <summary>Which level the children are at.</summary>
    public int Level { get; init; }
}

/// <summary>A mesh as a cluster DAG: every level of detail at once, and the cut that picks one.</summary>
/// <remarks>
///     <para>
///         What <see cref="MeshletBuilder" /> produces and what phase 2 pages into the device. The
///         indirection is two deep on purpose: <see cref="Triangles" /> holds one byte per corner
///         indexing a cluster's own vertex list, and <see cref="Vertices" /> maps that list into the
///         source mesh's vertex arrays. A cluster is therefore relocatable — its triangles say
///         nothing about where its vertices ended up — which is what a page pool that evicts needs.
///     </para>
///     <para>
///         <b>Vertices are the source mesh's, unchanged.</b> Simplification here only ever collapses
///         a vertex onto another vertex that already existed, so no level invents a position, a
///         normal or a skinning weight. That is a real constraint — a quadric's optimal placement is
///         a slightly better surface — and it buys three things worth more than that: attributes need
///         no interpolation and therefore cannot drift, a locked boundary is <em>bit-identical</em>
///         between a parent and its children rather than nearly so, and every level shares one vertex
///         buffer.
///     </para>
/// </remarks>
[DataContract("MeshletMesh")]
public sealed record MeshletMesh {
    /// <summary>Every cluster at every level, coarsest last.</summary>
    public Meshlet[] Meshlets { get; init; } = [];

    /// <summary>Every DAG edge.</summary>
    public MeshletGroup[] Groups { get; init; } = [];

    /// <summary>Each cluster's vertex list, concatenated: an index into the source mesh's vertices.</summary>
    public int[] Vertices { get; init; } = [];

    /// <summary>Three bytes per triangle, each an index into the owning cluster's vertex list.</summary>
    public byte[] Triangles { get; init; } = [];

    /// <summary>The clusters nothing simplified further, which is what a never-streamed object draws.</summary>
    public int[] Roots { get; init; } = [];

    /// <summary>How many levels there are, counting the original mesh as one.</summary>
    public int LevelCount { get; init; }

    /// <summary>The fallback mesh: three source-vertex indices per triangle.</summary>
    /// <remarks>
    ///     A cut through the DAG at <see cref="MeshletBuildSettings.FallbackTriangles" />, flattened
    ///     into an ordinary index buffer. This is what WebGL2 draws, what collision and the physics
    ///     cook read, and what anything the virtualized path does not reach falls back to — and it is
    ///     generated rather than authored so that it cannot disagree with the mesh it stands in for.
    /// </remarks>
    public int[] Fallback { get; init; } = [];

    /// <summary>How many triangles the whole DAG holds, across every level.</summary>
    public int TriangleCount => Triangles.Length / 3;

    /// <summary>The corners of one cluster's triangles, as indices into the source mesh's vertices.</summary>
    /// <param name="meshlet">The cluster.</param>
    /// <param name="indices">Where to write them. Three per triangle.</param>
    /// <exception cref="ArgumentException">The span is too small.</exception>
    public void GetTriangles(in Meshlet meshlet, Span<int> indices) {
        if (indices.Length < meshlet.TriangleCount * 3) {
            throw new ArgumentException("Too small to hold the cluster's triangles.", nameof(indices));
        }

        for (var corner = 0; corner < meshlet.TriangleCount * 3; corner++) {
            indices[corner] = Vertices[meshlet.VertexOffset + Triangles[(meshlet.TriangleOffset * 3) + corner]];
        }
    }
}
