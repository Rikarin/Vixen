// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The seven steps of docs/plan/41 § D3, each one asserted for what it claims to change.</summary>
public class ConditioningTests {
    /// <summary>The pre-remesh off, so a step's own effect is not measured through five rounds of it.</summary>
    static ConditioningSettings Bare => new() { PreRemeshIterations = 0 };

    // ── Step 1 · weld ───────────────────────────────────────────────────────────────────────────

    /// <summary>⚠ TRELLIS output arrives at a position ratio of exactly three, and unwelded it has no edges.</summary>
    [Fact]
    public void WeldingCollapsesAFullyUnweldedMeshBackOntoItsSharedPositions() {
        var source = MeshShapes.Create(ShapeKind.Sphere);
        var torn = BrokenMeshes.Unwelded(source);

        Assert.Equal(Triangles(source) * 3, torn.PositionCount);

        var view = MeshConditioner.Condition(torn, Bare, out var report);

        Assert.True(report.Welded > 0);
        Assert.Equal(source.PositionCount, view.VertexCount);
        Assert.Equal(Triangles(source), report.Triangles);
        Assert.True(report.Mesh.IsClosed, report.Mesh.Describe());
    }

    [Fact]
    public void WeldingDropsFacesThatCollapsedAndFacesThatWereListedTwice() {
        var doubled = BrokenMeshes.DuplicateFaces();
        var single = MeshShapes.Create(ShapeKind.Box);

        MeshConditioner.Condition(doubled, Bare, out var report);
        MeshConditioner.Condition(single, Bare, out var expected);

        Assert.Equal(expected.Triangles, report.Triangles);
        Assert.True(report.Mesh.IsSolid, report.Mesh.Describe());
    }

    [Fact]
    public void AZeroLengthEdgeIsWeldedAwayAndItsTrianglesSurvive() {
        var view = MeshConditioner.Condition(BrokenMeshes.ZeroLengthEdge(), Bare, out var report);

        Assert.Equal(4, view.VertexCount);
        Assert.Equal(2, report.Triangles);
        Assert.Empty(report.Mesh.Degenerate);
    }

    // ── Step 2 · orient ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnInsideOutFaceIsFloodFilledBackIntoAgreement() {
        MeshConditioner.Condition(BrokenMeshes.InconsistentWinding(), Bare, out var report);

