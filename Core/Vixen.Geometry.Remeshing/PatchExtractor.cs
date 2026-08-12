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

    /// <summary>Why the interior fell back to the blend, or <see langword="null" /> when it did not.</summary>
    /// <remarks>
    ///     ⚠ <b>Carried because the blend is where the folds are, and nothing was recording which
    ///     patches took it.</b> <see cref="PatchParameterization" /> is a provable embedding and the
    ///     transfinite blend beside it is not; a patch that fell back is the one place an inverted quad
    ///     can still be built, so the reason belongs in the grid rather than in a counter that only the
    ///     warning text sees.
    /// </remarks>
    public required string? Blended { get; init; }
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
    ///     <para>
    ///         ⚠ A fixed count rather than a convergence tolerance — § D14, the same rule the field
    ///         solver is under, and for the same reason: a residual read against a threshold is a
    ///         floating-point comparison that can land differently on two machines.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Thirty-two because the guarded sweep has stopped moving by then, measured rather
    ///         than chosen.</b> Eight was too few once <see cref="Relax" /> began refusing a move that
    ///         folds — the fixtures came back at 122 inverted faces at 32 rounds and at exactly 122
    ///         again at 96, with every worst corner identical to three decimals, so the extra rounds buy
    ///         nothing. What is left at that point is a fold no single-vertex move can improve, which is
    ///         an untangler's job and not a smoother's.
    ///     </para>
    /// </remarks>
    public const int RelaxIterations = 32;

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
            string? refused;

            if (layout.Patches[index].IsFan) {
                refused = Fan(
                    mesh,
                    output,
                    layout,
                    samples,
                    quantization,
                    layout.Patches[index],
                    index,
                    projector,
                    arcOf,
                    sourceOf,
                    pinned,
                    grids
                );
            } else {
                refused = Fill(
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

                if (refused is null && grid is not null) {
                    grids.Add(grid);
                }
            }

            if (refused is not null) {
                skipped[refused] = skipped.GetValueOrDefault(refused) + 1;
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

        // § D8's per-patch parameterization, with the blend beside it. A lifted point is already on the
        // conditioned surface — it is a barycentric point of one of its triangles — so it is the
        // blended one, and only the blended one, that needs projecting back.
        var embedded = PatchParameterization.Interior(
            mesh,
            layout.Arcs,
            patch,
            samples,
            output,
            side,
            wide,
            tall,
            out var blended
        );

        var interior = Chosen(output, projector, grid, embedded, wide, tall, ref blended);

        for (var i = 1; i < wide; i++) {
            for (var j = 1; j < tall; j++) {
                grid[i][j] = output.AddPosition(interior[i - 1, j - 1]);
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
            IsFeature = feature,
            Blended = blended
        };

        return null;
    }

    /// <summary>Fills a three-sided patch as three quads round a centre, or names why it could not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one construction in this file that adds a vertex the layout never asked for,
    ///         and it is what turns a dropped patch into a filled one.</b> A boundary of three arcs
    ///         holds no rectangle, so <see cref="Fill" /> has nothing to lay; what it does hold is the
    ///         standard three-to-quads subdivision — a centre point, a spoke out to the middle of each
    ///         of the three sides, and three quad blocks between consecutive spokes. § D7's answer for a
    ///         patch bounded by fewer than four arcs is divide or merge, and <c>Merge</c> will not
    ///         dissolve a crease; this is the third answer, and it costs no crease and leaves no hole.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every size here comes off <see cref="Quantization.Spokes" /> rather than being
    ///         chosen.</b> The blocks are <c>a × b</c>, <c>b × c</c> and <c>c × a</c> and the sides are
    ///         <c>a + c</c>, <c>a + b</c> and <c>b + c</c> — the router solved for those three numbers
    ///         under the same conservation constraints every other patch is under, so the halves meet
    ///         the neighbours' seams by index exactly as § D8 requires. A fan whose sides do not add up
    ///         to what its spokes say is refused rather than repaired, because the disagreement can only
    ///         mean the fan was left out of the system.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The interior comes through the same Tutte embedding, onto a triangle instead of a
    ///         square.</b> <see cref="PatchParameterization.Fan" /> maps the patch's own triangles onto
    ///         the reference triangle; the centre is its centroid, the spokes are straight runs in the
    ///         parameter domain, and every block is a bilinear patch of a convex quadrilateral there —
    ///         all of which lift back onto the surface by barycentric coordinates. The transfinite blend
    ///         is the fallback, exactly as it is for a four-sided patch, and each block keeps whichever
    ///         of the two folds less.
    ///     </para>
    /// </remarks>
    static string? Fan(
        ManifoldMesh mesh,
        EditMesh output,
        PatchLayout layout,
        int[][] samples,
        Quantization quantization,
        LayoutPatch patch,
        int index,
        SurfaceProjector projector,
        List<int> arcOf,
        List<int> sourceOf,
        List<bool> pinned,
        List<PatchGrid> grids
    ) {
        var (a, b, c) = quantization.Spokes[index];

        if (a < 1 || b < 1 || c < 1) {
            return "a three-sided patch had no centre to fan round";
        }

        // Where the spoke leaves side k, in quads from that side's own first corner, and how many
        // quads the side has in all: n₀ = a + c, n₁ = a + b, n₂ = b + c.
        int[] split = [a, b, c];
        int[] along = [a + c, a + b, b + c];

        // How many quads the spoke out of side k's midpoint has. Block k is `split[k]` by this.
        int[] spokes = [b, c, a];

        var side = new int[3][];
        var rim = new HashSet<int>();
        var walked = 0;

        for (var at = 0; at < 3; at++) {
            side[at] = Chain(samples, patch.Sides[at]);

            if (side[at].Length - 1 != along[at]) {
                return "a three-sided patch's sides disagreed with its centre";
            }

            // Every rim vertex once, counting each corner with the side it starts — which is the same
            // fold test `Fill`'s grid guards make, taken over the whole boundary at once because a fan
            // has three boundaries to lay rather than four edges of one grid.
            for (var step = 0; step < side[at].Length - 1; step++) {
                walked++;
                rim.Add(side[at][step]);
            }
        }

        for (var at = 0; at < 3; at++) {
            if (side[at][^1] != side[(at + 1) % 3][0]) {
                return "the three sides did not join up at a corner";
            }
        }

        if (rim.Count != walked) {
            return "a side walked the same vertex twice";
        }

        var lifted = Lifted(mesh, layout, patch, samples, output, side, split, along, spokes);
        var blended = lifted is null ? "the fan could not be embedded on a triangle" : null;

        var middle = lifted?[0] ?? projector.Project(Middle(output, side));
        var hub = Placed(output, arcOf, sourceOf, pinned, middle);
        var spoke = new int[3][];
        var cursor = 1;

        for (var at = 0; at < 3; at++) {
            var chain = new int[spokes[at] + 1];

            chain[0] = side[at][split[at]];
            chain[^1] = hub;

            for (var step = 1; step < spokes[at]; step++) {
                var position = lifted is not null
                    ? lifted[cursor + step - 1]
                    : projector.Project(
                        Vector3.Lerp(output.Positions[chain[0]], middle, (float) step / spokes[at])
                    );

                chain[step] = Placed(output, arcOf, sourceOf, pinned, position);
            }

            cursor += spokes[at] - 1;
            spoke[at] = chain;
        }

        for (var block = 0; block < 3; block++) {
            var wide = split[block];
            var tall = spokes[block];
            var back = (block + 2) % 3;

            // C → M(block) → centre → M(back) → C, which is the patch's own anticlockwise walk with
            // two of its four sides taken through the middle.
            int[][] chains = [
                side[block][..(wide + 1)],
                spoke[block],
                [.. spoke[back].Reverse()],
                side[back][split[back]..]
            ];

            var grid = new int[wide + 1][];

            for (var i = 0; i <= wide; i++) {
                grid[i] = new int[tall + 1];
            }

            for (var i = 0; i <= wide; i++) {
                grid[i][0] = chains[0][i];
                grid[i][tall] = chains[2][wide - i];
            }

            for (var j = 0; j <= tall; j++) {
                grid[wide][j] = chains[1][j];
                grid[0][j] = chains[3][tall - j];
            }

            Vector3[,]? embedded = null;

            if (lifted is not null) {
                embedded = new Vector3[wide - 1, tall - 1];

                for (var i = 1; i < wide; i++) {
                    for (var j = 1; j < tall; j++) {
                        embedded[i - 1, j - 1] = lifted[cursor++];
                    }
                }
            }

            var reason = blended;
            var interior = Chosen(output, projector, grid, embedded, wide, tall, ref reason);

            for (var i = 1; i < wide; i++) {
                for (var j = 1; j < tall; j++) {
                    grid[i][j] = Placed(output, arcOf, sourceOf, pinned, interior[i - 1, j - 1]);
                }
            }

            var group = mesh.Group(patch.Triangles[0]);
            var first = output.FaceCount;

            for (var i = 0; i < wide; i++) {
                for (var j = 0; j < tall; j++) {
                    output.AddFace([grid[i][j], grid[i + 1][j], grid[i + 1][j + 1], grid[i][j + 1]], group);
                }
            }

            grids.Add(
                new() {
                    Patch = index,
                    Wide = wide,
                    Tall = tall,
                    Vertices = grid,
                    FirstFace = first,
                    Sides = chains,

                    // Only the two halves that lie on the patch's own boundary can be a crease; the two
                    // spokes are interior to the fan and belong to nothing outside it.
                    IsFeature = [
                        patch.Sides[block].All(use => layout.Arcs[use.Arc].IsFeature),
                        false,
                        false,
                        patch.Sides[back].All(use => layout.Arcs[use.Arc].IsFeature)
                    ],
                    Blended = reason
                }
            );
        }

        return null;
    }

    /// <summary>The fan's centre, spokes and block interiors, lifted through a triangle embedding.</summary>
    /// <remarks>
    ///     The order is the one <see cref="Fan" /> reads back: the centroid, then each spoke's interior
    ///     from its side's midpoint inwards, then each block's interior in <c>i</c>-major order — the
    ///     same nesting the blocks are filled in.
    /// </remarks>
    static Vector3[]? Lifted(
        ManifoldMesh mesh,
        PatchLayout layout,
        LayoutPatch patch,
        int[][] samples,
        EditMesh output,
        int[][] side,
        int[] split,
        int[] along,
        int[] spokes
    ) {
        var corner = new Vector2[3];
        var middle = new Vector2[3];

        for (var at = 0; at < 3; at++) {
            corner[at] = PatchParameterization.Corner(3, at);
        }

        for (var at = 0; at < 3; at++) {
            middle[at] = Vector2.Lerp(
                corner[at],
                PatchParameterization.Corner(3, at + 1),
                (float) split[at] / along[at]
            );
        }

        var centroid = (corner[0] + corner[1] + corner[2]) / 3f;
        var wanted = new List<Vector2> { centroid };

        for (var at = 0; at < 3; at++) {
            for (var step = 1; step < spokes[at]; step++) {
                wanted.Add(Vector2.Lerp(middle[at], centroid, (float) step / spokes[at]));
            }
        }

        for (var block = 0; block < 3; block++) {
            var wide = split[block];
            var tall = spokes[block];
            var back = (block + 2) % 3;

            for (var i = 1; i < wide; i++) {
                for (var j = 1; j < tall; j++) {
                    wanted.Add(
                        Bilinear(
                            corner[block],
                            middle[block],
                            centroid,
                            middle[back],
                            (float) i / wide,
                            (float) j / tall
                        )
                    );
                }
            }
        }

        return PatchParameterization.Fan(
            mesh,
            layout.Arcs,
            patch,
            samples,
            output,
            side,
            along,
            [.. wanted],
            out _
        );
    }

    /// <summary>The bilinear point of a parameter-domain quadrilateral, corners anticlockwise.</summary>
    static Vector2 Bilinear(Vector2 c00, Vector2 c10, Vector2 c11, Vector2 c01, float s, float t) =>
        (c00 * (1f - s) * (1f - t)) + (c10 * s * (1f - t)) + (c11 * s * t) + (c01 * (1f - s) * t);

    /// <summary>The average of a fan's rim, which is where its centre goes when the embedding refuses.</summary>
    static Vector3 Middle(EditMesh output, int[][] side) {
        var sum = Vector3.Zero;
        var count = 0;

        foreach (var chain in side) {
            for (var step = 0; step < chain.Length - 1; step++) {
                sum += output.Positions[chain[step]];
                count++;
            }
        }

        return count > 0 ? sum / count : Vector3.Zero;
    }

    /// <summary>Adds one interior output position, with the three per-vertex tables the rest reads.</summary>
    static int Placed(
        EditMesh output,
        List<int> arcOf,
        List<int> sourceOf,
        List<bool> pinned,
        Vector3 position
    ) {
        var placed = output.AddPosition(position);

        arcOf.Add(-1);
        sourceOf.Add(-1);
        pinned.Add(false);

        return placed;
    }

    /// <summary>The interior that folds least, out of the embedding and the blend.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠⚠ <b>Both are built and the grid itself decides, because neither construction wins
    ///         everywhere and arguing about which should was costing quads on every fixture.</b>
    ///         <see cref="PatchParameterization" /> is a provable embedding of the patch onto a square,
    ///         which is exactly the guarantee the transfinite blend lacks — but the guarantee is about
    ///         the <i>map</i> and not about a <i>uniform grid laid on the square</i>. A harmonic map
    ///         distributes by its own weights rather than by arc length, so on a patch the quantizer cut
    ///         nine quads one way and two the other, the parameter row at <c>v = ½</c> can run backwards
    ///         along the surface relative to the row at <c>v = 0</c>, and the straight-sided cell between
    ///         them crosses itself. Measured on a union patch of that shape: the lifted interior corner
    ///         landed at <c>x = −0.651</c> between rim corners at <c>−0.632</c> and <c>−0.408</c>, and
    ///         the quad's two halves crossed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured against counted folds and not against a quality score, because a fold is
    ///         the defect and a score is a proxy for it.</b> The count is over the whole grid including
    ///         its rim, so a cell that folds against a boundary the interior never touches is still seen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The embedding wins every tie, which is what keeps this from being a coin toss.</b>
    ///         It is the construction with the theorem behind it and it is the one § D8 asks for; the
    ///         blend is only ever allowed to displace it by <i>measurably</i> folding less. A tie-break
    ///         the other way would let floating-point noise decide which construction a patch got, and
    ///         § D14 wants the same bytes out of the same input.
    ///     </para>
    /// </remarks>
    static Vector3[,] Chosen(
        EditMesh output,
        SurfaceProjector projector,
        int[][] grid,
        Vector3[,]? embedded,
        int wide,
        int tall,
        ref string? blended
    ) {
        var coons = new Vector3[wide - 1, tall - 1];

        for (var i = 1; i < wide; i++) {
            for (var j = 1; j < tall; j++) {
                coons[i - 1, j - 1] = projector.Project(
                    Coons(output, grid, i, j, wide, tall, (float) i / wide, (float) j / tall)
                );
            }
        }

        if (embedded is null) {
            return coons;
        }

        var folds = Folds(output, grid, embedded, wide, tall);

        if (folds == 0 || folds <= Folds(output, grid, coons, wide, tall)) {
            return embedded;
        }

        blended = "the blend folded less than the embedding";

        return coons;
    }

    /// <summary>How many of a candidate grid's cells have a corner wound against the cell's own normal.</summary>
    /// <remarks>
    ///     ⚠ <b>The same test <c>RemeshMetrics.ScaledJacobian</c> reports and <c>QuadQualityTests</c>
    ///     reads, applied before the face exists rather than after.</b> A degenerate corner contributes
    ///     nothing and the scan carries on — the sentinel that made four fixtures read <c>0.000</c> while
    ///     holding folded quads was exactly an early return here.
    /// </remarks>
    static int Folds(EditMesh output, int[][] grid, Vector3[,] interior, int wide, int tall) {
        var corner = new Vector3[wide + 1][];

        for (var i = 0; i <= wide; i++) {
            corner[i] = new Vector3[tall + 1];

            for (var j = 0; j <= tall; j++) {
                corner[i][j] = i > 0 && j > 0 && i < wide && j < tall
                    ? interior[i - 1, j - 1]
                    : output.Positions[grid[i][j]];
            }
        }

        var folded = 0;

        for (var i = 0; i < wide; i++) {
            for (var j = 0; j < tall; j++) {
                Span<Vector3> loop = [corner[i][j], corner[i + 1][j], corner[i + 1][j + 1], corner[i][j + 1]];

                var normal = Vector3.Zero;

                for (var at = 0; at < 4; at++) {
                    var here = loop[at];
                    var next = loop[(at + 1) % 4];

                    normal += new Vector3(
                        (here.Y - next.Y) * (here.Z + next.Z),
                        (here.Z - next.Z) * (here.X + next.X),
                        (here.X - next.X) * (here.Y + next.Y)
                    );
                }

                normal = ScaleSafe.Unit(normal);

                if (normal.LengthSquared() <= 0f) {
                    continue;
                }

                for (var at = 0; at < 4; at++) {
                    var here = loop[at];
                    var ahead = ScaleSafe.Unit(loop[(at + 1) % 4] - here);
                    var behind = ScaleSafe.Unit(loop[(at + 3) % 4] - here);

                    if (ahead.LengthSquared() > 0f
                        && behind.LengthSquared() > 0f
                        && Vector3.Dot(Vector3.Cross(ahead, behind), normal) < 0f) {
                        folded++;

                        break;
                    }
                }
            }
        }

        return folded;
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
    ///     <para>
    ///         ⚠ <b>Three constraints and they are not interchangeable.</b> A corner is pinned, because a
    ///         corner that moves is a hard edge that no longer meets the one next to it. A vertex on a
    ///         feature arc slides <i>along</i> the arc's own chain, because it may space itself better
    ///         and may not leave the crease. Everything else moves freely on the surface and is projected
    ///         back each round, which is what stops the smoothing from shrinking the model — the same
    ///         rule R1's isotropic pre-remesh is under.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>Applied as the sweep goes rather than gathered and applied at the end, and that is
    ///         what makes the fold guard below mean anything.</b> Each candidate is judged against the
    ///         faces around it; if the whole round is judged against the round's <i>starting</i>
    ///         positions and then applied at once, two adjacent vertices can each be told their own move
    ///         is harmless and land on a fold together. Writing each accepted move immediately makes the
    ///         guard's promise a global one: no accepted move increases the number of folded faces in
    ///         the mesh, so the count is non-increasing over the whole relaxation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Still deterministic, because the sweep is in ascending index order.</b> § D14 asks
    ///         that the same input produce the same bytes, which an ordered in-place sweep satisfies; it
    ///         does not ask that the answer be independent of the order, which no Gauss–Seidel is.
    ///     </para>
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
        var faces = Incident(output);
        var held = Held(layout, extraction, count);

        for (var round = 0; round < RelaxIterations; round++) {
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

                var candidate = arc >= 0 && layout.Arcs[arc].IsFeature
                    ? Slide(mesh, layout.Arcs[arc], wanted)
                    : projector.Project(wanted);

                // ⚠ A smoothing step that folds one of its own faces is refused, and that is what makes
                // the rounds monotone rather than merely usually-helpful. Plain Laplacian smoothing has
                // no idea what a fold is: measured over the seven fixtures at 32 rounds it took the
                // sphere to zero inverted faces and pushed the stairs from −0.966 to −1.000 in the same
                // run, because the average of a ring is not a point inside the ring's faces. Comparing
                // before and after is cheap — a grid vertex has four faces — and it turns the round
                // budget into a bound that only ever pays.
                //
                // ⚠ <b>The count first and the depth only as a tie-break, and a neighbourhood that is
                // already clean is exempt from the depth test entirely.</b> Requiring the worst corner
                // never to shrink would freeze the mesh, because almost every smoothing step trades a
                // little shape somewhere for a lot somewhere else; requiring only the count made the
                // sphere's worst quad go from −0.081 to −0.449 while its face count improved, because a
                // fold that stays a fold was free to deepen. Measured, the pair together is what moved
                // every fixture in the same direction at once.
                var was = Around(output, faces, vertex, output.Positions[vertex]);
                var now = Around(output, faces, vertex, candidate);

                if (now.Folded > was.Folded || (now.Folded == was.Folded && was.Folded > 0 && now.Worst < was.Worst)) {
                    continue;
                }

                output.MovePosition(vertex, candidate);
            }
        }
    }

    /// <summary>Every face each position is a corner of, ascending.</summary>
    static int[][] Incident(EditMesh output) {
        var sets = new List<int>[output.PositionCount];

        for (var vertex = 0; vertex < sets.Length; vertex++) {
            sets[vertex] = [];
        }

        for (var face = 0; face < output.FaceCount; face++) {
            foreach (var corner in output.CornersOf(face)) {
                if (sets[corner].Count == 0 || sets[corner][^1] != face) {
                    sets[corner].Add(face);
                }
            }
        }

        var incident = new int[sets.Length][];

        for (var vertex = 0; vertex < sets.Length; vertex++) {
            incident[vertex] = [.. sets[vertex]];
        }

        return incident;
    }

    /// <summary>How many of a position's own faces are folded, with that position moved to a candidate.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the moving vertex is substituted and every other corner is read from the mesh</b>,
    ///     so the answer is a function of one candidate against one fixed neighbourhood — which is what
    ///     lets the whole round be evaluated against the round's own starting positions and stay
    ///     independent of the order the vertices are visited in. § D14.
    /// </remarks>
    static (int Folded, float Worst) Around(EditMesh output, int[][] faces, int vertex, Vector3 candidate) {
        var folded = 0;
        var worst = 1f;

        foreach (var face in faces[vertex]) {
            var loop = output.CornersOf(face);
            var normal = Vector3.Zero;

            for (var at = 0; at < loop.Length; at++) {
                var here = loop[at] == vertex ? candidate : output.Positions[loop[at]];
                var next = loop[(at + 1) % loop.Length] == vertex
                    ? candidate
                    : output.Positions[loop[(at + 1) % loop.Length]];

                normal += new Vector3(
                    (here.Y - next.Y) * (here.Z + next.Z),
                    (here.Z - next.Z) * (here.X + next.X),
                    (here.X - next.X) * (here.Y + next.Y)
                );
            }

            normal = ScaleSafe.Unit(normal);

            // A face with no normal at all has already collapsed; it is counted so that a move cannot
            // reach one, and there is nothing further to measure on it.
            if (normal.LengthSquared() <= 0f) {
                folded++;

                continue;
            }

            var any = false;

            for (var at = 0; at < loop.Length; at++) {
                var here = loop[at] == vertex ? candidate : output.Positions[loop[at]];
                var one = loop[(at + 1) % loop.Length];
                var two = loop[(at + loop.Length - 1) % loop.Length];

                var ahead = ScaleSafe.Unit((one == vertex ? candidate : output.Positions[one]) - here);
                var behind = ScaleSafe.Unit((two == vertex ? candidate : output.Positions[two]) - here);

                // ⚠ <b>A corner whose two edges do not both have a direction is counted as bad rather
                // than skipped, and that is what stops the smoothing welding the mesh shut.</b> Two
                // output positions that land on exactly the same point are one point to anything that
                // writes the mesh out — an <c>.obj</c> shares a position between faces — so a quad
                // whose corner has collapsed turns an edge into one with three faces on it. Measured
                // over the 16-file corpus, letting the sweep run to convergence with this corner
                // skipped took position-welded non-manifold edges from 75 to 99; counting it holds
                // them, because the move that would collapse the corner now scores worse than the
                // move that would not.
                if (ahead.LengthSquared() <= 0f || behind.LengthSquared() <= 0f) {
                    any = true;

                    continue;
                }

                var corner = Vector3.Dot(Vector3.Cross(ahead, behind), normal);

                worst = MathF.Min(worst, corner);
                any |= corner < 0f;
            }

            if (any) {
                folded++;
            }
        }

        return (folded, worst);
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
