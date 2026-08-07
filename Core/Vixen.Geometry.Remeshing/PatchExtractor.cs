// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>One patch's grid, kept because docs/plan/41 § D13's atlas is a statement about it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A quantized quad patch <i>is</i> a rectangle, and this is the rectangle.</b> § D13:
///         the layout is already a chart decomposition with zero in-chart distortion, so the atlas
///         comes off it directly rather than from re-cutting the output with a general charter —
///         which doc 42 § D2 keeps here for exactly that reason while moving the packing out. Losing
///         the grid at the end of extraction and recovering it afterwards from the faces would mean
///         inferring what was known for certain.
///     </para>
///     <para>
///         ⚠ <b>The faces of one patch are contiguous and <see cref="FirstFace" /> is where they
///         start</b>, because <see cref="PatchExtractor" /> adds them in one nested loop and nothing
///         reorders them afterwards. A patch that was skipped contributes no grid at all, so the
///         index is not the patch's own.
///     </para>
/// </remarks>
sealed class PatchGrid {
    /// <summary>Which patch of the layout it came from.</summary>
    public required int Patch { get; init; }

    /// <summary>How many quads across.</summary>
    public required int Wide { get; init; }

    /// <summary>How many quads up.</summary>
    public required int Tall { get; init; }

    /// <summary>The output positions, indexed <c>[i][j]</c> over <c>[0, Wide] × [0, Tall]</c>.</summary>
    public required int[][] Vertices { get; init; }

    /// <summary>Where this patch's run of output faces begins.</summary>
    public required int FirstFace { get; init; }

    /// <summary>The four boundary chains, in output positions, as the sides were walked.</summary>
    /// <remarks>Side 0 runs C0 → C1, side 1 runs C1 → C2, and so on anticlockwise.</remarks>
    public required int[][] Sides { get; init; }

    /// <summary>Whether each side is made only of feature arcs — where a seam is least visible.</summary>
    public required bool[] IsFeature { get; init; }
}

/// <summary>What extraction produced, and everything the report needs to measure it.</summary>
sealed class Extraction {
    internal Extraction(
        EditMesh mesh,
        int[] arcOf,
        int[] sourceOf,
        bool[] pinned,
        int[][] samples,
        PatchGrid[] grids,
        string[] warnings
    ) {
        Mesh = mesh;
        ArcOf = arcOf;
        SourceOf = sourceOf;
        Pinned = pinned;
        Samples = samples;
        Grids = grids;
        Warnings = warnings;
    }

    /// <summary>Every patch that produced a grid, in patch order. § D13's chart decomposition.</summary>
    public PatchGrid[] Grids { get; }

    /// <summary>The all-quad result.</summary>
    public EditMesh Mesh { get; }

    /// <summary>Per output position, which arc it came off, or <c>-1</c> for an interior one.</summary>
    public int[] ArcOf { get; }

    /// <summary>Per output position, which conditioned-mesh vertex it sits on, or <c>-1</c>.</summary>
    public int[] SourceOf { get; }

    /// <summary>Per output position, whether it ends an arc — a patch corner.</summary>
    public bool[] Pinned { get; }

    /// <summary>Per arc, the output positions along it in the arc's canonical direction.</summary>
    /// <remarks>
    ///     ⚠ <b>One list per arc, and both patches sharing that arc index into it.</b> docs/plan/41
    ///     § D8: "grid vertices on shared sides are the <i>same</i> vertices, by index, so the seam is an
    ///     equality rather than a weld". A tolerance weld here is how a mesh acquires a crack that only
    ///     appears under subdivision, on a model whose scale nobody thought about.
    /// </remarks>
    public int[][] Samples { get; }

