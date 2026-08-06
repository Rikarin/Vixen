// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

namespace Vixen.Geometry.Remeshing;

/// <summary>One level of the vertex-cluster hierarchy: a graph, a field on it, and its constraints.</summary>
/// <remarks>
///     ⚠ <b>A graph and not a mesh, and it has to be.</b> A vertex cluster has no triangles — two
///     contracted vertices are one node with the union of their edges — so there is no
///     <see cref="ManifoldMesh" /> to build above level zero, no one-ring order and no triangle
///     normal. What survives contraction is exactly what the smoothing operator reads: a position, a
///     normal, a neighbour list and a weight per neighbour.
/// </remarks>
sealed class FieldLevel {
    /// <summary>Cluster centroids, or the mesh's own positions at level zero.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>Cluster normals, normalised.</summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>Where each node's neighbours start in <see cref="Neighbours" />; one longer than the node count.</summary>
    public required int[] Starts { get; init; }

    /// <summary>Every node's neighbours, ascending within each node.</summary>
    public required int[] Neighbours { get; init; }

    /// <summary>One weight per entry in <see cref="Neighbours" />.</summary>
    /// <remarks>
    ///     ⚠ <b>This is where docs/plan/41 § D6's second and third corrections live, and they are one
    ///     mechanism rather than two.</b> The energy is <c>Σ w·deviation</c>, so a heavy edge is one
    ///     the field may not turn across and a light edge is one where turning is cheap — and a
    ///     singularity is nothing but concentrated turning. Stiffening the edges along a feature
    ///     pushes singularities off it; softening the edges round a vertex with a large angle defect
    ///     pulls them onto it. § D6 describes a repulsion and an attraction; they are the same knob
    ///     with two signs.
    /// </remarks>
    public required float[] Weights { get; init; }

    /// <summary>Whether each node's cross is pinned rather than solved.</summary>
    public required bool[] Hard { get; init; }

    /// <summary>What it is pinned to, where it is.</summary>
    public required Vector3[] HardDirection { get; init; }

    /// <summary>The principal-curvature direction, where the anisotropy earned one.</summary>
    public required Vector3[] SoftDirection { get; init; }

    /// <summary>How hard the curvature pulls.</summary>
    public required float[] SoftWeight { get; init; }

    /// <summary>An artist's guide direction, where one runs.</summary>
    public required Vector3[] GuideDirection { get; init; }

    /// <summary>How hard the guide pulls — ZRemesher's Curves Strength.</summary>
    public required float[] GuideWeight { get; init; }

    /// <summary>Which coarser cluster each node belongs to, or empty at the coarsest level.</summary>
    public required int[] Parent { get; init; }

    /// <summary>Every node, grouped by colour and ascending within each group.</summary>
    public required int[] ColourOrder { get; init; }

    /// <summary>Where each colour's group starts; one longer than the colour count.</summary>
    public required int[] ColourStarts { get; init; }

    /// <summary>The field, which the sweep rewrites in place.</summary>
    public required Vector3[] Directions { get; init; }

    /// <summary>How many nodes there are.</summary>
    public int Count => Positions.Length;

    /// <summary>How many colours the greedy colouring needed.</summary>
    public int Colours => ColourStarts.Length - 1;

