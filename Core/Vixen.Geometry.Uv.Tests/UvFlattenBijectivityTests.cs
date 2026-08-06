// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry.Uv.Flattening;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The one invariant that is pass or fail: nothing that ships is folded.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5 and § D6. ⚠ <b>An island either comes back with zero flipped triangles or
///         it does not come back at all.</b> There is no third answer and no threshold, because a
///         flipped triangle is a region of the atlas where the mapping is not invertible: a bake writes
///         to the wrong texel and sampling reads from it. Reporting a fold as a quality figure would
///         make the one field that is pass-or-fail look like one that could be traded against the
///         others.
///     </para>
///     <para>
///         ⚠ <b>The flip test is <see cref="ExactPredicates.Orient2D" /> on the coordinates that
///         ship.</b> A <see langword="float" /> cross product compared against zero is not even
///         antisymmetric on near-collinear input — three points on <c>y = 3x</c> with coordinates a
///         float holds exactly give <c>+16</c> in one argument order and <c>−67108864</c> in another —
///         and a triangle that is <i>exactly</i> degenerate in the parameterization is both the case
///         that matters and the case the naive test gets wrong.
///     </para>
/// </remarks>
public class UvFlattenBijectivityTests {
    public static TheoryData<string> Everything =>
        [
            "sphere-cut-open",
            "cylinder-slit",
            "torus-slit",
            "hemisphere",
            "saddle",
            "strip",
            "obtuse-grid",
            "sphere-nearly-closed",
            "one-triangle",
            "two-triangles"
        ];

    /// <summary>Either an island with no fold, or no island — over the whole corpus, hard cases included.</summary>
    [Theory]
    [MemberData(nameof(Everything))]
    public void AnIslandEitherHasNoFoldOrIsNotAnIsland(string shape) {
        var mesh = Hard(shape);
        var detail = UvUnwrap.Detail(mesh, ShapeCorpus.OneChart(mesh), new(), null, 0);

        Assert.Equal(0, detail.Report.Distortion.Flipped);
        Assert.True(detail.Report.IsInjective);

        foreach (var measured in detail.Distortion) {
            Assert.Equal(0, measured.Flipped);
        }

        // And the flip test run again from outside, on the coordinates as a caller would read them,
        // because a measure that agreed with itself would agree with itself however it was wrong.
        foreach (var island in detail.Islands) {
            for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
                var a = island.Coordinates[triangle * 3];
                var b = island.Coordinates[(triangle * 3) + 1];
                var c = island.Coordinates[(triangle * 3) + 2];

                Assert.True(
                    ExactPredicates.Orient2D(a, b, c) > 0,
                    $"{shape}: triangle {triangle} is {ExactPredicates.Orient2D(a, b, c)} — {a}, {b}, {c}."
                );
            }
        }

        foreach (var refusal in detail.Refused) {
            Assert.NotEqual(ChartRefusal.None, refusal.Reason);
        }
    }

    /// <summary>Every triangle winds the same way, which is what makes the flip test a global statement.</summary>
    /// <remarks>
    ///     ⚠ <b>A chart mapped entirely by a reflection has every triangle "flipped" and is a perfectly
    ///     good parameterization of the mirror image.</b> That is one sign rather than a fold, so the
    ///     first rung reflects the whole chart when its signed area comes out negative — and this is
    ///     what says the reflection landed on the side the flip test expects.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Everything))]
    public void EveryChartComesBackCounterClockwise(string shape) {
        var mesh = Hard(shape);
        var islands = UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new());

        foreach (var island in islands) {
            var signed = 0d;

            for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
                var a = island.Coordinates[triangle * 3];
                var b = island.Coordinates[(triangle * 3) + 1];
                var c = island.Coordinates[(triangle * 3) + 2];

                signed += ((b.X - (double)a.X) * (c.Y - (double)a.Y))
                    - ((b.Y - (double)a.Y) * (c.X - (double)a.X));
            }

            Assert.True(signed > 0d, $"{shape} came back mirrored: signed area {signed}.");
        }
    }

    static EditMesh Hard(string shape) =>
        shape switch {
            "sphere-nearly-closed" => ShapeCorpus.SphereNearlyClosed(),
            "one-triangle" => ShapeCorpus.OneTriangle(),
            "two-triangles" => ShapeCorpus.TwoTriangles(),
            _ => FlattenFixtures.Build(shape)
        };
}
