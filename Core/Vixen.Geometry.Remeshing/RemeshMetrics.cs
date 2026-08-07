// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Everything docs/plan/41 Part 4 asks the report to say, measured off the result.</summary>
/// <remarks>
///     ⚠ <b>Every length here is a fraction of the bounding-box diagonal and never a distance.</b>
///     <see cref="RemeshReport" /> says so for the deviations and docs/plan/41's exit criterion states
///     the feature error as a bare <c>1e-5</c>; it is made relative here for the same reason the
///     deviations are, which is the lesson doc 24 records twice and R1's <c>ScaleInvarianceTests</c>
///     exists to catch. A model measured in millimetres and the same model in metres must produce the
///     same number, and an absolute tolerance is a claim about how big a model is.
/// </remarks>
static class RemeshMetrics {
    /// <summary>How many faces have four sides, and how many do not.</summary>
    /// <param name="output">The result.</param>
    /// <returns>The two counts.</returns>
    public static (int Quads, int Others) Faces(EditMesh output) {
        ArgumentNullException.ThrowIfNull(output);

        var quads = 0;
        var others = 0;

        for (var face = 0; face < output.FaceCount; face++) {
            if (output.Faces[face].Count == 4) {
                quads++;
            } else {
                others++;
            }
        }

        return (quads, others);
    }

    /// <summary>Every irregular interior vertex, and how many of them sit on a feature chain's interior.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>"Zero singularities on features" means zero on chain <i>interiors</i>, and R2
    ///         established that a cube is why the distinction is not pedantry.</b> On a box every vertex
    ///         is a feature corner and every edge is a feature edge, and χ = 2 demands eight quarter
    ///         turns somewhere — so reading the criterion as "off every feature vertex" makes the
    ///         simplest hard-surface shape unsatisfiable. A corner is exempt; a bend in the middle of a
    ///         chain is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Boundary vertices are excluded, because four is not their regular valence.</b> A
    ///         vertex on an open rim meets two quads and has three edges, and counting it as a
    ///         singularity would make every open surface read as though its topology were wrong.
    ///     </para>
    /// </remarks>
    public static (List<Singularity> Found, int OnFeatures) Singularities(
        EditMesh output,
        Extraction extraction,
        PatchLayout layout,
        FeatureGraph features
    ) {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(features);

        var found = new List<Singularity>();
        var onFeatures = 0;

        for (var position = 0; position < output.PositionCount; position++) {
            var edges = output.EdgesAt(position);

            if (edges.Length == 0) {
                continue;
            }

            var interior = true;

            foreach (var edge in edges) {
                interior &= output.FacesOf(edge).Length == 2;
            }

            if (!interior || edges.Length == 4) {
                continue;
            }

            found.Add(new(position, edges.Length, 4 - edges.Length));

            if (OnFeature(extraction, layout, features, position)) {
                onFeatures++;
            }
        }

        return (found, onFeatures);
    }

    /// <summary>Whether an output position sits on the interior of a feature chain.</summary>
    static bool OnFeature(Extraction extraction, PatchLayout layout, FeatureGraph features, int position) {
        var arc = extraction.ArcOf[position];

        if (arc >= 0) {
            return layout.Arcs[arc].IsFeature;
        }

        var source = extraction.SourceOf[position];

        // An arc end that is a feature corner is where a singularity belongs; one that is only a bend
        // in the middle of a chain is not.
        return source >= 0 && features.IsFeatureVertex(source) && !features.IsCorner(source);
    }

    /// <summary>How far the result strays from the source, at its vertices and at its face centres.</summary>
    /// <param name="output">The result.</param>
    /// <param name="projector">The conditioned surface.</param>
    /// <param name="diagonal">The bounding box's diagonal.</param>
    /// <returns>The worst and the mean, as fractions of the diagonal.</returns>
    /// <remarks>
    ///     ⚠ <b>Face centres as well as vertices, because a quad can have all four corners on the
    ///     surface and bulge between them.</b> Measuring vertices alone reports zero deviation on a
    ///     cage that cuts every corner off a sphere, which is the one shape the number exists to catch.
    /// </remarks>
    public static (float Max, float Mean) Deviation(EditMesh output, SurfaceProjector projector, float diagonal) {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(projector);

        if (diagonal <= 0f || projector.IsEmpty) {
            return (0f, 0f);
        }

        var worst = 0f;
        var total = 0d;
        var samples = 0;

        for (var position = 0; position < output.PositionCount; position++) {
            var away = projector.Distance(output.Positions[position]) / diagonal;

            worst = MathF.Max(worst, away);
            total += away;
            samples++;
        }

        for (var face = 0; face < output.FaceCount; face++) {
            var loop = output.CornersOf(face);

            if (loop.Length == 0) {
                continue;
            }

            var centre = Vector3.Zero;

            foreach (var corner in loop) {
                centre += output.Positions[corner];
            }

            var away = projector.Distance(centre / loop.Length) / diagonal;

            worst = MathF.Max(worst, away);
            total += away;
            samples++;
        }

        return (worst, samples == 0 ? 0f : (float) (total / samples));
    }

