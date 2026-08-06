// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Which of docs/plan/41 § D4's five sources put an edge into the feature set.</summary>
/// <remarks>
///     A flag set rather than a single value, because an edge is very often several at once — a
///     boolean result's seam is a dihedral angle <i>and</i> a group boundary <i>and</i> a UV seam, and
///     which of the three a caller turned off decides whether it survives.
/// </remarks>
[Flags]
enum FeatureSource : byte {
    /// <summary>Not a feature.</summary>
    None = 0,

    /// <summary>The dihedral angle across the edge exceeds <see cref="RemeshSettings.FeatureAngle" />.</summary>
    Dihedral = 1,

    /// <summary>An explicit crease came in on a curve — a smoothing-group boundary, or an importer's.</summary>
    Crease = 2,

    /// <summary>The two triangles are in different <see cref="MeshFace.Group" />s — ZRemesher's Keep Groups.</summary>
    Group = 4,

    /// <summary>The source's texture coordinates are discontinuous across the edge.</summary>
    UvSeam = 8,

    /// <summary>An artist's guide curve runs along it.</summary>
    Guide = 16,

    /// <summary>The edge has one triangle rather than two.</summary>
    Boundary = 32
}

/// <summary>A polyline handed <i>in</i> to detection, in the mesh's own space.</summary>
/// <param name="Points">The curve. Two points is one segment; fewer is nothing.</param>
/// <param name="Source">What it is — a crease, a seam or a guide.</param>
/// <param name="Strength">How hard the field is pulled toward it, in <c>[0, 1]</c>. Ignored for hard sources.</param>
/// <remarks>
///     ⚠ <b>Three of docs/plan/41 § D4's five sources are the same shape, and unifying them is what
///     lets conditioning renumber the mesh without losing them.</b> Creases, UV seams and guides all
///     live on the <i>source</i> mesh, and stage one welds, cuts, de-specks and re-meshes it — so an
///     edge index or a vertex index taken before conditioning means nothing after it. A polyline in
///     world space survives all of it, and resolving one onto the conditioned mesh is one routine
///     rather than three.
/// </remarks>
readonly record struct FeatureCurve(IReadOnlyList<Vector3> Points, FeatureSource Source, float Strength);

/// <summary>One chained run of feature edges: a chain of vertex indices into the manifold view.</summary>
/// <param name="Vertices">
///     The chain, in order. Consecutive entries are an edge of the mesh, exactly — never a chord.
/// </param>
/// <param name="Keys">
///     Indices <i>into <see cref="Vertices" /></i> that survive the straight-chain simplification.
///     Always contains the first and the last.
/// </param>
/// <param name="Sources">The union of what put the chain's edges into the set.</param>
/// <param name="Strength">The softest strength any of its edges carried, or one for a hard chain.</param>
/// <param name="IsClosed">Whether the last vertex is the first — a loop with no corner on it.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The chain keeps every vertex it passes through, and the simplification is
///         <see cref="Keys" /> beside it rather than a shorter chain.</b> docs/plan/41 § D4 asks for
///         two things at once: nearly-straight chains are simplified, and a feature polyline is a
///         chain of output edges <i>by construction</i>, asserted at a tolerance of exact. Dropping a
///         collinear interior vertex satisfies the first and breaks the second — the layout would be
///         free to span the whole simplified run with one enormous quad edge, which is the
///         approximated hard edge § D4 exists to rule out. So the run stays whole and the
///         simplification is advice: <see cref="Keys" /> is where a patch corner may go, and every
///         vertex between two keys is a point the boundary still has to pass through.
///     </para>
///     <para>
///         ⚠ <b>A closed chain repeats its first vertex as its last.</b> That makes
///         <c>Vertices[i], Vertices[i + 1]</c> an edge for every <c>i</c> without a modulus, which is
///         what every consumer wants; the cost is that a loop of <i>n</i> edges has <i>n + 1</i>
///         entries.
///     </para>
/// </remarks>
readonly record struct FeaturePolyline(
    int[] Vertices,
    int[] Keys,
    FeatureSource Sources,
    float Strength,
    bool IsClosed
) {
    /// <summary>How many edges the chain has.</summary>
    public int EdgeCount => Vertices.Length - 1;

    /// <summary>Whether this chain constrains the field hard, or only pulls at <see cref="Strength" />.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/41 contradicts itself here and this property is the resolution.</b> § D4
    ///     lists guide curves as the fifth feature source, unioned with the other four; § D5 then says
    ///     feature polylines fix the cross <b>hard</b> and guide curves fix it <b>softly, at the
    ///     user's strength</b>. Both cannot be true of one set. A guide is therefore detected,
    ///     chained, pruned and simplified exactly as the other four are — it is a feature — and it is
    ///     the one kind whose constraint is soft. A guide that is also a dihedral angle is hard,
    ///     because the flag set unions and the hard sources win.
    /// </remarks>
    public bool IsHard => (Sources & ~FeatureSource.Guide) != FeatureSource.None;
}

