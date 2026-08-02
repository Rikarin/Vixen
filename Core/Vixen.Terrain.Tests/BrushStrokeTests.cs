// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>
///     A drag turned into stamps — [docs/plan/31 § D12].
/// </summary>
public sealed class BrushStrokeTests {
    static TerrainBrush Brush(float radius = 2f, float spacing = 0.5f) =>
        TerrainBrush.Default with { Radius = radius, Spacing = spacing };

    static List<BrushStamp> Drag(TerrainBrush brush, params Vector2[] path) {
        var stroke = new BrushStroke(brush);
        var stamps = new List<BrushStamp>();

        foreach (var point in path) {
            stroke.MoveTo(point, stamps);
        }

        return stamps;
    }

    [Fact]
    public void AClickWithoutADragStillStampsOnce() {
        // Which is what makes this the Single tool as well as the Paint one: an artist who clicks
        // and does not move expects an instance, not nothing.
        var stamps = Drag(Brush(), Vector2.Zero);
        Assert.Single(stamps);
        Assert.Equal(Vector2.Zero, stamps[0].Centre);
    }

    [Fact]
    public void StampsAreSpacedByDistanceRatherThanByPointerEvent() {
        // Radius 2 with spacing 0.5 is a stamp every metre. Ten metres in one move is eleven stamps:
        // the first from touching down, then one per metre.
        var stamps = Drag(Brush(), Vector2.Zero, new(10f, 0f));

        Assert.Equal(11, stamps.Count);

        for (var index = 0; index < stamps.Count; index++) {
            Assert.Equal(index, stamps[index].Centre.X, 4);
            Assert.Equal(0f, stamps[index].Centre.Y, 4);
        }
    }

    [Fact]
    public void TheSamePathStampsTheSameWhateverRateTheEventsArriveAt() {
        // The property the whole design is for. A slow machine sees the drag as two events and a
        // fast one as twenty; the stroke must not be able to tell.
        var coarse = Drag(Brush(), Vector2.Zero, new(5f, 0f), new(10f, 0f));

        var fine = new List<Vector2>();

        for (var step = 0; step <= 40; step++) {
            fine.Add(new(step * 0.25f, 0f));
        }

        var dense = Drag(Brush(), [.. fine]);

        Assert.Equal(coarse.Count, dense.Count);

        for (var index = 0; index < coarse.Count; index++) {
            Assert.Equal(coarse[index].Centre.X, dense[index].Centre.X, 3);
        }
    }

    [Fact]
    public void LeftoverDistanceCarriesAcrossTheJoinBetweenSegments() {
        // Two segments of 1.5 m at a 1 m spacing. Without carrying, each segment stamps once and the
        // gap across the join is 2 m — a stroke that is visibly sparser wherever the pointer paused.
        var stamps = Drag(Brush(), Vector2.Zero, new(1.5f, 0f), new(3f, 0f));

        Assert.Equal(4, stamps.Count);
        Assert.Equal([0f, 1f, 2f, 3f], stamps.Select(stamp => MathF.Round(stamp.Centre.X, 3)));
    }

    [Fact]
    public void AMoveShorterThanTheSpacingStampsNothingAndIsNotLost() {
        var stroke = new BrushStroke(Brush());
        var stamps = new List<BrushStamp>();

        stroke.MoveTo(Vector2.Zero, stamps);
        Assert.Single(stamps);

        stroke.MoveTo(new(0.4f, 0f), stamps);
        stroke.MoveTo(new(0.8f, 0f), stamps);
        Assert.Single(stamps);

        // The 1.2 m accumulated so far is not discarded — the next move crosses a metre and stamps.
        stroke.MoveTo(new(1.2f, 0f), stamps);
        Assert.Equal(2, stamps.Count);
        Assert.Equal(1f, stamps[1].Centre.X, 3);
    }

