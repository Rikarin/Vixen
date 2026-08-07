// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D9: one target edge length, and the three terms that write into it.</summary>
public class DensityFieldTests {
    /// <summary>Adaptivity zero, no mask and no features is <c>base</c> at every vertex, exactly.</summary>
    /// <remarks>
    ///     ⚠ <b>ZRemesher's uniform squares, and a real setting rather than a degenerate case.</b> The
    ///     assertion is exact equality and not a tolerance: at adaptivity zero the curvature term is
    ///     <c>1 + 0 × (curved − 1)</c>, which is one whatever <c>curved</c> is, so a field that came
    ///     out nearly-but-not-quite uniform would mean the term was written as a lerp toward one rather
    ///     than a scale on the deviation from it, and would drift with the surface.
    /// </remarks>
    [Fact]
    public void Adaptivity_zero_is_one_length_everywhere() {
        var mesh = FeatureDetectionTests.Fixture("sphere");
        var settings = new RemeshSettings { Adaptivity = 0f, FeatureAngle = 180f, TargetQuads = 4000 };
        var features = FeatureDetector.Detect(mesh, settings);
        var density = DensityField.Build(mesh, settings, features, CurvatureField.Build(mesh));

        Assert.Empty(features.Polylines);
        Assert.True(density.Base > 0f);

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            Assert.Equal(density.Base, density.Target(vertex));
        }

