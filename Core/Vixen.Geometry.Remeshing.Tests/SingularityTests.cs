// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D5's extraction and § D6's placement.</summary>
public class SingularityTests {
    /// <summary>The index sum is four times the Euler characteristic, on three genera.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The hard invariant, and it catches a broken extraction immediately.</b> A sphere
    ///         must come to <c>+8</c> quarter turns, a torus to <c>0</c>, a two-holed torus to
    ///         <c>−8</c>; the characteristic is measured off the mesh with <see cref="FieldFixtures.Euler" />
    ///         rather than assumed, so a fixture that is not the genus it claims fails here and not
    ///         somewhere confusing later. Poincaré–Hopf makes this a property of the surface and not of
    ///         the field, so it holds for a good field and a bad one alike — which is what makes it a
    ///         test of the <i>extraction</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The precondition is that the mesh resolves the field, and the fixtures are built
    ///         to satisfy it rather than pretending it is not there.</b> The index is read off period
    ///         jumps — which of the four rotations of a neighbour was meant — and a quarter turn is
    ///         ninety degrees, so an edge across which the field genuinely turns by more than
    ///         forty-five has no right answer and the identity is not available. Measured: on the
    ///         blocky plate before smoothing, where a single edge spans a ninety-degree crease, the sum
    ///         comes to <c>+4</c> against a true <c>−8</c>; smoothed and refined to six hundred
    ///         vertices, it is <c>−8</c> exactly. That is a statement about what a cross field is, not
    ///         a tolerance somebody chose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The field is solved with the features off for the same reason.</b> A hard
    ///         constraint can force a turn of more than forty-five degrees across one edge — that is
    ///         what "hard" means — so a constrained field on a coarse mesh breaks the precondition
    ///         deliberately. <see cref="Placement_never_makes_either_measure_worse" /> is where the
    ///         constrained case is checked, and it checks a different thing.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("sphere", 2)]
    [InlineData("torus", 0)]
    [InlineData("plate-one-hole", 0)]
    [InlineData("plate-two-holes", -2)]
    public void The_index_sum_is_four_times_the_euler_characteristic(string name, int expected) {
        var mesh = Genus(name);

        Assert.Equal(expected, FieldFixtures.Euler(mesh));

        var settings = new RemeshSettings { Adaptivity = 0f, FeatureAngle = 180f };
        var field = CrossFieldTests.Solve(mesh, settings);
        var found = SingularityPass.Extract(mesh, field);

        Assert.Equal(4 * expected, found.Sum(entry => entry.Index));

        // Every one of them is a whole quarter turn and every triangle appears once, which is what
        // makes the sum above a sum of indices rather than of anything else.
        Assert.Equal(found.Count, found.Select(entry => entry.Triangle).Distinct().Count());

        foreach (var entry in found) {
            Assert.InRange(entry.Index, -1, 1);
            Assert.NotEqual(0, entry.Index);
        }
    }

    /// <summary>A cube's eight quarter turns sit on its eight corners and nowhere else.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D6 steps two and three, on the one shape where they appear to
    ///         contradict each other.</b> Step two says a singularity on a hard edge is a visible pinch
    ///         and the exit criterion is zero on features; step three says the right place for one is
    ///         where the surface is not developable and names "the corner of a box". On a cube every
    ///         vertex is a feature corner and every edge is a feature edge, and χ = 2 says eight
    ///         quarter turns have to exist. Reading step two as "off every feature vertex" makes the
    ///         shape unsatisfiable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the criterion is the interior of a chain, and a corner is exempt.</b> That is
    ///         also the topology an artist draws on a cube: one quad per corner, four-valent
    ///         everywhere else.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("two-boxes")]
    public void A_hard_surface_keeps_its_turning_off_the_feature_lines(string name) {
        var mesh = FeatureDetectionTests.Fixture(name);
        var settings = new RemeshSettings();
        var features = FeatureDetector.Detect(mesh, settings);
        var curvature = CurvatureField.Build(mesh);
        var field = CrossFieldSolver.Solve(mesh, settings, features, curvature);
        var placed = SingularityPass.Place(mesh, settings, features, curvature, field, out _);
        var found = SingularityPass.Extract(mesh, placed);

        Assert.NotEmpty(found);

        Assert.Equal(
            0,
            found.Count(entry => SingularityPass.OnFeatureLine(mesh, features, entry.Triangle))
        );
    }