    /// <summary>One Gauss–Seidel update at one node. Writes <see cref="Directions" /> at that index and nowhere else.</summary>
    /// <param name="node">Which node.</param>
    /// <remarks>
    ///     ⚠ <b>Each contribution is aligned to the running sum rather than to the node's current
    ///     value, and the accumulation order is the neighbour list's own.</b> Aligning to the current
    ///     value converges slower and is worse near a singularity, where the current value is the
    ///     thing that is wrong. Aligning to the running sum is order-dependent by construction — which
    ///     is fine and is <i>why</i> the neighbour list is sorted and the reduction is in its order:
    ///     the order is fixed, so the answer is.
    /// </remarks>
    public void Relax(int node) {
        if (Hard[node]) {
            Directions[node] = HardDirection[node];

            return;
        }

        var normal = Normals[node];
        var sum = Directions[node];

        if (normal.LengthSquared() <= 0f || sum.LengthSquared() <= 0f) {
            return;
        }

        for (var at = Starts[node]; at < Starts[node + 1]; at++) {
            var other = Neighbours[at];
            var transported = CrossField.Transport(Directions[other], Normals[other], normal);

            if (transported.LengthSquared() <= 0f) {
                continue;
            }

            sum += CrossField.Align(transported, sum, normal) * Weights[at];
        }

        if (SoftWeight[node] > 0f && SoftDirection[node].LengthSquared() > 0f) {
            sum += CrossField.Align(SoftDirection[node], sum, normal) * SoftWeight[node];
        }

        if (GuideWeight[node] > 0f && GuideDirection[node].LengthSquared() > 0f) {
            sum += CrossField.Align(GuideDirection[node], sum, normal) * GuideWeight[node];
        }

        var flat = ScaleSafe.Flatten(sum, normal);

        if (flat.LengthSquared() > 0f) {
            Directions[node] = flat;
        }
    }
}

/// <summary>The hierarchical, deterministic 4-RoSy solve.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D5 and § D14.</b> A local smoothing operator rather than a global solve,
///         which is what buys linear time; a vertex-cluster hierarchy, without which the smoothing
///         propagates one ring per iteration and a large mesh never converges; and four decisions that
///         exist only so the answer is the same bytes twice — a geometric initialization, a
///         deterministic graph colouring, a fixed iteration count, and every reduction in index order.
///     </para>
///     <para>
///         ⚠ <b>§ B3 is why, and it is the single largest constraint on this design.</b> Doc 08 caches
///         compiled assets on a content hash and doc 22's crack-freedom is an equality over shared
///         boundary vertices, so a remesher whose output differs run to run makes every one of those
///         rebuild-unstable. That rules out Instant Meshes' randomized initialization and its unordered
///         parallel Gauss–Seidel <i>verbatim</i>, and everything below is the replacement.
///     </para>
///     <para>
///         ⚠ <b>It runs on <c>JobScheduler</c> and not on the GPU, and § D17 records that as a
///         decision rather than an omission.</b> A local operator over a colouring on millions of
///         vertices is textbook GPU work; bit-exact float reduction across drivers and vendors is not
///         achievable, and this is an import-time cost rather than a frame cost.
///     </para>
/// </remarks>
static class CrossFieldSolver {
    /// <summary>The hierarchy stops coarsening at this many nodes.</summary>
    /// <remarks>
    ///     § D5: "the coarse level fixes the global structure in a few hundred elements". Sixty-four
    ///     nodes is a graph whose diameter a fixed iteration count crosses several times over, which
    ///     is the property that matters — not the count itself.
    /// </remarks>
    public const int CoarsestCount = 64;

    /// <summary>How many levels are built before coarsening gives up, whatever the node count.</summary>
    public const int MaxLevels = 40;

    /// <summary>How hard a fully anisotropic vertex pulls the cross, against a neighbour weight of one.</summary>
    public const float CurvatureWeight = 3f;

    /// <summary>How hard a guide at full strength pulls.</summary>
    /// <remarks>Heavier than the curvature, because a guide is the artist saying so and curvature is
    ///     the surface being asked.</remarks>
    public const float GuideWeight = 8f;

    /// <summary>The anisotropy at which the curvature pull is already at full weight.</summary>
    /// <remarks>
    ///     Dimensionless: <see cref="CurvatureField.Anisotropy" /> is <c>|κ₁ − κ₂|</c> times the
    ///     bounding-box diagonal, so a cylinder whose radius is a fraction of its own diagonal is well
    ///     past one and a sphere is at zero however large it is.
    /// </remarks>
    public const float AnisotropyReference = 1f;

