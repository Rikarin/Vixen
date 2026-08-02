// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>The authored form of a path — [docs/plan/31 § T8].</summary>
public sealed class SplineAssetTests {
    static SplineAsset Road =>
        SplineAsset.Through("Road", [new(0f, 0f, 0f), new(20f, 0f, 10f), new(40f, 0f, 0f), new(60f, 0f, 20f)]);

    [Fact]
    public void AnAssetWithOnePointIsLegalAndIsNotACurve() {
        var asset = new SplineAsset { Name = "Started" };

        asset.Add(SplinePoint.At(new(1f, 2f, 3f)));

        Assert.Equal(1, asset.Count);
        Assert.False(asset.CanBuild);
        Assert.Throws<InvalidOperationException>(() => asset.Build());
    }

    /// <summary>Inserting on the curve does not move the curve.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole reason <c>InsertOn</c> is not <c>Insert</c> with an evaluated position.</b>
    ///     Dropping a point on and leaving the tangents alone reparameterises both halves, so the
    ///     road moves — and the author's next act is to drag the point back to where it already was.
    /// </remarks>
    [Fact]
    public void InsertingOnTheCurvePreservesItsShape() {
        var asset = Road;
        var before = asset.Build();

        // ⚠ Compared by arc length, not by parameter. Splitting a segment reparameterises the two
        // halves — that is what makes the shape survive at all — so the same fraction of the
        // *parameter* range is a different place on the curve afterwards, and a test written that way
        // fails on correct output. Distance along the path is intrinsic to the shape.
        var samples = new Vector3[64];

        for (var index = 0; index < samples.Length; index++) {
            samples[index] = before.EvaluateAtDistance(before.Length * (index / (float)(samples.Length - 1)));
        }

        var inserted = asset.InsertOn(1.4f);

        Assert.True(inserted > 0);
        Assert.Equal(5, asset.Count);

        var after = asset.Build();

        Assert.Equal(before.Length, after.Length, 1);

        for (var index = 0; index < samples.Length; index++) {
            var moved = after.EvaluateAtDistance(after.Length * (index / (float)(samples.Length - 1)));

            Assert.True(
                Vector3.Distance(samples[index], moved) < 0.05f,
                $"sample {index} moved from {samples[index]} to {moved} when a point was inserted."
            );
        }
    }

    [Fact]
    public void InsertingOnAControlPointIsRefusedRatherThanDuplicating() {
        var asset = Road;

        Assert.Equal(-1, asset.InsertOn(2f));
        Assert.Equal(4, asset.Count);
    }

    /// <summary>A tangent moved on its own makes a corner.</summary>
    [Fact]
    public void AnUnmirroredTangentIsACorner() {
        var asset = Road;

        asset.SetTangentOut(1, new(0f, 0f, 10f), mirror: false);

        var point = asset[1];

        Assert.NotEqual(-point.TangentOut, point.TangentIn);

        asset.SetTangentOut(1, new(0f, 0f, 10f));

        Assert.Equal(-asset[1].TangentOut, asset[1].TangentIn);
    }

    [Fact]
    public void SplittingKeepsThePointInBothHalves() {
        var asset = Road;
        var tail = asset.Split(2);

        Assert.NotNull(tail);
        Assert.Equal(3, asset.Count);
        Assert.Equal(2, tail.Count);
        Assert.Equal(asset[^1].Position, tail[0].Position);
    }

    [Fact]
    public void SplittingAtAnEndIsRefused() {
        var asset = Road;

        Assert.Null(asset.Split(0));
        Assert.Null(asset.Split(asset.Count - 1));
        Assert.Equal(4, asset.Count);
    }

    /// <summary>A ring cut once is one path, not two.</summary>
    [Fact]
    public void SplittingAClosedPathOpensIt() {
        var asset = SplineAsset.Through(
            "Loop",
            [new(0f, 0f, 0f), new(10f, 0f, 0f), new(10f, 0f, 10f), new(0f, 0f, 10f)],
            closed: true
        );

        var tail = asset.Split(2);

        Assert.Null(tail);
        Assert.False(asset.IsClosed);
        Assert.Equal(5, asset.Count);
        Assert.Equal(asset[0].Position, asset[^1].Position);
    }

    /// <summary>Joining two paths that meet merges the shared point.</summary>
    /// <remarks>
    ///     ⚠ <b>Two control points at the same place make a segment of zero length, which has no
    ///     direction.</b> A road joined without the merge has a frame that flips at the seam and a
    ///     mesh placement that stacks everything it puts there.
    /// </remarks>
    [Fact]
    public void JoiningMergesACoincidentEnd() {
        var first = SplineAsset.Through("A", [new(0f, 0f, 0f), new(10f, 0f, 0f)]);
        var second = SplineAsset.Through("B", [new(10f, 0f, 0f), new(20f, 0f, 0f)]);

        Assert.True(first.Join(second));
        Assert.Equal(3, first.Count);
        Assert.Null(first.Validate());
    }

    [Fact]
    public void JoiningReversedWalksTheOtherPathBackwards() {
        var first = SplineAsset.Through("A", [new(0f, 0f, 0f), new(10f, 0f, 0f)]);
        var second = SplineAsset.Through("B", [new(30f, 0f, 0f), new(10f, 0f, 0f)]);

        Assert.True(first.Join(second, reversed: true));
        Assert.Equal(3, first.Count);
        Assert.Equal(new Vector3(30f, 0f, 0f), first[^1].Position);
    }

    [Fact]
    public void AClosedPathHasNoEndToJoinOnto() {
        var loop = SplineAsset.Through(
            "Loop",
            [new(0f, 0f, 0f), new(10f, 0f, 0f), new(10f, 0f, 10f)],
            closed: true
        );

        Assert.False(loop.Join(SplineAsset.Through("B", [new(10f, 0f, 10f), new(20f, 0f, 20f)])));
    }

    [Fact]
    public void MovingAPointCarriesItsTangents() {
        var asset = Road;
        var before = asset[1];

        asset.MoveTo(1, new(25f, 5f, 15f));

        Assert.Equal(new Vector3(25f, 5f, 15f), asset[1].Position);
        Assert.Equal(before.TangentIn, asset[1].TangentIn);
        Assert.Equal(before.TangentOut, asset[1].TangentOut);
    }

    [Fact]
    public void TwoPointsAtTheSamePlaceAreRefused() {
        var asset = new SplineAsset("Doubled", [SplinePoint.At(Vector3.Zero), SplinePoint.At(Vector3.Zero)]);

        Assert.Contains("no length", asset.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACloneIsIndependent() {
        var asset = Road;
        var copy = asset.Clone();

        copy.MoveTo(0, new(99f, 99f, 99f));

        Assert.NotEqual(asset[0].Position, copy[0].Position);
        Assert.Equal(asset.Count, copy.Count);
    }

    [Fact]
    public void SmoothingKeepsThePositionsAndTheRolls() {
        var asset = Road;

        asset.Set(1, asset[1] with { Roll = 0.4f });
        asset.Smooth();

        Assert.Equal(new Vector3(20f, 0f, 10f), asset[1].Position);
        Assert.Equal(0.4f, asset[1].Roll, 4);
        Assert.Equal(-asset[1].TangentOut, asset[1].TangentIn);
    }
}
