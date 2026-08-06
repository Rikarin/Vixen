// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry.Uv.Flattening;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The third rung, handed a fold that was made on purpose.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D5's third rung is <i>"a progressive/injectivity-preserving pass on the
///         flipped neighbourhood only, and if that fails, split the chart and recurse"</i>.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the corpus reaches it, and that is a finding rather than a gap in the
///         corpus.</b> A free-boundary least-squares conformal map turns out to be much harder to fold
///         than § D5's "no injectivity guarantee" suggests: the guarantee really is absent, but on a
///         sphere with a hairline slit, on a hyperbolic fan, on a strip of 170° slivers and on every
///         other shape tried here it simply does not happen. So the fold is injected — a handful of
///         interior vertices dragged across their neighbours — because <b>a rung reached by nothing is
///         a rung nobody has run</b>, and shipping one is worse than not having it.
///     </para>
/// </remarks>
public class UvFlattenRepairTests {
    [Fact]
    public void AnInjectedFoldIsUndoneByTheNeighbourhoodPass() {
        var fixture = Fold(ShapeCorpus.Hemisphere(), 0xC0FFEEu, 12);

        Assert.True(fixture.Before > 0, "the fold was not injected, so this proves nothing");

        var repaired = Flattener.Repair(
            fixture.Chart,
            fixture.Frames,
            fixture.Weights,
            new(),
            fixture.Anchor,
            Distortion.Measure(fixture.Chart, fixture.Frames, fixture.Coordinates, fixture.Rounded),
            fixture.Coordinates,
            fixture.Rounded
        );

        Assert.True(
            repaired.Distortion.Flipped == 0,
            $"{fixture.Before} triangles were folded and {repaired.Distortion.Flipped} still are."
        );
    }

    /// <summary>The pass leaves the rest of the chart where it found it.</summary>
    /// <remarks>
    ///     ⚠ <b>That restriction is the whole reason the third rung is not just more of the second.</b>
    ///     Re-solving the whole chart to fix one fold trades a local failure for stretch everywhere,
    ///     and on a chart that was already inside its bound that is a strictly worse asset.
    /// </remarks>
    [Fact]
    public void TheRepairDoesNotDisturbTheFarSideOfTheChart() {
        var fixture = Fold(ShapeCorpus.Hemisphere(), 0xBEEFu, 6);
        var before = (double[])fixture.Coordinates.Clone();

        Flattener.Repair(
            fixture.Chart,
            fixture.Frames,
            fixture.Weights,
            new(),
            fixture.Anchor,
            Distortion.Measure(fixture.Chart, fixture.Frames, fixture.Coordinates, fixture.Rounded),
            fixture.Coordinates,
            fixture.Rounded
        );

        var moved = 0;

        for (var vertex = 0; vertex < fixture.Chart.VertexCount; vertex++) {
            if (before[vertex * 2] != fixture.Coordinates[vertex * 2]
                || before[(vertex * 2) + 1] != fixture.Coordinates[(vertex * 2) + 1]) {
                moved++;
            }
        }

        Assert.True(moved > 0, "nothing moved at all, so the pass did nothing");

        Assert.True(
            moved < fixture.Chart.VertexCount / 2,
            $"{moved} of {fixture.Chart.VertexCount} vertices moved, which is not a neighbourhood."
        );
    }

    /// <summary>A fold the pass cannot undo is refused rather than shipped.</summary>
    /// <remarks>
    ///     The chart is turned inside out — every interior vertex reflected through the island's centre
    ///     — which no local pass can recover, and which U3 answers by splitting.
    /// </remarks>
    [Fact]
    public void AFoldThatCannotBeUndoneIsRefusedByName() {
        var mesh = ShapeCorpus.Hemisphere();
        var chart = ChartMesh.Extract(mesh, ShapeCorpus.OneChart(mesh))[0];
        var frames = Frames(chart);
        var coordinates = new double[chart.VertexCount * 2];
        var rounded = new Vector2[chart.VertexCount];

        Lscm.Solve(chart, frames, new(), coordinates);

        // Collapse the whole chart onto a line: every triangle is then exactly degenerate, which
        // `Orient2D` answers with zero and which no neighbourhood is small enough to fix.
        for (var vertex = 0; vertex < chart.VertexCount; vertex++) {
            coordinates[(vertex * 2) + 1] = 0d;
        }

        Round(coordinates, rounded);

        var weights = CotangentWeights.Build(chart, frames);
        var anchor = Pins.Choose(chart).First;

        var repaired = Flattener.Repair(
            chart,
            frames,
            weights,
            new() { FlattenIterations = 1, SolverIterations = 1 },
            anchor,
            Distortion.Measure(chart, frames, coordinates, rounded),
            coordinates,
            rounded
        );

        Assert.True(repaired.Distortion.Flipped > 0, "a chart collapsed to a line came back injective");
    }