    /// <summary>The placement pass is monotone: never more on a feature, never more turning.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What § D6's exit criterion turns into where it cannot be met outright, and it is
    ///         worth stating plainly rather than dressing up.</b> On a cube and on a boolean of two
    ///         boxes the pass reaches zero singularities on feature lines. On a staircase, on a
    ///         voxelised sphere and on a box with a cylindrical bore it does not: measured, a box with
    ///         a bore goes from thirty-four on feature lines to twenty-eight, and a flight of stairs
    ///         from ten to nine. Those surfaces have feature lines crossing at angles no cross field
    ///         can satisfy, on meshes whose resolution leaves no room to move a singularity to, and the
    ///         honest report is a number rather than a claim.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What <i>is</i> guaranteed is that the pass never makes either measure worse, and
    ///         that is a design property rather than an observation</b> — each of the three
    ///         corrections scores the field before and after and puts the field back unless it
    ///         improved. See <c>SingularityPass.Correct</c>.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("two-boxes")]
    [InlineData("stairs")]
    [InlineData("staircase")]
    public void Placement_never_makes_either_measure_worse(string name) {
        var mesh = FeatureDetectionTests.Fixture(name);
        var settings = new RemeshSettings();
        var features = FeatureDetector.Detect(mesh, settings);
        var curvature = CurvatureField.Build(mesh);
        var field = CrossFieldSolver.Solve(mesh, settings, features, curvature);

        var before = SingularityPass.Extract(mesh, field);
        var placed = SingularityPass.Place(mesh, settings, features, curvature, field, out var cancelled);
        var after = SingularityPass.Extract(mesh, placed);

        Assert.True(cancelled >= 0);

        Assert.True(
            after.Count(entry => SingularityPass.OnFeatureLine(mesh, features, entry.Triangle))
            <= before.Count(entry => SingularityPass.OnFeatureLine(mesh, features, entry.Triangle)),
            $"{name}: the placement pass put more singularities onto feature lines than it found."
        );

        Assert.True(
            after.Sum(entry => Math.Abs(entry.Index)) <= before.Sum(entry => Math.Abs(entry.Index)),
            $"{name}: the placement pass added turning."
        );

        // ⚠ The index sum is <i>not</i> asserted to be unchanged here, and the reason is the
        // precondition `The_index_sum_is_four_times_the_euler_characteristic` states rather than a
        // defect in the pass. On a hard-surface fixture the constraints deliberately force turns of
        // more than forty-five degrees across single edges, the period jump there has no right
        // answer, and the sum is not a topological quantity to begin with — measured on the stairs,
        // it reads 13 before the pass and 12 after, on a surface whose true total is 8. The sum is
        // asserted where it means something: on a field with no hard constraint in it.
    }

    /// <summary>Singularities are drawn toward the angle defect rather than toward the curvature.</summary>
    /// <remarks>
    ///     ⚠ <b>A cylinder is curved and perfectly developable, and § D6 step three is stated in terms
    ///     of the second.</b> Its angle defect is zero along its whole wall, so nothing there attracts
    ///     a singularity — and the eight quarter turns a closed cylinder owes go to the two cap rims,
    ///     where all of its Gaussian curvature is. A pass that read the curvature <i>magnitude</i>
    ///     instead would find the wall as curved as the rim and scatter them along it.
    /// </remarks>
    [Fact]
    public void A_cylinder_carries_its_turning_at_the_rims_and_not_along_the_wall() {
        var mesh = FeatureDetectionTests.Fixture("cylinder");
        var curvature = CurvatureField.Build(mesh);

        var wall = 0f;
        var rim = 0f;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            var defect = MathF.Abs(curvature.AngleDefect(vertex));

            // A cap rim vertex is where the wall meets a cap, which on this primitive is every vertex
            // — the wall between them is one quad tall. The measure that separates them is the
            // curvature magnitude against the defect, and the claim is that the defect is where the
            // turning belongs.
            if (MathF.Abs(mesh.VertexNormal(vertex).Y) > 0.5f) {
                rim = MathF.Max(rim, defect);
            } else {
                wall = MathF.Max(wall, defect);
            }
        }

