// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Geometry.Testing;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41's robustness criterion over the <i>pipeline</i>, which is what it is a sentence about.</summary>
/// <remarks>
///     <para>
///         <b>Exit criterion 7 says "a corpus of 200 deliberately broken meshes … produces a valid
///         all-quad result <i>or</i> a <c>RemeshReport</c> naming the stage that refused, and
///         <b>never</b> an exception or a hang", and until this file existed nothing generative called
///         <see cref="Remesher.Remesh" />.</b> <see cref="ConditioningPropertyTests" /> states the
///         criterion over <see cref="MeshConditioner.Condition" />, which is stage <i>one</i> of seven,
///         and <see cref="FieldPropertyTests" /> reaches stages two and three. Every property that
///         holds over a stage can still fail over the composition — a layout is built on a field that
///         a conditioner declared healthy, and the words "all-quad" name a thing only stage six
///         produces.
///     </para>
///     <para>
///         ⚠ <b><see cref="RunawayGuard" /> is not decoration and the allocation half is the one that
///         matters.</b> Criterion 7's own note says so: five loops were suspected of non-termination
///         and every one was refuted, and what is actually unbounded is <i>growth</i> — an isotropic
///         pre-remesh handed a target far below the mesh's own mean quadruples its triangle count
///         every round and allocates 763 MB on the tenth. No timeout catches that before the runner
///         dies. ⚠ <b>So nothing here passes <see cref="RemeshSettings.TargetEdgeLength" /></b>; the
///         budget comes off <see cref="RemeshSettings.TargetQuads" />, which derives the length from
///         the surface's own area and is what keeps the ordinary path safe.
///     </para>
///     <para>
///         ⚠ <b>The build count is single digits per property and that is deliberate.</b> One case
///         here is a whole seven-stage remesh — seconds on a 400-quad fixture, against milliseconds
///         for a conditioning — and this suite is already the slow one. These are correctness-shaped
///         claims rather than a search, so a handful of cases a build is a real regression test and
///         <see cref="PropertyBudget" /> is what turns them into a search overnight. See the nightly's
///         <c>properties</c> matrix.
///     </para>
///     <para>
///         ⚠ <b>The budget is deliberately small — 96 quads rather than the fixtures' 400 — and
///         criterion 7 does not care.</b> It is about whether a stage refuses or throws, not about how
///         many quads came out; the quantizer's flow and the extractor's grids run either way.
///     </para>
///     <para>
///         ⚠ <b>What a small budget does <i>not</i> buy is a cheap case, and that is a finding rather
///         than a mis-estimate.</b> A patch's quad count is a <i>product</i> of two side lengths, so
///         the overshoot § D9 already records is quadratic in how snaky the partition is and is not
///         reduced by asking for less: measured, a 648-face capsule asked for 96 quads produced
///         <b>339,330</b> of them. <see cref="Cap" /> has the case and the timing, and
///         <see cref="Every_broken_mesh_remeshes_to_all_quads_or_to_a_report_naming_the_stage_that_refused" />
///         asserts that a result over <see cref="Remesher.BudgetTolerance" /> says so in the report.
///     </para>
/// </remarks>
public class RemeshPipelinePropertyTests {
    /// <summary>How many quads a case asks for. Small, for the reason in the class remarks.</summary>
    const int Budget = 96;