    /// <summary>What could not be built.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>docs/plan/41 § D8's extraction: per-patch grids, stitched by index, relaxed and validated.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>All-quad is not a nicety and § D8 says why in one sentence.</b> Doc 24's
///         <c>MeshOperations</c> is built on the assumption that a loop, a ring and a loop cut are
///         statements about four-sided faces — a quad-<i>dominant</i> result has no rings to cut and the
///         mesh kernel's whole vocabulary stops working on it. This is where the plan refuses to
///         compromise with Instant Meshes, which extracts from a position field and produces triangles,
///         pentagons and T-junctions that every downstream consumer then has to cope with.
///     </para>
///     <para>
///         <b>The grid comes out of the quantization by construction.</b> A patch whose two opposite
///         side groups agree on <i>m</i> and <i>n</i> holds an <c>m × n</c> grid and nothing else has to
///         be decided; the interior comes from <see cref="PatchParameterization" />, which maps the
///         patch's own triangles onto the unit square and lifts the grid back through them. There is no
///         marching, no snapping and no clean-up pass, which is what makes the T-junction-free claim an
///         argument rather than a hope.
///     </para>
///     <para>
///         ⚠ <b>The Coons blend is still here and it is the fallback, not the plan.</b> A patch the
///         parameterization refuses — one whose triangles are not a disc, or whose rim the map does not
///         reproduce — keeps the transfinite interior it always had, projected back onto the surface.
///         Dropping the patch instead would turn an inverted quad into a hole, and a hole is the half of
///         an unusable output that nothing downstream can repair.
///     </para>
/// </remarks>
static class PatchExtractor {
    /// <summary>How many rounds of tangential smoothing the result gets.</summary>
    /// <remarks>
    ///     ⚠ A fixed count rather than a convergence tolerance — § D14, the same rule the field solver
    ///     is under, and for the same reason: a residual read against a threshold is a floating-point
    ///     comparison that can land differently on two machines.
    /// </remarks>
    public const int RelaxIterations = 8;

    /// <summary>How far a vertex moves toward its neighbours' average each round.</summary>
    public const float RelaxRate = 0.5f;

    /// <summary>Builds the quads.</summary>
    /// <param name="mesh">The conditioned surface.</param>
    /// <param name="features">The feature graph, for what may slide and what is pinned.</param>
    /// <param name="layout">The partition.</param>
    /// <param name="quantization">How many quads each arc gets.</param>
    /// <param name="projector">The reference surface the relaxation projects back onto.</param>
    /// <returns>The extraction.</returns>
    public static Extraction Extract(
        ManifoldMesh mesh,
        FeatureGraph features,
        PatchLayout layout,
        Quantization quantization,
        SurfaceProjector projector
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(quantization);
        ArgumentNullException.ThrowIfNull(projector);

        // The quads inherit their patch's group, so they inherit what the group ids meant as well —
        // otherwise a remeshed mesh unwraps as though its material boundaries had never existed.
        var output = new EditMesh { GroupSource = mesh.GroupSource };
        var warnings = new List<string>();
        var arcOf = new List<int>();
        var sourceOf = new List<int>();
        var pinned = new List<bool>();

        var samples = new int[layout.Arcs.Count][];
        var corners = Corners(mesh, layout, quantization, output, arcOf, sourceOf, pinned);

        for (var arc = 0; arc < layout.Arcs.Count; arc++) {
            samples[arc] = Sample(mesh, output, layout.Arcs[arc], quantization.Counts[arc], corners, arc, arcOf, sourceOf, pinned);
        }

        var skipped = new Dictionary<string, int>();
        var grids = new List<PatchGrid>(layout.Patches.Count);

        for (var index = 0; index < layout.Patches.Count; index++) {
            var refused = Fill(
                mesh,
                output,
                layout,
                samples,
                layout.Patches[index],
                index,
                projector,
                arcOf,
                sourceOf,
                pinned,
                out var grid
            );

            if (refused is not null) {
                skipped[refused] = skipped.GetValueOrDefault(refused) + 1;
            } else if (grid is not null) {
                grids.Add(grid);
            }
        }

        foreach (var (reason, count) in skipped.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            warnings.Add($"{count} patches were skipped: {reason}.");
        }

        var extraction = new Extraction(
            output,
            [.. arcOf],
            [.. sourceOf],
            [.. pinned],
            samples,
            [.. grids],
            [.. warnings]
        );

        Relax(mesh, features, layout, extraction, projector);

        return extraction;
    }

