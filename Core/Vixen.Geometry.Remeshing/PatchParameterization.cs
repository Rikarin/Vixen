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
///         ⚠⚠ <b>It does what it claims and it is <i>not</i> the whole of the inverted-quad defect —
///         measured, and § D8's attribution is narrowed rather than confirmed.</b> On the sphere, the
///         one fixture with no hard edge, the patches this fills come back with no inverted quad at
///         all: 0 of 175, worst corner <c>+0.474</c>, against 4 of 130 in the patches it still
///         refuses. On the six hard-surface fixtures it does not close them — the box keeps 11
///         inverted faces of 215 in patches it filled, the stairs 11 of 210 — and those quads are
///         near-<i>planar</i> bow-ties, their two halves 176° to 180° apart with edges within a factor
///         of three of each other. A planar bow-tie is not a blend that folded and it is not curvature
///         either; the patch is bounded by a map that is provably an embedding, so what is left is
///         the region itself doubling back, which is a patch spanning a crease the layout should have
///         cut at. A further 3 to 11 faces per fixture are in patches only one quad wide, which have
///         no interior vertex at all and which no interior construction reaches.
///         <c>QuadQualityTests</c> holds the per-fixture numbers.
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

    /// <summary>Lays one patch's interior grid through a Tutte embedding of the patch's own triangles.</summary>
    /// <param name="mesh">The conditioned surface, whose triangles the patch covers.</param>
    /// <param name="arcs">The partition's arcs, which the patch's sides index into.</param>
    /// <param name="patch">The patch.</param>
    /// <param name="samples">Per arc, the output positions along it — read only for its count.</param>
    /// <param name="output">The result being built, for the rim the grid already has.</param>
    /// <param name="grid">The patch's grid, with its four sides filled in and its interior not.</param>
    /// <param name="wide">How many quads across, along sides 0 and 2.</param>
    /// <param name="tall">How many quads up, along sides 1 and 3.</param>
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
        int[][] grid,
        int wide,
        int tall
    ) {
        if (wide < 2 || tall < 2) {
            return null;
        }

        var local = Localize(mesh, patch, out var triangles, out var positions);

        if (local.Count == 0 || triangles.Length == 0) {
            return null;
        }

        var pinned = new Vector2[local.Count];
        var isPinned = new bool[local.Count];

        if (!Pin(mesh, arcs, patch, samples, local, wide, tall, pinned, isPinned)) {
            return null;
        }

        if (!IsDisc(triangles, local.Count, isPinned)) {
            return null;
        }

        var uv = Solve(positions, triangles, pinned, isPinned);

        if (!IsEmbedded(triangles, uv, out var sign)) {
            return null;
        }

        if (!Agrees(positions, triangles, uv, sign, output, grid, wide, tall)) {
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

    /// <summary>Pins the four sides' chains to the four sides of the unit square.</summary>
    /// <remarks>
    ///     <para>
    ///         The walk is the one <see cref="PatchExtractor" /> fills against: side 0 runs C0 → C1 and
    ///         becomes <c>(t, 0)</c>, side 1 runs C1 → C2 and becomes <c>(1, t)</c>, side 2 becomes
    ///         <c>(1 - t, 1)</c> and side 3 becomes <c>(0, 1 - t)</c>. The four joins agree by
    ///         construction, so a corner pinned twice is pinned to the same point twice.
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
        int wide,
        int tall,
        Vector2[] pinned,
        bool[] isPinned
    ) {
        for (var at = 0; at < 4; at++) {
            var quads = at % 2 == 0 ? wide : tall;
            var collapsed = 0;
            var walked = 0;

            foreach (var use in patch.Sides[at]) {
                var count = samples[use.Arc].Length - 1;

                walked += count;

                if (count <= 0) {
                    collapsed++;
                }
            }

            if (walked != quads) {
                return false;
            }

            var total = quads + (collapsed * Sliver);
            var offset = 0f;

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

                    pinned[index] = at switch {
                        0 => new(t, 0f),
                        1 => new(1f, t),
                        2 => new(1f - t, 1f),
                        _ => new(0f, 1f - t)
                    };

                    isPinned[index] = true;
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
    ///     ⚠ <b>Checked rather than assumed, and the reason is that the guarantee is about the exact
    ///     solution.</b> <see cref="Sweeps" /> is a fixed count, so a patch whose graph is long and thin
    ///     may still be short of converged — and an unconverged iterate has no such property. A single
    ///     triangle that has turned over, or has no area at all, refuses the whole patch, which costs it
    ///     the blend it would have had anyway.
    /// </remarks>
    static bool IsEmbedded(int[] triangles, Vector2[] uv, out float sign) {
        sign = 0f;

        for (var at = 0; at < triangles.Length; at += 3) {
            var a = uv[triangles[at + 0]];
            var b = uv[triangles[at + 1]];
            var c = uv[triangles[at + 2]];

            var area = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

            if (area == 0f || !float.IsFinite(area)) {
                return false;
            }

            var here = MathF.Sign(area);

            if (sign == 0f) {
                sign = here;
            } else if (sign != here) {
                return false;
            }
        }

        return sign != 0f;
    }

    /// <summary>Whether the square's rim lifts back onto the rim the grid actually has.</summary>
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
    ///         Only the rim is checked, because the rim is the only place the two constructions are
    ///         supposed to produce the same answer — the interior has nothing to be compared against,
    ///         which is why it needed a parameterization in the first place.
    ///     </para>
    /// </remarks>
    static bool Agrees(
        Vector3[] positions,
        int[] triangles,
        Vector2[] uv,
        float sign,
        EditMesh output,
        int[][] grid,
        int wide,
        int tall
    ) {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);

        foreach (var position in positions) {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        var diagonal = (maximum - minimum).Length();

        if (!(diagonal > 0f)) {
            return false;
        }

        var allowed = diagonal * RimAgreement;

        for (var i = 0; i <= wide; i++) {
            for (var j = 0; j <= tall; j++) {
                if (i != 0 && i != wide && j != 0 && j != tall) {
                    continue;
                }

                var lifted = Lift(positions, triangles, uv, sign, new((float) i / wide, (float) j / tall));

                if (Vector3.Distance(lifted, output.Positions[grid[i][j]]) > allowed) {
                    return false;
                }
            }
        }

        return true;
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

            var area = (((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X))) * sign;

            if (area <= 0f) {
                continue;
            }

            var wa = ((((b.X - point.X) * (c.Y - point.Y)) - ((b.Y - point.Y) * (c.X - point.X))) * sign) / area;
            var wb = ((((c.X - point.X) * (a.Y - point.Y)) - ((c.Y - point.Y) * (a.X - point.X))) * sign) / area;
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