    /// <summary>Solves the field over a conditioned surface.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="settings">Adaptivity, guide strength and the fixed iteration count.</param>
    /// <param name="features">The hard and soft polyline constraints from § D4.</param>
    /// <param name="curvature">The principal directions and their anisotropy.</param>
    /// <param name="scheduler">The jobs to sweep on, or null to sweep on the calling thread.</param>
    /// <param name="batchSize">How many nodes one work item covers, or zero to let the scheduler choose.</param>
    /// <param name="stiffness">A per-vertex edge-weight multiplier, or null for one everywhere.</param>
    /// <param name="hierarchical">
    ///     Whether to coarsen. ⚠ False is not a setting — it exists so the tests can show what the
    ///     hierarchy buys, and it is what § D5 says a large mesh never converges without.
    /// </param>
    /// <returns>The field.</returns>
    public static CrossField Solve(
        ManifoldMesh mesh,
        RemeshSettings settings,
        FeatureGraph features,
        CurvatureField curvature,
        JobScheduler? scheduler = null,
        int batchSize = 0,
        float[]? stiffness = null,
        bool hierarchical = true
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(curvature);

        if (mesh.VertexCount == 0) {
            return new([]);
        }

        var levels = new List<FieldLevel> { Base(mesh, settings, features, curvature, stiffness) };

        if (hierarchical) {
            while (levels.Count < MaxLevels && levels[^1].Count > CoarsestCount) {
                var coarser = Coarsen(levels[^1]);

                if (coarser is null) {
                    break;
                }

                levels.Add(coarser);
            }
        }

        var iterations = Math.Max(settings.FieldIterations, 0);

        Initialize(levels[^1]);

        for (var level = levels.Count - 1; level >= 0; level--) {
            if (level < levels.Count - 1) {
                Prolong(levels[level], levels[level + 1]);
            }

            Sweep(levels[level], iterations, scheduler, batchSize);
        }

        return new(levels[0].Directions);
    }

    /// <summary>Level zero, straight off the mesh.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="settings">Adaptivity, guide strength and the rest.</param>
    /// <param name="features">The § D4 constraints.</param>
    /// <param name="curvature">The principal directions and their anisotropy.</param>
    /// <param name="stiffness">A per-vertex edge-weight multiplier, or null for one everywhere.</param>
    /// <param name="directions">
    ///     The array the sweep writes into, or null for a fresh one. ⚠ Passing one in is how
    ///     <see cref="SingularityPass" /> re-weights the same field in place rather than solving a
    ///     second one and copying it back.
    /// </param>
    /// <returns>The level.</returns>
    public static FieldLevel Base(
        ManifoldMesh mesh,
        RemeshSettings settings,
        FeatureGraph features,
        CurvatureField curvature,
        float[]? stiffness,
        Vector3[]? directions = null
    ) {
        var count = mesh.VertexCount;
        var (starts, neighbours) = Adjacency(mesh);
        var weights = new float[neighbours.Length];

        for (var vertex = 0; vertex < count; vertex++) {
            for (var at = starts[vertex]; at < starts[vertex + 1]; at++) {
                // Symmetric by construction, so the same edge carries the same weight seen from
                // either end — an asymmetric weight is an energy that is not a sum over edges, and a
                // Gauss–Seidel on one does not descend anything.
                weights[at] = Stiffness(stiffness, vertex) * Stiffness(stiffness, neighbours[at]);
            }
        }

        var hard = new bool[count];
        var hardDirection = new Vector3[count];
        var soft = new Vector3[count];
        var softWeight = new float[count];
        var guide = new Vector3[count];
        var guideWeight = new float[count];

        var adaptivity = Math.Clamp(settings.Adaptivity, 0f, 1f);

        for (var vertex = 0; vertex < count; vertex++) {
            var normal = mesh.VertexNormal(vertex);

            if (normal.LengthSquared() <= 0f) {
                continue;
            }

            if (features.IsFeatureVertex(vertex) && features.Tangent(vertex).LengthSquared() > 0f) {
                if (features.IsHardVertex(vertex)) {
                    hard[vertex] = true;
                    hardDirection[vertex] = features.Tangent(vertex);
                } else {
                    guide[vertex] = features.Tangent(vertex);
                    guideWeight[vertex] = GuideWeight * Strength(features, mesh, vertex);
                }
            }

            // ⚠ Anisotropy and never magnitude. § D5: on a sphere the two principal curvatures are
            // equal, this is zero, and the field is left free to be smooth — which is the correct
            // answer and the one a naive curvature alignment gets wrong by chasing noise.
            var pull = adaptivity * MathF.Min(1f, curvature.Anisotropy(vertex) / AnisotropyReference);

            if (pull > 0f && curvature.Direction(vertex).LengthSquared() > 0f) {
                soft[vertex] = curvature.Direction(vertex);
                softWeight[vertex] = CurvatureWeight * pull;
            }
        }

        var (order, colourStarts) = Colour(count, starts, neighbours);

        return new() {
            Positions = [.. mesh.Positions],
            Normals = Normals(mesh),
            Starts = starts,
            Neighbours = neighbours,
            Weights = weights,
            Hard = hard,
            HardDirection = hardDirection,
            SoftDirection = soft,
            SoftWeight = softWeight,
            GuideDirection = guide,
            GuideWeight = guideWeight,
            Parent = [],
            ColourOrder = order,
            ColourStarts = colourStarts,
            Directions = directions ?? new Vector3[count]
        };
    }