    /// <summary>The worst quad corner's shape quality, where one is a square and a negative is inverted.</summary>
    /// <param name="output">The result.</param>
    /// <returns>The minimum over every corner of every face, or one where there are none.</returns>
    /// <remarks>
    ///     <para>
    ///         The sine of the corner's angle, signed against the face's own normal — so a quad folded
    ///         over on itself reports a negative and a quad that has collapsed to a line reports zero.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A degenerate corner contributes zero and the scan carries on; it used to return
    ///         zero and stop, which made the field read as a sentinel rather than as a minimum.</b> The
    ///         three degenerate cases — a face with fewer than three corners, a face whose Newell normal
    ///         cancels to nothing, and a corner with a zero-length edge — each returned <c>0f</c> from
    ///         the whole function, so the first one encountered ended the scan and every face after it
    ///         went unmeasured. Measured at a 400-quad budget: box reported <c>0.000</c> and its true
    ///         worst corner is <c>−0.965</c>, cylinder <c>0.000</c> against <c>−0.997</c>, plate
    ///         <c>0.000</c> against <c>−0.840</c>, union <c>0.000</c> against <c>−0.991</c>. Four of
    ///         seven fixtures were reporting "there is a flat quad somewhere" while holding quads folded
    ///         almost completely back on themselves, and docs/plan/41 § D8 read those zeroes as the
    ///         defect being an absence of area. It is not: it is inversion, on six fixtures of seven.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero is still the right contribution for a collapsed corner rather than skipping
    ///         it.</b> A corner whose two edges have no direction has no angle to measure, and a
    ///         degenerate quad is not a good quad — dropping it from the minimum would let a result full
    ///         of collapsed faces report the quality of the ones that survived.
    ///     </para>
    /// </remarks>
    public static float ScaledJacobian(EditMesh output) {
        ArgumentNullException.ThrowIfNull(output);

        var worst = 1f;

        for (var face = 0; face < output.FaceCount; face++) {
            var loop = output.CornersOf(face);

            if (loop.Length < 3) {
                worst = MathF.Min(worst, 0f);

                continue;
            }

            var normal = ScaleSafe.Unit(output.Normal(face));

            if (normal.LengthSquared() <= 0f) {
                worst = MathF.Min(worst, 0f);

                continue;
            }

            for (var at = 0; at < loop.Length; at++) {
                var here = output.Positions[loop[at]];
                var ahead = ScaleSafe.Unit(output.Positions[loop[(at + 1) % loop.Length]] - here);
                var behind = ScaleSafe.Unit(output.Positions[loop[(at + loop.Length - 1) % loop.Length]] - here);

                if (ahead.LengthSquared() <= 0f || behind.LengthSquared() <= 0f) {
                    worst = MathF.Min(worst, 0f);

                    continue;
                }

                worst = MathF.Min(worst, Vector3.Dot(Vector3.Cross(ahead, behind), normal));
            }
        }

        return worst;
    }

    /// <summary>How far the furthest point of a feature polyline sits from the nearest output edge.</summary>
    /// <param name="mesh">The conditioned surface, whose vertices the chains index.</param>
    /// <param name="output">The result.</param>
    /// <param name="layout">The partition, whose feature arcs are the chains.</param>
    /// <param name="extraction">The samples along each arc.</param>
    /// <param name="diagonal">The bounding box's diagonal.</param>
    /// <returns>The largest distance, as a fraction of the diagonal.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41's second exit criterion, measured rather than claimed.</b> Every point of
    ///         every feature chain is compared against the output edges of the <i>same</i> arc, which is
    ///         the arc the layout made a boundary of two patches — so a non-zero here means the arc's
    ///         samples cut a corner, and never that the search looked in the wrong place.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against the output <i>edges</i> and not the output vertices.</b> A chain vertex that
    ///         is nowhere near a sample but sits exactly on the segment between two of them is
    ///         reproduced, and a measure taken to the nearest vertex would report it as an error whose
    ///         size is the sampling rate.
    ///     </para>
    /// </remarks>
    public static float FeatureError(
        ManifoldMesh mesh,
        EditMesh output,
        PatchLayout layout,
        Extraction extraction,
        float diagonal
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(extraction);

        if (diagonal <= 0f) {
            return 0f;
        }

        var worst = 0f;

        for (var arc = 0; arc < layout.Arcs.Count; arc++) {
            if (!layout.Arcs[arc].IsFeature) {
                continue;
            }

            var samples = extraction.Samples[arc];

            if (samples.Length < 2) {
                continue;
            }

            foreach (var vertex in layout.Arcs[arc].Vertices) {
                var point = mesh.Positions[vertex];
                var best = float.PositiveInfinity;

                for (var at = 0; at + 1 < samples.Length; at++) {
                    best = MathF.Min(best, Segment(point, output.Positions[samples[at]], output.Positions[samples[at + 1]]));
                }

                if (float.IsFinite(best)) {
                    worst = MathF.Max(worst, best / diagonal);
                }
            }
        }

        return worst;
    }

    /// <summary>The distance from a point to a segment.</summary>
    static float Segment(Vector3 point, Vector3 from, Vector3 to) {
        var along = to - from;
        var span = along.LengthSquared();
        var fraction = span > 0f ? Math.Clamp(Vector3.Dot(point - from, along) / span, 0f, 1f) : 0f;

        return Vector3.Distance(point, from + (along * fraction));
    }
}
