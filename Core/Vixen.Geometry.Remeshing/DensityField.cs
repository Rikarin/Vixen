// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>One per-vertex target edge length, computed once, consumed by everything downstream.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D9.</b>
///         <c>targetLength(v) = clamp(base × curvatureTerm(v) × densityPaint(v) × featureTerm(v), min, max)</c>,
///         where <c>base</c> comes from the quad budget and the surface area, <c>curvatureTerm</c> is
///         driven by <see cref="RemeshSettings.Adaptivity" />, <c>densityPaint</c> is
///         <see cref="RemeshSettings.DensityMask" />, and <c>featureTerm</c> tightens near a feature
///         polyline so a hard edge is not straddled by one enormous quad.
///     </para>
///     <para>
///         ⚠ <b>One field and not three multipliers applied at three stages, and § D9 says why in as
///         many words.</b> "Adaptive Size", density masking and keep-detail-near-the-creases are the
///         same mechanism here; three separate ones interacting by accident is how a remesher acquires
///         settings that only work in certain combinations. The pre-remesh, the quantization and the
///         extraction all read this one array.
///     </para>
///     <para>
///         ⚠ <b>Every bound is relative to <c>base</c> and never a length.</b> A clamp written as
///         "no smaller than a millimetre" is a claim about how big a model is, which is the mistake
///         doc 24 records twice and R1's <c>ScaleInvarianceTests</c> exists to catch.
///     </para>
///     <para>
///         ⚠ <b>At <see cref="RemeshSettings.Adaptivity" /> zero the curvature term is one
///         <i>everywhere</i>, which is ZRemesher's uniform squares and is a real setting rather than a
///         degenerate case.</b> An empty mask, no guides, no features and a flat plane all reduce this
///         to <c>base</c> exactly, and that is the answer.
///     </para>
///     <para>
///         ⚠ <b><c>base</c> is <i>solved</i> from the budget rather than computed from the area, and
///         assuming the terms away was worth about 4× on the count.</b> Two of the three terms are at
///         most one, so <c>√(area / quads)</c> is the length the field would use if it never modulated
///         anything — see <see cref="Normalise" /> for the measurement and for why this is not the
///         rejected "scale the targets afterwards" fix.
///     </para>
///     <para>
///         ⚠ <b>A consequence worth stating: a <i>uniform</i> <see cref="RemeshSettings.DensityMask" />
///         is now a no-op.</b> The budget is the budget, so a mask that says "twice as dense
///         everywhere" says nothing — it is <i>where</i> the mask varies that moves quads, and a
///         painted region is paid for by the unpainted ones. Painting the whole model and expecting
///         more quads is what <see cref="RemeshSettings.TargetQuads" /> is for.
///     </para>
/// </remarks>
sealed class DensityField {
    /// <summary>How much smaller than <c>base</c> a target length may get.</summary>
    public const float MinScale = 0.25f;

    /// <summary>How much larger.</summary>
    public const float MaxScale = 4f;

    /// <summary>How sharply the curvature term shrinks the target — the coefficient on <c>|κ|·diagonal</c>.</summary>
    public const float CurvatureResponse = 0.5f;

    /// <summary>What the target is multiplied by right on a feature line.</summary>
    /// <remarks>
    ///     ⚠ Below one, so a hard edge is straddled by several quads rather than by one. § D9 names
    ///     this as the third of the three terms and it is the one with no user-facing setting — a
    ///     remesher that reproduced a crease and then put a single quad across it has reproduced
    ///     nothing.
    /// </remarks>
    public const float FeatureTighten = 0.5f;

    /// <summary>How far the tightening reaches, in multiples of <c>base</c>.</summary>
    public const float FeatureReach = 3f;

    /// <summary>How many times the normalisation re-solves for <c>base</c> before settling for what it has.</summary>
    /// <remarks>
    ///     ⚠ <b>The solve is a fixed point rather than a formula, because <see cref="FeatureReach" /> is
    ///     stated in multiples of <c>base</c>.</b> A longer <c>base</c> widens the band the feature term
    ///     tightens, which lowers the mean multiplier, which lengthens <c>base</c> again. The map is
    ///     bounded — no multiplier leaves <c>[MinScale, MaxScale]</c>, so <c>base</c> can never leave
    ///     <c>[seed × MinScale, seed / MinScale]</c> — and in practice it settles in two or three rounds.
    ///     This is the cap, not the count.
    /// </remarks>
    public const int NormalisationRounds = 8;

