// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Geometry.Testing;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>Stage one's contract stated over the space of broken meshes rather than over eighteen of them.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41's robustness criterion is a property and was being tested as a corpus.</b>
///         "A corpus of 200 deliberately broken meshes — non-manifold, self-intersecting, open,
///         disconnected, zero-area — produces a valid all-quad result or a <c>RemeshReport</c> naming
///         the stage that refused, and <b>never</b> an exception or a hang." Two hundred is a number
///         somebody chose. <see cref="BrokenMeshes" /> builds eighteen by hand, which is eighteen
///         points chosen for being interesting; <see cref="BrokenMeshSpace" /> is the space they came
///         from.
///     </para>
///     <para>
///         ⚠ <b>The scale property is the one this repository has been bitten by three times in one
///         day</b> — <c>EditMesh.Normal</c>, <c>ManifoldMesh.TriangleNormal</c> and doc 24's capsule
///         poles — because <c>Vector3.Normalize</c> gives up below an <i>absolute</i>
///         <c>MathUtil.ZeroTolerance</c> of <c>1e-6</c>, and a cross product scales as the square of
///         the model. <see cref="ScaleSafe" /> is the established fix and this is the net under it.
///     </para>
///     <para>
///         ⚠ <b>Exact invariance is not expected everywhere, and each place it is not is measured and
///         named rather than tolerated.</b> Past one round the pre-remesh's four operators are
///         threshold comparisons on lengths, and <c>0.001f</c> is not a binary fraction — so a mesh
///         full of exactly-equal edges breaks its ties differently at a thousandth scale and five
///         rounds amplify one different decision into a different mesh;
///         <see cref="ScaleInvarianceTests.FiveRoundsOfThePreRemeshStayWithinARateOfEachOther" /> has
///         the numbers. Two more turned up here that its corpus does not reach: the de-speck's area
///         ratio on a degenerate face, and the hole filler's size threshold. Both are ties in a
///         relative comparison and neither is an absolute constant, which is the distinction every
///         bound below is chosen to preserve — a constant is an order-of-magnitude failure and a tie
///         is a few per cent.
///     </para>
/// </remarks>
public class ConditioningPropertyTests {
    /// <summary>How many triangles a mesh needs before a rate is a measurement about it.</summary>
    const int Floor = 64;