    static float Stiffness(float[]? stiffness, int vertex) => stiffness is null ? 1f : stiffness[vertex];

    static float Strength(FeatureGraph features, ManifoldMesh mesh, int vertex) {
        var softest = 1f;

        foreach (var half in mesh.Outgoing(vertex)) {
            if (features.IsFeatureEdge(half)) {
                softest = MathF.Min(softest, features.EdgeStrength(half));
            }
        }

        return softest;
    }

    static Vector3[] Normals(ManifoldMesh mesh) {
        var normals = new Vector3[mesh.VertexCount];

        for (var vertex = 0; vertex < normals.Length; vertex++) {
            normals[vertex] = mesh.VertexNormal(vertex);
        }

        return normals;
    }

    /// <summary>The symmetric vertex adjacency, ascending within each vertex.</summary>
    /// <remarks>
    ///     ⚠ Built from the half-edges rather than from <see cref="ManifoldMesh.Ring" />, because the
    ///     ring of a vertex whose fans do not agree can be short — the traversal guards against a
    ///     bowtie by stopping — and an asymmetric adjacency makes the energy asymmetric. Two passes
    ///     over the half-edges and a sort cost nothing at this size.
    /// </remarks>
    static (int[] Starts, int[] Neighbours) Adjacency(ManifoldMesh mesh) {
        var count = mesh.VertexCount;
        var lists = new List<int>[count];

        for (var vertex = 0; vertex < count; vertex++) {
            lists[vertex] = [];
        }

        for (var half = 0; half < mesh.Triangles.Length; half++) {
            var from = mesh.Triangles[half];
            var to = mesh.Triangles[ManifoldMesh.Next(half)];

            if (from == to) {
                continue;
            }

            lists[from].Add(to);
            lists[to].Add(from);
        }

        var starts = new int[count + 1];
        var flat = new List<int>();

        for (var vertex = 0; vertex < count; vertex++) {
            starts[vertex] = flat.Count;

            var list = lists[vertex];

            list.Sort();

            for (var at = 0; at < list.Count; at++) {
                if (at == 0 || list[at] != list[at - 1]) {
                    flat.Add(list[at]);
                }
            }
        }

        starts[count] = flat.Count;

        return (starts, [.. flat]);
    }