    /// <summary>How long one whole-pipeline case may run before that is a finding rather than a slow test.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="RunawayGuard.Cap" />'s sixty seconds was measured against the
    ///         <i>stage-level</i> suites and a seven-stage remesh is a different measurement.</b> Its own
    ///         remarks say so: "the slowest healthy case in either suite is the packer's sixteen-attempt
    ///         refusal at just under ten seconds, and the slowest conditioning case is under four". One
    ///         case here runs all seven stages, and the extraction alone is where the time goes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The number is four minutes because of a measured case that is a defect and not a
    ///         hang, and the defect is recorded rather than tuned around.</b> Found by this property at
    ///         sixty times its build count:
    ///         <c>new(ShapeKind.Capsule, 9, 3, [MeshDefect.TinyComponent], 5, 0.1f, 1f)</c>, mirrored
    ///         about <c>(0, 1, 0)</c>, 648 faces in, asked for 96 quads and produced <b>339,330</b> in
    ///         <b>93 seconds</b> — 48.7 of them in the extract stage. It terminates, every face is a
    ///         quad, and the report says so in as many words: "the budget was not met: 169665 quads
    ///         against 96 asked for, because the partition's patches are longer round than they are
    ///         wide". So it is not a hang and it is not an all-quad failure; it is
    ///         <see cref="Remesher.BudgetTolerance" /> being 1.35 against a measured 3,534×.
    ///         <see cref="Every_broken_mesh_remeshes_to_all_quads_or_to_a_report_naming_the_stage_that_refused" />
    ///         asserts that such a result warns, so the overshoot is visible in CI rather than only on a
    ///         stopwatch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The overshoot is a <i>ratio</i> and not a floor, which means no budget is a safe
    ///         one.</b> The same mesh without the mirror, at three budgets: 96 asked for gives 57,100
    ///         quads in 18.5 s, 400 gives 233,490 in 102.6 s, and 2,000 gives <b>1,166,707 in 624.8
    ///         s</b> — 595×, 584× and 583×. Asking for fewer quads buys a proportionally cheaper case
    ///         and does not bring the factor down at all, which is what makes 96 the right number here
    ///         and what makes the factor a defect rather than a small-budget artefact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The allocation ceiling is untouched and it is the one that matters.</b> The clock is
    ///         raised here; <see cref="RunawayGuard.RetentionCeiling" /> is not, because the measured
    ///         runaway in this code — a pre-remesh quadrupling its triangle count every round and
    ///         allocating 763 MB in one — is a growth failure that no timeout catches before the runner
    ///         dies.
    ///     </para>
    /// </remarks>
    static readonly TimeSpan Cap = TimeSpan.FromMinutes(4);

    /// <summary>A recipe and the three settings that change which stages actually run.</summary>
    /// <remarks>
    ///     ⚠ <b>Stage seven is swept because it is two stages wearing one name.</b>
    ///     <see cref="RemeshSettings.TransferAttributes" /> resamples the source's layers onto the
    ///     output and <see cref="RemeshSettings.GenerateUvs" /> builds an atlas out of the patch grids
    ///     — different code, different failure modes, and both off is a supported configuration that
    ///     nothing else generative reaches.
    /// </remarks>
    static readonly Gen<(MeshRecipe Recipe, bool Transfer, bool Uvs, int Rounds)> Case = Gen.Select(
        BrokenMeshSpace.Sized,
        Gen.Bool,
        Gen.Bool,
        Gen.Int[0, 2],
        (recipe, transfer, uvs, rounds) => (recipe, transfer, uvs, rounds)
    );

    /// <summary>docs/plan/41's exit criterion 7, over the whole of what it is a sentence about.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three claims, and the criterion is exactly their conjunction.</b> The result is
    ///         all-quad or it is empty — never a third thing, and a quad-<i>dominant</i> mesh is the
    ///         third thing § D8 refuses. An empty result carries a warning naming the stage that
    ///         refused, because a caller in an unattended content build has nothing else to act on.
    ///         And the case comes back at all, which is <see cref="RunawayGuard" />'s half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The stage ledger is asserted as a <i>prefix</i> rather than as all seven, and that
    ///         is what <see cref="Remesher" /> actually promises.</b> A refusal at the layout stage
    ///         adds four timings and stops; the criterion's "naming the stage that refused" is served
    ///         by the last entry being the stage that did it. What must hold everywhere is that the
    ///         entries are in <see cref="RemeshStage" />'s own order with nothing repeated and nothing
    ///         skipped — a ledger with two <c>Field</c> rows or a <c>Layout</c> before a <c>Features</c>
    ///         is a report that cannot be read — and that a result with faces in it carries all seven.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="RemeshReport.QuadCount" /> plus <see cref="RemeshReport.NonQuadCount" />
    ///         is asserted against the mesh's own face count rather than against itself.</b> A report
    ///         that disagrees with the mesh it describes is worse than no report, and the two are
    ///         computed in different places — <see cref="RemeshMetrics.Faces" /> on the whole-mesh path
    ///         and again in <see cref="SymmetryPass" />, which recounts rather than doubling.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_broken_mesh_remeshes_to_all_quads_or_to_a_report_naming_the_stage_that_refused() {
        var produced = 0;
        var refused = 0;