/// <summary>The feature set, chained, pruned and simplified — what the field is constrained by and what the layout is bounded by.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D4.</b> Five sources unioned into an edge set, chained into polylines,
///         endpoints and junctions promoted to corners, short chains pruned and nearly-straight ones
///         simplified. Every query here is an array lookup: the field solver asks
///         <see cref="IsHardVertex" /> once per vertex per sweep and the layout asks
///         <see cref="IsFeatureEdge" /> once per half-edge per trace, so neither may be a search.
///     </para>
/// </remarks>
sealed class FeatureGraph {
    readonly FeatureSource[] halfSources;
    readonly FeatureSource[] vertexSources;
    readonly float[] halfStrength;
    readonly Vector3[] tangents;
    readonly bool[] corner;
    readonly int[] degree;

    internal FeatureGraph(
        FeatureSource[] halfSources,
        FeatureSource[] vertexSources,
        float[] halfStrength,
        Vector3[] tangents,
        bool[] corner,
        int[] degree,
        FeaturePolyline[] polylines,
        int[] corners,
        int prunedChains,
        int prunedEdges
    ) {
        this.halfSources = halfSources;
        this.vertexSources = vertexSources;
        this.halfStrength = halfStrength;
        this.tangents = tangents;
        this.corner = corner;
        this.degree = degree;

        Polylines = polylines;
        Corners = corners;
        PrunedChains = prunedChains;
        PrunedEdges = prunedEdges;
    }

    /// <summary>Every surviving chain, in a fixed order.</summary>
    /// <remarks>Ordered by their lowest vertex index, so two runs list them the same way.</remarks>
    public IReadOnlyList<FeaturePolyline> Polylines { get; }

    /// <summary>Every endpoint and junction, ascending.</summary>
    public IReadOnlyList<int> Corners { get; }

    /// <summary>How many chains the prune threw away.</summary>
    public int PrunedChains { get; }

    /// <summary>How many edges went with them.</summary>
    /// <remarks>
    ///     ⚠ <b>A pruned chain leaves the edge set as well as the polyline list.</b> Leaving the edges
    ///     marked would keep every one of them a hard constraint on the field and a candidate boundary
    ///     for the layout, which is the whole cost the prune exists to avoid — and it would make
    ///     "zero singularities on features" a claim about voxel noise.
    /// </remarks>
    public int PrunedEdges { get; }

    /// <summary>Whether a half-edge is a feature.</summary>
    /// <param name="half">The half-edge index.</param>
    /// <returns>Whether it survived detection and pruning. Both halves of an edge agree.</returns>
    public bool IsFeatureEdge(int half) => halfSources[half] != FeatureSource.None;

    /// <summary>What put a half-edge into the set.</summary>
    /// <param name="half">The half-edge index.</param>
    /// <returns>The flags, or <see cref="FeatureSource.None" />.</returns>
    public FeatureSource EdgeSource(int half) => halfSources[half];

    /// <summary>How hard a half-edge pulls, in <c>[0, 1]</c>.</summary>
    /// <param name="half">The half-edge index.</param>
    /// <returns>One for a hard edge; the guide's own strength for a soft one; zero for a non-feature.</returns>
    public float EdgeStrength(int half) => halfStrength[half];

    /// <summary>Whether a vertex has any feature edge at it.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>Whether it lies on a chain.</returns>
    public bool IsFeatureVertex(int vertex) => vertexSources[vertex] != FeatureSource.None;

    /// <summary>Whether a vertex is hard-constrained — on a chain that is not purely a guide.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>Whether the cross there is pinned rather than pulled.</returns>
    public bool IsHardVertex(int vertex) => (vertexSources[vertex] & ~FeatureSource.Guide) != FeatureSource.None;

    /// <summary>Whether a vertex is an endpoint or a junction.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>Whether its feature degree is anything other than two.</returns>
    /// <remarks>
    ///     ⚠ <b>A corner is where a singularity <i>belongs</i>, and that is not the same statement as
    ///     § D6's repulsion.</b> § D6 step two pushes singularities off feature <i>lines</i> because a
    ///     singularity on a hard edge is a visible pinch; § D6 step three attracts them to where the
    ///     surface is not developable, and names "the corner of a box" as an example. A box's corner
    ///     is a feature corner. Reading step two as "off every feature vertex" makes a cube
    ///     impossible — every one of its vertices is on a feature and its eight quarter turns have to
    ///     live somewhere — so the criterion is zero singularities on the <i>interiors</i> of chains,
    ///     with corners exempt. See <see cref="SingularityPass" />.
    /// </remarks>
    public bool IsCorner(int vertex) => corner[vertex];

    /// <summary>How many feature edges meet at a vertex.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>Zero off a feature, two along a chain, one at an end, three or more at a junction.</returns>
    public int Degree(int vertex) => degree[vertex];

    /// <summary>The direction the cross is pinned to at a feature vertex.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>A unit direction in the vertex's tangent plane, or zero where there is no constraint.</returns>
    /// <remarks>
    ///     ⚠ <b>At a junction the choice is arbitrary and it does not matter, which is a property of
    ///     4-RoSy rather than luck.</b> A cross stands for four directions ninety degrees apart, so a
    ///     junction of two perpendicular features is satisfied by <i>either</i> of them. Where they
    ///     are not perpendicular no single direction satisfies both, and the lowest-index incident
    ///     feature neighbour is taken — deterministic, and the residual is what the smoothing spreads.
    /// </remarks>
    public Vector3 Tangent(int vertex) => tangents[vertex];
}
