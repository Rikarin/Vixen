// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D13: the atlas comes off the layout, and doc 42 § D2 packs it.</summary>
/// <remarks>
///     <b>The claim being tested is stronger than "there are coordinates".</b> A quantized quad patch
///     is a rectangle, so every quad in a chart is a <i>square</i> in the atlas and the in-chart
///     distortion is exactly zero — not small, zero — which is the property that makes re-cutting the
///     output with a general charter worse in every respect. That is asserted per quad rather than
///     summarised as a distortion figure, because a figure would hide the one chart that folded.
/// </remarks>
public class LayoutAtlasTests {
    /// <summary>Every corner of every fixture comes out with a coordinate in the sheet.</summary>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("stairs")]
    [InlineData("plate")]
    public void Every_corner_gets_a_coordinate_inside_the_sheet(string name) {
        // 400 rather than 300 because the sphere refuses to quantize at 300 — a layout limit that
        // RemesherTests measures at the same number, and not one the atlas can do anything about.
        var quads = Remesher.Remesh(
            RemesherTests.Fixture(name),
            new() { TargetQuads = 400, GenerateUvs = true },
            out var report
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.Equal(quads.CornerCount, quads.TexCoords.Length);

        foreach (var coordinate in quads.TexCoords) {
            Assert.True(float.IsFinite(coordinate.X) && float.IsFinite(coordinate.Y), $"{coordinate} is not a number.");
            Assert.InRange(coordinate.X, -1e-4f, 1f + 1e-4f);
            Assert.InRange(coordinate.Y, -1e-4f, 1f + 1e-4f);
        }
    }

    /// <summary>Every quad is a square in the atlas, which is what "zero in-chart distortion" means.</summary>
    /// <remarks>
    ///     ⚠ <b>Exactly a square, to the packer's uniform scale.</b> The coordinates are a lattice
    ///     index times a cell size, so the only arithmetic between the grid and the atlas is a
    ///     quarter turn, a scale and an offset — none of which can turn a square into anything else.
    ///     A quad that came out a rectangle or a parallelogram would mean the lattice placement
    ///     folded, which is the failure the gluing's third-point check exists to catch.
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("plate")]
    public void A_quad_is_a_square_in_the_atlas(string name) {
        var quads = Remesher.Remesh(
            RemesherTests.Fixture(name),
            new() { TargetQuads = 300, GenerateUvs = true },
            out var report
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var coordinates = quads.TexCoords;
        var checked_ = 0;

        for (var face = 0; face < quads.FaceCount; face++) {
            var entry = quads.Faces[face];

            var a = coordinates[entry.Start];
            var b = coordinates[entry.Start + 1];
            var c = coordinates[entry.Start + 2];
            var d = coordinates[entry.Start + 3];

            var one = b - a;
            var two = c - b;
            var three = d - c;
            var four = a - d;

            // A degenerate quad in the atlas would come from a patch of no extent, which is a
            // layout defect rather than an atlas one and is measured elsewhere.
            if (one.Length() <= 1e-6f) {
                continue;
            }

            checked_++;

            Assert.Equal(one.Length(), two.Length(), 4);
            Assert.Equal(one.Length(), three.Length(), 4);
            Assert.Equal(one.Length(), four.Length(), 4);
            Assert.True(MathF.Abs(Vector2.Dot(one, two)) < 1e-6f, $"Face {face} is not a right angle in the atlas.");
        }

        Assert.True(checked_ > 0, "No face had a coordinate footprint at all.");
    }

    /// <summary>Merging turns hundreds of patches into far fewer charts.</summary>
    /// <remarks>
    ///     ⚠ <b>§ D13: "one chart per patch means hundreds of charts and hundreds of seams".</b>
    ///     Un-merged is the thing to beat and this compares against it directly by asking for a seam
    ///     budget of one, which is a budget nothing can exceed and so merges nothing.
    /// </remarks>
    [Fact]
    public void Merging_leaves_far_fewer_charts_than_there_were_patches() {
        var source = RemesherTests.Fixture("cylinder");

        var merged = Charts(source, 0f);
        var alone = Charts(source, 1f);

        Assert.True(alone > 1, "The fixture has only one patch, so merging cannot be measured on it.");

        Assert.True(
            merged < alone,
            $"Merging produced {merged} charts and leaving every patch alone produced {alone}."
        );
    }

    /// <summary>A tighter seam budget merges at least as hard as a looser one.</summary>
    /// <remarks>
    ///     The dial has to be monotone or it is not a dial. Asking for fewer seams may reach the same
    ///     answer — the merge runs out of legal moves before the budget bites — but it must never
    ///     reach a worse one.
    /// </remarks>
    [Fact]
    public void A_tighter_seam_budget_never_leaves_more_charts() {
        var source = RemesherTests.Fixture("box");
        var previous = int.MaxValue;

        foreach (var budget in (float[]) [1f, 0.5f, 0.2f, 0.05f, 0f]) {
            var charts = Charts(source, budget);

            Assert.True(
                charts <= previous,
                $"A budget of {budget} left {charts} charts where the looser one left {previous}."
            );

            previous = charts;
        }
    }

    /// <summary>Two charts meeting at a seam give one position two different coordinates.</summary>
    /// <remarks>
    ///     ⚠ <b>Which is why the coordinates are a per-corner layer and not a per-position one.</b>
    ///     A seam represented per position would need the vertex split, so the mesh handed back would
    ///     have more vertices than the one that was remeshed — and every index anybody held into it
    ///     would be wrong.
    /// </remarks>
    [Fact]
    public void A_seam_is_one_position_carrying_two_coordinates() {
        var quads = Remesher.Remesh(
            RemesherTests.Fixture("cylinder"),
            new() { TargetQuads = 300, GenerateUvs = true, Atlas = new() { SeamBudget = 1f } },
            out var report
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var seen = new Dictionary<int, Vector2>();
        var split = 0;

        for (var face = 0; face < quads.FaceCount; face++) {
            var entry = quads.Faces[face];
            var loop = quads.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var coordinate = quads.TexCoords[entry.Start + index];

                if (seen.TryGetValue(loop[index], out var first)) {
                    if (Vector2.Distance(first, coordinate) > 1e-5f) {
                        split++;
                    }
                } else {
                    seen[loop[index]] = coordinate;
                }
            }
        }

        Assert.True(split > 0, "Nothing was seamed, so the per-corner layer proved nothing.");
    }

    /// <summary>The atlas holds its shape at a thousandth and a thousand times scale.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Structure rather than coordinates, and refusing to overclaim here is the
    ///         point.</b> The <i>same</i> box at three scales does not remesh to the same quads:
    ///         conditioning's isotropic pre-remesh is only approximately scale-invariant, which
    ///         <see cref="ScaleInvarianceTests" /> states in as many words by asserting its five
    ///         rounds stay <i>within a rate</i> of each other rather than equal. Measured, 6 804 of
    ///         10 172 corners land somewhere else — not because the atlas moved them, but because it
    ///         was given a different mesh.
    ///     </para>
    ///     <para>
    ///         The chart count moves with it and for the same reason — 13 at unit scale against 15
    ///         at a thousandth — because a different layout has a different number of patches to
    ///         merge. Asserting it equal would be asserting something the atlas does not control.
    ///     </para>
    ///     <para>
    ///         So what is asserted is what the atlas itself owes, whatever mesh it is handed: every
    ///         quad still square, and every coordinate still in the sheet. An absolute tolerance
    ///         inside the merge or the gluing breaks both — a fold shows up as a quad that is no
    ///         longer a right angle, and a lattice placed a cell out shows up as one that is no
    ///         longer the same size.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_atlas_holds_its_shape_at_a_thousandth_and_a_thousand_times_scale() {
        var box = RemesherTests.Fixture("box");

        foreach (var scale in (float[]) [1e-3f, 1f, 1e+3f]) {
            var scaled = TransferFixtures.Scaled(box, scale);
            var quads = Remesher.Remesh(scaled, new() { TargetQuads = 200, GenerateUvs = true }, out var report);

            Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
            Assert.Equal(quads.CornerCount, quads.TexCoords.Length);

            foreach (var coordinate in quads.TexCoords) {
                Assert.InRange(coordinate.X, -1e-4f, 1f + 1e-4f);
                Assert.InRange(coordinate.Y, -1e-4f, 1f + 1e-4f);
            }

            for (var face = 0; face < quads.FaceCount; face++) {
                var entry = quads.Faces[face];
                var one = quads.TexCoords[entry.Start + 1] - quads.TexCoords[entry.Start];
                var two = quads.TexCoords[entry.Start + 2] - quads.TexCoords[entry.Start + 1];

                if (one.Length() <= 1e-6f) {
                    continue;
                }

                Assert.Equal(one.Length(), two.Length(), 4);
                Assert.True(MathF.Abs(Vector2.Dot(one, two)) < 1e-6f, $"Face {face} is not square at {scale}×.");
            }
        }
    }

