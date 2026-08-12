// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>docs/plan/41 § D8's "real per-patch parameterization", in place of a transfinite blend.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A Coons blend of four <i>curved</i> boundary chains is not injective, and that is not a
///         tolerance — it is the construction.</b> The bilinear surface between two curved opposite
///         sides passes through itself wherever the patch bends, so the interior grid folds and the
///         quads come out inverted. Measured at a 400-quad budget before this existed: 24 inverted
///         faces of 554 on the box, 62 of 687 on the cylinder, 47 of 646 on the stairs, and −1.000 —
///         a quad turned completely inside out — on the difference. Attributed rather than assumed:
///         <i>every</i> inverted face lying in a patch that has interior points at all had a blended
///         vertex on it, on all seven fixtures — which places the blend at the scene without yet
///         convicting it, and the paragraph below is where the measurement narrows that.
///     </para>
///     <para>
///         <b>What replaces it is Tutte's theorem, in Floater's form.</b> The patch's own triangles are
///         mapped onto the unit square with the boundary pinned to the square's boundary, and every
///         interior vertex placed at a convex combination of its neighbours with strictly positive
///         weights. A triangulated disc mapped that way onto a <i>convex</i> region is a valid
///         embedding — no triangle flips and no two overlap — which is exactly the property the blend
///         lacks. The grid is then laid on the square, where it is a rectangle by definition, and
///         lifted back through the map. Interior positions land on the source triangles by barycentric
///         coordinates, so they are on the surface by construction rather than projected onto it.
///     </para>
///     <para>
///         ⚠ <b>Mean-value weights and not cotangent ones, and the guarantee is the whole reason.</b>
///         Cotangent weights go negative on an obtuse triangle, which is precisely the case Floater's
///         proof excludes; mean-value weights (Floater, 2003) are positive for every triangle that has
///         an area at all. A harmonic map with a negative weight can fold, and a patch conditioned by
///         § D3 has obtuse triangles in it.
///     </para>
///     <para>
///         ⚠ <b>The boundary is pinned with the <i>same</i> per-arc parameterization the samples were
///         placed with, so the square's corner at <c>u = i / wide</c> is the source-chain point sample
///         <c>i</c> sits on.</b> Pinning by the side's total arc length instead would be subtly wrong
///         wherever a side is made of two arcs that quantized to different counts: the interior would
///         be solved against a boundary the boundary vertices are not actually on, and the first ring
///         of interior quads would shear.
///     </para>
///     <para>
///         ⚠⚠ <b>It was refusing most of the patches it should have filled, and the refusals were
///         arithmetic rather than geometric.</b> It once read here that the residual folds were
///         near-<i>planar</i> bow-ties inside patches this had filled, and therefore a patch region
///         doubling back across a crease — a layout defect. That is measured to be false: the number of
///         feature edges with the same patch on both sides is <b>0</b> on every fixture, and
///         <c>CreasesBoundPatchesTests</c> holds that. The bow-ties were in patches this <i>refused</i>,
///         which fell back to the blend. <see cref="IsEmbedded" /> refused every collinear boundary ear
///         and <see cref="Agrees" /> refused every rim point that landed on one; between them they cost
///         the cylinder 23 patches and the union 19. Both are fixed and each carries its own note.
///     </para>
///     <para>
///         ⚠ <b>An embedding of the patch is still not a good <i>grid</i> on the patch, and that is the
///         part no theorem covers.</b> Tutte guarantees the map; the grid is laid out <i>uniformly on
///         the square</i>, and a harmonic map distributes by its own weights rather than by arc length.
///         On a patch the quantizer cut nine quads one way and two the other, the lifted row at
///         <c>v = ½</c> can run backwards along the surface relative to the row at <c>v = 0</c> and the
///         straight-sided cell between them crosses itself. So <c>PatchExtractor.Fill</c> builds this
///         <i>and</i> the blend and keeps whichever folds less, with this one taking every tie.
///     </para>
///     <para>
///         ⚠ <b>Every answer is verified and a refusal falls back to the blend rather than to a
///         hole.</b> Tutte's guarantee is a statement about the <i>exact</i> solution of a system this
///         solves iteratively, and about a patch that is a triangulated disc — which a patch whose side
///         holds an arc quantized to zero is not. Both are checked by measuring the answer, because a
///         precondition that is argued rather than measured is how the blend survived this long. A
///         patch that fails keeps the old interior; dropping it would trade an inverted quad for a
///         hole, and holes are the other half of what makes an output unusable.
///     </para>
/// </remarks>
static class PatchParameterization {
    /// <summary>How many relaxation sweeps the interior solve gets.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed count rather than a residual against a threshold</b> — docs/plan/41 § D14, the
    ///     same rule <see cref="PatchExtractor.RelaxIterations" /> and the field solver are under. A
    ///     stopping test is a floating-point comparison, and a run that stops after a different number
    ///     of sweeps on another machine produces different bytes. Under-convergence is not a
    ///     correctness risk here because the answer is verified either way.
    /// </remarks>
    public const int Sweeps = 256;