    [Fact]
    public void AMoveToTheSamePlaceDoesNothing() {
        var stroke = new BrushStroke(Brush());
        var stamps = new List<BrushStamp>();

        stroke.MoveTo(Vector2.Zero, stamps);
        stroke.MoveTo(Vector2.Zero, stamps);
        stroke.MoveTo(Vector2.Zero, stamps);

        Assert.Single(stamps);
    }

    [Fact]
    public void AlongStrokeTurnsEachStampToFaceTheDirectionOfTravel() {
        var brush = Brush() with { Rotation = BrushRotation.AlongStroke };
        var stamps = Drag(brush, Vector2.Zero, new(0f, 5f));

        // Straight up +Z, which is atan2(1, 0) — a quarter turn.
        foreach (var stamp in stamps.Skip(1)) {
            Assert.Equal(MathF.PI / 2f, stamp.Rotation, 4);
        }
    }

    [Fact]
    public void FixedKeepsTheBrushsOwnAngle() {
        var brush = Brush() with { Rotation = BrushRotation.Fixed, Angle = 0.75f };
        var stamps = Drag(brush, Vector2.Zero, new(5f, 0f));

        Assert.All(stamps, stamp => Assert.Equal(0.75f, stamp.Rotation, 5));
    }

    /// <summary>
    ///     A random rotation is a hash of the stamp index, so a stroke can be replayed.
    /// </summary>
    /// <remarks>
    ///     The property an undo and a redo need: the angle of stamp N depends on N and the seed, not
    ///     on how many stamps a shared generator has produced since the process started. A stroke
    ///     drawn from <c>Random.Shared</c> would redo differently from how it was done.
    /// </remarks>
    [Fact]
    public void RandomRotationIsDeterministicAndReplayable() {
        var brush = Brush() with { Rotation = BrushRotation.Random };

        var first = Drag(brush, Vector2.Zero, new(10f, 0f));
        var second = Drag(brush, Vector2.Zero, new(10f, 0f));

        Assert.Equal(
            first.Select(stamp => stamp.Rotation),
            second.Select(stamp => stamp.Rotation)
        );

        // And it is actually random-looking rather than constant.
        Assert.True(first.Select(stamp => stamp.Rotation).Distinct().Count() > 5);
        Assert.All(first, stamp => Assert.InRange(stamp.Rotation, 0f, MathF.Tau));
    }

    [Fact]
    public void TwoSeedsGiveTwoStrokes() {
        var brush = Brush() with { Rotation = BrushRotation.Random };

        List<BrushStamp> Run(uint seed) {
            var stroke = new BrushStroke(brush, seed);
            var stamps = new List<BrushStamp>();
            stroke.MoveTo(Vector2.Zero, stamps);
            stroke.MoveTo(new(10f, 0f), stamps);
            return stamps;
        }

        Assert.NotEqual(Run(1).Select(s => s.Rotation), Run(2).Select(s => s.Rotation));
    }

    [Fact]
    public void TheStrokesFootprintCoversEveryStamp() {
        var brush = Brush(radius: 2f);
        var stroke = new BrushStroke(brush);
        var stamps = new List<BrushStamp>();

        Assert.True(stroke.IsEmpty);

        stroke.MoveTo(new(0f, 0f), stamps);
        stroke.MoveTo(new(6f, 3f), stamps);

        Assert.False(stroke.IsEmpty);
        Assert.Equal(stamps.Count, stroke.StampCount);

        foreach (var stamp in stamps) {
            var footprint = brush.FootprintOf(stamp);

            Assert.True(stroke.Footprint.Contains(footprint.Minimum));
            Assert.True(stroke.Footprint.Contains(footprint.Maximum));
        }
    }

    [Fact]
    public void ADiagonalDragIsSpacedAlongItsLengthNotItsAxes() {
        // A 3-4-5 triangle: five metres of travel at a one-metre spacing.
        var stamps = Drag(Brush(), Vector2.Zero, new(3f, 4f));

        Assert.Equal(6, stamps.Count);

        for (var index = 1; index < stamps.Count; index++) {
            Assert.Equal(1f, (stamps[index].Centre - stamps[index - 1].Centre).Length(), 4);
        }
    }
}