    /// <summary>Greedy graph colouring by ascending index, so one colour's nodes are independent.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what replaces Instant Meshes' unordered parallel Gauss–Seidel.</b> Two nodes
    ///     of the same colour share no edge, so neither reads what the other writes and the sweep over
    ///     one colour can run on any number of threads in any order without changing a bit. What is
    ///     <i>not</i> allowed is a colouring that depends on the order the nodes were visited in — so
    ///     it is ascending index, always, and never a heuristic that reads a degree.
    /// </remarks>
    static (int[] Order, int[] Starts) Colour(int count, int[] starts, int[] neighbours) {
        var colours = new int[count];
        var taken = new bool[16];

        Array.Fill(colours, -1);

        var used = 0;

        for (var node = 0; node < count; node++) {
            var degree = starts[node + 1] - starts[node];

            if (taken.Length < degree + 1) {
                taken = new bool[Math.Max(degree + 1, taken.Length * 2)];
            }

            Array.Clear(taken);

            for (var at = starts[node]; at < starts[node + 1]; at++) {
                var colour = colours[neighbours[at]];

                if (colour >= 0 && colour < taken.Length) {
                    taken[colour] = true;
                }
            }

            var pick = 0;

            while (pick < taken.Length && taken[pick]) {
                pick++;
            }

            colours[node] = pick;
            used = Math.Max(used, pick + 1);
        }

        var groupStarts = new int[used + 1];

        foreach (var colour in colours) {
            groupStarts[colour + 1]++;
        }

        for (var colour = 1; colour <= used; colour++) {
            groupStarts[colour] += groupStarts[colour - 1];
        }

        var order = new int[count];
        var cursor = new int[used];

        for (var node = 0; node < count; node++) {
            order[groupStarts[colours[node]] + cursor[colours[node]]] = node;
            cursor[colours[node]]++;
        }

        return (order, groupStarts);
    }

