// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § Part 4's nine fields, each populated <i>and</i> each read by an assertion.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A field in a report that no test reads is a field that can quietly become wrong</b>, and
///         that is the whole failure § Part 4 exists to prevent — a remesher that cannot tell you it
///         went wrong will be trusted until it embarrasses somebody. Being <i>computed</i> is not the
///         property; being <i>checked</i> is. <see cref="RemeshReport.MinScaledJacobian" /> was filled
///         in and read by nothing until <c>QuadQualityTests</c> was written, and an audit of the rest
///         found three more in the same state:
///         <see cref="RemeshReport.Singularities" /> — the entire list, so its positions, valences and
///         indices could all be wrong — <see cref="RemeshReport.MeanDeviation" />, and
///         <see cref="RemeshStageTiming.Elements" /> for all seven stages. This class is the assertions
///         those three were missing.
///     </para>
///     <para>
///         ⚠ <b>Where it can be, each field is computed a second way and compared.</b> A metric that
///         agrees with a naive recomputation is one you can trust; one that does not is a finding. The
///         deviation is re-measured by brute force against every conditioned triangle rather than
///         through the BVH the report used, and the singularity list is rebuilt from the output mesh's
///         own valences rather than from the extraction.
///     </para>
/// </remarks>
public class RemeshReportTests {
    /// <summary>Every irregular vertex, checked against the output mesh rather than against the extraction.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="RemeshMetrics.Singularities" /> returns a pair and only the second half
    ///         of it was ever asserted.</b> The count on features is checked in three places; the list
    ///         itself was read by nothing, so an empty list, a duplicated entry or a valence off by one
    ///         would have gone through every existing test. The recomputation here walks the output's
    ///         one-rings directly: an interior vertex whose valence is not four is a singularity, and
    ///         nothing else is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Interior only, and that is not a simplification.</b> A boundary vertex has an open
    ///         one-ring, so "four quads meet here" is not a statement about it at all — counting one
    ///         would report every vertex on a rim as a defect.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("plate")]
    public void The_singularity_list_is_the_output_mesh_own_irregular_vertices(string name) {
        var quads = Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, $"{name}: {string.Join(" · ", report.Warnings)}");

        var expected = new List<(int Position, int Valence)>();

        for (var position = 0; position < quads.PositionCount; position++) {
            var edges = quads.EdgesAt(position);

            if (edges.Length == 0) {
                continue;
            }

            var interior = true;

            foreach (var edge in edges) {
                interior &= quads.FacesOf(edge).Length == 2;
            }

            if (interior && edges.Length != 4) {
                expected.Add((position, edges.Length));
            }
        }

        Assert.Equal(expected.Count, report.Singularities.Count);
        Assert.Equal(expected.Count, report.Singularities.Select(entry => entry.Position).Distinct().Count());

        // Ascending by position, which is what makes the list itself a deterministic artefact rather
        // than a set that happens to have the right members.
        for (var index = 0; index < expected.Count; index++) {
            var found = report.Singularities[index];

            Assert.Equal(expected[index].Position, found.Position);
            Assert.Equal(expected[index].Valence, found.Valence);
            Assert.Equal(4 - found.Valence, found.Index);
            Assert.NotEqual(4, found.Valence);
            Assert.InRange(found.Position, 0, quads.PositionCount - 1);
        }