    readonly record struct Fixture(
        ChartMesh Chart,
        TriangleFrame[] Frames,
        CotangentWeights Weights,
        int Anchor,
        double[] Coordinates,
        Vector2[] Rounded,
        int Before
    );

    /// <summary>Flattens a mesh and then drags interior vertices across their neighbours.</summary>
    static Fixture Fold(EditMesh mesh, uint seed, int count) {
        var chart = ChartMesh.Extract(mesh, ShapeCorpus.OneChart(mesh))[0];
        var frames = Frames(chart);
        var coordinates = new double[chart.VertexCount * 2];
        var rounded = new Vector2[chart.VertexCount];

        Lscm.Solve(chart, frames, new(), coordinates);

        var state = seed;
        var folded = 0;

        for (var attempt = 0; attempt < count * 16 && folded < count; attempt++) {
            var vertex = (int)(Next(ref state) % (uint)chart.VertexCount);

            if (chart.IsBoundary[vertex]) {
                continue;
            }

            // ⚠ Thrown past one of its own neighbours, not reflected through their average. A
            // reflection was the first attempt and it moved nothing: in a parameterization worth
            // repairing every interior vertex already sits almost exactly at its neighbours' centroid,
            // so `centre − k(v − centre)` is `k` times about zero. Landing on the far side of a
            // neighbour turns that neighbour's whole star inside out, which is the fold wanted here.
            double x = 0d, y = 0d;
            var valence = chart.AdjacentStart[vertex + 1] - chart.AdjacentStart[vertex];

            for (var index = chart.AdjacentStart[vertex]; index < chart.AdjacentStart[vertex + 1]; index++) {
                x += coordinates[chart.Adjacent[index] * 2];
                y += coordinates[(chart.Adjacent[index] * 2) + 1];
            }

            x /= valence;
            y /= valence;

            var neighbour = chart.Adjacent[chart.AdjacentStart[vertex]];

            coordinates[vertex * 2] = x + (3d * (coordinates[neighbour * 2] - x));
            coordinates[(vertex * 2) + 1] = y + (3d * (coordinates[(neighbour * 2) + 1] - y));
            folded++;
        }

        Round(coordinates, rounded);

        return new(
            chart,
            frames,
            CotangentWeights.Build(chart, frames),
            Pins.Choose(chart).First,
            coordinates,
            rounded,
            Distortion.Measure(chart, frames, coordinates, rounded).Distortion.Flipped
        );
    }

    static TriangleFrame[] Frames(ChartMesh chart) {
        var frames = new TriangleFrame[chart.TriangleCount];

        for (var triangle = 0; triangle < chart.TriangleCount; triangle++) {
            frames[triangle] = TriangleFrame.Build(
                chart.Positions[chart.Triangles[triangle * 3]],
                chart.Positions[chart.Triangles[(triangle * 3) + 1]],
                chart.Positions[chart.Triangles[(triangle * 3) + 2]]
            );
        }

        return frames;
    }

    static void Round(double[] coordinates, Vector2[] rounded) {
        for (var vertex = 0; vertex < rounded.Length; vertex++) {
            rounded[vertex] = new((float)coordinates[vertex * 2], (float)coordinates[(vertex * 2) + 1]);
        }
    }

    static uint Next(ref uint state) => state = (state * 1664525u) + 1013904223u;
}
