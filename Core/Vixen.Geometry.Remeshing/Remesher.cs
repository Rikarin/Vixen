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
    ) =>
        Remesh(source, SourceAttributes.None, settings, out report, out _, scheduler);

    /// <summary>The same, carrying the channels the mesh has no room for as well.</summary>
    /// <param name="source">The input. Read, never modified.</param>
    /// <param name="attributes">
    ///     The source's colours and skinning weights, or <see cref="SourceAttributes.None" />. ⚠
    ///     <b>This overload is what makes a character remeshable</b> — docs/plan/41 § D12 and doc 33.
    ///     Normals, coordinates and face groups ride inside <see cref="EditMesh" /> and need no
    ///     argument; a weight has nowhere to live but beside it.
    /// </param>
    /// <param name="settings">What the output should be, and what it should keep.</param>
    /// <param name="report">What happened, per stage, and how good the result is.</param>
    /// <param name="transferred">The colours and weights that came out, and what could not be carried.</param>
    /// <param name="scheduler">Workers for the field solve, or <see langword="null" />.</param>
    /// <returns>The all-quad result, or an empty mesh when a stage refused.</returns>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="source" />, <paramref name="attributes" /> or <paramref name="settings" />
    ///     is null.
    /// </exception>
    public static EditMesh Remesh(
        EditMesh source,
        SourceAttributes attributes,
        RemeshSettings settings,
        out RemeshReport report,
        out TransferResult transferred,
        JobScheduler? scheduler = null
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(settings);

        transferred = new([], null, 0, 0, []);

        // § D11's exact mirror, and it is a wrapper around this method rather than a stage inside it:
        // it cuts the source, calls back in with the setting cleared, and reflects what comes out. A
        // stage would have had to thread "which half" through all seven.
        //
        // ⚠ It runs stage seven itself rather than letting the inner call do it, and that is the
        // whole reason it takes the attributes. A colour is indexed per corner and a weight per
        // position of the mesh the caller handed in, and the plane cut renumbered both — so the
        // transfer has to be made from the uncut source onto the half's output, and then reflected.
        // Reflecting it is not glue: a mirrored vertex's weights belong to the *mirrored* bone, which
        // is what `SourceAttributes.BoneMirror` is for and what a symmetric remesh of a rigged mesh
        // is refused without.
        if (settings.Symmetry is { } plane) {
            return SymmetryPass.Remesh(source, attributes, settings, plane, out report, out transferred, scheduler);
        }

        var stages = new List<RemeshStageTiming>();
        var warnings = new List<string>();
        var clock = Stopwatch.StartNew();

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

        var quantization = Braked(layout, settings, warnings);

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

        if (output.FaceCount == 0) {
            warnings.Add("The extract stage produced no faces.");
            stages.Add(new(RemeshStage.Transfer, TimeSpan.Zero, 0));

            report = Refused(conditioning, stages, warnings);

            return new();
        }

        // ⑦ Transfer — § D12's attributes, then § D13's atlas. The order is not interchangeable:
        // both write the texture-coordinate layer and the atlas is the one that wins, so the
        // transfer runs first with its own coordinate pass disabled.
        mark = clock.Elapsed;
        var carried = 0;

        if (settings.TransferAttributes) {
            transferred = AttributeTransfer.Transfer(
                source,
                attributes,
                output,
                settings.Transfer with { KeepTexCoords = settings.Transfer.KeepTexCoords && !settings.GenerateUvs }
            );

            warnings.AddRange(transferred.Warnings);
            carried += output.CornerCount;
        }

        if (settings.GenerateUvs) {
            var atlas = LayoutAtlas.Build(output, extraction.Grids, settings.Atlas);

            warnings.AddRange(atlas.Warnings);
            carried += atlas.Charts;
        }

        stages.Add(new(RemeshStage.Transfer, clock.Elapsed - mark, carried));

        var (quads, others) = RemeshMetrics.Faces(output);
        var (found, onFeatures) = RemeshMetrics.Singularities(output, extraction, layout, features);
        var diagonal = mesh.Diagonal;
        var (max, mean) = RemeshMetrics.Deviation(output, projector, diagonal);
        var validated = output.Validate();

        Overspent(quads + others, settings, warnings);
        Attribute(conditioning.Mesh, validated, warnings);

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
            validated,
            stages,
            warnings
        );

        return output;
    }

    /// <summary>Says whether the result's defects were inherited from the input or made here.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A silent hole is the failure class the whole report exists to prevent, and the
    ///         report had every fact and drew no conclusion.</b> docs/plan/41 § Part 4: "a remesher that
    ///         cannot tell you it went wrong will be trusted until it embarrasses somebody." Every stage
    ///         counted what <i>it</i> dropped, so a result whose rim came in with the input came back not
    ///         watertight with no line naming a reason — indistinguishable, to a build script, from one
    ///         that lost a patch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured on <c>Data 8.glb</c>, which is the mesh that made this necessary.</b> It is
    ///         the one of sixteen that reports no dropped patch at all and is still not solid: its
    ///         <i>input</i> arrives with 70 boundary edges, <see cref="ConditioningSettings.FillHoles" />
    ///         is off by default so conditioning leaves them — <see cref="ConditioningReport.Filled" />
    ///         is zero and says nothing — and the output's 56 boundary edges are that same rim,
    ///         remeshed. What the pipeline <i>did</i> make there is one non-manifold edge, which nothing
    ///         counted either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two warnings and not one, because "inherited" and "made here" are different facts
    ///         and only the second is a bug.</b> An open input is a legitimate subject — a rim is what a
    ///         block-out or a scanned surface has — and § D3 is explicit that conditioning reports rather
    ///         than insists. What is never legitimate is the pipeline adding a defect class the surface
    ///         it was handed did not have.
    ///     </para>
    /// </remarks>
    static void Attribute(MeshReport conditioned, MeshReport output, List<string> warnings) {
        if (!conditioned.IsSolid) {
            warnings.Add(
                $"The conditioned surface was not a closed solid to begin with — {conditioned.Describe()} — so "
                + "the result inherits that much. Set ConditioningSettings.FillHoles to close a rim."
            );
        }

        List<string> made = [];

        Made(made, "non-manifold edge", conditioned.NonManifold.Count, output.NonManifold.Count);
        Made(made, "boundary edge", conditioned.Boundary.Count, output.Boundary.Count);
        Made(made, "inconsistently wound edge", conditioned.Reversed.Count, output.Reversed.Count);
        Made(made, "face with no area", conditioned.Degenerate.Count, output.Degenerate.Count);
        Made(made, "orphaned position", conditioned.Orphans, output.Orphans);

        if (made.Count > 0) {
            warnings.Add(
                $"The result has {string.Join(", ", made)} that the conditioned surface did not, so this much "
                + "of it was lost here rather than arriving that way."
            );
        }
    }

    /// <summary>Records a defect class the output has more of than the surface it was handed.</summary>
    static void Made(List<string> into, string what, int conditioned, int output) {
        if (output > conditioned) {
            into.Add($"{output - conditioned} more {what}(s)");
        }
    }

    /// <summary>How far over the budget a result may be before the report says so.</summary>
    public const float BudgetTolerance = 1.35f;

    /// <summary>How many times the budget the quantization may plan before the brake engages.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Far above <see cref="BudgetTolerance" /> on purpose, and the gap between the two is
    ///         the whole design.</b> <see cref="Overspent" /> records that scaling every target down to
    ///         <i>hit</i> the count was tried and is worse — it brings a box to 444 quads and takes the
    ///         feature reproduction error from <c>5.1e-5</c> to <c>5.1e-2</c>, because the arcs paying
    ///         for the reduction are the ones running along the creases. That argument is about a
    ///         result 1.4× over. It is not about one 7,256× over, where there is no feature
    ///         reproduction to protect because the mesh is unusable at any tolerance.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sixty-four is above every measured ordinary overshoot, so the brake cannot fire on
    ///         the path <see cref="Overspent" /> is written about.</b> At a 400-quad budget the fixtures
    ///         plan 2,678 on a box and 3,000 on a union — 6.7× and 7.5× — and the 16-file real corpus at
    ///         5,000 stays under 1.4×. The case it exists for is the property space's: seed
    ///         <c>9hqwA86TjVk1</c>, a sphere carrying <c>LargeComponent</c>, <c>TinyComponent</c> and
    ///         <c>ZeroLengthEdge</c>, mirrored, at 96 quads, whose 602 input faces planned 696,592 per
    ///         half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It returns, so nothing that was measuring for a hang or for a non-quad could see
    ///         it.</b> Every face has four sides and the ledger is well formed; what is unbounded is
    ///         <i>growth</i>, and only the allocation reading catches that. A patch's quad count is a
    ///         product of two side lengths, so a partition of snaky patches overshoots quadratically —
    ///         which is why the back-off below is a square root.
    ///     </para>
    /// </remarks>
    public const float RunawayMultiple = 64f;

    /// <summary>How many times the brake may halve its way down before it takes what it has.</summary>
    /// <remarks>
    ///     ⚠ A fixed count and not a loop until satisfied — docs/plan/41 § D14. Four attempts cover a
    ///     factor of <c>2^8</c> in the planned count, because each one scales the targets by the square
    ///     root of how far over they are; a partition that is still over after that is one no scaling
    ///     rescues, and the smallest answer seen is better than the first.
    /// </remarks>
    public const int BrakeAttempts = 4;

    /// <summary>The quantization, re-solved smaller when it planned orders of magnitude over budget.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off <see cref="Quantizer.QuadCount" /> — the plan — and not off the output, which
    ///     is the opposite of what <see cref="Overspent" /> does and is right for the opposite
    ///     reason.</b> The warning is about a mesh that shipped, so it must count the mesh that shipped.
    ///     This has to act <i>before</i> the extraction allocates the thing, so the only number it can
    ///     have is the one the quantization intends.
    /// </remarks>
    static Quantization Braked(PatchLayout layout, RemeshSettings settings, List<string> warnings) {
        var solved = Quantizer.Solve(layout);

        // An explicit edge length is the caller stating the density outright, and a brake on it would
        // silently return something other than what was asked for. Only the budget path is braked.
        if (settings.TargetQuads <= 0 || settings.TargetEdgeLength > 0f || !solved.IsFeasible) {
            return solved;
        }

        var allowed = (long) settings.TargetQuads * (long) RunawayMultiple;
        var scale = 1f;

        for (var attempt = 0; attempt < BrakeAttempts; attempt++) {
            var planned = Quantizer.QuadCount(layout, solved.Counts);

            if (planned <= allowed) {
                if (attempt > 0) {
                    warnings.Add(
                        $"The quantization planned more than {RunawayMultiple:0}× the budget, so every arc "
                        + $"target was scaled by {scale:0.####} and it now plans {planned}."
                    );
                }

                return solved;
            }

            // Quads go as the square of the target density, so the scaling that brings a plan of P down
            // to an allowance of A is √(A / P). Half again on top of it, because the layout's arcs are
            // integers and rounding always lands above rather than below.
            scale *= MathF.Sqrt((float) allowed / planned) * 0.5f;

            var smaller = Quantizer.Solve(layout, QuantizeMode.Exact, scale);

            if (!smaller.IsFeasible) {
                break;
            }

            solved = smaller;
        }

        warnings.Add(
            $"The quantization planned {Quantizer.QuadCount(layout, solved.Counts)} quads against "
            + $"{settings.TargetQuads} asked for and would not come down in {BrakeAttempts} attempts. "
            + "The partition is unusable rather than merely coarse."
        );

        return solved;
    }

    /// <summary>Says so when the mesh that came out has more faces than the budget allowed.</summary>
    /// <param name="faces">How many faces the output actually has, counted off it.</param>
    /// <param name="settings">The budget.</param>
    /// <param name="warnings">Where to say so.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Counted off the output, and it used to be predicted off the layout.</b> The warning
    ///         was decided from <c>Quantizer.QuadCount(layout, counts)</c> — what the quantization
    ///         intended — while <see cref="RemeshReport.QuadCount" /> is what the extraction produced,
    ///         and the two diverge by every patch dropped between them. Measured: a result of 138 quads
    ///         against 96 asked for is 1.44× against a tolerance of 1.35 and carried three warnings,
    ///         none of which mentioned the budget, because the prediction was under the line and the
    ///         mesh was not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mirror is the case that makes it unarguable.</b> With
    ///         <see cref="RemeshSettings.Symmetry" /> on, the prediction is the <i>half</i> mesh's, so
    ///         the report said "169665 against 96" while the mesh it shipped had 339330 faces — exactly
    ///         twice, and the number that was wrong was the one in the warning.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Faces rather than quads, so a non-quad still counts against the budget.</b> A budget
    ///         is about how heavy the result is, and a triangle the extraction emitted is a face
    ///         somebody pays for whether or not it has four sides.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A patch's quad count is a <i>product</i> of two side lengths, so a partition of
    ///         snaky patches overshoots the budget quadratically.</b> A region of area <c>A</c> that is
    ///         compact has a perimeter of about <c>4√A</c> and quantizes to about <c>A</c> quads; one
    ///         whose perimeter is three times that quantizes to about nine times as many. That was the
    ///         dominant term while the partition still contained slits — a cut with a loose end is
    ///         walked once in each direction, so it counts its own length twice into the perimeter and
    ///         adds no area at all. Measured on a box at a 400-quad budget: 5,047 then, 2,678 once
    ///         <see cref="PatchLayout" /> walked its loose ends out; on a union, 18,795 then and 3,000
    ///         now.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is left is <i>not</i> the partition's, and this comment used to say it was.</b>
    ///         § D9's density field asks for 1,454 to 2,207 quads on these fixtures against a budget of
    ///         400, before any partition exists: <c>curvatureTerm</c> and <c>featureTerm</c> are both at
    ///         most one, so every target length comes out at or below <c>base</c>, and
    ///         <c>base = √(area / quads)</c> is derived as though they were exactly one. Against what it
    ///         is actually handed, the layout now lands within about 1.4×. The warning is still phrased
    ///         about the partition because that is what a caller can act on, and the row belongs to the
    ///         field.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Scaling every target down to hit the count was tried and is <i>worse</i>, which is
    ///         why this only measures.</b> It brings a box to 444 quads and takes the feature
    ///         reproduction error from <c>5.1e-5</c> to <c>5.1e-2</c>, because the arcs that pay for the
    ///         reduction are the ones running along the creases and a crease quantized to one segment
    ///         cuts every corner it used to follow. docs/plan/41's second exit criterion is the one this
    ///         phase exists to make achievable, and a quad count is not worth it.
    ///     </para>
    /// </remarks>
    internal static void Overspent(int faces, RemeshSettings settings, List<string> warnings) {
        if (settings.TargetQuads <= 0 || settings.TargetEdgeLength > 0f) {
            return;
        }

        if (faces > settings.TargetQuads * BudgetTolerance) {
            warnings.Add(
                $"The budget was not met: {faces} quads against {settings.TargetQuads} asked for, "
                + "because the partition's patches are longer round than they are wide."
            );
        }
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