        Assert.InRange(report.SingularitiesOnFeatures, 0, report.Singularities.Count);
    }

    /// <summary>On a closed result the indices sum to four times the Euler characteristic.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The Poincaré–Hopf statement for a 4-RoSy field, and the strongest single check the
    ///         list admits.</b> Every quad has four sides, so <c>4F = 2E</c>; summing
    ///         <c>4 − valence</c> over every vertex gives <c>4V − 2E = 4(V − E + F) = 4χ</c>, and a
    ///         regular vertex contributes nothing — so the sum over the <i>irregular</i> ones is the
    ///         whole of it. A list that dropped an entry, counted one twice or recorded a valence
    ///         wrongly cannot satisfy this by accident.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Closed results only, and the guard is the boundary rather than
    ///         <see cref="MeshReport.IsSolid" />.</b> The identity above counts quarter turns around a
    ///         closed one-ring; a vertex on a rim has an open one-ring and contributes a term this
    ///         arithmetic does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured on a 400-quad budget: four of the five fixtures now come out closed
    ///         <i>and</i> manifold, where one did.</b> A sphere always did, at <c>χ = 2</c>, summing to
    ///         exactly 8; a box, a plate and a flight of stairs joined it when the layout stopped
    ///         leaving slits in the partition — every one of them used to lose patches to the
    ///         extractor's refusal and come back either non-manifold or with a rim. A cylinder still
    ///         comes out with a twelve-edge rim from one patch the layout could neither divide nor
    ///         merge, which <c>RemesherTests</c> records as the remaining gap. The guard excludes it,
    ///         and <see cref="The_euler_check_is_not_vacuous" /> is what stops the guard excluding
    ///         everything.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("plate")]
    [InlineData("stairs")]
    public void A_closed_result_sums_its_indices_to_four_times_the_euler_characteristic(string name) {
        var quads = Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

        if (report.QuadCount == 0 || report.Mesh.Boundary.Count > 0 || !report.Mesh.IsManifold) {
            return;
        }

        // ⚠ Counted rather than derived from `4F = 2E`. Deriving it would make the identity an
        // arrangement of the same two numbers and the test would pass on any singularity list at all.
        var incidences = 0;

        for (var position = 0; position < quads.PositionCount; position++) {
            incidences += quads.EdgesAt(position).Length;
        }

        var euler = quads.PositionCount - (incidences / 2) + quads.FaceCount;
        var sum = report.Singularities.Sum(entry => entry.Index);

        Assert.Equal(0, incidences % 2);
        Assert.Equal(4 * euler, sum);
    }

    /// <summary>At least one fixture is closed, or the check above is vacuous everywhere.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A guarded theory that skips every case is a passing test that asserts nothing</b>,
    ///         and the guard above is exactly the shape that invites it. This is the assertion that the
    ///         guard lets something through — and it names the fixtures, so that the day a layout fix
    ///         closes the cylinder this test fails and gets tightened rather than quietly covering more
    ///         than it claims.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It has already done that twice, which is the argument for writing it this way.</b>
    ///         The list read <c>["sphere"]</c> for as long as the partition left dangling cuts in it: a
    ///         cut with a loose end is a slit, the flood puts the same patch on both sides of it, and
    ///         the extractor refuses every patch whose boundary walks an arc twice — so the box, the
    ///         plate and the stairs all came back holed. Walking those loose ends out to existing
    ///         structure closed three of the four. The cylinder was the fourth, and it took the other
    ///         repair this comment predicted: a patch bounded by three arcs that are all creases can be
    ///         neither divided nor merged, and <c>PatchLayout</c> now carries it as a fan — three quads
    ///         round a centre — rather than dropping it. Both times the assertion made the improvement
    ///         announce itself instead of silently widening a guard.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_euler_check_is_not_vacuous() {
        var closed = new List<string>();

        foreach (var name in new[] { "box", "sphere", "cylinder", "plate", "stairs" }) {
            Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

            if (report.QuadCount > 0 && report.Mesh.Boundary.Count == 0 && report.Mesh.IsManifold) {
                closed.Add(name);
            }
        }

        Assert.Equal(["box", "sphere", "cylinder", "plate", "stairs"], closed);
    }

    /// <summary>The mean deviation, against a brute-force recomputation over every conditioned triangle.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Unasserted until now, while <see cref="RemeshReport.MaxDeviation" /> is checked in
    ///         three places.</b> The two are returned from one call, so a mean that was accumulating
    ///         the wrong samples, dividing by the wrong count or quietly reporting the max again would
    ///         have been invisible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Brute force rather than the BVH, because the BVH is what produced the number.</b>
    ///         Re-measuring with <see cref="SurfaceProjector" /> would agree with itself whatever it
    ///         got wrong; the loop below is <c>O(vertices × triangles)</c> point-to-triangle distance
    ///         and owes the thing under test nothing. It is affordable exactly once, on one fixture, at
    ///         a 400-quad budget.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Face centres count as samples as well as vertices</b>, which is the one thing the
    ///         recomputation has to copy rather than derive: a quad can have all four corners on the
    ///         surface and bulge between them, and a mean over vertices alone reports zero on a cage
    ///         that cuts every corner off a sphere.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_mean_deviation_is_the_mean_of_the_samples() {
        var source = RemesherTests.Fixture("sphere");
        var settings = new RemeshSettings { TargetQuads = 400 };
        var quads = Remesher.Remesh(source, settings, out var report);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var soup = TriangleSoup.From(source);
        var area = 0f;

        for (var triangle = 0; triangle < soup.TriangleCount; triangle++) {
            area += soup.Area(triangle);
        }

        var conditioned = MeshConditioner.Condition(source, settings.Conditioning, out _, MathF.Sqrt(area / 400f));
        var points = conditioned.Positions.ToArray();
        var indices = conditioned.Triangles.ToArray();
        var diagonal = conditioned.Diagonal;
        var total = 0d;
        var worst = 0f;
        var samples = 0;

        for (var position = 0; position < quads.PositionCount; position++) {
            Accumulate(quads.Positions[position]);
        }

        for (var face = 0; face < quads.FaceCount; face++) {
            var loop = quads.CornersOf(face);
            var centre = Vector3.Zero;

            foreach (var corner in loop) {
                centre += quads.Positions[corner];
            }

            Accumulate(centre / loop.Length);
        }

        void Accumulate(Vector3 query) {
            var nearest = float.MaxValue;

            for (var triangle = 0; triangle < indices.Length / 3; triangle++) {
                var closest = SurfaceProjector.ClosestOnTriangle(
                    query,
                    points[indices[(triangle * 3) + 0]],
                    points[indices[(triangle * 3) + 1]],
                    points[indices[(triangle * 3) + 2]]
                );

                nearest = MathF.Min(nearest, Vector3.Distance(query, closest));
            }

            var away = nearest / diagonal;

            worst = MathF.Max(worst, away);
            total += away;
            samples++;
        }

        var mean = (float)(total / samples);

        Assert.Equal(quads.PositionCount + quads.FaceCount, samples);
        Assert.Equal(mean, report.MeanDeviation, 5);
        Assert.Equal(worst, report.MaxDeviation, 5);

        // And the two orderings that must hold whatever the fixture: a mean of non-negative samples is
        // non-negative, is not above the worst of them, and on a curved surface is not zero.
        Assert.InRange(report.MeanDeviation, 0f, report.MaxDeviation);
        Assert.True(report.MeanDeviation > 0f, "A quad cage over a sphere deviates from it somewhere.");
    }

    /// <summary>Every stage says how much it handled, and each count is the artefact it handled.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="RemeshStageTiming.Elements" /> was populated for all seven stages and read by
    ///     nothing.</b> The field is half of what makes § D1's stage boundaries a debugging surface —
    ///     <i>which stage dropped something</i> is the question it answers, and a stage that reported
    ///     the wrong count would send whoever asked to the wrong stage. Three of the seven can be
    ///     checked against a number that is in the report anyway, which is exactly the two-ways
    ///     comparison this file is for.
    /// </remarks>
    [Theory]
    [InlineData("sphere")]
    [InlineData("box")]
    [InlineData("plate")]
    public void Every_stage_reports_what_it_handled(string name) {
        var quads = Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, $"{name}: {string.Join(" · ", report.Warnings)}");
        Assert.Equal(Enum.GetValues<RemeshStage>().Length, report.Stages.Count);

        foreach (var timing in report.Stages) {
            Assert.InRange(timing.Elements, 0, int.MaxValue);
            Assert.True(timing.Elapsed >= TimeSpan.Zero, $"{timing.Stage} took {timing.Elapsed}.");
        }

        var stages = report.Stages.ToDictionary(timing => timing.Stage);

        Assert.Equal(report.Conditioning.Triangles, stages[RemeshStage.Condition].Elements);
        Assert.Equal(quads.FaceCount, stages[RemeshStage.Extract].Elements);
        Assert.Equal(report.QuadCount + report.NonQuadCount, stages[RemeshStage.Extract].Elements);

        // ⚠ This asserted stage seven was *zero* until attribute transfer landed, on the reasoning
        // that recording an empty stage is what keeps the report's shape from changing when the
        // stage arrives. The shape did not change and the promise held — what changed is that the
        // stage now does something, so the assertion is inverted rather than deleted.
        //
        // ⚠ And it does something even through the overload that passes no attributes, which is the
        // part worth pinning: normals, texture coordinates and face groups live *inside* EditMesh, so
        // they transfer whether or not a caller has colours or skin weights to hand. A zero here now
        // would mean the output came back with the source's shape and none of its shading.
        Assert.True(
            stages[RemeshStage.Transfer].Elements > 0,
            $"{name}: stage seven handled nothing, so the quads carry no normals, coordinates or groups."
        );

        // The field stage counts the singularities it extracted, so it is positive wherever the layout
        // had structure to work with — which every one of these fixtures does.
        Assert.True(stages[RemeshStage.Field].Elements > 0, $"{name}: the field found no structure at all.");
        Assert.True(stages[RemeshStage.Layout].Elements > 0, $"{name}: the layout produced no patches.");
        Assert.True(stages[RemeshStage.Quantize].Elements > 0, $"{name}: the quantizer had no arcs.");
    }

    /// <summary>All nine of § Part 4's rows come back with a value on a successful remesh.</summary>
    /// <remarks>
    ///     ⚠ <b>The zero that means "not measured" is the failure this catches.</b> Every field below
    ///     has a legitimate zero — no singularities, no deviation, no feature error — so a stage that
    ///     stopped filling one in would look exactly like a perfect result. Asserting the whole row set
    ///     on a fixture where none of them <i>can</i> honestly be zero is what tells the two apart.
    /// </remarks>
    [Fact]
    public void Every_row_of_part_four_is_filled_in() {
        var quads = Remesher.Remesh(RemesherTests.Fixture("sphere"), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0);
        Assert.Equal(0, report.NonQuadCount);
        Assert.True(report.IsAllQuad);
        Assert.NotEmpty(report.Singularities);
        Assert.Equal(0, report.SingularitiesOnFeatures);
        Assert.True(report.MaxDeviation > 0f);
        Assert.True(report.MeanDeviation > 0f);
        Assert.NotEqual(0f, report.MinScaledJacobian);
        Assert.InRange(report.FeatureReproductionError, 0f, 1f);
        Assert.True(report.Conditioning.Triangles > 0);
        Assert.Equal(quads.FaceCount, report.QuadCount);
        Assert.NotEmpty(report.Stages);
        Assert.NotNull(report.Warnings);
        Assert.NotNull(report.Mesh.Boundary);
    }

    /// <summary>A refusal fills the same rows with zeros, and says which stage in the warnings.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of "a field that is structurally always zero".</b> On the refusal path
    ///     every measured row is a literal zero, which is honest only because the warnings are not
    ///     empty and <see cref="RemeshReport.QuadCount" /> is zero beside them. A caller reading
    ///     <see cref="RemeshReport.MinScaledJacobian" /> off a refused remesh gets <c>0</c>, which
    ///     reads as a collapsed quad rather than as no quads at all — so the rule is that the quad count
    ///     is looked at first, and this is where that rule is written down.
    /// </remarks>
    [Fact]
    public void A_refusal_zeroes_every_row_and_names_itself() {
        var empty = new EditMesh();
        var quads = Remesher.Remesh(empty, new() { TargetQuads = 400 }, out var report);

        Assert.Equal(0, quads.FaceCount);
        Assert.Equal(0, report.QuadCount);
        Assert.Equal(0, report.NonQuadCount);
        Assert.Empty(report.Singularities);
        Assert.Equal(0f, report.MaxDeviation);
        Assert.Equal(0f, report.MeanDeviation);
        Assert.Equal(0f, report.MinScaledJacobian);
        Assert.NotEmpty(report.Warnings);
        Assert.NotEmpty(report.Stages);
    }
}
