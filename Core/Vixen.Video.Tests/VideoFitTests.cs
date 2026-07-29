// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Video.Gpu;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>The aspect arithmetic both renderers share, checked without either of them.</summary>
/// <remarks>
///     Worth its own file because it is the one piece of video code a person can be wrong about
///     without anything crashing: a picture a fifth too narrow looks like a picture.
/// </remarks>
public sealed class VideoFitTests {
    [Fact]
    public void StretchIsTheRectangleAndTheWholePicture() {
        var target = new Rectangle(10, 20, 300, 100);
        var placed = VideoFit.Place(VideoScaling.Stretch, new Vector2(16, 9), target);

        Assert.Equal(target, placed.Target);
        Assert.Equal(Vector2.One, placed.TextureScale);
        Assert.Equal(Vector2.Zero, placed.TextureOffset);
    }

    [Fact]
    public void ContainBarsTheTopAndBottomOfATallBox() {
        // 16:9 in a square: the width is the constraint, so the picture is 200 × 112.5, centred.
        var placed = VideoFit.Place(VideoScaling.Contain, new Vector2(16, 9), new Rectangle(0, 0, 200, 200));

        Assert.Equal(200f, placed.Target.Width, 3);
        Assert.Equal(112.5f, placed.Target.Height, 3);
        Assert.Equal(0f, placed.Target.X, 3);
        Assert.Equal(43.75f, placed.Target.Y, 3);

        // ⚠ The whole picture is still shown. Contain moves the rectangle and never the coordinates,
        // which is the half of VideoPlacement that Cover does not use.
        Assert.Equal(Vector2.One, placed.TextureScale);
    }

    [Fact]
    public void ContainBarsTheSidesOfAWideBox() {
        // 1:1 in a 16:9 box: the height is the constraint.
        var placed = VideoFit.Place(VideoScaling.Contain, new Vector2(100, 100), new Rectangle(0, 0, 320, 180));

        Assert.Equal(180f, placed.Target.Width, 3);
        Assert.Equal(180f, placed.Target.Height, 3);
        Assert.Equal(70f, placed.Target.X, 3);
        Assert.Equal(0f, placed.Target.Y, 3);
    }

    [Fact]
    public void CoverKeepsTheRectangleAndCropsTheCoordinates() {
        // 16:9 in a square: the sides are cropped, and the crop is centred.
        var target = new Rectangle(0, 0, 200, 200);
        var placed = VideoFit.Place(VideoScaling.Cover, new Vector2(16, 9), target);

        Assert.Equal(target, placed.Target);
        Assert.Equal(9f / 16f, placed.TextureScale.X, 4);
        Assert.Equal(1f, placed.TextureScale.Y, 4);
        Assert.Equal((1f - (9f / 16f)) / 2f, placed.TextureOffset.X, 4);
        Assert.Equal(0f, placed.TextureOffset.Y, 4);
    }

    [Fact]
    public void MatchingShapesAreLeftAlone() {
        // Within a pixel of the target's height, the letterbox would be thinner than the edge it sat
        // against — so the answer is the rectangle itself, and one seam fewer.
        var target = new Rectangle(0, 0, 1920, 1080);
        var placed = VideoFit.Place(VideoScaling.Contain, new Vector2(1280, 720), target);

        Assert.Equal(target, placed.Target);
    }

    [Theory]
    [InlineData(0, 9)]
    [InlineData(16, 0)]
    public void ADegenerateSourceFillsTheTarget(float width, float height) {
        // The ordinary case for the first frame or two of any video: something asks where the
        // picture goes before there is one. Dividing by zero here would be a crash on start-up.
        var target = new Rectangle(0, 0, 100, 50);

        Assert.Equal(target, VideoFit.Place(VideoScaling.Contain, new Vector2(width, height), target).Target);
    }

    [Fact]
    public void AnamorphicContentIsFittedByItsDisplaySizeAndNotItsSamples() {
        // The case the DisplaySize seam exists for: 720×480 samples meant to be shown at 16:9. Fitted
        // by the sample count it comes out 4:3 — a fifth too narrow — and every number on the way
        // there is correct.
        var target = new Rectangle(0, 0, 1000, 1000);

        var bySamples = VideoFit.Place(VideoScaling.Contain, new Vector2(720, 480), target).Target;
        var byDisplay = VideoFit.Place(VideoScaling.Contain, new Vector2(853, 480), target).Target;

        Assert.Equal(1000f, bySamples.Width, 3);
        Assert.Equal(1000f, byDisplay.Width, 3);
        Assert.True(byDisplay.Height < bySamples.Height);
    }
}