    /// <summary>How close two successive rounds must be for the normalisation to stop early.</summary>
    public const float NormalisationTolerance = 1e-4f;

    readonly float[] lengths;

    DensityField(float[] lengths, float baseLength) {
        this.lengths = lengths;

        Base = baseLength;
    }

    /// <summary>The length the whole surface would use if nothing modulated it.</summary>
    /// <remarks>
    ///     <para>
    ///         Solved from the quad budget and the surface area so that the field <i>as modulated</i>
    ///         asks for the budget: <c>base² = Σ A(v)/m(v)² / quads</c>, where <c>m</c> is the clamped
    ///         product of the three terms. See <see cref="Normalise" />.
    ///         <see cref="RemeshSettings.TargetEdgeLength" /> overrides it when it is given.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It reduces to <c>√(area / quads)</c> exactly when nothing modulates it</b>, which is
    ///         the closed form the naive derivation gives and the reason that form survived so long: it
    ///         is right on a flat surface at adaptivity zero with no creases, and short everywhere else.
    ///     </para>
    /// </remarks>
    public float Base { get; }

    /// <summary>The target edge length at a vertex.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The length, in world units.</returns>
    public float Target(int vertex) => lengths[vertex];

    /// <summary>Every target, for a comparison.</summary>
    public ReadOnlySpan<float> Targets => lengths;

    /// <summary>Computes § D9's field.</summary>
    /// <param name="mesh">The conditioned surface.</param>
    /// <param name="settings">The budget, the adaptivity and the mask.</param>
    /// <param name="features">The feature graph, for the tightening.</param>
    /// <param name="curvature">The curvature field.</param>
    /// <param name="paint">
    ///     A density multiplier per <i>mesh</i> vertex, or null to read
    ///     <see cref="RemeshSettings.DensityMask" /> when it happens to be the right length. See
    ///     <see cref="Resample" />.
    /// </param>
    /// <returns>The field.</returns>
    public static DensityField Build(
        ManifoldMesh mesh,
        RemeshSettings settings,
        FeatureGraph features,
        CurvatureField curvature,
        IReadOnlyList<float>? paint = null
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(curvature);

        var count = mesh.VertexCount;
        var lengths = new float[count];
        var seed = BaseLength(mesh, settings);

        if (count == 0 || seed <= 0f) {
            return new(lengths, seed);
        }

        // ⚠ The mask is indexed by the *conditioned* mesh's vertices and the setting's is indexed by
        // the source's, which stage one welds, cuts, de-specks and re-meshes. A mask of the wrong
        // length is therefore not a mask at all, and is ignored rather than truncated — silently
        // applying the first n entries of somebody else's vertex order is worse than applying none.
        var mask = paint ?? settings.DensityMask;

        if (mask.Count != count) {
            mask = [];
        }

        var adaptivity = Math.Clamp(settings.Adaptivity, 0f, 1f);
        var standing = new float[count];

        for (var vertex = 0; vertex < count; vertex++) {
            // At adaptivity zero this is exactly one, at every vertex, whatever the surface does.
            var curved = 1f / (1f + (CurvatureResponse * curvature.Magnitude(vertex)));
            var curvatureTerm = 1f + (adaptivity * (curved - 1f));

            var density = mask.Count == count ? mask[vertex] : 1f;

            standing[vertex] = curvatureTerm * (density > 0f ? 1f / density : 1f);
        }

        // ⚠ Capped at the *largest* reach the normalisation can arrive at rather than at the seed's, so
        // the walk is done once. `base` is bounded above by `seed / MinScale` because no multiplier is
        // smaller than `MinScale`, and the reach is `FeatureReach × base`.
        var distances = FeatureDistance(mesh, features, FeatureReach * seed / MinScale);
        var areas = VertexAreas(mesh);

        var baseLength = settings.TargetEdgeLength > 0f
            ? seed
            : Normalise(seed, standing, distances, areas, Math.Max(settings.TargetQuads, 1));

        var reach = FeatureReach * baseLength;

        for (var vertex = 0; vertex < count; vertex++) {
            lengths[vertex] = baseLength * Multiplier(standing[vertex], distances[vertex], reach);
        }

        return new(lengths, baseLength);
    }