    /// <summary>One output position per arc end, with the ends of a collapsed arc sharing one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An arc that quantized to zero has no length in the output, so its two ends are one
    ///         vertex — and merging them is what "a five-sided patch becomes four-sided" actually
    ///         means.</b> docs/plan/41 § D7 permits the zero and this is the half of it that makes the
    ///         permission usable: leaving the two ends as separate positions produces a grid row with a
    ///         repeated index, a quad with no area, and a scaled Jacobian of exactly zero. Measured
    ///         before this existed: every fixture reported <c>MinScaledJacobian</c> of 0.000.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The merge is transitive, which is why it is a union-find and not a pair swap.</b> A
    ///         chain of three collapsed arcs is one vertex, not two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A class containing an end of a feature arc is placed <i>there</i>, and picking the
    ///         lower index instead is a crease moved.</b> The merged position is one point standing for
    ///         several source vertices, so where it goes is a decision rather than a detail: put it on the
    ///         vertex that has no crease through it and the arc that <i>does</i> starts somewhere its
    ///         chain never went. Measured on a union, the worst feature arc was a single straight edge —
    ///         chord sagitta exactly zero, so nothing about its sampling could be wrong — reporting
    ///         <c>1.66e-2</c> of the diagonal purely because a collapsed neighbour had won the tie and
    ///         taken its endpoint with it. docs/plan/41 § D7 permits the collapse and § D4 says what it
    ///         may not cost.
    ///     </para>
    /// </remarks>
    static Dictionary<int, int> Corners(
        ManifoldMesh mesh,
        PatchLayout layout,
        Quantization quantization,
        EditMesh output,
        List<int> arcOf,
        List<int> sourceOf,
        List<bool> pinned
    ) {
        var parent = new Dictionary<int, int>();
        var creased = new HashSet<int>();

        foreach (var arc in layout.Arcs) {
            parent.TryAdd(arc.Vertices[0], arc.Vertices[0]);
            parent.TryAdd(arc.Vertices[^1], arc.Vertices[^1]);

            if (arc.IsFeature) {
                creased.Add(arc.Vertices[0]);
                creased.Add(arc.Vertices[^1]);
            }
        }

        int Root(int vertex) {
            while (parent[vertex] != vertex) {
                vertex = parent[vertex] = parent[parent[vertex]];
            }

            return vertex;
        }

        for (var arc = 0; arc < layout.Arcs.Count; arc++) {
            if (quantization.Counts[arc] > 0) {
                continue;
            }

            var one = Root(layout.Arcs[arc].Vertices[0]);
            var two = Root(layout.Arcs[arc].Vertices[^1]);

            if (one == two) {
                continue;
            }

            // An end of a feature arc wins outright; otherwise the lower index does, so which arc
            // happened to be visited first decides nothing either way.
            var winner = creased.Contains(one) != creased.Contains(two)
                ? creased.Contains(one) ? one : two
                : Math.Min(one, two);

            parent[one == winner ? two : one] = winner;
        }

        var placed = new Dictionary<int, int>();
        var ends = parent.Keys.ToList();

        ends.Sort();

        foreach (var end in ends) {
            var root = Root(end);

            if (placed.ContainsKey(root)) {
                continue;
            }

            placed[root] = output.AddPosition(mesh.Positions[root]);
            arcOf.Add(-1);
            sourceOf.Add(root);
            pinned.Add(true);
        }

        var corners = new Dictionary<int, int>(ends.Count);

        foreach (var end in ends) {
            corners[end] = placed[Root(end)];
        }

        return corners;
    }

    /// <summary>Places one arc's output positions along its chain, by arc length.</summary>
    /// <remarks>
    ///     ⚠ <b>On the chain rather than on the chord between its ends, and that is the exit
    ///     criterion.</b> docs/plan/41's second criterion is "every feature polyline is a chain of output
    ///     edges, to 1e-5". A sample interpolated along the chain lies exactly on the source polyline,
    ///     and — because <see cref="PatchLayout" /> splits arcs at every key of a feature chain, so the
    ///     run between two samples is straight — the output <i>edge</i> between two samples lies on it
    ///     too. Placing samples on the chord instead would give a hard edge that is approximated, which
    ///     is precisely the good-but-wobbly result § D4 exists to rule out.
    /// </remarks>
    static int[] Sample(
        ManifoldMesh mesh,
        EditMesh output,
        LayoutArc arc,
        int count,
        Dictionary<int, int> corners,
        int index,
        List<int> arcOf,
        List<int> sourceOf,
        List<bool> pinned
    ) {
        var chain = arc.Vertices;
        var placed = new int[Math.Max(count, 0) + 1];

        placed[0] = corners[chain[0]];
        placed[^1] = corners[chain[^1]];

        if (count <= 1) {
            return placed;
        }

        var lengths = new float[chain.Length];

        for (var at = 1; at < chain.Length; at++) {
            lengths[at] = lengths[at - 1] + Vector3.Distance(mesh.Positions[chain[at - 1]], mesh.Positions[chain[at]]);
        }

        var total = lengths[^1];

        for (var step = 1; step < count; step++) {
            var wanted = total * step / count;
            var at = 1;

            while (at < chain.Length - 1 && lengths[at] < wanted) {
                at++;
            }

            var span = lengths[at] - lengths[at - 1];
            var fraction = span > 0f ? (wanted - lengths[at - 1]) / span : 0f;

            var position = Vector3.Lerp(
                mesh.Positions[chain[at - 1]],
                mesh.Positions[chain[at]],
                Math.Clamp(fraction, 0f, 1f)
            );

            placed[step] = output.AddPosition(position);
            arcOf.Add(index);
            sourceOf.Add(-1);
            pinned.Add(false);
        }

        return placed;
    }

