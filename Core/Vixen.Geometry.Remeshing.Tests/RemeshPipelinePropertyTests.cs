// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Geometry.Testing;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The classes that measure a case, which therefore may not run while another one measures.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This was bought to protect <see cref="RunawayGuard" />'s clock, and it now earns its
///         keep on the <i>heap</i> instead — the reason is worth keeping because deleting the
///         collection is the obvious next move and it would be wrong.</b> The clock no longer needs
///         defending: <see cref="RunawayGuard.Cap" /> stopped being a threshold sized against a
///         healthy case and became a liveness backstop sized against the nightly leg's own timeout,
///         and nothing scheduling can do reaches twenty minutes. What is still exposed is
///         <see cref="RunawayGuard.RetentionCeiling" />, because <c>GC.GetTotalMemory</c> is
///         process-wide in a way <see cref="Stopwatch" /> is not: two whole-pipeline cases running
///         side by side are one heap, and the growth of either is charged to whichever happens to be
///         in flight. The sixteen-sample grace answers a <i>transient</i> neighbour; it does not
///         answer a neighbour that allocates for the whole case, and the measured runaways in this
///         code are 763 MB and 8.7 GB against a one-gigabyte ceiling.
///     </para>
///     <para>
///         <b>The measurement that forced it, kept because it is the worked example of the defect
///         class.</b> Adding <see cref="RemeshPipelinePropertyTests" /> to this assembly failed
///         <see cref="ConditioningPropertyTests" /> on the first full run — seed
///         <c>3cgNRbukkll2</c>, <c>new(ShapeKind.Sphere, 6, 5, [], 4, 0.27586207f, 0.01f)</c>, "still
///         running after 60.2 s, against a cap of 60.0 s". Re-run with that same seed and that class
///         alone, all six of its tests finish in 1 m 36 s and nothing comes near the cap. The case was
///         never slow; ten cores were.
///     </para>
///     <para>
///         ⚠ <b>Serialising the timers was the right <i>half</i> of the fix and it was mistaken for
///         the whole of it.</b> It removes the interference inside this assembly and does nothing
///         about the machine — a hosted runner with another job on it, or a laptop with three agents
///         on it, contends just the same. Four runs on 2026-08-18, one ten-core machine, one build,
///         the build's counts, with this collection already in force: the suite totalled 93 s, 138 s,
///         187 s and 309 s, and its slowest single case read 12.7 s, 38.1 s, 23.0 s and <b>54.4 s</b>
///         — against a cap that was then sixty. Serialisation had not made the clock a property of
///         the case; it had made it a property of a quieter machine. Only retiring the clock as a
///         threshold did.
///     </para>
///     <para>
///         ⚠ <b>The uv assembly is still deliberately parallel, and now that is a claim about its
///         heap rather than about its clock.</b> Nothing there conditions or remeshes, so nothing
///         there approaches a gigabyte of retention; if something does, this collection is the shape
///         to copy.
///     </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class TimedCases {
    /// <summary>What the collection is called.</summary>
    public const string Name = "timed-cases";
}

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
///         reduced by asking for less. Found by this property and re-measured against the current
///         base: <c>new(ShapeKind.Capsule, 9, 3, [MeshDefect.TinyComponent], 5, 0.1f, 1f)</c>,
///         mirrored about <c>(0, 1, 0)</c>, 648 faces in, asked for 96 quads and produced
///         <b>339,330</b> of them. It terminates, every face is a quad, and the report says so in as
///         many words: "the budget was not met: 169665 quads against 96 asked for, because the
///         partition's patches are longer round than they are wide". So it is neither a hang nor an
///         all-quad failure; it is <see cref="Remesher.BudgetTolerance" /> being 1.35 against a
///         measured <b>3,534×</b>, and
///         <see cref="Every_broken_mesh_remeshes_to_all_quads_or_to_a_report_naming_the_stage_that_refused" />
///         asserts that such a result says so in the report.
///     </para>
///     <para>
///         ⚠ <b>The overshoot is a <i>ratio</i> and not a floor, which means no budget is a safe
///         one.</b> The same mesh without the mirror, at two budgets: 96 asked for gives 57,100 quads
///         and 400 gives 233,490 — <b>595×</b> and <b>584×</b>, against the mirror's 3,534×. Asking
///         for fewer quads buys a proportionally cheaper case and does not bring the factor down at
///         all, which is what makes 96 the right number here and what makes the factor a defect
///         rather than a small-budget artefact. Turning both of stage seven's halves on changes
///         neither the counts nor, measurably, the time.
///     </para>
///     <para>
///         ⚠ <b>The counts above are the finding; the seconds are not, and the two must not be quoted
///         with the same confidence.</b> Every quad count here reproduces exactly — the same integers
///         to the digit on a different machine and several commits later — because they are what the
///         algorithm computes. The wall clock is not: the worst of those four cases runs in
///         <b>11 s</b> on the machine that paragraph was written on and was recorded at <b>93 s</b>
///         when the case was first found, and a whole-pipeline remesh takes every core it is given.
///         ⚠ <b>This class used to carry its own four-minute cap on the strength of that spread, and
///         it was the tighter of the two wall-clock thresholds in the tree</b> — 240 s against a
///         worst healthy recording of 93 s is 2.6× of headroom, where the sixty seconds that actually
///         fired had six. It is gone: <see cref="RunawayGuard.Cap" /> is now a liveness backstop
///         sized against the nightly leg's own timeout rather than against any case's duration, so
///         there is nothing left for a per-suite measurement to tune. See its remarks.
///     </para>
///     <para>
///         ⚠ <b>That assertion currently fails at the nightly's counts, and it is left failing on
///         purpose.</b> The report's budget warning is decided from the quantization's <i>prediction</i>
///         and its <see cref="RemeshReport.QuadCount" /> is counted off the mesh, so a run that drops
///         patches between the two can come out over the tolerance saying nothing —
///         <see cref="Divergence" /> has the one-line reproducer, the counts and the mechanism. A
///         property loosened on its first run is a finding rather than a test, so nothing here is
///         scoped around it and the nightly is where it shows.
///     </para>
///     <para>
///         ⚠ <b>"Currently fails" is dated, and dating it is the point.</b> It has <i>not</i> fired on
///         a nightly in the six runs to <b>2026-08-18</b> — the three red remeshing legs in that window
///         were all the refusal coverage assertion, which is now
///         <see cref="The_inputs_that_refuse_still_name_the_stage_that_refused" />. That is not a claim
///         the defect is closed: nothing has been changed on either side of it and the reproducer in
///         <see cref="Divergence" /> is untouched. It is a claim that the sentence above stopped
///         describing what this leg does, which is exactly how the nightly's own "expected to FAIL"
///         note rotted until three red nights in six went unread. Re-date it or delete it when
///         somebody next has the measurement.
///     </para>
/// </remarks>
[Collection(TimedCases.Name)]
public class RemeshPipelinePropertyTests {
    /// <summary>How many quads a case asks for. Small, for the reason in the class remarks.</summary>
    const int Budget = 96;

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
                    () => Remesher.Remesh(mesh, settings, out var report) is var quads ? (quads, report) : default
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
                // unattended content build has nothing else to read. See the class remarks for the 3,534×
                // this property measured, and Divergence for the case where it does not say so.
                if (outcome.report.QuadCount > Budget * Remesher.BudgetTolerance) {
                    Assert.True(
                        outcome.report.Warnings.Any(warning => warning.Contains("budget", StringComparison.Ordinal)),
                        Divergence(recipe, transfer, uvs, rounds, outcome.report)
                    );
                }
            },
            iter: PropertyBudget.Iterations(8),
            threads: 1
        );

        // ⚠ The guard on the guard, and only one of its two directions can honestly be sampled for.
        // A generator that drifted into producing only rubble would leave every claim about a quad
        // untested, and this catches that: it fails only when every sampled case refuses, which at
        // the rate below is 10⁻²⁵⁵ and is therefore a statement about the generator rather than a
        // coin toss.
        if (PropertyBudget.IsNightly) {
            Assert.True(produced > 0, $"None of {produced + refused} sampled meshes produced a single quad.");
        }
    }

    /// <summary>Two inputs that refuse every time, because the refusal half cannot be sampled for.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what the nightly's <c>refused &gt; 0</c> was trying to buy, bought
    ///         deterministically.</b> That assertion said "at least one of the sampled meshes reached a
    ///         refusal", and the refusal half is where the criterion's "naming the stage that refused"
    ///         lives — so leaving it unexercised would leave half of exit criterion 7 untested. The
    ///         claim is right; sampling was the wrong way to make it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The arithmetic, because a sampled coverage check is only honest with the arithmetic
    ///         beside it.</b> Measured over <b>1,200 cases</b> of this property's own generator:
    ///         <b>9 refusals, a rate of 0.75%</b>. The nightly runs 120 cases, so the chance of seeing
    ///         no refusal at all is <c>(1 − 0.0075)¹²⁰ = 0.41</c> — the assertion lost a coin toss two
    ///         nights in five. It duly did, on <b>2026-08-13, 08-16 and 08-18</b>, three red legs in
    ///         six nights, each reporting "None of 120 sampled meshes reached a refusal" with no seed
    ///         and no case attached, because there was no case: nothing had gone wrong. Sizing it to a
    ///         one-in-a-thousand false failure needs <c>ln(10⁻³) / ln(1 − 0.0075) ≈ 918</c> cases
    ///         against the 120 it runs, on a leg already budgeted at eighteen minutes where one case is
    ///         a whole seven-stage remesh. That is not a number this leg can afford, so the check is
    ///         not resized — it is replaced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A nightly that cries wolf every other night is worse than no nightly.</b> It is
    ///         most of why three red legs in six nights went unread, alongside the workflow's own
    ///         standing "expected to FAIL" note, which had gone stale in both halves and is now
    ///         corrected. Whatever is red here should be the code.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two recipes rather than one, because they refuse at opposite ends of the
    ///         pipeline.</b> Eight of the nine refusals measured were the same shallow one — conditioning
    ///         eats a staircase mesh and stops with "Conditioning left no triangles at all", one stage
    ///         in. The ninth ran the whole way down and refused at the extract stage with a four-part
    ///         report about arcs collapsing and patches quantizing away, which is the only sampled case
    ///         in 1,200 that exercised a refusal <i>after</i> the field and the layout had run. Pinning
    ///         only the cheap one would leave the deep path exactly as untested as sampling did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted through <see cref="Verify" />, the same function the property calls.</b>
    ///         A parallel assertion here would be a second statement of the criterion that could drift
    ///         from the first; <c>Verify</c> returning <c>false</c> <i>is</i> the refusal branch, and it
    ///         is what checks that a warning names a stage.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_inputs_that_refuse_still_name_the_stage_that_refused() {
        foreach (var (recipe, transfer, uvs, rounds) in Refusals) {
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
                () => Remesher.Remesh(mesh, settings, out var report) is var quads ? (quads, report) : default
            );

            // ⚠ Both, and the second is the one that matters. That the case refuses is the premise;
            // that Verify agrees is the criterion, because Verify's refusal branch is where "a report
            // naming the stage that refused" is actually asserted.
            Assert.True(
                outcome.quads.FaceCount == 0,
                $"{what}: pinned as a refusal and came back with {outcome.quads.FaceCount} faces."
            );

            Assert.False(Verify(what, outcome.quads, outcome.report), what);
        }
    }

    /// <summary>The refusing cases, written out so they are inputs rather than a seed.</summary>
    /// <remarks>
    ///     ⚠ Found by sampling — see
    ///     <see cref="The_inputs_that_refuse_still_name_the_stage_that_refused" /> — and then written
    ///     down, which is the whole point. A seed is a claim about a generator and a library version
    ///     and both move; a recipe is the mesh.
    /// </remarks>
    static readonly (MeshRecipe Recipe, bool Transfer, bool Uvs, int Rounds)[] Refusals = [
        // Refuses one stage in: "Conditioning left no triangles at all."
        (
            new(
                ShapeKind.Torus,
                4,
                2,
                [MeshDefect.LargeComponent, MeshDefect.Staircase, MeshDefect.Staircase],
                5,
                0f,
                1f
            ),
            true,
            true,
            0
        ),

        // Refuses at the far end, after the field and the layout have both run: "The extract stage
        // produced no faces", behind three warnings about arcs collapsing and patches quantizing away.
        (new(ShapeKind.Cone, 3, 3, [MeshDefect.DuplicateFaces], 6, 2.0379999E-14f, 1f), false, false, 1)
    ];

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
    ///     <para>
    ///         ⚠ <b>This property found an allocation runaway on its first run at the build's own
    ///         counts. It is closed, by <see cref="Remesher.RunawayMultiple" />, and
    ///         <see cref="The_runaway_recipe_stays_within_a_bounded_multiple_of_its_budget" /> holds the
    ///         line so it cannot come back unnoticed.</b> The numbers below are what it did before that
    ///         brake existed; the same recipe now plans 1,526 quads per half and ships 3,052, with the
    ///         extract stage down from 1.3 s to 48 ms. Seed <c>9hqwA86TjVk1</c>,
    ///         and it reproduces from one line:
    ///         <c>new(ShapeKind.Sphere, 4, 4, [MeshDefect.LargeComponent, MeshDefect.TinyComponent,
    ///         MeshDefect.ZeroLengthEdge], 4, 0.12195122f, 1f)</c> about <c>(1, 0, 0)</c> at
    ///         <see cref="Budget" /> quads. <b>602 faces in; 3,999,656 quads out — 41,663× the budget,
    ///         6,644× the input — allocating 8.7 GB on the calling thread over 42 s.</b>
    ///         <see cref="RunawayGuard" /> stops it at 1.40 GB <i>retained</i>, sixteen samples in a
    ///         row, eight seconds in.
    ///     </para>
    ///     <para>
    ///         <b>It is neither a hang nor an all-quad failure, which is exactly why nothing caught it
    ///         before.</b> The call returns, every face has four sides and the ledger is well formed —
    ///         the criterion's stated half is satisfied. What is unbounded is <i>growth</i>, and the
    ///         allocation ceiling is the only reading that sees it. ⚠ <b>It is also a different runaway
    ///         from the one <see cref="RunawayGuard.RetentionCeiling" /> was measured against</b>: that
    ///         one is an isotropic pre-remesh handed a <see cref="RemeshSettings.TargetEdgeLength" />
    ///         far below the mesh's mean, and nothing here passes that setting at all. This one comes
    ///         off <see cref="RemeshSettings.TargetQuads" />, which is the path the class remarks call
    ///         "what keeps the ordinary path safe".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three defects in the recipe are not separable and the shrinker is right not to
    ///         drop them.</b> Measured on the same plane and budget: the full recipe gives 3,999,656
    ///         quads, but <c>[LargeComponent]</c> alone at the same multiplicity and degeneracy gives
    ///         706, at multiplicity 1 gives 1,680, and the bare sphere gives 1,680. The amplification
    ///         needs the whole combination, so the one line above is the smallest thing to paste.
    ///     </para>
    /// </remarks>
    /// <summary>The allocation runaway's own line, held under the brake rather than under a clock.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one recipe out of the property space above, pinned so the fix has something that
    ///         fails without it.</b> Before <see cref="Remesher.RunawayMultiple" /> existed this planned
    ///         <b>696,592 quads per half against a budget of 96</b> — 7,256× — and spent 1.3 s and
    ///         gigabytes inside the extract stage building them. It now plans 1,526 and ships 3,052,
    ///         and the extract stage takes 48 ms.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is against the ceiling and not against 3,052.</b> Pinning the count
    ///         would break on any change that moves a patch boundary, and the property under test is
    ///         that growth is <i>bounded</i> — not that it lands on a particular number. The brake is
    ///         allowed to leave the result well over the budget; it is not allowed to leave it
    ///         unbounded.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are counted, because the mirror is where the old warning was wrong.</b>
    ///         <see cref="Remesher.Overspent" />'s remarks record a report saying "169665 against 96"
    ///         while the mesh it shipped had 339330 faces. The ceiling here is read off the mesh that
    ///         came back, so the reflection cannot hide inside it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_runaway_recipe_stays_within_a_bounded_multiple_of_its_budget() {
        var recipe = new MeshRecipe(
            ShapeKind.Sphere,
            4,
            4,
            [MeshDefect.LargeComponent, MeshDefect.TinyComponent, MeshDefect.ZeroLengthEdge],
            4,
            0.12195122f,
            1f
        );

        var quads = Remesher.Remesh(
            BrokenMeshSpace.Build(recipe),
            new() {
                TargetQuads = Budget,
                Symmetry = new Plane(Vector3.UnitX, 0f),
                Conditioning = new() { PreRemeshIterations = 1 }
            },
            out var report
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.Equal(0, report.NonQuadCount);

        // Twice the multiple, because the mirror ships both halves and the brake reads one.
        var ceiling = (int) (Budget * Remesher.RunawayMultiple * 2f);

        Assert.True(
            quads.FaceCount <= ceiling,
            $"the mirrored result has {quads.FaceCount} faces against a budget of {Budget} — "
            + $"{(float) quads.FaceCount / Budget:F0}×, over a ceiling of {ceiling}. This recipe is the "
            + "one that allocated 8.7 GB over 42 s before the quantization was braked, and it returns "
            + "and comes back all-quad either way, so nothing but the count catches it."
        );
    }

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
                        () => Remesher.Remesh(mesh, settings, out var report) is var quads ? (quads, report) : default
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

    /// <summary>The message for a result over the tolerance whose report does not say so.</summary>
    /// <param name="recipe">The mesh. ⚠ Printed as a constructor call, so the finding is one paste.</param>
    /// <param name="transfer">Whether attributes were transferred.</param>
    /// <param name="uvs">Whether an atlas was generated.</param>
    /// <param name="rounds">How many pre-remesh rounds ran.</param>
    /// <param name="report">What the pipeline said.</param>
    /// <returns>The finding, with the reproducer and the mechanism.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is a measured defect in <see cref="Remesher" /> and it is deliberately not
    ///         worked around, so this property fails at the nightly's counts until it is fixed.</b>
    ///         Found by this property at fifteen times its build count, seed <c>8dLG7n4AvGV1</c>, and
    ///         it reproduces from one line:
    ///         <c>new(ShapeKind.Sphere, 3, 5, [MeshDefect.SelfIntersection, MeshDefect.LargeComponent],
    ///         1, 0f, 0.01f)</c> at <see cref="Budget" /> quads with both halves of stage seven on and
    ///         one pre-remesh round. 96 faces in; <b>138 quads out against 96 asked for — 1.44×,
    ///         over <see cref="Remesher.BudgetTolerance" />'s 1.35 — and not one of the three warnings
    ///         mentions the budget.</b>
    ///     </para>
    ///     <para>
    ///         <b>The mechanism is that the two numbers are counted in different places and one of them
    ///         is a prediction.</b> <c>Remesher.Budget</c> decides whether to warn from
    ///         <c>Quantizer.QuadCount(layout, quantization.Counts)</c> — what the quantization implies,
    ///         computed before anything is extracted — while <see cref="RemeshReport.QuadCount" /> is
    ///         counted off the mesh that came out. They agree until a patch is dropped between them,
    ///         and this case drops twenty-two: "22 patches quantized to zero in one direction and were
    ///         forced open", "forcing the collapsed patches open made the system unsolvable, so 22 were
    ///         left out", "22 patches were skipped: a side quantized away entirely". The prediction
    ///         landed under the line and the mesh came out over it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The same divergence is visible on the symmetry path at a size nobody could miss,
    ///         which is what says it is one defect rather than a rounding argument.</b>
    ///         The class remarks' 339,330-quad case reports "the budget was not met: 169665 quads
    ///         against 96 asked for" — exactly half, because the prediction is the half-mesh's and
    ///         <see cref="SymmetryPass" /> recounts after reflecting. A caller reading the sentence
    ///         gets a number that is not the number of quads it was handed.
    ///     </para>
    /// </remarks>
    static string Divergence(MeshRecipe recipe, bool transfer, bool uvs, int rounds, RemeshReport report) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Describe(recipe, transfer, uvs, rounds)}: {report.QuadCount} quads came out against {Budget} asked "
            + $"for — {report.QuadCount / (float)Budget:N2}×, over BudgetTolerance's {Remesher.BudgetTolerance:N2} "
            + $"— and no warning mentions the budget: [{string.Join(" · ", report.Warnings)}]. Paste "
            + $"{recipe} into RemesherTests with TargetQuads = {Budget}, TransferAttributes = {transfer}, "
            + $"GenerateUvs = {uvs}, PreRemeshIterations = {rounds}."
        );

    static string Describe(MeshRecipe recipe, bool transfer, bool uvs, int rounds) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"remeshing {recipe} at {Budget} quads, transfer {transfer}, uvs {uvs}, {rounds} pre-remesh rounds"
        );
}