    /// <summary>The over-relaxation factor the sweeps use.</summary>
    /// <remarks>
    ///     Plain Gauss–Seidel on a Laplacian converges at a rate set by the patch's graph diameter;
    ///     over-relaxing gets the same answer in roughly a tenth of the sweeps. The iterate may leave
    ///     the square on the way, which costs nothing — only the answer is read.
    /// </remarks>
    public const float Relaxation = 1.6f;

    /// <summary>How far outside a triangle a grid point may land and still be lifted through it.</summary>
    /// <remarks>
    ///     ⚠ A barycentric coordinate is a ratio, so this is scale-free and stays scale-free however
    ///     large the model is — which is the trap R1's <c>ScaleInvarianceTests</c> exists for. It
    ///     absorbs the last bits of a point that sits exactly on a shared edge.
    /// </remarks>
    public const float Inside = 1e-4f;

    /// <summary>How far a lifted rim point may sit from the sample it stands for, as a patch fraction.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction of the patch's own diagonal and never a distance</b> — the rule every length
    ///     in <see cref="RemeshMetrics" /> is under, and the one R1's <c>ScaleInvarianceTests</c> exists
    ///     to catch. It is four times <see cref="Sliver" />, so the sliver a collapsed arc costs the rim
    ///     is absorbed and nothing else is: any real disagreement means the interior has been solved
    ///     against a boundary the output does not have.
    /// </remarks>
    public const float RimAgreement = 0.004f;

    /// <summary>How much of one quad's parameter a side's collapsed arc is given, so it stays ordered.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An arc that quantized to zero cannot be pinned to a point, and refusing the patch
    ///         over it costs more than any other refusal here.</b> docs/plan/41 § D7 permits the
    ///         collapse — it is how a five-sided patch becomes four-sided — and it is common: measured
    ///         at a 400-quad budget, a collapsed arc was the <i>only</i> reason any patch failed to
    ///         pin, and it cost 19 of the cylinder's 38 patches with an interior, 17 of the stairs' 40,
    ///         and 5 of the sphere's 11.
    ///     </para>
    ///     <para>
    ///         Tutte's boundary has to be a homeomorphism onto the square's, so a run of distinct
    ///         source vertices mapped to one square point breaks it. A sliver of parameter keeps the
    ///         run ordered while placing it, for every purpose the grid has, at the point it collapsed
    ///         to. It moves every sample on that side by at most a thousandth of a quad, which
    ///         <see cref="RimAgreement" /> is four times looser than.
    ///     </para>
    /// </remarks>
    public const float Sliver = 1e-3f;

    /// <summary>A corner of the reference polygon a patch with this many sides is embedded in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both are convex, and that is the whole of what Tutte's theorem asks of the
    ///         domain.</b> A four-sided patch goes onto the unit square, where a uniform grid is a
    ///         rectangle by definition; a three-sided one goes onto an equilateral triangle, where the
    ///         three quad blocks round its centroid are the same statement one side short. Nothing else
    ///         in this file distinguishes the two — the pin, the solve, the embedding test and the lift
    ///         are the same code over a different corner list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Equilateral rather than right-angled, because the fan is symmetric and its domain
    ///         should be.</b> The three blocks meet at the centroid, and a domain triangle with a short
    ///         side would hand one of them a grid the other two do not get; the lifted result would then
    ///         depend on which of the three arcs the boundary walk happened to call side 0.
    ///     </para>
    /// </remarks>
    /// <param name="sides">Three or four.</param>
    /// <param name="at">Which corner, taken round the polygon.</param>
    /// <returns>The corner.</returns>
    public static Vector2 Corner(int sides, int at) {
        at = ((at % sides) + sides) % sides;

        if (sides == 3) {
            return at switch {
                0 => new(0f, 0f),
                1 => new(1f, 0f),
                _ => new(0.5f, MathF.Sqrt(3f) * 0.5f)
            };
        }

        return at switch {
            0 => new(0f, 0f),
            1 => new(1f, 0f),
            2 => new(1f, 1f),
            _ => new(0f, 1f)
        };
    }