    /// <summary>Fills one patch with its grid, or names why it could not be.</summary>
    /// <remarks>
    ///     ⚠ <b>Four checks before a single face is added, and each of them is a defect that reaches
    ///     <c>Validate</c> as something unattributable.</b> A side that collapsed entirely, two opposite
    ///     sides that disagree, four corners that do not join up, and a side that walks the same output
    ///     vertex twice all produce a grid whose rows fold on themselves — and a folded row comes back
    ///     as a non-manifold edge, an inconsistently wound edge and a zero-area face at once, which
    ///     reads as three separate bugs. A skipped patch is a hole and a hole is honest.
    /// </remarks>
    static string? Fill(
        ManifoldMesh mesh,
        EditMesh output,
        PatchLayout layout,
        int[][] samples,
        LayoutPatch patch,
        int index,
        SurfaceProjector projector,
        List<int> arcOf,
        List<int> sourceOf,
        List<bool> pinned,
        out PatchGrid? built
    ) {
        built = null;

        var side = new int[4][];

        for (var at = 0; at < 4; at++) {
            side[at] = Chain(samples, patch.Sides[at]);

            if (side[at].Length < 2) {
                return "a side quantized away entirely";
            }

            if (side[at].Distinct().Count() != side[at].Length) {
                return "a side walked the same vertex twice";
            }
        }

        var wide = side[0].Length - 1;
        var tall = side[1].Length - 1;

        if (side[2].Length - 1 != wide || side[3].Length - 1 != tall) {
            return "two opposite sides disagreed on their length";
        }

        for (var at = 0; at < 4; at++) {
            if (side[at][^1] != side[(at + 1) % 4][0]) {
                return "the four sides did not join up at a corner";
            }
        }

        var grid = new int[wide + 1][];

        for (var i = 0; i <= wide; i++) {
            grid[i] = new int[tall + 1];
        }

        // The loop is walked with the patch on its left, so side 0 runs C0 → C1, side 1 runs C1 → C2,
        // side 2 runs C2 → C3 and side 3 runs C3 → C0. Increasing `j` is into the patch.
        for (var i = 0; i <= wide; i++) {
            grid[i][0] = side[0][i];
            grid[i][tall] = side[2][wide - i];
        }

        for (var j = 0; j <= tall; j++) {
            grid[wide][j] = side[1][j];
            grid[0][j] = side[3][tall - j];
        }

        // ⚠ The last guard, and it is the one that catches a patch that wraps onto itself. A patch
        // whose boundary walks the same arc in two different sides puts one run of output vertices
        // into two edges of the grid; where the two runs meet, a quad has two corners that are the
        // same index, which reaches `Validate` as a non-manifold edge, an inconsistently wound edge
        // and a zero-area face all at once. Refusing here costs a hole and keeps the rest manifold.
        for (var i = 0; i < wide; i++) {
            if (grid[i][0] == grid[i + 1][0] || grid[i][tall] == grid[i + 1][tall]) {
                return "the grid folded onto itself at its boundary";
            }
        }

        for (var j = 0; j < tall; j++) {
            if (grid[0][j] == grid[0][j + 1] || grid[wide][j] == grid[wide][j + 1]) {
                return "the grid folded onto itself at its boundary";
            }
        }

        // A quad whose two boundary edges are the same edge is the same fold seen from the other side:
        // opposite rows meeting where they should not.
        if (wide > 1 && tall > 1) {
            for (var i = 1; i < wide; i++) {
                if (grid[i][0] == grid[i][tall]) {
                    return "the grid folded onto itself at its boundary";
                }
            }

            for (var j = 1; j < tall; j++) {
                if (grid[0][j] == grid[wide][j]) {
                    return "the grid folded onto itself at its boundary";
                }
            }
        }

        // § D8's per-patch parameterization, with the blend kept for the patches it refuses. A lifted
        // point is already on the conditioned surface — it is a barycentric point of one of its
        // triangles — so it is the blended one, and only the blended one, that needs projecting back.
        var embedded = PatchParameterization.Interior(mesh, layout.Arcs, patch, samples, output, grid, wide, tall);

        for (var i = 1; i < wide; i++) {
            for (var j = 1; j < tall; j++) {
                var u = (float) i / wide;
                var v = (float) j / tall;

                var position = embedded is not null
                    ? embedded[i - 1, j - 1]
                    : projector.Project(Coons(output, grid, i, j, wide, tall, u, v));

                grid[i][j] = output.AddPosition(position);
                arcOf.Add(-1);
                sourceOf.Add(-1);
                pinned.Add(false);
            }
        }

        var group = mesh.Group(patch.Triangles[0]);
        var first = output.FaceCount;

        for (var i = 0; i < wide; i++) {
            for (var j = 0; j < tall; j++) {
                output.AddFace([grid[i][j], grid[i + 1][j], grid[i + 1][j + 1], grid[i][j + 1]], group);
            }
        }

        var feature = new bool[4];

        for (var at = 0; at < 4; at++) {
            // A side is a run of arcs and it is a feature side only when every one of them is —
            // half a crease is not a place a seam is invisible, which is what § D13's preference is
            // actually about.
            feature[at] = patch.Sides[at].Length > 0
                && patch.Sides[at].All(use => layout.Arcs[use.Arc].IsFeature);
        }

        built = new() {
            Patch = index,
            Wide = wide,
            Tall = tall,
            Vertices = grid,
            FirstFace = first,
            Sides = side,
            IsFeature = feature
        };

        return null;
    }