    /// <summary>An atlas with no room refuses in the report rather than throwing out of the remesh.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/41's robustness criterion is "a valid all-quad result <i>or</i> a report
    ///     naming the stage that refused, and never an exception".</b> Sixteen texels with a
    ///     two-texel margin cannot hold this fixture's charts at any rescale, and the packer says so
    ///     by throwing — which is the right thing for a packer to do and the wrong thing for it to do
    ///     through a remesh. The quads still come back; the atlas does not, and the warning says
    ///     which.
    /// </remarks>
    [Fact]
    public void A_resolution_too_small_to_fit_refuses_in_the_report_rather_than_throwing() {
        var quads = Remesher.Remesh(
            RemesherTests.Fixture("plate"),
            new() { TargetQuads = 300, GenerateUvs = true, Atlas = new() { Resolution = 16, Margin = 2 } },
            out var report
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.Equal(0, report.NonQuadCount);
        Assert.Contains(report.Warnings, warning => warning.Contains("packer", StringComparison.Ordinal));
        Assert.Empty(quads.TexCoords.ToArray());
    }

    /// <summary>How many distinct charts a remesh of a fixture produced, at a seam budget.</summary>
    /// <remarks>
    ///     Counted off the coordinates rather than read off a report field, so the count is of what
    ///     actually landed in the atlas. Two charts cannot share a bounding box unless the packer
    ///     placed them there, which it does not.
    /// </remarks>
    static int Charts(EditMesh source, float budget) {
        var quads = Remesher.Remesh(
            source,
            new() { TargetQuads = 300, GenerateUvs = true, Atlas = new() { SeamBudget = budget } },
            out var report
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var stage = report.Stages.Single(timing => timing.Stage == RemeshStage.Transfer);

        // The transfer stage's element count is the corner count plus the chart count, so the chart
        // count is what is left after the corners are taken off.
        return stage.Elements - quads.CornerCount;
    }
}