    /// <summary>docs/plan/41's robustness criterion, over the space and under the clock.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every setting is swept, not just the defaults, because five of the seven steps are
    ///         optional and a step that does not run cannot fail.</b> Hole filling, the shrinkwrap and
    ///         the pre-remesh round count are all caller-controlled, and the shrinkwrap in particular
    ///         re-runs the cheap half of conditioning over a surface it has just re-extracted — which
    ///         is fresh input with fresh defects, and the one place in the stage where a defect can be
    ///         manufactured rather than found.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pre-remesh round count stops at three, and that is a finding rather than a
    ///         convenience.</b> <see cref="IsotropicRemesh" />'s loop is bounded by its iteration count
    ///         and cannot fail to terminate, but each round splits every edge longer than four thirds
    ///         of the target, so a target far below the mesh's own mean quadruples the triangle count
    ///         every round: measured, one triangle two thousand units across at a target of one gives
    ///         4, 16, 64… and by the tenth round a million triangles and 763 MB in that round alone.
    ///         The default target is the mesh's own mean edge length, which is what keeps the ordinary
    ///         path safe — <see cref="RemeshSettings.TargetEdgeLength" /> is not, and this test does
    ///         not pass one. <see cref="RunawayGuard" /> is what would catch it if it did.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_broken_mesh_conditions_to_a_manifold_or_a_report_and_never_to_an_exception() {
        Gen.Select(
                BrokenMeshSpace.Sized,
                Gen.Int[0, 3],
                Gen.Bool,
                Gen.Bool,
                (recipe, rounds, fill, shrinkwrap) => (recipe, rounds, fill, shrinkwrap)
            )
            .Sample(
                entry => {
                    var (recipe, rounds, fill, shrinkwrap) = entry;

                    var settings = new ConditioningSettings {
                        PreRemeshIterations = rounds,
                        FillHoles = fill,
                        Shrinkwrap = shrinkwrap
                    };

                    var what = $"conditioning {recipe} with {rounds} rounds, fill {fill}, shrinkwrap {shrinkwrap}";
                    var mesh = RunawayGuard.Run($"building {recipe}", () => BrokenMeshSpace.Build(recipe));

                    var view = RunawayGuard.Run(
                        what,
                        () => MeshConditioner.Condition(mesh, settings, out var report) is var conditioned
                            ? (Mesh: conditioned, Report: report)
                            : default
                    );

                    // The stage's own claim about what it returns: a manifold view, every count in the
                    // report a count of something that happened, and every triangle index in range.
                    Assert.True(view.Report.Mesh.IsManifold, $"{what}: {view.Report.Mesh.Describe()}.");
                    Assert.Equal(view.Mesh.TriangleCount, view.Report.Triangles);
                    Assert.True(view.Report.Welded >= 0, what);
                    Assert.True(view.Report.Unorientable >= 0, what);
                    Assert.True(view.Report.Despecked >= 0, what);
                    Assert.True(view.Report.Cut >= 0, what);
                    Assert.True(view.Report.Filled >= 0, what);

                    foreach (var corner in view.Mesh.Triangles) {
                        Assert.InRange(corner, 0, Math.Max(view.Mesh.VertexCount - 1, 0));
                    }

                    foreach (var position in view.Mesh.Positions) {
                        Assert.True(
                            float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z),
                            $"{what}: a vertex came out at {position}."
                        );
                    }
                },
                iter: PropertyBudget.Iterations(200),
                threads: 1
            );
    }

    /// <summary>Steps one to four, over the space, at a thousandth and a thousand times.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Four facts exactly and two counts within a tenth, and the split is where the finding
    ///         is.</b> Whether the mesh came out manifold, whether it came out closed, whether the
    ///         orientation flood fill gave up and whether the shrinkwrap fired are statements about
    ///         topology: <see cref="ScaleInvarianceTests.Compare" /> asserts all four at every strength
    ///         and they never moved, over two thousand generated recipes at a thousandth and a thousand
    ///         times. The triangle and vertex counts did move, by up to 3.45 per cent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exact agreement is a property of the eighteen named fixtures and not of the space,
    ///         and the difference is a finding rather than a weakening.</b>
    ///         <see cref="ScaleInvarianceTests.TheFirstFiveStepsGiveTheSameAnswerAtBothScales" /> gets
    ///         every count and every triangle index to agree on <see cref="BrokenMeshes" />' corpus;
    ///         over the space it does not, and the case that breaks it is instructive. Measured:
    ///         <c>de-specked 16 against 17</c> on a capsule carrying a zero-area face and five tiny
    ///         components. The de-speck compares component areas as a <i>ratio</i>, which is scale-free
    ///         in exact arithmetic — but an area is a cross product and a cross product scales as the
    ///         square of the model, so a degenerate face's area is representable at a thousand times
    ///         and underflows to nothing at a thousandth. That is the same family as
    ///         <see cref="ScaleSafe" />'s and it is a tie in a relative comparison rather than an
    ///         absolute constant, which is what a tenth distinguishes: a hard-coded area bound would
    ///         drop every component at one scale and none at the other.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_first_four_steps_agree_on_their_topology_at_both_scales() {
        BrokenMeshSpace.Recipe.Sample(
            recipe => {
                var mesh = RunawayGuard.Run($"building {recipe}", () => BrokenMeshSpace.Build(recipe));

                RunawayGuard.Run(
                    $"conditioning {recipe} at both scales",
                    () => ScaleInvarianceTests.Compare(
                        recipe.ToString(),
                        mesh,
                        new() { PreRemeshIterations = 0, FillHoles = false },
                        ScaleInvarianceTests.Agreement.Rate,
                        0.1f
                    )
                );
            },
            iter: PropertyBudget.Iterations(150),
            threads: 1
        );
    }

    /// <summary>Step five, whose threshold is relative and whose comparison is still a knife edge.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ Hole filling is the one step of the seven that is not scale-invariant, and what
    ///         moves is not a count — it is whether the result is a closed surface.</b> Measured over
    ///         eight runs of a hundred and fifty generated recipes: two of them produced "one scale
    ///         came out closed and the other did not", both on a flight of stairs, and the same sweep
    ///         with filling off produced none in three thousand. The remaining disagreements are
    ///         <c>filled 0 against 1</c> and <c>filled 1 against 2</c>, one of which carried through to
    ///         <c>27 triangles against 25</c>.
    ///     </para>
    ///     <para>
    ///         <b>It is a tie rather than an absolute constant, and the distinction decides what the
    ///         fix would be.</b> The bound is <c>HoleSize × diagonal</c>, a fraction of the model like
    ///         everything else in the stage. What it compares against is a hole's own extent, and a
    ///         hole sitting on the line falls the other way once every coordinate has been perturbed by
    ///         an ulp — which is what multiplying by <c>0.001f</c> does, because it is not a binary
    ///         fraction. A hard-coded hole size would not fill one hole at one scale and two at the
    ///         other; it would fill every hole at one scale and none at the other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So this pins the size of the disagreement rather than denying it, and deliberately
    ///         does not use <see cref="ScaleInvarianceTests.Compare" />:</b> that helper asserts
    ///         closedness at every strength, which is the assertion this step cannot satisfy. One hole
    ///         either way is float noise on a threshold; two would be something else, and this is where
    ///         that would be caught.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Filling_holes_disagrees_by_at_most_one_hole_across_six_orders_of_magnitude() {
        BrokenMeshSpace.Recipe.Sample(
            recipe => {
                var mesh = RunawayGuard.Run($"building {recipe}", () => BrokenMeshSpace.Build(recipe));
                var settings = new ConditioningSettings { PreRemeshIterations = 0, FillHoles = true, HoleSize = 0.3f };

                var small = RunawayGuard.Run(
                    $"filling holes in {recipe} at a thousandth",
                    () => Condition(mesh, settings, 1e-3f)
                );

                var large = RunawayGuard.Run(
                    $"filling holes in {recipe} at a thousand times",
                    () => Condition(mesh, settings, 1e+3f)
                );

                Assert.True(
                    small.Report.Mesh.IsManifold && large.Report.Mesh.IsManifold,
                    $"{recipe}: {small.Report.Mesh.Describe()} against {large.Report.Mesh.Describe()}."
                );

                Assert.True(
                    small.Report.Unorientable == large.Report.Unorientable,
                    $"{recipe}: unorientable {small.Report.Unorientable} against {large.Report.Unorientable}."
                );

                Assert.True(
                    Math.Abs(small.Report.Filled - large.Report.Filled) <= 1,
                    $"{recipe}: filled {small.Report.Filled} against {large.Report.Filled}."
                );

                Near(recipe.ToString(), "triangles", small.Report.Triangles, large.Report.Triangles);
                Near(recipe.ToString(), "vertices", small.Vertices, large.Vertices);
            },
            iter: PropertyBudget.Iterations(150),
            threads: 1
        );
    }

    /// <summary>Step six, one round: the four operators are all comparisons against a length.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Where an absolute constant would do the most damage and be hardest to see.</b>
    ///         Split above four thirds of the target and collapse below four fifths are the two
    ///         comparisons, and the target is derived from the mesh's own mean edge length — so the
    ///         whole loop is scale-free or none of it is. A hard-coded target would turn the
    ///         thousandth-scale mesh into one triangle and the thousand-scale one into a million.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A rate, where
    ///         <see cref="ScaleInvarianceTests.OneRoundOfThePreRemeshGivesTheSameCountsAtBothScales" />
    ///         asserts exact counts — and that difference is a finding rather than a weakening.</b>
    ///         Exact counts hold on its five named fixtures and do not hold over the space: measured
    ///         over three thousand generated recipes, 41 disagreed at one round, the worst by 11.1 per
    ///         cent, and the family that does it is the cone. A cone's base ring is a run of edges of
    ///         <i>exactly</i> equal length, so the mean they are compared against is a tie the whole
    ///         ring sits on and one ulp turns all of it at once — which is the same mechanism the
    ///         five-round remarks describe, arriving four rounds early because the tie is a ring rather
    ///         than an edge. Measured on one such cone: 22 triangles at every scale from a thousandth
    ///         to a hundred, 22 again at a million, and 20 at exactly a thousand. A constant would not
    ///         care which of the eight it was given.
    ///     </para>
    /// </remarks>
    [Fact]
    public void One_round_of_the_pre_remesh_stays_within_a_fifth_at_both_scales() {
        BrokenMeshSpace.Recipe.Sample(
            recipe => {
                var mesh = RunawayGuard.Run($"building {recipe}", () => BrokenMeshSpace.Build(recipe));

                RunawayGuard.Run(
                    $"one pre-remesh round on {recipe} at both scales",
                    () => ScaleInvarianceTests.Compare(
                        recipe.ToString(),
                        mesh,
                        new() { PreRemeshIterations = 1 },
                        ScaleInvarianceTests.Agreement.Rate,
                        0.2f
                    )
                );
            },
            iter: PropertyBudget.Iterations(100),
            threads: 1
        );
    }

    /// <summary>Five rounds, where exact agreement is not achievable and the reason is not a constant.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Measured and reported rather than papered over, and the bound is
    ///         <see cref="ScaleInvarianceTests.FiveRoundsOfThePreRemeshStayWithinARateOfEachOther" />'s.</b>
    ///         From the second round the counts separate: on the staircase sphere, five rounds give
    ///         348 triangles at a thousandth scale against 394 at unit scale and 394 at a thousand
    ///         times. That is float sensitivity in the fixture — <c>0.001f</c> is not a binary
    ///         fraction, so every coordinate is perturbed by an ulp and a mesh full of exactly-equal
    ///         edges breaks its ties differently. An absolute tolerance in the pre-remesh is an
    ///         order-of-magnitude failure, not a twelve-percent one, so a quarter catches it and leaves
    ///         room for the tie-breaking that is genuinely there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Floor" /> triangles or the case is skipped, and the reason is that a
    ///         rate is not a measurement on a mesh of ten triangles.</b> Over three thousand recipes
    ///         with no floor the worst disagreement was 40 per cent — four triangles out of ten, on a
    ///         cone that five rounds had eaten down to nothing much. With the floor the worst over
    ///         eleven hundred qualifying recipes was 12.1 per cent, which is what leaves the quarter
    ///         its headroom. The skip is counted, and the count is asserted, so a generator that
    ///         drifted below the floor could not pass this by testing nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Five_rounds_of_the_pre_remesh_stay_within_a_rate_of_each_other() {
        var measured = 0;

        BrokenMeshSpace.Recipe.Sample(
            recipe => {
                var mesh = RunawayGuard.Run($"building {recipe}", () => BrokenMeshSpace.Build(recipe));
                var settings = new ConditioningSettings { PreRemeshIterations = 5 };

                var size = RunawayGuard.Run(
                    $"sizing {recipe}",
                    () => {
                        MeshConditioner.Condition(mesh, settings, out var report);

                        return report.Triangles;
                    }
                );

                if (size < Floor) {
                    return;
                }

                measured++;

                RunawayGuard.Run(
                    $"five pre-remesh rounds on {recipe} at both scales",
                    () => ScaleInvarianceTests.Compare(
                        recipe.ToString(),
                        mesh,
                        settings,
                        ScaleInvarianceTests.Agreement.Rate,
                        0.25f
                    )
                );
            },
            iter: PropertyBudget.Iterations(150),
            threads: 1
        );

        Assert.True(measured > 20, $"Only {measured} of the sampled recipes reached {Floor} triangles.");
    }

    /// <summary>⚠ And the guard on the guard: a scale property proves nothing about a mesh that has no size.</summary>
    [Fact]
    public void Every_generated_mesh_really_is_six_orders_of_magnitude_from_its_own_small_copy() {
        BrokenMeshSpace.Recipe.Sample(
            recipe => {
                var mesh = BrokenMeshSpace.Build(recipe);

                if (mesh.PositionCount == 0) {
                    return;
                }

                var small = Diagonal(BrokenMeshes.Scaled(mesh, 1e-3f));
                var large = Diagonal(BrokenMeshes.Scaled(mesh, 1e+3f));

                Assert.True(small > 0f, $"{recipe} has no extent at all.");
                Assert.True(large / small > 1e5f, $"{recipe} only grew by {large / small:E2}.");
            },
            iter: PropertyBudget.Iterations(100),
            threads: 1
        );
    }

    static (ConditioningReport Report, int Vertices) Condition(
        EditMesh mesh,
        ConditioningSettings settings,
        float scale
    ) {
        var view = MeshConditioner.Condition(BrokenMeshes.Scaled(mesh, scale), settings, out var report);

        return (report, view.VertexCount);
    }

    /// <summary>Two counts within a tenth of each other. Measured: the worst seen is 3.85 per cent.</summary>
    static void Near(string name, string what, int one, int two) {
        var span = Math.Abs(one - two);
        var bound = Math.Max(one, two) * 0.1f;

        Assert.True(span <= bound, $"{name}: {one} {what} against {two}.");
    }

    static float Diagonal(EditMesh mesh) {
        var box = mesh.Bounds;

        return (box.Maximum - box.Minimum).Length();
    }
}