    /// <summary>One vertex's multiplier on <c>base</c>, clamped — the whole of § D9's bracket but the scale.</summary>
    /// <param name="standing">The curvature and paint terms, which do not move when <c>base</c> does.</param>
    /// <param name="distance">How far the vertex is from a feature, along the surface.</param>
    /// <param name="reach">How far the tightening reaches, in world units.</param>
    /// <returns>The multiplier, within <c>[MinScale, MaxScale]</c>.</returns>
    static float Multiplier(float standing, float distance, float reach) {
        var near = reach > 0f ? MathF.Min(1f, distance / reach) : 1f;
        var featureTerm = FeatureTighten + ((1f - FeatureTighten) * near);

        return Math.Clamp(standing * featureTerm, MinScale, MaxScale);
    }

    /// <summary>Solves for the <c>base</c> at which the field asks for the budget it was given.</summary>
    /// <param name="seed">The naive <c>√(area / quads)</c>, which is where the fixed point starts.</param>
    /// <param name="standing">Every vertex's curvature-and-paint term.</param>
    /// <param name="distances">Every vertex's distance to the nearest feature.</param>
    /// <param name="areas">Every vertex's share of the surface.</param>
    /// <param name="quads">The budget, at least one.</param>
    /// <returns>The solved <c>base</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>base = √(area / quads)</c> is the answer to a different question, and this is the
    ///         defect docs/plan/41's first exit criterion recorded as belonging to § D9's field.</b> That
    ///         formula is derived as though every multiplier were exactly one. <c>curvatureTerm</c> and
    ///         <c>featureTerm</c> are both at most one, so on any surface with curvature or a crease every
    ///         target comes out <i>shorter</i> than <c>base</c> — and since a quad of side <c>L</c> covers
    ///         <c>L²</c>, the count overshoots by roughly the square of the mean multiplier. Measured on
    ///         the synthetic fixtures at a 400-quad budget the field alone asked for 1,454 to 2,207 quads
    ///         before any partition existed.
    ///     </para>
    ///     <para>
    ///         <b>What the budget actually says is <c>∫ dA / targetLength² = quads</c>.</b> Writing
    ///         <c>targetLength(v) = base × m(v)</c> and discretising the integral as a sum over vertices
    ///         carrying a third of each incident triangle gives <c>base² = Σ A(v)/m(v)² / quads</c>
    ///         directly, which is what this returns. On a flat surface with no features and no paint
    ///         every <c>m</c> is one and it reduces to <c>√(area / quads)</c> exactly, so the uniform case
    ///         is unchanged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is <i>not</i> the rejected "scale the targets afterwards" fix, and the
    ///         difference is what is being matched.</b> That one scaled by the ratio of the emitted quad
    ///         count to the budget, so it also absorbed the partition's own overshoot — it brought a box
    ///         to 444 quads and took the feature reproduction error from <c>5.1e-5</c> to <c>5.1e-2</c>,
    ///         because the arcs paying for the reduction were the ones running along the creases. This one
    ///         only makes the field mean what it says: the layout's residual factor is left where it is,
    ///         and <see cref="Remesher.BudgetTolerance" /> still measures it.
    ///     </para>
    /// </remarks>
    static float Normalise(float seed, float[] standing, float[] distances, float[] areas, int quads) {
        var baseLength = seed;

        for (var round = 0; round < NormalisationRounds; round++) {
            var wanted = 0d;

            for (var vertex = 0; vertex < standing.Length; vertex++) {
                var multiplier = Multiplier(standing[vertex], distances[vertex], FeatureReach * baseLength);

                wanted += areas[vertex] / (multiplier * multiplier);
            }

            if (wanted <= 0d) {
                return baseLength;
            }

            var solved = (float) Math.Sqrt(wanted / quads);

            if (MathF.Abs(solved - baseLength) <= NormalisationTolerance * baseLength) {
                return solved;
            }

            baseLength = solved;
        }

        return baseLength;
    }

