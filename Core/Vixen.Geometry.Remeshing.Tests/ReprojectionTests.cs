// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The pre-remesh's reprojection, and the sabotage that proves the assertion can fail.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D3 step 6: "The relaxation is projected back onto the original surface
///         each iteration, so the shape does not drift."</b> A bound on the deviation is only a test
///         if it fails when the thing it is bounding is removed —
///         <see cref="IsotropicRemesh.Run" />'s <c>reproject</c> parameter exists so that this file
///         can turn the reprojection off and watch the bound break, in the suite, permanently, rather
///         than in a commented-out edit somebody made once.
///     </para>
///     <para>
///         ⚠ The deviation is a <i>fraction of the bounding-box diagonal</i>, never a distance —
///         which is the same rule <see cref="RemeshReport.MaxDeviation" /> states and the same lesson
///         doc 24 records twice.
///     </para>
/// </remarks>
public class ReprojectionTests {
    /// <summary>The deviation a reprojected pre-remesh is allowed, as a fraction of the diagonal.</summary>
    /// <remarks>
    ///     Every relaxed vertex is placed <i>on</i> the reference surface by construction, so the
    ///     honest expectation is nought and the bound is here to catch floating-point drift rather
    ///     than to leave room for shape change.
    /// </remarks>
    public const float Bound = 1e-4f;

    [Fact]
    public void ReprojectingKeepsThePreRemeshOnTheOriginalSurface() {
        var (deviation, _) = Deviate(BrokenMeshes.StaircaseSphere(), 5, reproject: true);

        Assert.True(deviation < Bound, $"Max deviation was {deviation:E3} of the diagonal.");
    }

    /// <summary>⚠ The same measurement with the reprojection taken out — and it must not pass.</summary>
    /// <remarks>
    ///     <para>
    ///         This is the whole evidence for the test above. Without it, a bound of a ten-thousandth
    ///         would also be satisfied by a pre-remesh that did nothing at all, by one whose
    ///         relaxation step was accidentally a no-op, and by one that reprojected onto the wrong
    ///         surface.
    ///     </para>
    ///     <para>
    ///         Measured over five rounds, as a fraction of the diagonal: 2.535e-8 reprojected against
    ///         3.638e-2 not, on <see cref="BrokenMeshes.StaircaseSphere" />; and 2.433e-8 against
    ///         1.032e-2 on a smooth sphere. Six orders of magnitude, either way round.
    ///     </para>
    /// </remarks>
    [Fact]
    public void WithoutReprojectionTheSameBoundIsBroken() {
        var source = BrokenMeshes.StaircaseSphere();

        var (kept, _) = Deviate(source, 5, reproject: true);
        var (drifted, _) = Deviate(source, 5, reproject: false);

        Assert.True(
            drifted > Bound,
            $"Removing the reprojection left the deviation at {drifted:E3}, under the bound of "
            + $"{Bound:E3} — so the bound is not measuring the reprojection."
        );

        Assert.True(
            drifted > kept * 100f,
            $"Reprojected {kept:E3} against un-reprojected {drifted:E3}, which is not a difference."
        );
    }

    /// <summary>⚠ And the drift has a direction: a tangential relaxation without reprojection shrinks.</summary>
    /// <remarks>
    ///     <para>
    ///         The centroid of a ring on a convex surface is inside the surface, and a split's
    ///         midpoint sits on a chord rather than on the arc, so every round pulls inward. That is
    ///         why the failure mode is not noise round the true shape but a model that is quietly
    ///         smaller than the one that came in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On a smooth convex fixture, and not on the staircase one.</b> Smoothing a
    ///         staircase moves the concave facets out as much as it moves the convex ones in, so the
    ///         mean radius of <see cref="BrokenMeshes.StaircaseSphere" /> <i>grows</i> without
    ///         reprojection — measured at 0.5020 to 0.5063 — and a deflation claim tested there would
    ///         be a claim about the fixture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void WithoutReprojectionTheSurfaceDeflatesRatherThanWandering() {
        var source = MeshShapes.Create(ShapeKind.Sphere);

        var (_, kept) = Deviate(source, 5, reproject: true);
        var (_, drifted) = Deviate(source, 5, reproject: false);

        // Measured at 0.49596 reprojected against 0.49306 not, over five rounds — a shrink of 0.586
        // percent, on a sphere nobody asked to be made smaller.
        Assert.True(
            drifted < kept * 0.997f,
            $"The mean radius went from {kept:F4} to {drifted:F4} without reprojection, which is not "
            + "the deflation the reprojection exists to prevent."
        );
    }

    [Fact]
    public void ThePreRemeshLeavesACleanMeshManifoldAndConsistent() {
        var soup = TriangleSoup.From(MeshShapes.Create(ShapeKind.Sphere));
        var reference = SurfaceProjector.Build([.. soup.Positions], [.. soup.Triangles]);

        IsotropicRemesh.Run(soup, reference, 0f, 5);

        var report = ManifoldMesh.Build(soup).ToEditMesh().Validate();

        Assert.True(report.IsClosed, report.Describe());
        Assert.True(report.IsConsistent, report.Describe());
        Assert.Empty(report.Degenerate);
    }

    /// <summary>⚠ <c>FreezeBorder</c> is the default, and the pre-remesh must not eat an open rim.</summary>
    [Fact]
    public void ThePreRemeshLeavesAnOpenRimWhereItWas() {
        var source = BrokenMeshes.OpenSurface();
        var box = source.Bounds;

        var soup = TriangleSoup.From(source);
        var reference = SurfaceProjector.Build([.. soup.Positions], [.. soup.Triangles]);

        IsotropicRemesh.Run(soup, reference, 0f, 5);

        var view = ManifoldMesh.Build(soup);
        var after = view.Bounds;

        Assert.Equal(box.Minimum.X, after.Minimum.X, 4);
        Assert.Equal(box.Maximum.X, after.Maximum.X, 4);
        Assert.Equal(box.Minimum.Z, after.Minimum.Z, 4);
        Assert.Equal(box.Maximum.Z, after.Maximum.Z, 4);

        Assert.True(view.ToEditMesh().Validate().IsManifold);
    }

    /// <summary>The furthest a pre-remeshed vertex strays, and the mean distance from the centre.</summary>
    static (float Deviation, float Radius) Deviate(EditMesh source, int iterations, bool reproject) {
        var soup = TriangleSoup.From(source);

        soup.Compact();

        var diagonal = soup.Diagonal;

        MeshConditioner.Weld(soup, new ConditioningSettings().WeldTolerance, diagonal);
        MeshConditioner.Orient(soup);
        MeshConditioner.Repair(soup);

        var positions = soup.Positions.ToArray();
        var triangles = soup.Triangles.ToArray();
        var reference = SurfaceProjector.Build(positions, triangles);

        IsotropicRemesh.Run(soup, reference, 0f, iterations, reproject);

        var centre = Vector3.Zero;

        foreach (var position in positions) {
            centre += position;
        }

        centre /= positions.Length;

        var furthest = 0f;
        var radius = 0d;

        foreach (var position in soup.Positions) {
            furthest = MathF.Max(furthest, reference.Distance(position));
            radius += Vector3.Distance(position, centre);
        }

        return (
            diagonal > 0f ? furthest / diagonal : furthest,
            soup.Positions.Count == 0 ? 0f : (float) (radius / soup.Positions.Count)
        );
    }
}