    /// <summary>The bilinearly-blended interior point of a patch, from its four boundary chains.</summary>
    /// <remarks>
    ///     The textbook Coons patch: the two linear interpolations between opposite sides, minus the
    ///     bilinear interpolation of the four corners that both of them counted.
    /// </remarks>
    static Vector3 Coons(EditMesh output, int[][] grid, int i, int j, int wide, int tall, float u, float v) {
        var left = output.Positions[grid[0][j]];
        var right = output.Positions[grid[wide][j]];
        var bottom = output.Positions[grid[i][0]];
        var top = output.Positions[grid[i][tall]];

        var c00 = output.Positions[grid[0][0]];
        var c10 = output.Positions[grid[wide][0]];
        var c01 = output.Positions[grid[0][tall]];
        var c11 = output.Positions[grid[wide][tall]];

        var along = (left * (1f - u)) + (right * u);
        var across = (bottom * (1f - v)) + (top * v);

        var blend = (c00 * (1f - u) * (1f - v))
            + (c10 * u * (1f - v))
            + (c01 * (1f - u) * v)
            + (c11 * u * v);

        return along + across - blend;
    }

    /// <summary>One side's output positions, concatenating its arcs and dropping the shared joins.</summary>
    static int[] Chain(int[][] samples, ArcUse[] side) {
        var chain = new List<int>();

        foreach (var use in side) {
            var run = samples[use.Arc];

            for (var at = 0; at < run.Length; at++) {
                var vertex = use.Reversed ? run[run.Length - 1 - at] : run[at];

                if (chain.Count == 0 || chain[^1] != vertex) {
                    chain.Add(vertex);
                }
            }
        }

        return [.. chain];
    }

    /// <summary>§ D8's relaxation: tangential smoothing with reprojection, feature vertices sliding.</summary>
    /// <remarks>
    ///     ⚠ <b>Three constraints and they are not interchangeable.</b> A corner is pinned, because a
    ///     corner that moves is a hard edge that no longer meets the one next to it. A vertex on a
    ///     feature arc slides <i>along</i> the arc's own chain, because it may space itself better and
    ///     may not leave the crease. Everything else moves freely on the surface and is projected back
    ///     each round, which is what stops the smoothing from shrinking the model — the same rule R1's
    ///     isotropic pre-remesh is under.
    /// </remarks>
    static void Relax(
        ManifoldMesh mesh,
        FeatureGraph features,
        PatchLayout layout,
        Extraction extraction,
        SurfaceProjector projector
    ) {
        var output = extraction.Mesh;

        if (output.FaceCount == 0) {
            return;
        }

        var count = output.PositionCount;
        var neighbours = Adjacency(output);
        var moving = new Vector3[count];
        var held = Held(layout, extraction, count);

        for (var round = 0; round < RelaxIterations; round++) {
            for (var vertex = 0; vertex < count; vertex++) {
                moving[vertex] = output.Positions[vertex];
            }

            for (var vertex = 0; vertex < count; vertex++) {
                var source = extraction.SourceOf[vertex];

                // A corner sits on the source and stays there. A corner that is not on a feature is
                // still an arc end, and moving one end of an arc but not the other is how a straight
                // crease acquires a kink.
                if (held[vertex] || (extraction.Pinned[vertex] && source >= 0 && features.IsFeatureVertex(source))) {
                    continue;
                }

                var ring = neighbours[vertex];

                if (ring.Length == 0) {
                    continue;
                }

                var sum = Vector3.Zero;

                // In index order, which is § D14's rule: two runs must add the same numbers in the
                // same sequence or the last bit of the answer is not a function of the input.
                foreach (var neighbour in ring) {
                    sum += output.Positions[neighbour];
                }

                var wanted = Vector3.Lerp(output.Positions[vertex], sum / ring.Length, RelaxRate);
                var arc = extraction.ArcOf[vertex];

                moving[vertex] = arc >= 0 && layout.Arcs[arc].IsFeature
                    ? Slide(mesh, layout.Arcs[arc], wanted)
                    : projector.Project(wanted);
            }

            for (var vertex = 0; vertex < count; vertex++) {
                output.MovePosition(vertex, moving[vertex]);
            }
        }
    }

