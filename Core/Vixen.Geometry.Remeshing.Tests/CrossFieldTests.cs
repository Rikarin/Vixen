// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D5: the 4-RoSy field, its hierarchy, and what the constraints are weighted by.</summary>
public class CrossFieldTests {
    /// <summary>A sphere's anisotropy is nothing and a cylinder's is everything.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D5: "This weight is the whole of Adaptive Size on the direction
    ///         side".</b> The soft alignment is weighted by <c>|κ₁ − κ₂|·diagonal</c> and never by
    ///         <c>|κ|</c>, and a sphere is where the two answers are furthest apart: its two principal
    ///         curvatures are equal at every point, so the anisotropy is zero and the field is left
    ///         free to be smooth — while the magnitude is large and uniform, which is what a naive
    ///         alignment would take as "align hard, everywhere", to principal directions that are the
    ///         noise of an ill-conditioned fit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured, and the gap is a factor of twenty-four.</b> On a forty-by-twenty sphere
    ///         the mean anisotropy is 0.15 against a mean magnitude of 3.55; on a cylinder it is 8.26
    ///         against 6.66. So the magnitude says "a sphere is half as curved as a cylinder" and the
    ///         anisotropy says "a sphere has no direction and a cylinder does", and only the second is
    ///         a statement about which way the quads should run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_alignment_weight_is_anisotropy_and_not_curvature() {
        var (sphereAnisotropy, sphereMagnitude) = Curvature(FeatureDetectionTests.Fixture("sphere"));
        var (cylinderAnisotropy, cylinderMagnitude) = Curvature(FeatureDetectionTests.Fixture("cylinder"));

        Assert.True(
            sphereAnisotropy < 0.25f * CrossFieldSolver.AnisotropyReference,
            $"A sphere's mean anisotropy is {sphereAnisotropy:F3}, which is not "
            + "\"the weight is zero and the field is free\"."
        );

        Assert.True(
            cylinderAnisotropy > 2f * CrossFieldSolver.AnisotropyReference,
            $"A cylinder's mean anisotropy is {cylinderAnisotropy:F3}, so nothing would align to its axis."
        );

        // ⚠ And the guard that makes the first assertion mean something: a sphere is not flat. A
        // curvature estimate that returned zero for everything would pass the anisotropy test by
        // having measured nothing at all.
        Assert.True(
            sphereMagnitude > 1f,
            $"A sphere's mean curvature magnitude is {sphereMagnitude:F3}, so the fit found no curvature."
        );