    /// <summary>Lays one patch's interior grid through a Tutte embedding of the patch's own triangles.</summary>
    /// <param name="mesh">The conditioned surface, whose triangles the patch covers.</param>
    /// <param name="arcs">The partition's arcs, which the patch's sides index into.</param>
    /// <param name="patch">The patch.</param>
    /// <param name="samples">Per arc, the output positions along it — read only for its count.</param>
    /// <param name="output">The result being built, for the rim the grid already has.</param>
    /// <param name="sides">The four sides' output chains, in the order the boundary walk laid them.</param>
    /// <param name="wide">How many quads across, along sides 0 and 2.</param>
    /// <param name="tall">How many quads up, along sides 1 and 3.</param>
    /// <param name="refused">Why the patch could not be embedded, or <see langword="null" /> when it was.</param>
    /// <returns>
    ///     The interior positions, indexed <c>[i - 1, j - 1]</c> over <c>[1, wide) × [1, tall)</c>, or
    ///     <see langword="null" /> where the patch could not be embedded and the caller should blend.
    /// </returns>
    public static Vector3[,]? Interior(
        ManifoldMesh mesh,
        IReadOnlyList<LayoutArc> arcs,
        LayoutPatch patch,
        int[][] samples,
        EditMesh output,
        int[][] sides,
        int wide,
        int tall,
        out string? refused
    ) {
        if (wide < 2 || tall < 2) {
            refused = "the patch is one quad wide";

            return null;
        }

        var uv = Embed(
            mesh,
            arcs,
            patch,
            samples,
            output,
            sides,
            [wide, tall, wide, tall],
            out var positions,
            out var triangles,
            out var sign,
            out refused
        );

        if (uv is null) {
            return null;
        }

        var interior = new Vector3[wide - 1, tall - 1];

        for (var i = 1; i < wide; i++) {
            for (var j = 1; j < tall; j++) {
                interior[i - 1, j - 1] = Lift(
                    positions,
                    triangles,
                    uv,
                    sign,
                    new((float) i / wide, (float) j / tall)
                );
            }
        }

        return interior;
    }

    /// <summary>Lifts a list of reference-triangle points through a three-sided patch's embedding.</summary>
    /// <remarks>
    ///     ⚠ <b>The three-sided patch's interior is not a grid, so the caller says which points it
    ///     wants rather than being handed a rectangle.</b> A fan is a centroid, three spokes out to the
    ///     side midpoints and three quad blocks between them — every one of those is a point of the
    ///     reference triangle the caller can name, and none of them is at <c>(i / wide, j / tall)</c>.
    ///     What this owes is the same guarantee <see cref="Interior" /> gives: a point that comes back
    ///     is a barycentric point of one of the patch's own triangles, so it is on the surface by
    ///     construction rather than projected onto it.
    /// </remarks>
    /// <param name="mesh">The conditioned surface, whose triangles the patch covers.</param>
    /// <param name="arcs">The partition's arcs, which the patch's sides index into.</param>
    /// <param name="patch">The patch, which must be three-sided.</param>
    /// <param name="samples">Per arc, the output positions along it — read only for its count.</param>
    /// <param name="output">The result being built, for the rim the sides already have.</param>
    /// <param name="sides">The three sides' output chains, in the order the boundary walk laid them.</param>
    /// <param name="quads">How many quads run along each of the three sides.</param>
    /// <param name="wanted">The reference-triangle points to lift.</param>
    /// <param name="refused">Why the patch could not be embedded, or <see langword="null" /> when it was.</param>
    /// <returns>One surface position per wanted point, or <see langword="null" /> where it could not.</returns>
    public static Vector3[]? Fan(
        ManifoldMesh mesh,
        IReadOnlyList<LayoutArc> arcs,
        LayoutPatch patch,
        int[][] samples,
        EditMesh output,
        int[][] sides,
        int[] quads,
        ReadOnlySpan<Vector2> wanted,
        out string? refused
    ) {
        var uv = Embed(
            mesh,
            arcs,
            patch,
            samples,
            output,
            sides,
            quads,
            out var positions,
            out var triangles,
            out var sign,
            out refused
        );

        if (uv is null) {
            return null;
        }

        var lifted = new Vector3[wanted.Length];

        for (var at = 0; at < wanted.Length; at++) {
            lifted[at] = Lift(positions, triangles, uv, sign, wanted[at]);
        }

        return lifted;
    }