        Case.Sample(
            entry => {
                var (recipe, transfer, uvs, rounds) = entry;

                var settings = new RemeshSettings {
                    TargetQuads = Budget,
                    TransferAttributes = transfer,
                    GenerateUvs = uvs,
                    Conditioning = new() { PreRemeshIterations = rounds }
                };

                var what = Describe(recipe, transfer, uvs, rounds);
                var mesh = RunawayGuard.Run($"building {recipe}", () => BrokenMeshSpace.Build(recipe));

                var outcome = RunawayGuard.Run(
                    what,
                    () => Remesher.Remesh(mesh, settings, out var report) is var quads ? (quads, report) : default,
                    Cap
                );

                if (Verify(what, outcome.quads, outcome.report)) {
                    produced++;
                } else {
                    refused++;
                }

                // ⚠ The budget is not a bound and this is what makes that visible. § D9's density
                // field asks for more quads than the budget before a partition exists, and a patch's
                // quad count is a product of two side lengths — so a snaky partition overshoots
                // quadratically. What is asserted is not a ceiling on the overshoot, which would be a
                // number nobody has: it is that a result over BudgetTolerance says so, because an
                // unattended content build has nothing else to read. See Cap's remarks for the 3,534×
                // this property measured.
                if (outcome.report.QuadCount > Budget * Remesher.BudgetTolerance) {
                    Assert.Contains(
                        outcome.report.Warnings,
                        warning => warning.Contains("budget", StringComparison.Ordinal)
                    );
                }
            },
            iter: PropertyBudget.Iterations(8),
            threads: 1
        );