    /// <summary>Greedy edge contraction into a coarser graph, or null when nothing contracted.</summary>
    /// <remarks>
    ///     ⚠ <b>Ascending by node, and the partner is the shortest unmatched edge with the lowest
    ///     index breaking a tie.</b> Every published matching heuristic worth using picks by degree or
    ///     by a random order; both make the hierarchy — and therefore the answer — depend on something
    ///     that is not the input.
    /// </remarks>
    static FieldLevel? Coarsen(FieldLevel fine) {
        var parent = new int[fine.Count];
        var clusters = 0;

        Array.Fill(parent, -1);

        for (var node = 0; node < fine.Count; node++) {
            if (parent[node] >= 0) {
                continue;
            }

            var partner = -1;
            var shortest = float.MaxValue;

            for (var at = fine.Starts[node]; at < fine.Starts[node + 1]; at++) {
                var other = fine.Neighbours[at];

                if (other == node || parent[other] >= 0) {
                    continue;
                }

                var length = (fine.Positions[other] - fine.Positions[node]).LengthSquared();

                if (length < shortest || (length == shortest && other < partner)) {
                    shortest = length;
                    partner = other;
                }
            }

            parent[node] = clusters;

            if (partner >= 0) {
                parent[partner] = clusters;
            }

            clusters++;
        }

        if (clusters >= fine.Count) {
            return null;
        }

        var positions = new Vector3[clusters];
        var normals = new Vector3[clusters];
        var members = new int[clusters];
        var hard = new bool[clusters];
        var hardDirection = new Vector3[clusters];
        var soft = new Vector3[clusters];
        var softWeight = new float[clusters];
        var guide = new Vector3[clusters];
        var guideWeight = new float[clusters];

        // Ascending over the fine nodes, so every sum is in index order and the "lowest-index
        // constrained member wins" rule below is a consequence of the loop rather than a search.
        for (var node = 0; node < fine.Count; node++) {
            var cluster = parent[node];

            positions[cluster] += fine.Positions[node];
            normals[cluster] += fine.Normals[node];
            members[cluster]++;

            if (fine.Hard[node] && !hard[cluster]) {
                hard[cluster] = true;
                hardDirection[cluster] = fine.HardDirection[node];
            }

            if (fine.SoftWeight[node] > softWeight[cluster]) {
                softWeight[cluster] = fine.SoftWeight[node];
                soft[cluster] = fine.SoftDirection[node];
            }

            if (fine.GuideWeight[node] > guideWeight[cluster]) {
                guideWeight[cluster] = fine.GuideWeight[node];
                guide[cluster] = fine.GuideDirection[node];
            }
        }

        for (var cluster = 0; cluster < clusters; cluster++) {
            positions[cluster] /= members[cluster];

            normals[cluster] = ScaleSafe.Unit(normals[cluster]);

            hardDirection[cluster] = Flatten(hardDirection[cluster], normals[cluster]);
            soft[cluster] = Flatten(soft[cluster], normals[cluster]);
            guide[cluster] = Flatten(guide[cluster], normals[cluster]);
        }

        var lists = new List<(int Node, float Weight)>[clusters];

        for (var cluster = 0; cluster < clusters; cluster++) {
            lists[cluster] = [];
        }

        for (var node = 0; node < fine.Count; node++) {
            for (var at = fine.Starts[node]; at < fine.Starts[node + 1]; at++) {
                var one = parent[node];
                var two = parent[fine.Neighbours[at]];

                if (one != two) {
                    lists[one].Add((two, fine.Weights[at]));
                }
            }
        }

        var starts = new int[clusters + 1];
        var flat = new List<int>();
        var weights = new List<float>();

        for (var cluster = 0; cluster < clusters; cluster++) {
            starts[cluster] = flat.Count;

            var list = lists[cluster];

            list.Sort((one, two) => one.Node.CompareTo(two.Node));

            for (var at = 0; at < list.Count; at++) {
                if (at > 0 && list[at].Node == list[at - 1].Node) {
                    weights[^1] += list[at].Weight;

                    continue;
                }

                flat.Add(list[at].Node);
                weights.Add(list[at].Weight);
            }
        }

        starts[clusters] = flat.Count;

        var neighbours = flat.ToArray();
        var (order, colourStarts) = Colour(clusters, starts, neighbours);

        return new() {
            Positions = positions,
            Normals = normals,
            Starts = starts,
            Neighbours = neighbours,
            Weights = [.. weights],
            Hard = hard,
            HardDirection = hardDirection,
            SoftDirection = soft,
            SoftWeight = softWeight,
            GuideDirection = guide,
            GuideWeight = guideWeight,
            Parent = parent,
            ColourOrder = order,
            ColourStarts = colourStarts,
            Directions = new Vector3[clusters]
        };
    }

    static Vector3 Flatten(Vector3 direction, Vector3 normal) => ScaleSafe.Flatten(direction, normal);