    /// <summary>Every output position that ends a feature arc, which the relaxation may not move.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read off the <i>arc</i> rather than off the source vertex, and the difference is
    ///         docs/plan/41's second exit criterion.</b> An end that <see cref="Corners" /> merged with
    ///         another — which is what a collapsed arc does — carries the <i>root</i>'s source index, and
    ///         the root need not be a feature vertex even when the arc it terminates is a crease. The
    ///         relaxation then moved it freely and dragged the first output edge of a hard edge off the
    ///         hard edge.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured, and it is why the error survived on arcs that are a single straight
    ///         edge.</b> On a union, the worst feature arc was a two-vertex chain with a chord sagitta of
    ///         exactly zero — nothing about its samples could be wrong — reporting <c>1.66e-2</c> of the
    ///         diagonal, because one of its two ends had drifted. Interior samples were never the
    ///         problem: <see cref="Slide" /> already keeps those on the chain.
    ///     </para>
    /// </remarks>
    static bool[] Held(PatchLayout layout, Extraction extraction, int count) {
        var held = new bool[count];

        for (var arc = 0; arc < layout.Arcs.Count; arc++) {
            if (!layout.Arcs[arc].IsFeature) {
                continue;
            }

            foreach (var end in (int[]) [extraction.Samples[arc][0], extraction.Samples[arc][^1]]) {
                if ((uint) end < (uint) count) {
                    held[end] = true;
                }
            }
        }

        return held;
    }

    /// <summary>The nearest point of an arc's own chain — where a feature vertex is allowed to be.</summary>
    static Vector3 Slide(ManifoldMesh mesh, LayoutArc arc, Vector3 wanted) {
        var best = wanted;
        var distance = float.PositiveInfinity;

        for (var at = 0; at + 1 < arc.Vertices.Length; at++) {
            var a = mesh.Positions[arc.Vertices[at]];
            var b = mesh.Positions[arc.Vertices[at + 1]];

            var along = b - a;
            var span = along.LengthSquared();
            var fraction = span > 0f ? Math.Clamp(Vector3.Dot(wanted - a, along) / span, 0f, 1f) : 0f;

            var point = a + (along * fraction);
            var away = Vector3.DistanceSquared(point, wanted);

            if (away < distance) {
                distance = away;
                best = point;
            }
        }

        return best;
    }

    /// <summary>Every position's neighbours across the quad edges, in ascending order.</summary>
    public static int[][] Adjacency(EditMesh output) {
        ArgumentNullException.ThrowIfNull(output);

        var sets = new HashSet<int>[output.PositionCount];

        for (var vertex = 0; vertex < sets.Length; vertex++) {
            sets[vertex] = [];
        }

        for (var face = 0; face < output.FaceCount; face++) {
            var loop = output.CornersOf(face);

            for (var at = 0; at < loop.Length; at++) {
                var a = loop[at];
                var b = loop[(at + 1) % loop.Length];

                sets[a].Add(b);
                sets[b].Add(a);
            }
        }

        var neighbours = new int[sets.Length][];

        for (var vertex = 0; vertex < sets.Length; vertex++) {
            var ordered = sets[vertex].ToArray();

            Array.Sort(ordered);
            neighbours[vertex] = ordered;
        }

        return neighbours;
    }
}