    /// <summary>A third of each incident triangle's area, which is the surface measure the sum needs.</summary>
    /// <param name="mesh">The conditioned surface.</param>
    /// <returns>One area per vertex, summing to the surface area.</returns>
    /// <remarks>
    ///     ⚠ <b>Barycentric rather than Voronoi, and the choice is deliberate.</b> The Voronoi area is
    ///     the better one-ring measure and it is negative on an obtuse triangle, which on the
    ///     marching-cubes input this exists for is a large minority of them. A third of each incident
    ///     triangle sums to the surface area exactly on every mesh, which is the only property the budget
    ///     needs.
    /// </remarks>
    static float[] VertexAreas(ManifoldMesh mesh) {
        var areas = new float[mesh.VertexCount];

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++) {
            var third = mesh.Cross(triangle).Length() / 6f;
            var corners = mesh.Corners(triangle);

            areas[corners[0]] += third;
            areas[corners[1]] += third;
            areas[corners[2]] += third;
        }

        return areas;
    }

    /// <summary>Moves a mask from the source mesh's vertices onto the conditioned mesh's.</summary>
    /// <param name="positions">The source's positions.</param>
    /// <param name="triangles">The source's triangulation.</param>
    /// <param name="mask">One value per source position.</param>
    /// <param name="mesh">The conditioned surface.</param>
    /// <returns>One value per conditioned vertex, or an empty list when the mask does not fit its mesh.</returns>
    /// <remarks>
    ///     ⚠ <b>Barycentric off the nearest source triangle, using <c>TriangleTree.Closest</c>.</b> A
    ///     nearest-<i>vertex</i> lookup is one line shorter and produces a mask with the source's own
    ///     tessellation printed on it — visible as blocks of uniform density wherever the source was
    ///     coarser than the output.
    /// </remarks>
    public static float[] Resample(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<int> triangles,
        IReadOnlyList<float> mask,
        ManifoldMesh mesh
    ) {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(mesh);

        if (mask.Count != positions.Length || triangles.Length < 3) {
            return [];
        }

        var tree = new TriangleTree(positions, triangles);
        var resampled = new float[mesh.VertexCount];
        var source = triangles.ToArray();

        for (var vertex = 0; vertex < resampled.Length; vertex++) {
            var closest = tree.Closest(mesh.Positions[vertex]);

            if (closest.Triangle < 0) {
                resampled[vertex] = 1f;

                continue;
            }

            var corner = closest.Triangle * 3;

            resampled[vertex] = (mask[source[corner + 0]] * closest.Barycentric.X)
                + (mask[source[corner + 1]] * closest.Barycentric.Y)
                + (mask[source[corner + 2]] * closest.Barycentric.Z);
        }

        return resampled;
    }

    /// <summary>The quad budget and the surface area, or the caller's own length.</summary>
    static float BaseLength(ManifoldMesh mesh, RemeshSettings settings) {
        if (settings.TargetEdgeLength > 0f) {
            return settings.TargetEdgeLength;
        }

        var area = mesh.Area();
        var quads = Math.Max(settings.TargetQuads, 1);

        return area > 0f ? MathF.Sqrt(area / quads) : 0f;
    }

    /// <summary>Distance from each vertex to the nearest feature vertex, along edges, capped.</summary>
    /// <remarks>
    ///     A Dijkstra over the one-ring graph rather than a Euclidean query, so the tightening follows
    ///     the surface: two sides of a thin wall are far apart along it and near in space, and a
    ///     Euclidean radius would shrink the quads on the far side for no reason.
    /// </remarks>
    static float[] FeatureDistance(ManifoldMesh mesh, FeatureGraph features, float cap) {
        var distances = new float[mesh.VertexCount];

        Array.Fill(distances, cap);

        var queue = new PriorityQueue<int, float>();

        for (var vertex = 0; vertex < distances.Length; vertex++) {
            if (features.IsFeatureVertex(vertex)) {
                distances[vertex] = 0f;
                queue.Enqueue(vertex, 0f);
            }
        }

        while (queue.TryDequeue(out var vertex, out var distance)) {
            if (distance > distances[vertex]) {
                continue;
            }

            foreach (var neighbour in mesh.Ring(vertex)) {
                var step = distance + Vector3.Distance(mesh.Positions[vertex], mesh.Positions[neighbour]);

                if (step >= distances[neighbour]) {
                    continue;
                }

                distances[neighbour] = step;
                queue.Enqueue(neighbour, step);
            }
        }

        return distances;
    }
}
