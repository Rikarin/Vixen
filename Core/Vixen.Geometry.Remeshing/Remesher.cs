// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Threading;

namespace Vixen.Geometry.Remeshing;

/// <summary>A triangle mesh in, an all-quad mesh out, with a report saying whether it went well.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D1's seven stages, and every one of them is an inspectable artefact.</b>
///         Condition, features, field, layout, quantize, extract, transfer — a
///         <see cref="RemeshStageTiming" /> per stage says which one was slow and which one dropped
///         something, because when a remesh looks wrong "which stage" is the first question and a
///         monolith cannot answer it.
///     </para>
///     <para>
///         ⚠ <b>A hard edge is reproduced rather than approximated, and that is a structural claim
///         rather than a quality one.</b> § D4: feature polylines are chained <i>before</i> the field is
///         solved and are boundaries of the patch layout, so a crease is a chain of output edges by
///         construction. The alternative — extract, then nudge extracted vertices toward what was
///         detected — is what produces good-but-wobbly hard surface, and it is why the established tool
///         still ships its previous algorithm under a button.
///     </para>
///     <para>
///         ⚠ <b>All-quad, and <see cref="RemeshReport.NonQuadCount" /> being non-zero is a bug rather
///         than a setting.</b> § D8 and § D15: doc 24's <c>MeshOperations</c> is built on the assumption
///         that a loop, a ring and a loop cut are statements about four-sided faces, so a
///         quad-<i>dominant</i> result has no rings to cut and the mesh kernel's whole vocabulary stops
///         working on it.
///     </para>
///     <para>
///         ⚠ <b>It refuses rather than throws, and it never hangs.</b> § Exit criterion 7: a corpus of
///         deliberately broken meshes produces a valid all-quad result <i>or</i> a report naming the
///         stage that refused. A refusal comes back as an empty mesh with the reason in
///         <see cref="RemeshReport.Warnings" />; every walk, every repair and every solve in the pipeline
///         is bounded.
///     </para>
/// </remarks>
public static class Remesher {
    /// <summary>Remeshes a triangle mesh into quads.</summary>
    /// <param name="source">The input. Read, never modified; n-gons are triangulated on the way in.</param>
    /// <param name="settings">What the output should be, and what it should keep.</param>
    /// <param name="report">What happened, per stage, and how good the result is.</param>
    /// <param name="scheduler">
    ///     Workers for the field solve, or <see langword="null" /> to run it on the calling thread. ⚠ The
    ///     answer is byte-identical either way, which docs/plan/41 § D14 calls a gate rather than an
    ///     aspiration.
    /// </param>
    /// <returns>The all-quad result, or an empty mesh when a stage refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> or <paramref name="settings" /> is null.</exception>
    /// <example>
    ///     <code>
    /// var quads = Remesher.Remesh(triangles, new RemeshSettings { TargetQuads = 5000 }, out var report);
    ///
    /// report.IsAllQuad;                  // asserted, not hoped
    /// report.SingularitiesOnFeatures;    // zero, or the layout was wrong
    /// report.MaxDeviation;               // a fraction of the diagonal, so it compares across models
    ///     </code>
    /// </example>
    public static EditMesh Remesh(
        EditMesh source,
        RemeshSettings settings,
        out RemeshReport report,
        JobScheduler? scheduler = null
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var stages = new List<RemeshStageTiming>();
        var warnings = new List<string>();
        var clock = Stopwatch.StartNew();

        if (settings.Symmetry is not null) {
            // R7 owns § D11's exact mirror. Saying so is better than quietly producing an asymmetric
            // result on a setting whose whole point is that it is exact.
            warnings.Add("Symmetry was requested and is not applied yet — docs/plan/41 § D11 is R7's.");
        }

        // ① Condition. The pre-remesh needs a length before the density field exists, so the base is
        // taken off the source's own area — the same formula § D9 uses, on the surface as it arrived.
        var soup = TriangleSoup.From(source);
        var length = BaseLength(soup, settings);
        var mesh = MeshConditioner.Condition(source, settings.Conditioning, out var conditioning, length);

        stages.Add(new(RemeshStage.Condition, clock.Elapsed, mesh.TriangleCount));

        if (mesh.TriangleCount == 0) {
            warnings.Add("Conditioning left no triangles at all.");

            report = Refused(conditioning, stages, warnings);

            return new();
        }

        // ② Features. Creases, seams and guides come off the source and are resolved onto whatever
        // conditioning produced; the dihedral and group sources are read there directly.
        var mark = clock.Elapsed;
        var features = FeatureDetector.Detect(mesh, settings, FeatureCurves.All(source, settings));

        stages.Add(new(RemeshStage.Features, clock.Elapsed - mark, features.Polylines.Count));

        // ③ Field.
        mark = clock.Elapsed;

        var curvature = CurvatureField.Build(mesh);
        var solved = CrossFieldSolver.Solve(mesh, settings, features, curvature, scheduler);
        var field = SingularityPass.Place(mesh, settings, features, curvature, solved, out _);
        var singularities = SingularityPass.Extract(mesh, field);

        var paint = settings.DensityMask.Count == source.PositionCount && settings.DensityMask.Count > 0
            ? DensityField.Resample(source.Positions, soup.Triangles.ToArray(), settings.DensityMask, mesh)
            : null;

        var density = DensityField.Build(mesh, settings, features, curvature, paint);

        stages.Add(new(RemeshStage.Field, clock.Elapsed - mark, singularities.Count));

        // ④ Layout.
        mark = clock.Elapsed;

        var layout = PatchLayout.Build(mesh, field, features, density, singularities);

        stages.Add(new(RemeshStage.Layout, clock.Elapsed - mark, layout.Patches.Count));
        warnings.AddRange(layout.Warnings);

        if (!layout.IsUsable) {
            warnings.Add("The layout stage refused: no usable patch decomposition.");

            report = Refused(conditioning, stages, warnings);

            return new();
        }

        // ⑤ Quantize.
        mark = clock.Elapsed;

        var quantization = Budget(layout, settings, warnings);

        stages.Add(new(RemeshStage.Quantize, clock.Elapsed - mark, layout.Arcs.Count));
        warnings.AddRange(quantization.Warnings);

        if (!quantization.IsFeasible) {
            warnings.Add("The quantize stage refused: the consistency system has no usable answer.");

            report = Refused(conditioning, stages, warnings);

            return new();
        }

        // ⑥ Extract.
        mark = clock.Elapsed;

        var projector = SurfaceProjector.Build(mesh.Positions.ToArray(), mesh.Triangles.ToArray());
        var extraction = PatchExtractor.Extract(mesh, features, layout, quantization, projector);
        var output = extraction.Mesh;

        stages.Add(new(RemeshStage.Extract, clock.Elapsed - mark, output.FaceCount));
        warnings.AddRange(extraction.Warnings);

        // ⑦ Transfer is R5's — normals, coordinates, colours, materials, weights and the baked maps.
        // The stage is recorded so the report's shape does not change when it lands.
        stages.Add(new(RemeshStage.Transfer, TimeSpan.Zero, 0));

        if (output.FaceCount == 0) {
            warnings.Add("The extract stage produced no faces.");

            report = Refused(conditioning, stages, warnings);

            return new();
        }

        var (quads, others) = RemeshMetrics.Faces(output);
        var (found, onFeatures) = RemeshMetrics.Singularities(output, extraction, layout, features);
        var diagonal = mesh.Diagonal;
        var (max, mean) = RemeshMetrics.Deviation(output, projector, diagonal);

        report = new(
            quads,
            others,
            found,
            onFeatures,
            max,
            mean,
            RemeshMetrics.ScaledJacobian(output),
            RemeshMetrics.FeatureError(mesh, output, layout, extraction, diagonal),
            conditioning,
            output.Validate(),
            stages,
            warnings
        );

        return output;
    }