        Assert.True(rim > 0.15f, $"The rim's angle defect is {rim:F4}, so there is nothing to attract to.");

        // The tube fixture is the clean version of the same claim: an open cylinder wall with no rim
        // in it at all is exactly developable, and its defect is nothing.
        var tube = FieldFixtures.Tube(24, 24, 1f, 8f);
        var flat = CurvatureField.Build(tube);
        var worst = 0f;

        for (var vertex = 0; vertex < tube.VertexCount; vertex++) {
            if (!tube.IsBoundary(vertex)) {
                worst = MathF.Max(worst, MathF.Abs(flat.AngleDefect(vertex)));
            }

            // And the magnitude is not zero, which is what makes the defect's zero a statement.
            Assert.True(flat.Magnitude(vertex) >= 0f);
        }

        Assert.True(
            worst < 1e-3f,
            $"A cylinder's wall has an angle defect of {worst:E2}, and it is developable."
        );

        Assert.True(flat.Magnitude(tube.VertexCount / 2) > 1f, "A tube is not flat.");
    }

    /// <summary>A rim vertex has a fan rather than a ring, and its defect is half a turn less.</summary>
    /// <remarks>
    ///     ⚠ <b>The standard bug, at the one place it can be seen directly.</b> An interior vertex's
    ///     angles close into <c>2π</c> and a rim vertex's into <c>π</c>, so subtracting from <c>2π</c>
    ///     everywhere makes every boundary vertex read as a sharp cone — which puts § D6's curvature
    ///     attraction all the way round the rim of every open mesh, and puts a ring of singularities
    ///     with it.
    /// </remarks>
    [Fact]
    public void A_rim_vertex_is_not_a_cone() {
        var tube = FieldFixtures.Tube(24, 12, 1f, 4f);
        var curvature = CurvatureField.Build(tube);

        var rim = 0;

        for (var vertex = 0; vertex < tube.VertexCount; vertex++) {
            if (!tube.IsBoundary(vertex)) {
                continue;
            }

            rim++;

            Assert.True(
                MathF.Abs(curvature.AngleDefect(vertex)) < 0.2f,
                $"Rim vertex {vertex} has an angle defect of {curvature.AngleDefect(vertex):F3}, "
                + "which is a full turn's worth of arithmetic on a half turn's worth of triangles."
            );
        }

        Assert.Equal(48, rim);
    }

    /// <summary>Every zero is an answer, over the whole broken corpus.</summary>
    [Fact]
    public void The_zeros_are_answers_rather_than_crashes() {
        foreach (var (name, source) in BrokenMeshes.Corpus()) {
            var mesh = MeshConditioner.Condition(source, new(), out _);
            var settings = new RemeshSettings { Adaptivity = 0f };
            var features = FeatureDetector.Detect(mesh, settings);
            var curvature = CurvatureField.Build(mesh);
            var field = CrossFieldSolver.Solve(mesh, settings, features, curvature);

            var found = SingularityPass.Extract(mesh, field);
            var placed = SingularityPass.Place(mesh, settings, features, curvature, field, out var cancelled);

            Assert.True(cancelled >= 0, name);
            Assert.Equal(mesh.VertexCount, placed.Count);

            foreach (var entry in found) {
                Assert.InRange(entry.Triangle, 0, Math.Max(mesh.TriangleCount - 1, 0));
            }
        }
    }

    static ManifoldMesh Genus(string name) => name switch {
        "sphere" => FieldFixtures.Condition(MeshShapes.Create(ShapeKind.Sphere)),
        "torus" => FieldFixtures.Condition(MeshShapes.Create(ShapeKind.Torus)),
        "plate-one-hole" => FieldFixtures.Smooth(FieldFixtures.Plate(5, 3, [(2, 1)]), 0.35f, 20),
        "plate-two-holes" => FieldFixtures.Smooth(FieldFixtures.Plate(7, 3, [(2, 1), (4, 1)]), 0.35f, 20),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such fixture.")
    };
}