    /// <summary>The Tutte embedding itself: localize, pin, solve, and verify all three ways.</summary>
    static Vector2[]? Embed(
        ManifoldMesh mesh,
        IReadOnlyList<LayoutArc> arcs,
        LayoutPatch patch,
        int[][] samples,
        EditMesh output,
        int[][] sides,
        int[] quads,
        out Vector3[] positions,
        out int[] triangles,
        out float sign,
        out string? refused
    ) {
        refused = null;
        sign = 0f;

        var local = Localize(mesh, patch, out triangles, out positions);

        if (local.Count == 0 || triangles.Length == 0) {
            refused = "the patch has no triangles";

            return null;
        }

        var pinned = new Vector2[local.Count];
        var isPinned = new bool[local.Count];

        if (!Pin(mesh, arcs, patch, samples, local, quads, pinned, isPinned, out var rims)) {
            refused = "the rim could not be pinned to the reference polygon";

            return null;
        }

        if (!IsDisc(triangles, local.Count, isPinned)) {
            refused = "the patch is not a triangulated disc";

            return null;
        }

        var uv = Solve(positions, triangles, pinned, isPinned);

        if (!IsEmbedded(triangles, uv, isPinned, out sign)) {
            refused = "the solved map is not an embedding";

            return null;
        }

        if (!Agrees(positions, rims, output, sides, quads)) {
            refused = "the pinned rim disagrees with the grid's";

            return null;
        }

        return uv;
    }

    /// <summary>The patch's vertices, renumbered from zero in ascending source order.</summary>
    /// <remarks>
    ///     ⚠ <b>Ascending source order and not first-seen order</b> — docs/plan/41 § D14. First-seen
    ///     order depends on how the flood happened to visit the triangles, so the same patch would
    ///     solve in a different sequence between two runs and the last bit of the answer would not be a
    ///     function of the input.
    /// </remarks>
    static Dictionary<int, int> Localize(
        ManifoldMesh mesh,
        LayoutPatch patch,
        out int[] triangles,
        out Vector3[] positions
    ) {
        var seen = new SortedSet<int>();

        foreach (var triangle in patch.Triangles) {
            foreach (var corner in mesh.Corners(triangle)) {
                seen.Add(corner);
            }
        }

        var local = new Dictionary<int, int>(seen.Count);
        var found = new Vector3[seen.Count];

        foreach (var vertex in seen) {
            found[local.Count] = mesh.Positions[vertex];
            local[vertex] = local.Count;
        }

        positions = found;
        triangles = new int[patch.Triangles.Length * 3];

        for (var at = 0; at < patch.Triangles.Length; at++) {
            var corners = mesh.Corners(patch.Triangles[at]);

            triangles[(at * 3) + 0] = local[corners[0]];
            triangles[(at * 3) + 1] = local[corners[1]];
            triangles[(at * 3) + 2] = local[corners[2]];
        }

        return local;
    }

