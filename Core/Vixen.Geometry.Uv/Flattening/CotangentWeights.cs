// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv.Flattening;

/// <summary>The three cotangents of a triangle, one per edge, already made safe for a Laplacian.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5's second rung is a cotangent Laplacian and § B1's conjugate gradient is
///         <b>only valid on a symmetric positive-definite matrix</b>. Those two facts collide on the
///         first obtuse triangle, and the collision is the most consequential decision in U2.
///     </para>
///     <para>
///         ⚠ <b>A cotangent goes negative on an obtuse angle, and clamping it away silently is not the
///         answer — but neither is leaving it.</b> The Laplacian <c>Σ w(eᵢ − eⱼ)(eᵢ − eⱼ)ᵀ</c> is
///         positive semi-definite exactly when every <c>w</c> is non-negative. With one negative weight
///         the matrix may still be definite and may not; there is no test cheaper than factorizing it,
///         and when it is not, conjugate gradient does not fail — it converges to a saddle. The chart
///         comes back <i>folded</i>, the flip count catches it, the repair pass runs on a system that
///         is indefinite in the same way, and nothing anywhere names the obtuse triangle.
///     </para>
///     <para>
///         <b>What this does, and why that and not the alternatives.</b> Every weight is clamped up to
///         <see cref="Floor" /> of the chart's largest, which makes the Laplacian positive definite by
///         construction once one vertex is anchored, and provably so rather than usually. Three other
///         answers were available:
///     </para>
///     <para>
///         <b>Clamp to zero</b> is the common one and it is worse than a small positive floor for a
///         reason that only shows up on bad input: a zero weight <i>removes the edge</i>, and a fan of
///         very obtuse triangles can lose enough edges to disconnect a vertex from the chart. An
///         isolated vertex is an empty row, <see cref="Solving.ConjugateGradient" /> masks empty rows
///         out by design, and the vertex then stays wherever the initialization left it — which is a
///         fold that looks like a solver bug. A floor keeps the graph connected for the price of a
///         weight that is numerically zero anyway.
///     </para>
///     <para>
///         <b>Uniform weights on the affected edge</b> mixes two discretizations in one matrix. It is
///         stable, but the weight it substitutes is <i>larger</i> than the cotangents around it by
///         whatever the mesh's units happen to be, so an obtuse triangle becomes the stiffest thing in
///         the chart and drags the parameterization towards itself. That is a visible artefact where a
///         clamp is an invisible one.
///     </para>
///     <para>
///         <b>Mean-value coordinates</b> are the principled fix — they are positive everywhere by
///         construction and need no clamp at all — and they are ruled out by the solver rather than by
///         taste: the mean-value matrix is <b>not symmetric</b>, and every line of § B1's conjugate
///         gradient assumes it is. Using them would mean a second solver, which § D5 spent its
///         argument on not owning.
///     </para>
///     <para>
///         <b>Intrinsic Delaunay flipping</b> is the answer that loses nothing — flip the mesh's edges
///         intrinsically until no cotangent is negative, and the Laplacian is unconditionally
///         PSD with no clamp anywhere. It is also a signed-distance-carrying edge-flip machine over an
///         intrinsic triangulation, which is a phase of its own and is not U2's. ⚠ <b>This is the
///         upgrade path if the clamp ever shows up in an asset</b>, and the count in
///         <see cref="Clamped" /> is what would say that it had.
///     </para>
///     <para>
///         ⚠ <b>Cotangents are scale-free, so the floor being relative costs nothing and is still the
///         right shape.</b> A cotangent is a ratio of two lengths and does not change when the model
///         does, which is what makes the whole second rung's matrix identical at any scale. The floor
///         is taken against the chart's largest weight anyway, because <c>1e-12</c> against a chart
///         whose weights are all <c>1e-9</c> is an absolute epsilon wearing a relative one's clothes —
///         the mistake <c>EditMesh.cs</c>'s weld tolerance and docs/plan/24 § P1's poles both record.
///     </para>
/// </remarks>
readonly struct CotangentWeights {
    /// <summary>How small a weight may be, against the chart's largest, before it is raised.</summary>
    public const double Floor = 1e-9;

    /// <summary>The weight of the edge opposite each corner: index 0 is the edge from corner 1 to 2.</summary>
    public required double[] Edge { get; init; }

    /// <summary>How many weights came out <i>negative</i> and were raised to the floor.</summary>
    /// <remarks>
    ///     <para>
    ///         Reported rather than swallowed. See the remarks on the type.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Negative, not "below the floor", and the difference is a whole grid of quads.</b> A
    ///         right angle has a cotangent of exactly zero, which is raised for the same
    ///         graph-connectivity reason as a negative one and is <i>not</i> an obtuse triangle — and an
    ///         axis-aligned quad grid split along its diagonal produces one per quad. Counting those
    ///         would report a thousand obtuse corners on a mesh that has none, which is a warning
    ///         nobody would read twice.
    ///     </para>
    /// </remarks>
    public required int Clamped { get; init; }

    /// <summary>The most negative cotangent there was, raw, or zero when none was negative.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The count alone is not actionable and this is what makes it so.</b> Measured while
    ///         writing this: a plain 32×32 saddle grid produces one negative cotangent per quad — a
    ///         thousand of them — because a square stretched over a curved surface has a corner past a
    ///         right angle. A thousand is also what the sheared grid of 170° slivers reports. The count
    ///         does not separate the two cases and this number does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Raw and <i>not</i> a fraction of the largest weight, which was the first attempt and
    ///         was worse than useless.</b> A chart full of slivers has enormous <i>positive</i> weights
    ///         too — the acute corners of the same triangles — so dividing by the largest made the bad
    ///         mesh look better than the ordinary one: <c>−0.25</c> against <c>−0.44</c>, exactly
    ///         backwards. A cotangent is already dimensionless and already reads as an angle, which is
    ///         the whole reason it is the right number to print: <c>−0.4</c> is about 114°, <c>−2</c> is
    ///         about 153°, and <c>−6</c> is about 170°.
    ///     </para>
    /// </remarks>
    public required double Worst { get; init; }

    /// <summary>Builds every triangle's three weights.</summary>
    /// <param name="chart">The chart.</param>
    /// <param name="frames">Each triangle laid flat in its own plane.</param>
    /// <returns>Three weights per triangle, in triangle order, and how many were raised.</returns>
    /// <remarks>
    ///     Two passes, because the floor is relative to the largest weight and the largest weight is
    ///     not known until every triangle has been visited. A degenerate triangle contributes no
    ///     weights at all — its cotangents are infinite — and its three entries are left at the floor.
    /// </remarks>
    public static CotangentWeights Build(ChartMesh chart, TriangleFrame[] frames) {
        var edge = new double[chart.TriangleCount * 3];
        var largest = 0d;

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            var frame = frames[triangle];

            if (frame.IsDegenerate) {
                continue;
            }

            var doubled = frame.DoubleArea;

            // The cotangent at a corner is the dot product of the two edges leaving it over twice the
            // area. Written out in the triangle's own frame, where corner 0 is the origin, corner 1 is
            // (X1, 0) and corner 2 is (X2, Y2). Each weight belongs to the edge *opposite* its corner.
            var atZero = frame.X1 * frame.X2 / doubled;
            var atOne = frame.X1 * (frame.X1 - frame.X2) / doubled;
            var atTwo = ((frame.Y2 * frame.Y2) - (frame.X2 * (frame.X1 - frame.X2))) / doubled;

            edge[(triangle * 3) + 0] = atZero;
            edge[(triangle * 3) + 1] = atOne;
            edge[(triangle * 3) + 2] = atTwo;

            largest = Math.Max(largest, Math.Max(Math.Abs(atZero), Math.Max(Math.Abs(atOne), Math.Abs(atTwo))));
        }

        var floor = largest > 0d ? Floor * largest : Floor;
        var clamped = 0;
        var worst = 0d;

        for (var index = 0; index < edge.Length; index++) {
            if (edge[index] > floor) {
                continue;
            }

            // ⚠ A degenerate triangle is raised but not counted. It contributes nothing to the matrix
            // — `Arap` skips it — so a count that included it would report a clamp on every sliver a
            // conditioning pass left behind and say nothing about obtuse angles, which is what the
            // count is read for. Nor is a right angle's exact zero counted; see `Clamped`.
            if (edge[index] < 0d && !frames[index / 3].IsDegenerate) {
                clamped++;

                worst = Math.Min(worst, edge[index]);
            }

            edge[index] = floor;
        }

        return new() { Edge = edge, Clamped = clamped, Worst = worst };
    }
}