        Assert.True(cylinderMagnitude > 1f);
    }

    /// <summary>A sphere's field stays smooth however hard the curvature is allowed to pull.</summary>
    /// <remarks>
    ///     The consequence of the weighting, rather than the weighting itself: turning Adaptivity from
    ///     zero to one on a sphere must not tear the field, because the weight it scales is zero.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void A_sphere_stays_smooth_at_every_adaptivity(float adaptivity) {
        var mesh = FeatureDetectionTests.Fixture("sphere");
        var settings = new RemeshSettings { Adaptivity = adaptivity };
        var field = Solve(mesh, settings);

        Assert.True(
            MathUtil.RadiansToDegrees(field.Energy(mesh)) < 6f,
            $"At adaptivity {adaptivity} the mean edge deviation is "
            + $"{MathUtil.RadiansToDegrees(field.Energy(mesh)):F2}°, which is a field chasing noise."
        );
    }

    /// <summary>A cylinder's field runs along its axis, at every adaptivity.</summary>
    /// <remarks>
    ///     ⚠ <b>Which of the two principal directions wins does not matter, and that is 4-RoSy rather
    ///     than luck.</b> The axis and the circumference are ninety degrees apart, a cross stands for
    ///     four directions ninety degrees apart, so aligning to either produces the same cross — which
    ///     is why nobody has to decide.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void A_cylinder_runs_along_its_axis(float adaptivity) {
        var mesh = FeatureDetectionTests.Fixture("cylinder");
        var field = Solve(mesh, new() { Adaptivity = adaptivity });

        var aligned = 0f;
        var count = 0;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            var direction = field.Direction(vertex);

            // The walls only. A cap's tangent plane contains the axis nowhere, so "aligned to the
            // axis" is not a question that has an answer there.
            if (direction.LengthSquared() <= 0f || MathF.Abs(mesh.VertexNormal(vertex).Y) > 0.5f) {
                continue;
            }

            aligned += MathF.Abs(
                Vector3.Dot(CrossField.Align(direction, Vector3.UnitY, mesh.VertexNormal(vertex)), Vector3.UnitY)
            );

            count++;
        }

        Assert.True(count > 32, $"Only {count} wall vertices, which proves little.");
        Assert.True(aligned / count > 0.95f, $"The mean alignment to the axis is {aligned / count:F3}.");
    }

    /// <summary>The hierarchy is what makes the field globally consistent, and the flat solve is not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D5: "Without it the smoothing propagates one ring per iteration and a
    ///         2M-vertex mesh never converges; with it, the coarse level fixes the global structure in
    ///         a few hundred elements."</b>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The measure is the singularity <i>count</i> and not the energy, and that is the
    ///         difference between local smoothness and global consistency.</b> A flat solve at the
    ///         same iteration count produces a field that is smooth everywhere it looks and wrong in
    ///         the large: the geometric initialization tears at the octant boundaries of each vertex's
    ///         own position, the tears cannot reach each other in the iterations available, and each
    ///         one freezes into a spurious singularity pair. Measured on a 128-meridian sphere: the
    ///         hierarchy leaves eight singularities, which is the fewest a sphere can have; the flat
    ///         solve leaves sixteen, and the eight extra are pairs that never met. Both sum to
    ///         <c>4χ = 8</c>, because no amount of being wrong changes the Euler characteristic —
    ///         which is exactly why the sum is not the thing to measure here.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(48)]
    [InlineData(128)]
    public void The_hierarchy_is_what_makes_the_field_global(int meridians) {
        var mesh = FieldFixtures.Condition(
            MeshShapes.Create(ShapeParameters.Default(ShapeKind.Sphere) with { Sides = meridians, Steps = meridians / 2 })
        );

        var settings = new RemeshSettings { Adaptivity = 0f, FeatureAngle = 180f };
        var features = FeatureDetector.Detect(mesh, settings);
        var curvature = CurvatureField.Build(mesh);

        var hierarchical = CrossFieldSolver.Solve(mesh, settings, features, curvature);
        var flat = CrossFieldSolver.Solve(mesh, settings, features, curvature, hierarchical: false);

        var one = SingularityPass.Extract(mesh, hierarchical);
        var two = SingularityPass.Extract(mesh, flat);

        Assert.Equal(8, one.Sum(entry => entry.Index));
        Assert.Equal(8, two.Sum(entry => entry.Index));

        Assert.Equal(8, one.Count);

        Assert.True(
            two.Count > one.Count,
            $"The flat solve found {two.Count} singularities and the hierarchy {one.Count}, so the "
            + "hierarchy bought nothing and this fixture is too easy."
        );

        Assert.True(
            hierarchical.Energy(mesh) < flat.Energy(mesh),
            $"Hierarchical energy {MathUtil.RadiansToDegrees(hierarchical.Energy(mesh)):F3}° against "
            + $"flat {MathUtil.RadiansToDegrees(flat.Energy(mesh)):F3}°."
        );
    }

    /// <summary>The field is smooth across a seam of the tangent frames, not only within a patch.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The trap this exists for: a per-vertex frame seeded independently is not
    ///         continuous.</b> <see cref="TangentFrame" /> is seeded from the first neighbour of the
    ///         ordered one-ring, so two adjacent vertices whose rings start in different places have
    ///         frames an arbitrary rotation apart. A field stored as an angle in that frame and
    ///         compared across the edge in two inconsistent bases converges to something that is
    ///         locally smooth in the numbers and torn in the geometry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the test finds the worst seam in the frames and asserts the field does not
    ///         notice it.</b> The edge whose two frames disagree most is located by measuring them
    ///         directly, the assertion is that the field's deviation there is ordinary, and the guard
    ///         is that the seam is real — a mesh whose frames all happened to agree would pass by
    ///         having nothing to cross.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_field_does_not_see_the_seam_in_the_frames() {
        var mesh = FeatureDetectionTests.Fixture("sphere");
        var field = Solve(mesh, new() { Adaptivity = 0f, FeatureAngle = 180f });

        var worstFrame = 0f;
        var atWorst = -1;

        for (var half = 0; half < mesh.Triangles.Length; half++) {
            var from = mesh.Triangles[half];
            var to = mesh.Triangles[ManifoldMesh.Next(half)];

            var here = mesh.Frame(from);
            var there = CrossField.Transport(mesh.Frame(to).Tangent, mesh.VertexNormal(to), mesh.VertexNormal(from));

            if (here.Tangent.LengthSquared() <= 0f || there.LengthSquared() <= 0f) {
                continue;
            }

            var apart = MathF.Acos(Math.Clamp(Vector3.Dot(here.Tangent, there), -1f, 1f));

            if (apart > worstFrame) {
                worstFrame = apart;
                atWorst = half;
            }
        }

        Assert.True(
            MathUtil.RadiansToDegrees(worstFrame) > 90f,
            $"The frames only disagree by {MathUtil.RadiansToDegrees(worstFrame):F1}°, so there is no seam to cross."
        );

        var vertex = mesh.Triangles[atWorst];
        var neighbour = mesh.Triangles[ManifoldMesh.Next(atWorst)];

        var transported = CrossField.Transport(
            field.Direction(neighbour),
            mesh.VertexNormal(neighbour),
            mesh.VertexNormal(vertex)
        );

        var deviation = MathF.Acos(
            Math.Clamp(
                Vector3.Dot(
                    CrossField.Align(transported, field.Direction(vertex), mesh.VertexNormal(vertex)),
                    field.Direction(vertex)
                ),
                -1f,
                1f
            )
        );

        Assert.True(
            MathUtil.RadiansToDegrees(deviation) < 10f,
            $"Across the worst frame seam the field turns by {MathUtil.RadiansToDegrees(deviation):F1}°, "
            + "which is a field that inherited the frames' discontinuity."
        );
    }

    /// <summary>A rim vertex has a fan rather than a ring, and the solve must survive one.</summary>
    [Fact]
    public void An_open_rim_is_solved_rather_than_skipped() {
        var mesh = FieldFixtures.Tube(16, 24, 1f, 8f);

        foreach (var freeze in new[] { false, true }) {
            var settings = new RemeshSettings { Adaptivity = 0f, FeatureAngle = 180f, FreezeBorder = freeze };
            var field = Solve(mesh, settings);
            var rim = 0;

            for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
                Assert.True(
                    field.Direction(vertex).LengthSquared() > 0f,
                    $"Vertex {vertex} has no representative at all."
                );

                Assert.True(
                    MathF.Abs(Vector3.Dot(field.Direction(vertex), mesh.VertexNormal(vertex))) < 1e-3f,
                    $"Vertex {vertex}'s representative left its tangent plane."
                );

                if (mesh.IsBoundary(vertex)) {
                    rim++;
                }
            }

            Assert.Equal(32, rim);

            Assert.True(
                MathUtil.RadiansToDegrees(field.Energy(mesh)) < 5f,
                $"Freeze {freeze}: the mean deviation is {MathUtil.RadiansToDegrees(field.Energy(mesh)):F2}°."
            );
        }
    }

    /// <summary>Every zero is an answer: no features, no guides, no adaptivity, no mesh.</summary>
    [Fact]
    public void The_zeros_are_answers_rather_than_crashes() {
        foreach (var (name, source) in BrokenMeshes.Corpus()) {
            var mesh = MeshConditioner.Condition(source, new(), out _);
            var settings = new RemeshSettings { Adaptivity = 0f, Guides = [], DensityMask = [] };
            var field = Solve(mesh, settings);

            Assert.Equal(mesh.VertexCount, field.Count);

            for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
                var direction = field.Direction(vertex);

                if (mesh.VertexNormal(vertex).LengthSquared() <= 0f) {
                    continue;
                }

                Assert.True(
                    MathF.Abs(direction.LengthSquared() - 1f) < 1e-3f,
                    $"{name}: vertex {vertex}'s representative has length {direction.Length():F4}."
                );
            }
        }
    }

    /// <summary>Nothing in the solve is a length, so its structure does not move with the model's size.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two claims of different strengths, and which one applies where is measured rather
    ///         than assumed — the shape R1's <c>ScaleInvarianceTests</c> settled on.</b> On a cylinder
    ///         and on a boolean of two boxes the two scales' fields agree to a hundredth of a degree.
    ///         On a sphere they do not agree at all: the mean 4-RoSy difference is twenty degrees and
    ///         five in six vertices differ by more than five.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And that is the fixture rather than the code, which is worth being exact about
    ///         because it is the same sentence R1 wrote and it would be an easy thing to say
    ///         defensively.</b> A sphere has a continuous symmetry: every rotation of a smooth cross
    ///         field on one is another smooth cross field on one, with the same energy and the same
    ///         eight singularities. Which member of that family the solve lands in is decided by the
    ///         geometric initialization, whose axis is chosen by comparing the magnitudes of the three
    ///         coordinates — and a sphere on a regular grid has thousands of vertices where two of them
    ///         are <i>exactly</i> equal. A thousandth is not a binary fraction, so scaling perturbs each
    ///         coordinate by an ulp in a direction that varies per coordinate, the ties break the other
    ///         way, and the solve converges to a rotated member of the same family.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An absolute tolerance in the solver would not look like this.</b> It would change
    ///         the <i>number</i> of singularities and the energy by orders of magnitude, and those two
    ///         are asserted for every fixture including the sphere.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("sphere", false)]
    [InlineData("cylinder", true)]
    [InlineData("two-boxes", true)]
    public void The_solve_has_the_same_structure_six_orders_of_magnitude_apart(string name, bool rigid) {
        var mesh = FeatureDetectionTests.Fixture(name);
        var settings = new RemeshSettings();

        var small = FieldFixtures.Scaled(mesh, 1e-3f);
        var large = FieldFixtures.Scaled(mesh, 1e+3f);

        var one = Solve(small, settings);
        var two = Solve(large, settings);

        Assert.Equal(SingularityPass.Extract(small, one).Count, SingularityPass.Extract(large, two).Count);

        var here = MathUtil.RadiansToDegrees(one.Energy(small));
        var there = MathUtil.RadiansToDegrees(two.Energy(large));

        Assert.True(
            MathF.Abs(here - there) <= 0.1f * MathF.Max(MathF.Max(here, there), 0.1f),
            $"{name}: the energy is {here:F4} at a thousandth against {there:F4} at a thousand times."
        );

        if (!rigid) {
            return;
        }

        // A shape with no continuous symmetry has one answer, and both scales have to find it.
        Assert.True(
            MathUtil.RadiansToDegrees(FieldFixtures.Apart(mesh, one, two)) < 0.5f,
            $"{name}: the two scales' fields are "
            + $"{MathUtil.RadiansToDegrees(FieldFixtures.Apart(mesh, one, two)):F3} degrees apart."
        );
    }

    internal static CrossField Solve(ManifoldMesh mesh, RemeshSettings settings) =>
        CrossFieldSolver.Solve(mesh, settings, FeatureDetector.Detect(mesh, settings), CurvatureField.Build(mesh));

    static (float Anisotropy, float Magnitude) Curvature(ManifoldMesh mesh) {
        var curvature = CurvatureField.Build(mesh);
        var anisotropy = 0f;
        var magnitude = 0f;
        var count = 0;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            // Rim vertices and low-valence ones have too few neighbours for the quadratic fit to be
            // determined, and including them would measure the fit's fallback rather than the surface.
            if (mesh.IsBoundary(vertex) || mesh.Valence(vertex) < 4) {
                continue;
            }

            anisotropy += curvature.Anisotropy(vertex);
            magnitude += curvature.Magnitude(vertex);
            count++;
        }

        return count == 0 ? (0f, 0f) : (anisotropy / count, magnitude / count);
    }
}