    /// <summary>Pins each side's chain to the matching side of the reference polygon.</summary>
    /// <remarks>
    ///     <para>
    ///         The walk is the one <see cref="PatchExtractor" /> fills against: side <i>k</i> runs
    ///         C<i>k</i> → C<i>k+1</i> and becomes the straight run from <see cref="Corner" />
    ///         <i>k</i> to <see cref="Corner" /> <i>k+1</i>, so on the square side 0 is <c>(t, 0)</c>,
    ///         side 1 is <c>(1, t)</c>, side 2 is <c>(1 - t, 1)</c> and side 3 is <c>(0, 1 - t)</c>.
    ///         The joins agree by construction, so a corner pinned twice is pinned to the same point
    ///         twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Within a side, <c>t</c> advances by the arc's <i>quantized count</i> and not by its
    ///         length.</b> A side made of a two-quad arc and a three-quad arc puts its join at
    ///         <c>t = 2/5</c> whatever the two arcs measure, because that is where the join's output
    ///         sample is. Pinning by length instead moves the boundary the interior is solved against
    ///         off the boundary the output actually has.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An arc that quantized to zero is refused here rather than pinned.</b> Its whole
    ///         chain would collapse onto one square point, which makes the boundary non-injective and
    ///         takes Tutte's guarantee with it — § D7 permits the collapse and this is a place that
    ///         cannot use it.
    ///     </para>
    /// </remarks>
    static bool Pin(
        ManifoldMesh mesh,
        IReadOnlyList<LayoutArc> arcs,
        LayoutPatch patch,
        int[][] samples,
        Dictionary<int, int> local,
        int[] quads,
        Vector2[] pinned,
        bool[] isPinned,
        out List<(float T, int Vertex)>[] rims
    ) {
        rims = new List<(float T, int Vertex)>[quads.Length];

        for (var at = 0; at < quads.Length; at++) {
            rims[at] = [];
        }

        for (var at = 0; at < quads.Length; at++) {
            var wanted = quads[at];
            var collapsed = 0;
            var walked = 0;

            foreach (var use in patch.Sides[at]) {
                var count = samples[use.Arc].Length - 1;

                walked += count;

                if (count <= 0) {
                    collapsed++;
                }
            }

            if (walked != wanted) {
                return false;
            }

            var total = wanted + (collapsed * Sliver);
            var offset = 0f;
            var from = Corner(quads.Length, at);
            var to = Corner(quads.Length, at + 1);

            foreach (var use in patch.Sides[at]) {
                var arc = arcs[use.Arc];
                var count = samples[use.Arc].Length - 1;
                var width = count <= 0 ? Sliver : count;
                var chain = arc.Vertices;
                var lengths = new float[chain.Length];

                for (var step = 1; step < chain.Length; step++) {
                    var one = chain[use.Reversed ? chain.Length - step : step - 1];
                    var two = chain[use.Reversed ? chain.Length - step - 1 : step];

                    lengths[step] = lengths[step - 1] + Vector3.Distance(
                        mesh.Positions[one],
                        mesh.Positions[two]
                    );
                }

                var span = lengths[^1];

                for (var step = 0; step < chain.Length; step++) {
                    var vertex = chain[use.Reversed ? chain.Length - 1 - step : step];

                    if (!local.TryGetValue(vertex, out var index)) {
                        return false;
                    }

                    var within = span > 0f ? lengths[step] / span : (float) step / (chain.Length - 1);
                    var t = Math.Clamp((offset + (within * width)) / total, 0f, 1f);

                    pinned[index] = Vector2.Lerp(from, to, t);
                    isPinned[index] = true;

                    if (rims[at].Count == 0 || rims[at][^1].Vertex != index) {
                        rims[at].Add((t, index));
                    }
                }

                offset += width;
            }
        }

        return true;
    }