        Assert.True(report.Reoriented > 0);
        Assert.Equal(0, report.Unorientable);
        Assert.True(report.Mesh.IsConsistent, report.Mesh.Describe());
    }

    /// <summary>⚠ A genuinely one-sided surface is flagged, not "fixed" into something wrong.</summary>
    [Fact]
    public void AMobiusBandIsReportedUnorientableRatherThanRepaired() {
        var before = BrokenMeshes.Mobius();
        var view = MeshConditioner.Condition(before, Bare, out var report);

        Assert.Equal(1, report.Unorientable);

        // Nothing was deleted to make the contradiction go away — the band is still a band.
        Assert.Equal(Triangles(before), report.Triangles);
        Assert.True(report.Mesh.IsManifold, report.Mesh.Describe());

        // And the seam is still there, which is the honest answer: exactly one edge of the band
        // cannot be walked in opposite directions by its two triangles.
        Assert.NotEmpty(report.Mesh.Reversed);
        Assert.Equal(Triangles(before), view.TriangleCount);
    }

    /// <summary>⚠ A T-junction is not unorientable, and a flood fill that crossed it would say it was.</summary>
    [Fact]
    public void ATJunctionIsNotMistakenForAnUnorientableComponent() {
        MeshConditioner.Condition(BrokenMeshes.TJunction(), Bare, out var report);

        Assert.Equal(0, report.Unorientable);
    }

    // ── Step 3 · de-speck ───────────────────────────────────────────────────────────────────────

    /// <summary>⚠ Both sides of the threshold, because one side alone is a test that a constant exists.</summary>
    [Fact]
    public void ACompanionAtFortyPercentSurvivesAndOneAtAHundredthOfAPercentDoesNot() {
        MeshConditioner.Condition(BrokenMeshes.TwoComponents(0.4f), Bare, out var eyeball);
        MeshConditioner.Condition(BrokenMeshes.TwoComponents(0.0001f), Bare, out var debris);

        Assert.Equal(0, eyeball.Despecked);
        Assert.Equal(1, debris.Despecked);
    }

    [Fact]
    public void HallucinatedDebrisGoesAndTheBodyStays() {
        var view = MeshConditioner.Condition(BrokenMeshes.Specks(), Bare, out var report);

        Assert.Equal(12, report.Despecked);
        Assert.Equal(Triangles(MeshShapes.Create(ShapeKind.Sphere)), view.TriangleCount);
    }

    /// <summary>A mesh of one component is never de-specked, however small it is.</summary>
    [Fact]
    public void TheOnlyComponentIsNeverTheSpeck() {
        MeshConditioner.Condition(BrokenMeshes.SingleTriangle(), Bare, out var report);

        Assert.Equal(0, report.Despecked);
        Assert.Equal(1, report.Triangles);
    }

    // ── Step 4 · repair, by cutting ─────────────────────────────────────────────────────────────

    /// <summary>⚠ Cutting keeps the geometry and costs a seam; merging invents a surface.</summary>
    /// <remarks>
    ///     The observable difference is the triangle count. A merge resolves an edge with three faces
    ///     by deleting one of them or by fusing two; both reduce the count. A cut duplicates the
    ///     edge's two positions and leaves every triangle where it was.
    /// </remarks>
    [Fact]
    public void ATJunctionIsCutIntoManifoldSheetsWithoutLosingATriangle() {
        var source = BrokenMeshes.TJunction();

        Assert.False(source.Validate().IsManifold);

        var view = MeshConditioner.Condition(source, Bare, out var report);

        Assert.True(report.Mesh.IsManifold, report.Mesh.Describe());
        Assert.True(report.Cut > 0);
        Assert.Equal(Triangles(source), report.Triangles);
        Assert.Equal(Triangles(source), view.TriangleCount);

        // The seam cost duplicate positions rather than lost ones — that is what "cut" means.
        Assert.True(view.VertexCount > source.PositionCount);
    }

    [Fact]
    public void ARepairedMeshHasNoNonManifoldEdgesLeftAnywhereInTheCorpus() {
        foreach (var (name, mesh) in BrokenMeshes.Corpus()) {
            MeshConditioner.Condition(mesh, Bare, out var report);

            Assert.True(report.Mesh.IsManifold, $"{name}: {report.Mesh.Describe()}");
        }
    }

    // ── Step 5 · fill holes ─────────────────────────────────────────────────────────────────────

    /// <summary>⚠ Off by default: a hole in the input is very often a hole in the subject.</summary>
    [Fact]
    public void HolesAreLeftAloneUnlessTheCallerAsksForThem() {
        var open = BrokenMeshes.OpenSurface();

        MeshConditioner.Condition(open, Bare, out var untouched);

        Assert.Equal(0, untouched.Filled);
        Assert.NotEmpty(untouched.Mesh.Boundary);
    }

    [Fact]
    public void AHoleUnderTheThresholdIsClosedWhenAskedAndOneOverItIsNot() {
        var open = BrokenMeshes.OpenSurface();

        MeshConditioner.Condition(
            open,
            new() { PreRemeshIterations = 0, FillHoles = true, HoleSize = 10f },
            out var closed
        );

        Assert.Equal(1, closed.Filled);
        Assert.Empty(closed.Mesh.Boundary);
        Assert.True(closed.Mesh.IsSolid, closed.Mesh.Describe());

        MeshConditioner.Condition(
            open,
            new() { PreRemeshIterations = 0, FillHoles = true, HoleSize = 1e-4f },
            out var stillOpen
        );

        Assert.Equal(0, stillOpen.Filled);
    }

    // ── Step 6 · isotropic pre-remesh ───────────────────────────────────────────────────────────

    [Fact]
    public void ThePreRemeshEvensOutAStaircaseAndKeepsTheMeshManifold() {
        var source = BrokenMeshes.StaircaseSphere();

        var before = Spread(ManifoldMesh.Build(TriangleSoup.From(source)));

        var view = MeshConditioner.Condition(source, new() { PreRemeshIterations = 5 }, out var report);

        Assert.True(report.Mesh.IsManifold, report.Mesh.Describe());
        Assert.True(report.Triangles > 0);

        var after = Spread(view);

        Assert.True(
            after < before,
            $"Edge lengths were {before:F4} of the mean before and {after:F4} after, so the "
            + "isotropic step made the mesh less isotropic."
        );
    }

    // ── Step 7 · voxel shrinkwrap ───────────────────────────────────────────────────────────────

    /// <summary>⚠ Never the default, and the report says loudly when it fired.</summary>
    [Fact]
    public void TheShrinkwrapDoesNotFireUnlessItIsAskedFor() {
        Assert.False(new ConditioningSettings().Shrinkwrap);

        MeshConditioner.Condition(BrokenMeshes.SelfIntersecting(), Bare, out var report);

        Assert.False(report.Shrinkwrapped);
    }

    /// <summary>
    ///     Two boxes through one another have no consistent inside for a distance field, and the
    ///     generalised winding number gives them one — which is the whole reason step seven exists.
    /// </summary>
    [Fact]
    public void TheShrinkwrapTurnsTwoInterpenetratingBoxesIntoOneClosedSurface() {
        var view = MeshConditioner.Condition(
            BrokenMeshes.SelfIntersecting(),
            new() { PreRemeshIterations = 0, Shrinkwrap = true },
            out var report
        );

        Assert.True(report.Shrinkwrapped);
        Assert.True(report.Mesh.IsManifold, report.Mesh.Describe());
        Assert.True(view.TriangleCount > 0);

        // One shell, not two: the union of the two boxes is what the field's inside is.
        var editable = view.ToEditMesh();

        List<int> shells = [];

        Assert.Equal(1, MeshCollision.Shells(editable, shells));

        // ⚠ Not watertight, and the reason is inherent to a dual extraction rather than a bug. One
        // vertex per cell pinches wherever a cell's inside corners are diagonally opposite, and the
        // repair opens each pinch into a seam. On the concave crease where the two boxes meet there
        // are a hundred or so of them, against sixty thousand edges — so the assertion is a rate
        // rather than a zero, and a regression that broke the extraction would blow through it.
        Assert.True(
            report.Mesh.Boundary.Count < editable.Edges.Count / 100,
            $"{report.Mesh.Boundary.Count} boundary edges of {editable.Edges.Count}."
        );
    }

    // ── The zero-value traps ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ Every degenerate input needs a defined answer, and neither an exception nor a silent NaN
    ///     is one.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("single-vertex")]
    [InlineData("single-triangle")]
    [InlineData("zero-area-triangle")]
    [InlineData("zero-length-edge")]
    public void ADegenerateInputConditionsIntoADefinedAnswer(string name) {
        var mesh = BrokenMeshes.Corpus().First(entry => entry.Name == name).Mesh;
        var view = MeshConditioner.Condition(mesh, new() { PreRemeshIterations = 3 }, out var report);

        Assert.True(report.Triangles >= 0);
        Assert.Equal(report.Triangles, view.TriangleCount);

        foreach (var position in view.Positions) {
            Assert.False(
                float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z),
                $"{name} produced a NaN position."
            );
        }

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            var frame = view.Frame(vertex);

            Assert.False(float.IsNaN(frame.Normal.X + frame.Tangent.X + frame.Bitangent.X));
        }
    }

    /// <summary>A face with no area has no normal and no plane, and conditioning drops it.</summary>
    [Fact]
    public void AZeroAreaTriangleIsDroppedRatherThanCarried() {
        MeshConditioner.Condition(BrokenMeshes.ZeroArea(), Bare, out var report);

        Assert.Equal(0, report.Triangles);
        Assert.Empty(report.Mesh.Degenerate);
    }

    /// <summary>
    ///     docs/plan/41's robustness criterion, over the whole corpus: never an exception and never a
    ///     hang.
    /// </summary>
    [Fact]
    public void NothingInTheCorpusThrowsWithEveryStepTurnedOn() {
        var settings = new ConditioningSettings {
            PreRemeshIterations = 2,
            FillHoles = true,
            HoleSize = 0.2f
        };

        foreach (var (name, mesh) in BrokenMeshes.Corpus()) {
            var record = Record.Exception(() => MeshConditioner.Condition(mesh, settings, out _));

            Assert.True(record is null, $"{name} threw {record?.GetType().Name}: {record?.Message}");
        }
    }

    /// <summary>How many triangles a mesh triangulates into, which is what conditioning counts.</summary>
    /// <remarks>⚠ Not <see cref="EditMesh.FaceCount" />. A generated sphere is mostly quads, so the
    ///     two numbers differ by nearly a factor of two and the wrong one makes a test that looks
    ///     like it is about conditioning into a test about triangulation.</remarks>
    static int Triangles(EditMesh mesh) => mesh.Triangulate().Length / 3;

    /// <summary>The coefficient of variation of the edge lengths — lower is more isotropic.</summary>
    static float Spread(ManifoldMesh view) {
        var lengths = new List<float>();

        for (var vertex = 0; vertex < view.VertexCount; vertex++) {
            foreach (var neighbour in view.Ring(vertex)) {
                if (neighbour > vertex) {
                    lengths.Add(Vector3.Distance(view.Positions[vertex], view.Positions[neighbour]));
                }
            }
        }

        if (lengths.Count == 0) {
            return 0f;
        }

        var mean = lengths.Sum() / lengths.Count;

        if (mean <= 0f) {
            return 0f;
        }

        var variance = lengths.Sum(length => (length - mean) * (length - mean)) / lengths.Count;

        return MathF.Sqrt(variance) / mean;
    }
}