    /// <summary>How far over the budget a result may be before the report says so.</summary>
    public const float BudgetTolerance = 1.35f;

    /// <summary>Quantizes, and says so when the count is not the count that was asked for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A patch's quad count is a <i>product</i> of two side lengths, so a partition of
    ///         snaky patches overshoots the budget quadratically.</b> A region of area <c>A</c> that is
    ///         compact has a perimeter of about <c>4√A</c> and quantizes to about <c>A</c> quads; one
    ///         whose perimeter is three times that quantizes to about nine times as many. Measured on a
    ///         box at a 400-quad budget: 5,047. The density field is not what is wrong there — the
    ///         partition is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Scaling every target down to hit the count was tried and is <i>worse</i>, which is
    ///         why this only measures.</b> It brings a box to 444 quads and takes the feature
    ///         reproduction error from <c>5.1e-5</c> to <c>5.1e-2</c>, because the arcs that pay for the
    ///         reduction are the ones running along the creases and a crease quantized to one segment
    ///         cuts every corner it used to follow. docs/plan/41's second exit criterion is the one this
    ///         phase exists to make achievable, and a quad count is not worth it. The report says the
    ///         budget was missed and by how much; making the patches compact enough to meet it is a
    ///         layout problem and it is stated as one.
    ///     </para>
    /// </remarks>
    static Quantization Budget(PatchLayout layout, RemeshSettings settings, List<string> warnings) {
        var quantization = Quantizer.Solve(layout);

        if (settings.TargetQuads <= 0 || settings.TargetEdgeLength > 0f) {
            return quantization;
        }

        var quads = Quantizer.QuadCount(layout, quantization.Counts);

        if (quads > settings.TargetQuads * BudgetTolerance) {
            warnings.Add(
                $"The budget was not met: {quads} quads against {settings.TargetQuads} asked for, "
                + "because the partition's patches are longer round than they are wide."
            );
        }

        return quantization;
    }

    /// <summary>The report a refusal comes back with: no quads, and the reason in the warnings.</summary>
    static RemeshReport Refused(
        ConditioningReport conditioning,
        List<RemeshStageTiming> stages,
        List<string> warnings
    ) =>
        new(0, 0, [], 0, 0f, 0f, 0f, 0f, conditioning, new EditMesh().Validate(), stages, [.. warnings]);

    /// <summary>§ D9's base length, off the source's own area.</summary>
    /// <remarks>
    ///     ⚠ <b>Computed here rather than read from <see cref="DensityField" />, and the ordering is
    ///     why.</b> The pre-remesh is step six of stage one and the density field is stage three, so the
    ///     length conditioning needs does not exist yet. It is the same formula — a quad of side
    ///     <c>L</c> covers <c>L²</c>, so <c>L = √(area / quads)</c> — evaluated on the surface as it
    ///     arrived rather than on the one that comes out, which differ by whatever the weld and the
    ///     de-speck removed.
    /// </remarks>
    static float BaseLength(TriangleSoup soup, RemeshSettings settings) {
        if (settings.TargetEdgeLength > 0f) {
            return settings.TargetEdgeLength;
        }

        var area = 0f;

        for (var triangle = 0; triangle < soup.TriangleCount; triangle++) {
            area += soup.Area(triangle);
        }

        return area > 0f ? MathF.Sqrt(area / Math.Max(settings.TargetQuads, 1)) : 0f;
    }
}