        // And `base` is the quad budget over the area, which is what makes the budget mean something:
        // a quad of side L covers L².
        Assert.Equal(mesh.Area() / 4000f, density.Base * density.Base, 4);
    }

    /// <summary>Adaptivity above zero makes the target shorter where the surface bends more.</summary>
    /// <remarks>
    ///     ⚠ <b>A rounded plate rather than a capsule, and the reason is a nice illustration of what
    ///     the curvature magnitude is.</b> A capsule's largest principal curvature is the reciprocal
    ///     of its radius on the hemispheres <i>and</i> on the cylinder between them, so it is uniform
    ///     from end to end and there is nothing to compare — which is exactly why the fixture had to
    ///     change rather than the assertion.
    /// </remarks>
    [Fact]
    public void Adaptivity_shortens_the_target_where_the_surface_bends() {
        var mesh = FieldFixtures.Smooth(FieldFixtures.Plate(5, 3, [(2, 1)]), 0.35f, 12);
        var settings = new RemeshSettings { Adaptivity = 1f, FeatureAngle = 180f };
        var features = FeatureDetector.Detect(mesh, settings);
        var curvature = CurvatureField.Build(mesh);
        var density = DensityField.Build(mesh, settings, features, curvature);

        var flattest = 0;
        var sharpest = 0;

        for (var vertex = 1; vertex < mesh.VertexCount; vertex++) {
            if (curvature.Magnitude(vertex) < curvature.Magnitude(flattest)) {
                flattest = vertex;
            }

            if (curvature.Magnitude(vertex) > curvature.Magnitude(sharpest)) {
                sharpest = vertex;
            }
        }

        Assert.True(
            curvature.Magnitude(sharpest) > 2f * curvature.Magnitude(flattest),
            "The fixture is not curved enough for this to be a comparison."
        );

        Assert.True(
            density.Target(sharpest) < density.Target(flattest),
            $"The sharpest vertex wants {density.Target(sharpest):F4} and the flattest "
            + $"{density.Target(flattest):F4}."
        );

        foreach (var target in density.Targets) {
            Assert.InRange(target, density.Base * DensityField.MinScale, density.Base * DensityField.MaxScale);
        }
    }

    /// <summary>The target tightens near a feature, so a hard edge is not straddled by one quad.</summary>
    [Fact]
    public void A_feature_tightens_the_target_around_it() {
        var mesh = FeatureDetectionTests.Fixture("two-boxes");
        var settings = new RemeshSettings { Adaptivity = 0f };
        var features = FeatureDetector.Detect(mesh, settings);
        var density = DensityField.Build(mesh, settings, features, CurvatureField.Build(mesh));

        Assert.NotEmpty(features.Polylines);

        var onFeature = 0;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            if (!features.IsFeatureVertex(vertex)) {
                continue;
            }

            onFeature++;

            Assert.True(
                density.Target(vertex) < density.Base,
                $"Feature vertex {vertex} wants {density.Target(vertex):F4} against a base of {density.Base:F4}."
            );
        }

        Assert.True(onFeature > 0, "There were no feature vertices, so nothing was tested.");
    }

    /// <summary>A density mask redistributes the budget, and one that does not fit the mesh is ignored.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A mask of the wrong length is not truncated, and the difference matters.</b>
    ///         <see cref="RemeshSettings.DensityMask" /> is indexed by the <i>source</i> mesh's
    ///         positions and stage one welds, cuts, de-specks and re-meshes those — so applying the
    ///         first <i>n</i> entries of somebody else's vertex order paints density in the wrong places
    ///         and looks like a working feature. <see cref="DensityField.Resample" /> is the way across.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This asserted that a uniform mask of two halves every target, and that stopped
    ///         being true when <c>base</c> started being solved from the budget rather than assumed from
    ///         the area.</b> The mask is one of the three terms § D9 multiplies together and
    ///         <see cref="DensityField.Normalise" /> divides all three back out, so <b>a uniform mask is
    ///         now exactly a no-op</b> — the budget is the budget, and a mask that says "twice as dense
    ///         everywhere" says nothing. It is <i>where</i> the mask varies that moves quads, and a
    ///         painted region is paid for by the unpainted ones. That is the behaviour asserted here,
    ///         and it is a change in what the setting means rather than a tolerance being loosened.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_mask_redistributes_the_budget_and_a_mismatched_one_is_ignored() {
        var mesh = FeatureDetectionTests.Fixture("sphere");
        var settings = new RemeshSettings { Adaptivity = 0f, FeatureAngle = 180f };
        var features = FeatureDetector.Detect(mesh, settings);
        var curvature = CurvatureField.Build(mesh);

        var plain = DensityField.Build(mesh, settings, features, curvature);
        var flat = DensityField.Build(mesh, settings, features, curvature, [.. Enumerable.Repeat(2f, mesh.VertexCount)]);
        var wrong = DensityField.Build(mesh, settings, features, curvature, [1f, 1f, 1f]);

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            Assert.Equal(plain.Target(vertex), flat.Target(vertex), 5);
            Assert.Equal(plain.Target(vertex), wrong.Target(vertex));
        }

        // Painted on one hemisphere, so the two halves are each other's control.
        var mask = new float[mesh.VertexCount];

        for (var vertex = 0; vertex < mask.Length; vertex++) {
            mask[vertex] = mesh.Positions[vertex].X > 0f ? 2f : 1f;
        }

        var painted = DensityField.Build(mesh, settings, features, curvature, mask);

        for (var vertex = 0; vertex < mask.Length; vertex++) {
            if (mask[vertex] > 1f) {
                Assert.True(
                    painted.Target(vertex) < plain.Target(vertex),
                    $"Vertex {vertex} is painted dense and wants {painted.Target(vertex):F5} against {plain.Target(vertex):F5}."
                );
            } else {
                Assert.True(
                    painted.Target(vertex) > plain.Target(vertex),
                    $"Vertex {vertex} is unpainted and wants {painted.Target(vertex):F5} against {plain.Target(vertex):F5}."
                );
            }
        }
    }

    /// <summary>A mask on the source arrives on the conditioned mesh through the nearest triangle.</summary>
    [Fact]
    public void A_mask_can_be_resampled_off_the_source() {
        var source = MeshShapes.Create(ShapeParameters.Default(ShapeKind.Sphere) with { Sides = 24, Steps = 12 });
        var indices = source.Triangulate();
        var mesh = FieldFixtures.Condition(source, 3);

        // A ramp along x, so the resampled value at a position is a known function of that position.
        var box = source.Bounds;
        var span = MathF.Max(box.Maximum.X - box.Minimum.X, float.Epsilon);
        var mask = new float[source.PositionCount];

        for (var position = 0; position < mask.Length; position++) {
            mask[position] = 1f + ((source.Positions[position].X - box.Minimum.X) / span);
        }

        var resampled = DensityField.Resample(source.Positions, indices, mask, mesh);

        Assert.Equal(mesh.VertexCount, resampled.Length);

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            var expected = 1f + ((mesh.Positions[vertex].X - box.Minimum.X) / span);

            Assert.True(
                MathF.Abs(resampled[vertex] - expected) < 0.05f,
                $"Vertex {vertex} resampled to {resampled[vertex]:F4} against {expected:F4}."
            );
        }

        // A mask that does not fit its own mesh is refused rather than guessed at.
        Assert.Empty(DensityField.Resample(source.Positions, indices, [1f], mesh));
    }

    /// <summary>The whole field scales with the model and nothing in it is a constant.</summary>
    [Theory]
    [InlineData("sphere")]
    [InlineData("two-boxes")]
    [InlineData("staircase")]
    public void The_target_scales_with_the_model(string name) {
        var mesh = FeatureDetectionTests.Fixture(name);
        var settings = new RemeshSettings { Adaptivity = 0.7f };

        var small = FieldFixtures.Scaled(mesh, 1e-3f);
        var large = FieldFixtures.Scaled(mesh, 1e+3f);

        var one = DensityField.Build(small, settings, FeatureDetector.Detect(small, settings), CurvatureField.Build(small));
        var two = DensityField.Build(large, settings, FeatureDetector.Detect(large, settings), CurvatureField.Build(large));

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            var expected = one.Target(vertex) * 1e+6f;

            Assert.True(
                MathF.Abs(two.Target(vertex) - expected) <= 1e-3f * MathF.Max(expected, float.Epsilon),
                $"{name}: vertex {vertex} wants {one.Target(vertex):E3} at a thousandth, which scaled up is "
                + $"{expected:E3}, against {two.Target(vertex):E3} at a thousand times."
            );
        }
    }

    /// <summary>Every zero is an answer, over the whole broken corpus.</summary>
    [Fact]
    public void The_zeros_are_answers_rather_than_crashes() {
        foreach (var (name, source) in BrokenMeshes.Corpus()) {
            var mesh = MeshConditioner.Condition(source, new(), out _);
            var settings = new RemeshSettings { Adaptivity = 0f, DensityMask = [] };
            var features = FeatureDetector.Detect(mesh, settings);
            var density = DensityField.Build(mesh, settings, features, CurvatureField.Build(mesh));

            Assert.Equal(mesh.VertexCount, density.Targets.Length);

            foreach (var target in density.Targets) {
                Assert.True(target >= 0f && float.IsFinite(target), $"{name}: a target of {target}.");
            }
        }
    }
}