    /// <summary>The geometric initialization § D5 puts in place of a random one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Derived from the vertex's own position, tie-broken by index — and neither the
    ///         clock nor a seed appears anywhere.</b> § D5 states it as a determinism requirement, and
    ///         it is also the reason the hierarchy is not optional: the axis a vertex picks changes
    ///         discontinuously across the octant boundaries of its own position, so the starting field
    ///         is piecewise and torn, and closing those tears is a global rearrangement rather than a
    ///         local one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Comparing <i>magnitudes</i> of coordinates is scale-free; comparing a coordinate
    ///         against a constant would not be.</b> Every coordinate scales by the same factor, so
    ///         which of the three is largest does not move.
    ///     </para>
    /// </remarks>
    static void Initialize(FieldLevel level) {
        for (var node = 0; node < level.Count; node++) {
            if (level.Hard[node]) {
                level.Directions[node] = level.HardDirection[node];

                continue;
            }

            var normal = level.Normals[node];

            if (normal.LengthSquared() <= 0f) {
                continue;
            }

            var position = level.Positions[node];

            Span<float> magnitude = [MathF.Abs(position.X), MathF.Abs(position.Y), MathF.Abs(position.Z)];

            var first = 0;

            for (var axis = 1; axis < 3; axis++) {
                if (magnitude[axis] > magnitude[first]) {
                    first = axis;
                }
            }

            // The tie-break. Two coordinates of exactly equal magnitude are common — an axis-aligned
            // primitive is full of them — and the winner has to be the same on every machine without
            // being the same for every vertex, or a whole face of a box starts from one direction.
            if (magnitude[(first + 1) % 3] == magnitude[first] || magnitude[(first + 2) % 3] == magnitude[first]) {
                var rotated = (first + (node % 3)) % 3;

                if (magnitude[rotated] == magnitude[first]) {
                    first = rotated;
                }
            }

            for (var attempt = 0; attempt < 3; attempt++) {
                var axis = (first + attempt) % 3;
                var direction = Flatten(Axis(axis), normal);

                if (direction.LengthSquared() > 0f) {
                    level.Directions[node] = direction;

                    break;
                }
            }
        }
    }

    static Vector3 Axis(int index) => index switch {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ
    };

    /// <summary>Copies a coarse solution down onto the level it was contracted from.</summary>
    static void Prolong(FieldLevel fine, FieldLevel coarse) {
        for (var node = 0; node < fine.Count; node++) {
            if (fine.Hard[node]) {
                fine.Directions[node] = fine.HardDirection[node];

                continue;
            }

            // ⚠ `Parent` lives on the coarse level and is indexed by the <i>fine</i> node, because
            // that is the direction the map is used in — a prolongation is a gather and never a
            // scatter, so two fine nodes reading one cluster is the ordinary case.
            var direction = Flatten(coarse.Directions[coarse.Parent[node]], fine.Normals[node]);

            if (direction.LengthSquared() > 0f) {
                fine.Directions[node] = direction;
            }
        }
    }

    /// <summary>A fixed number of colour-ordered Gauss–Seidel sweeps.</summary>
    /// <remarks>
    ///     ⚠ <b>A count and not a residual, which § D14 names as one of its four choices.</b> A
    ///     stopping rule that reads a convergence tolerance is a floating-point comparison, and a
    ///     floating-point comparison is exactly the thing that can land differently on two machines —
    ///     so a mesh that took nineteen iterations on the build server and twenty on a developer's is
    ///     a mesh with two different sets of meshlet pages.
    /// </remarks>
    static void Sweep(FieldLevel level, int iterations, JobScheduler? scheduler, int batchSize) {
        for (var iteration = 0; iteration < iterations; iteration++) {
            for (var colour = 0; colour < level.Colours; colour++) {
                var start = level.ColourStarts[colour];
                var length = level.ColourStarts[colour + 1] - start;

                if (length == 0) {
                    continue;
                }

                if (scheduler is null) {
                    for (var at = 0; at < length; at++) {
                        level.Relax(level.ColourOrder[start + at]);
                    }

                    continue;
                }

                scheduler.ParallelFor(new SmoothJob { Level = level, Start = start }, length, batchSize);
            }
        }
    }

    /// <summary>One colour's worth of relaxation, one node per index.</summary>
    /// <remarks>
    ///     ⚠ Writes <c>Directions[node]</c> and nothing else, which is the contract
    ///     <see cref="IJobParallelFor" /> states and the property one colour of a colouring provides.
    /// </remarks>
    readonly struct SmoothJob : IJobParallelFor {
        /// <summary>The level being swept.</summary>
        public required FieldLevel Level { get; init; }

        /// <summary>Where this colour's nodes start in the level's colour order.</summary>
        public required int Start { get; init; }

        /// <inheritdoc />
        public void Execute(int index) => Level.Relax(Level.ColourOrder[Start + index]);
    }
}