    /// <summary>Whether the patch is the triangulated disc Tutte's theorem is a statement about.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured off the triangles rather than argued from the layout.</b> Every edge is either
    ///     interior — two triangles, one from each side — or on the boundary, and every vertex the
    ///     boundary touches must be one the four sides pinned. A patch with a hole in it, a patch whose
    ///     triangles are two components, and a patch whose boundary walks a vertex the arcs never
    ///     visited all fail here, and all three would otherwise produce an embedding that is not one.
    /// </remarks>
    static bool IsDisc(int[] triangles, int count, bool[] isPinned) {
        var sides = new Dictionary<(int, int), int>(triangles.Length);
        var touched = new bool[count];

        for (var at = 0; at < triangles.Length; at += 3) {
            for (var corner = 0; corner < 3; corner++) {
                var a = triangles[at + corner];
                var b = triangles[at + ((corner + 1) % 3)];

                touched[a] = true;

                var key = a < b ? (a, b) : (b, a);

                sides[key] = sides.GetValueOrDefault(key) + 1;
            }
        }

        var rim = new bool[count];

        foreach (var (edge, uses) in sides) {
            if (uses > 2) {
                return false;
            }

            if (uses != 1) {
                continue;
            }

            if (!isPinned[edge.Item1] || !isPinned[edge.Item2]) {
                return false;
            }

            rim[edge.Item1] = true;
            rim[edge.Item2] = true;
        }

        for (var vertex = 0; vertex < count; vertex++) {
            if (touched[vertex] && isPinned[vertex] && !rim[vertex]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Places every unpinned vertex at the mean-value combination of its neighbours.</summary>
    /// <remarks>
    ///     <para>
    ///         The weight of edge <c>ij</c> is <c>(tan(α/2) + tan(β/2)) / |ij|</c> over the two angles
    ///         at <c>i</c> either side of it — accumulated a triangle at a time, because the angle at
    ///         <c>i</c> in triangle <c>ijk</c> is one of the two halves for edge <c>ij</c> and one of
    ///         the two for edge <c>ik</c> at once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The half-angle tangent is taken as <c>|û × v̂| / (1 + û · v̂)</c> off <i>normalised</i>
    ///         edge directions</b>, which is scale-free and stays finite. Forming it from the raw cross
    ///         product instead makes it a quantity that scales as the square of the model, and a
    ///         millimetre-wide mesh then has every weight underflow to zero — the failure
    ///         <see cref="ScaleSafe" /> is written about, in the one place it would be silent.
    ///     </para>
    /// </remarks>
    static Vector2[] Solve(Vector3[] positions, int[] triangles, Vector2[] pinned, bool[] isPinned) {
        var count = positions.Length;
        var weights = new Dictionary<(int, int), float>(triangles.Length);

        for (var at = 0; at < triangles.Length; at += 3) {
            for (var corner = 0; corner < 3; corner++) {
                var i = triangles[at + corner];
                var j = triangles[at + ((corner + 1) % 3)];
                var k = triangles[at + ((corner + 2) % 3)];

                var toJ = positions[j] - positions[i];
                var toK = positions[k] - positions[i];
                var half = HalfAngleTangent(toJ, toK);

                Add(weights, i, j, half / Math.Max(toJ.Length(), float.Epsilon));
                Add(weights, i, k, half / Math.Max(toK.Length(), float.Epsilon));
            }
        }

        // One row per vertex, its neighbours in ascending order: § D14 wants the same numbers added in
        // the same sequence on every run, and a dictionary's order is not that.
        var rows = new List<(int Neighbour, float Weight)>[count];

        for (var vertex = 0; vertex < count; vertex++) {
            rows[vertex] = [];
        }

        foreach (var ((i, j), weight) in weights) {
            rows[i].Add((j, weight));
        }

        var uv = new Vector2[count];

        for (var vertex = 0; vertex < count; vertex++) {
            rows[vertex].Sort((one, two) => one.Neighbour.CompareTo(two.Neighbour));
            uv[vertex] = isPinned[vertex] ? pinned[vertex] : new(0.5f, 0.5f);
        }

        for (var sweep = 0; sweep < Sweeps; sweep++) {
            for (var vertex = 0; vertex < count; vertex++) {
                if (isPinned[vertex] || rows[vertex].Count == 0) {
                    continue;
                }

                var sum = Vector2.Zero;
                var total = 0f;

                foreach (var (neighbour, weight) in rows[vertex]) {
                    sum += uv[neighbour] * weight;
                    total += weight;
                }

                if (total <= 0f) {
                    continue;
                }

                uv[vertex] += (sum / total - uv[vertex]) * Relaxation;
            }
        }

        return uv;
    }

    /// <summary>The tangent of half the angle between two directions, taken scale-free.</summary>
    static float HalfAngleTangent(Vector3 one, Vector3 two) {
        var a = ScaleSafe.Unit(one);
        var b = ScaleSafe.Unit(two);
        var denominator = 1f + Vector3.Dot(a, b);

        return denominator > 0f ? Vector3.Cross(a, b).Length() / denominator : 0f;
    }

    /// <summary>Accumulates one directed weight.</summary>
    static void Add(Dictionary<(int, int), float> into, int from, int to, float weight) {
        if (float.IsFinite(weight) && weight > 0f) {
            into[(from, to)] = into.GetValueOrDefault((from, to)) + weight;
        }
    }

    /// <summary>Whether the solved map is the embedding Tutte promises, measured triangle by triangle.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Checked rather than assumed, and the reason is that the guarantee is about the exact
    ///         solution.</b> <see cref="Sweeps" /> is a fixed count, so a patch whose graph is long and
    ///         thin may still be short of converged — and an unconverged iterate has no such property. A
    ///         single triangle that has turned over refuses the whole patch, which costs it the blend it
    ///         would have had anyway.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>A triangle whose three corners are all pinned has <i>exactly</i> zero area and that
    ///         is the square's fault rather than the map's.</b> Tutte's boundary here is a square, whose
    ///         four sides are straight: every vertex of side 0 is pinned at <c>y = 0</c> exactly, so an
    ///         "ear" — a patch triangle whose three corners are three consecutive vertices of one arc —
    ///         is collinear in the parameter domain by construction, whatever the solve does. It has not
    ///         folded, nothing overlaps it, and the interior grid never lifts through it because
    ///         <see cref="Lift" /> takes only strictly positive triangles.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Refusing on it was throwing away patches that were correct, and it was most of the
    ///         defect.</b> Measured across the seven fixtures before this distinction existed, <i>every
    ///         single</i> patch this refused had <b>zero</b> flipped triangles and one to four collinear
    ///         ears — on the cylinder that was 18 patches and 45 inverted quads, on the union 11 patches
    ///         and 17. Each fell back to the transfinite blend, which is the one construction here that
    ///         is not injective, and the blend is what folded them. ⚠ <b>The zero is still fatal when an
    ///         <i>unpinned</i> vertex is in it</b>: that is an interior vertex the solve has left on a
    ///         line, which is a genuine degeneracy and not a property of the domain.
    ///     </para>
    /// </remarks>
    static bool IsEmbedded(int[] triangles, Vector2[] uv, bool[] isPinned, out float sign) {
        sign = 0f;

        for (var at = 0; at < triangles.Length; at += 3) {
            var one = triangles[at + 0];
            var two = triangles[at + 1];
            var three = triangles[at + 2];
            var area = SignedArea(uv[one], uv[two], uv[three]);

            if (!double.IsFinite(area)) {
                return false;
            }

            if (area == 0d) {
                if (!isPinned[one] || !isPinned[two] || !isPinned[three]) {
                    return false;
                }

                continue;
            }

            var here = Math.Sign(area);

            if (sign == 0f) {
                sign = here;
            } else if (sign != here) {
                return false;
            }
        }

        return sign != 0f;
    }

    /// <summary>Twice a parameter-domain triangle's signed area, in double and about its own first corner.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A <see cref="float" /> cross product of unit-square coordinates cancels to
    ///         <i>exactly</i> zero on a triangle that has a perfectly good area, and that zero was
    ///         throwing whole patches away.</b> The corners live in <c>[0, 1]²</c>, so the products in
    ///         <c>(b−a)×(c−a)</c> are around a quarter while the answer for a triangle of a well-solved
    ///         patch is around <c>1/n</c> — at a hundred triangles the difference of two floats near
    ///         <c>0.25</c> has an absolute resolution of about <c>3e-8</c>, and a sliver of the interior
    ///         lands inside it. Measured across the seven fixtures, <i>every</i> patch
    ///         <see cref="IsEmbedded" /> refused had <b>zero</b> flipped triangles and one to four that
    ///         had cancelled to zero: the map was a valid embedding every time and the arithmetic said
    ///         otherwise. On the cylinder that cost 18 patches and 45 inverted quads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Subtracting first and widening to <see cref="double" /> is what makes it exact
    ///         enough, and both halves are needed.</b> The difference of two <see cref="float" /> values
    ///         is representable in a <see cref="double" /> exactly, so the two edge vectors are formed
    ///         with no error at all; the products and their difference then carry 53 bits instead of 24.
    ///         A zero out of this one is a triangle that is genuinely degenerate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a tolerance, which is the trap next door.</b> An epsilon here would be an
    ///         absolute bound in parameter space and so a statement about how many triangles a patch is
    ///         allowed to have — the mistake <see cref="ScaleSafe" /> exists for, in a domain where it
    ///         would be silent. The answer is to compute the quantity accurately, not to widen what
    ///         counts as zero.
    ///     </para>
    /// </remarks>
    static double SignedArea(Vector2 a, Vector2 b, Vector2 c) {
        double bx = (double) b.X - a.X;
        double by = (double) b.Y - a.Y;
        double cx = (double) c.X - a.X;
        double cy = (double) c.Y - a.Y;

        return (bx * cy) - (by * cx);
    }

    /// <summary>Whether the square's rim, resolved through the pinning, is the rim the grid actually has.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one check that is about the <i>seam</i> rather than about the map, and without
    ///         it a valid embedding still produces folded quads.</b> The interior is solved against a
    ///         boundary pinned from the arcs; the grid's boundary is the samples
    ///         <see cref="PatchExtractor" /> already placed. The two are the same curve by construction
    ///         — until they are not, which is what an arc whose ends
    ///         <see cref="PatchExtractor" />'s union-find merged onto a different vertex does, and what
    ///         a side that walks a vertex another side also walks does. The interior is then correct
    ///         about a rim the output has never had, and the first ring of quads reaches across the real
    ///         one and bow-ties. Measured on a box before this existed: an interior vertex 0.15 of the
    ///         patch away from where the map said the rim was, in a quad whose two halves came back
    ///         177° apart.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>Resolved along the pinned chain and <i>not</i> through <see cref="Lift" />, because
    ///         the map does not determine a rim point and never did.</b> A square point on side 0 has
    ///         <c>y = 0</c> exactly, and so does every vertex pinned to that side — so wherever the patch
    ///         triangle carrying that stretch of rim is an "ear" with all three corners on the side, its
    ///         image is a <i>segment</i> and a point on it stands for a whole line of the triangle.
    ///         <see cref="Lift" /> takes only strictly positive triangles, so it answered such a point
    ///         with the nearest triangle it would accept and returned a point off by the ear's own
    ///         height. Measured across the seven fixtures, that read as a rim disagreeing by
    ///         <c>0.02</c> to <c>0.31</c> of the patch diagonal against a tolerance of <c>0.004</c> —
    ///         5× to 78× over — on patches whose map was perfectly good, and it refused 19 patches of
    ///         the union and 23 of the cylinder into the blend that then folded them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pinning is the rim's parameterization and it is exact.</b>
    ///         <see cref="Pin" /> places chain vertex <i>v</i> at the same fraction of the same chain
    ///         that <c>PatchExtractor.Sample</c> places output sample <i>k</i> at, so walking the pinned
    ///         run and interpolating between consecutive vertices reproduces the sample exactly when
    ///         nothing has moved it — and differs by the whole of the move when something has, which is
    ///         the defect this exists to catch. No triangle search is involved, so the ear cannot lie.
    ///     </para>
    /// </remarks>
    static bool Agrees(
        Vector3[] positions,
        List<(float T, int Vertex)>[] rims,
        EditMesh output,
        int[][] sides,
        int[] quads
    ) {
        var diagonal = Diagonal(positions);

        if (!(diagonal > 0f)) {
            return false;
        }

        var allowed = diagonal * RimAgreement;

        for (var at = 0; at < quads.Length; at++) {
            var run = rims[at];

            if (run.Count < 2 || sides[at].Length != quads[at] + 1) {
                return false;
            }

            for (var step = 0; step <= quads[at]; step++) {
                var t = (float) step / quads[at];
                var placed = At(positions, run, t);

                // The side's own chain, in the order the boundary walk laid it — which is the order
                // the pinning walked the same side's arcs in, so step `k` is step `k`.
                if (Vector3.Distance(placed, output.Positions[sides[at][step]]) > allowed) {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The point at parameter <c>t</c> along one side's pinned run of chain vertices.</summary>
    static Vector3 At(Vector3[] positions, List<(float T, int Vertex)> run, float t) {
        for (var at = 1; at < run.Count; at++) {
            if (t > run[at].T && at != run.Count - 1) {
                continue;
            }

            var span = run[at].T - run[at - 1].T;
            var fraction = span > 0f ? Math.Clamp((t - run[at - 1].T) / span, 0f, 1f) : 0f;

            return Vector3.Lerp(positions[run[at - 1].Vertex], positions[run[at].Vertex], fraction);
        }

        return positions[run[0].Vertex];
    }

    /// <summary>The patch's own bounding-box diagonal, which every length here is a fraction of.</summary>
    static float Diagonal(Vector3[] positions) {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);

        foreach (var position in positions) {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        return (maximum - minimum).Length();
    }

    /// <summary>The surface point a square point stands for, through the triangle that contains it.</summary>
    /// <remarks>
    ///     ⚠ <b>The first triangle that contains the point, and the least-outside one when none
    ///     does.</b> Both are a function of the patch's triangle order, which § D14 requires and which
    ///     <see cref="LayoutPatch.Triangles" /> already fixes — the search is not free to reorder
    ///     itself. A point sitting exactly on a shared edge is claimed by both of its triangles and the
    ///     two answers are the same point, so the tie costs nothing; a point a hair outside the square's
    ///     boundary happens where the embedding has pushed the rim in, and the nearest triangle is what
    ///     it means.
    /// </remarks>
    static Vector3 Lift(Vector3[] positions, int[] triangles, Vector2[] uv, float sign, Vector2 point) {
        var best = 0;
        var bestOutside = float.PositiveInfinity;
        var bestWeights = new Vector3(1f, 0f, 0f);

        for (var at = 0; at < triangles.Length; at += 3) {
            var a = uv[triangles[at + 0]];
            var b = uv[triangles[at + 1]];
            var c = uv[triangles[at + 2]];

            var area = SignedArea(a, b, c) * sign;

            if (area <= 0d) {
                continue;
            }

            var wa = (float) (SignedArea(point, b, c) * sign / area);
            var wb = (float) (SignedArea(point, c, a) * sign / area);
            var wc = 1f - wa - wb;

            var outside = MathF.Max(MathF.Max(-wa, -wb), -wc);

            if (outside < bestOutside) {
                best = at;
                bestOutside = outside;
                bestWeights = new(wa, wb, wc);
            }

            if (outside <= Inside) {
                break;
            }
        }

        var weights = Vector3.Clamp(bestWeights, Vector3.Zero, Vector3.One);
        var scale = weights.X + weights.Y + weights.Z;

        if (scale <= 0f) {
            return positions[triangles[best]];
        }

        return ((positions[triangles[best + 0]] * weights.X)
            + (positions[triangles[best + 1]] * weights.Y)
            + (positions[triangles[best + 2]] * weights.Z)) / scale;
    }
}