        // ⚠ The guard on the guard, and it points both ways. A generator that drifted into producing
        // only rubble would leave every claim about a quad untested; one that never refused would
        // leave the refusal half untested, and the refusal half is where the criterion's "naming the
        // stage" lives. Both are asserted only overnight — eight cases a build cannot support a
        // statement about a distribution, and a build that failed here would be failing on a coin
        // toss rather than on the code.
        if (PropertyBudget.IsNightly) {
            Assert.True(produced > 0, $"None of {produced + refused} sampled meshes produced a single quad.");
            Assert.True(refused > 0, $"None of {produced + refused} sampled meshes reached a refusal.");
        }
    }

    /// <summary>The same criterion through § D11's mirror, which is a second entry point into all seven.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="SymmetryPass" /> is a wrapper around <see cref="Remesher.Remesh" />
    ///         rather than a stage inside it, so nothing above reaches it.</b> It cuts the source with
    ///         <see cref="MeshBoolean.PlaneCut" />, calls back in with the setting cleared, reflects
    ///         what comes out and recounts the faces — which is four opportunities to produce a mesh
    ///         the criterion forbids, on top of the seven stages, and one of them is a boolean handed a
    ///         deliberately non-manifold input.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The plane is swept over three axes and an off-axis one, because the pass branches
    ///         on exactly that.</b> An axis plane through the origin makes the mirror a sign flip and
    ///         § D11's fourth exit criterion holds bit-for-bit; anything else is a rounded reflection,
    ///         which the pass warns about and which is the branch where a mirrored vertex can land off
    ///         the seam.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_broken_mesh_remeshed_about_a_plane_obeys_the_same_criterion() {
        Gen.Select(
                BrokenMeshSpace.Recipe,
                Gen.OneOfConst(
                    new Plane(Vector3.UnitX, 0f),
                    new Plane(Vector3.UnitY, 0f),
                    new Plane(Vector3.UnitZ, 0f),
                    new Plane(Vector3.Normalize(new(1f, 1f, 0f)), 0.25f)
                ),
                (recipe, plane) => (recipe, plane)
            )
            .Sample(
                entry => {
                    var (recipe, plane) = entry;

                    var settings = new RemeshSettings {
                        TargetQuads = Budget,
                        Symmetry = plane,
                        Conditioning = new() { PreRemeshIterations = 1 }
                    };

                    var what = string.Create(
                        CultureInfo.InvariantCulture,
                        $"remeshing {recipe} about ({plane.Normal.X:R}, {plane.Normal.Y:R}, {plane.Normal.Z:R}), "
                        + $"{plane.D:R}"
                    );

                    var mesh = RunawayGuard.Run($"building {recipe}", () => BrokenMeshSpace.Build(recipe));

                    var outcome = RunawayGuard.Run(
                        what,
                        () => Remesher.Remesh(mesh, settings, out var report) is var quads ? (quads, report) : default,
                        Cap
                    );

                    Verify(what, outcome.quads, outcome.report);
                },
                iter: PropertyBudget.Iterations(6),
                threads: 1
            );
    }

    /// <summary>Asserts the criterion against one result, and says whether it produced quads.</summary>
    /// <param name="what">The case, printed on failure. ⚠ Make it pasteable — see the remarks.</param>
    /// <param name="quads">What came out.</param>
    /// <param name="report">What the pipeline said about it.</param>
    /// <returns>Whether the result had faces in it, rather than being a refusal.</returns>
    /// <remarks>
    ///     ⚠ <b>Every message here ends in the recipe rather than in the mesh, because a shrunk case is
    ///     only a finding if it can be pasted into a test.</b> <see cref="MeshRecipe.ToString" /> prints
    ///     a constructor call with round-trippable floats; a four-thousand-triangle failing mesh is not
    ///     something anybody can act on.
    /// </remarks>
    static bool Verify(string what, EditMesh quads, RemeshReport report) {
        Ledger(what, report);

        Assert.True(
            report.QuadCount + report.NonQuadCount == quads.FaceCount,
            $"{what}: the report counts {report.QuadCount} quads and {report.NonQuadCount} others "
            + $"against {quads.FaceCount} faces in the mesh."
        );

        if (quads.FaceCount == 0) {
            // The refusal half. An empty result is an answer, and the criterion asks that it name the
            // stage that gave it — which is the only thing an unattended content build can act on.
            Assert.True(
                report.Warnings.Any(warning => Named(warning) is not null),
                $"{what}: refused with nothing naming a stage — [{string.Join(" · ", report.Warnings)}]."
            );

            return false;
        }

        // The all-quad half. § D8 is the one place the plan refuses to compromise: doc 24's loops,
        // rings and loop cuts are statements about four-sided faces, so a triangle in the output
        // stops the mesh kernel's whole vocabulary working rather than lowering a quality figure.
        Assert.True(report.IsAllQuad, $"{what}: {report.NonQuadCount} of {quads.FaceCount} faces are not quads.");

        for (var face = 0; face < quads.FaceCount; face++) {
            Assert.True(quads.Faces[face].Count == 4, $"{what}: face {face} has {quads.Faces[face].Count} sides.");
        }

        Assert.True(
            report.Conditioning.Triangles > 0,
            $"{what}: {quads.FaceCount} quads came out of a conditioning that reported no triangles."
        );

        foreach (var position in quads.Positions) {
            Assert.True(
                float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z),
                $"{what}: a vertex came out at {position}."
            );
        }

        return true;
    }

    /// <summary>The stage ledger: in order, nothing repeated, and all seven when there is a result.</summary>
    static void Ledger(string what, RemeshReport report) {
        var seen = -1;

        foreach (var timing in report.Stages) {
            Assert.True(
                (int)timing.Stage > seen,
                $"{what}: the stage ledger runs [{string.Join(", ", report.Stages.Select(entry => entry.Stage))}]."
            );

            seen = (int)timing.Stage;

            Assert.True(timing.Elements >= 0, $"{what}: {timing.Stage} reports {timing.Elements} elements.");
            Assert.True(timing.Elapsed >= TimeSpan.Zero, $"{what}: {timing.Stage} took {timing.Elapsed}.");
        }

        if (report.QuadCount > 0) {
            Assert.Equal(Enum.GetValues<RemeshStage>(), report.Stages.Select(timing => timing.Stage));
        }
    }

    /// <summary>Which stage a warning names, or null for one that names none.</summary>
    /// <remarks>
    ///     ⚠ <b>Matched on the stage's own name rather than on the sentence, so a reworded warning
    ///     still satisfies the criterion and a warning that stopped naming a stage does not.</b>
    ///     <c>Condition</c> is a prefix of "Conditioning", which is what the first refusal says.
    /// </remarks>
    static RemeshStage? Named(string warning) {
        foreach (var stage in Enum.GetValues<RemeshStage>()) {
            if (warning.Contains(stage.ToString(), StringComparison.OrdinalIgnoreCase)) {
                return stage;
            }
        }

        return null;
    }

    static string Describe(MeshRecipe recipe, bool transfer, bool uvs, int rounds) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"remeshing {recipe} at {Budget} quads, transfer {transfer}, uvs {uvs}, {rounds} pre-remesh rounds"
        );
}
