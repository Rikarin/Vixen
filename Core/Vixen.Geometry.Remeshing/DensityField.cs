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

    readonly float[] lengths;

    DensityField(float[] lengths, float baseLength) {
        this.lengths = lengths;

        Base = baseLength;
    }

    /// <summary>The length the whole surface would use if nothing modulated it.</summary>
    /// <remarks>
    ///     From the quad budget and the surface area: a quad of side <c>L</c> covers <c>L²</c>, so
    ///     <c>base = √(area / quads)</c>. <see cref="RemeshSettings.TargetEdgeLength" /> overrides it
    ///     when it is given.
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
        var baseLength = BaseLength(mesh, settings);

        if (count == 0 || baseLength <= 0f) {
            return new(lengths, baseLength);
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
        var distances = FeatureDistance(mesh, features, FeatureReach * baseLength);
        var reach = FeatureReach * baseLength;

        for (var vertex = 0; vertex < count; vertex++) {
            // At adaptivity zero this is exactly one, at every vertex, whatever the surface does.
            var curved = 1f / (1f + (CurvatureResponse * curvature.Magnitude(vertex)));
            var curvatureTerm = 1f + (adaptivity * (curved - 1f));

            var density = mask.Count == count ? mask[vertex] : 1f;
            var paintTerm = density > 0f ? 1f / density : 1f;

            var near = reach > 0f ? MathF.Min(1f, distances[vertex] / reach) : 1f;
            var featureTerm = FeatureTighten + ((1f - FeatureTighten) * near);

            lengths[vertex] = Math.Clamp(
                baseLength * curvatureTerm * paintTerm * featureTerm,
                baseLength * MinScale,
                baseLength * MaxScale
            );
        }

        return new(lengths, baseLength);
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
