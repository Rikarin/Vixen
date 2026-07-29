// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>The shapes [13](../../docs/plan/13-diagnostics.md) § Debug rendering names.</summary>
public sealed class DebugDrawShapeTests {
    [Fact]
    public void AnOrientedBoxIsTheCornersTransformedAndNotTheExtents() {
        var box = new BoundingBox(-Vector3.One, Vector3.One);
        var turned = Matrix4x4.FromRotationY(MathUtil.PiOverFour);

        var draw = new DebugDraw();
        draw.Box(box, turned, Color4.White);

        Assert.Equal(12, draw.Count);

        // Turned by an eighth, a unit cube's corners reach √2 along X and Z and are still at ±1 on Y.
        // Transforming the extents rather than the corners would give a box that reached √2 on all
        // three, which is the axis-aligned bound of the rotated one and a different picture.
        var reach = 0f;
        var height = 0f;

        foreach (var line in draw.Lines) {
            reach = MathF.Max(reach, MathF.Max(MathF.Abs(line.From.X), MathF.Abs(line.From.Z)));
            height = MathF.Max(height, MathF.Abs(line.From.Y));
        }

        Assert.Equal(MathF.Sqrt(2f), reach, 4);
        Assert.Equal(1f, height, 4);
    }

    /// <summary>
    ///     A capsule standing on end is the commonest one there is, and it is exactly the axis that
    ///     a basis built by crossing with a fixed up vector degenerates on.
    /// </summary>
    [Fact]
    public void AnUprightCapsuleIsNotDegenerate() {
        var draw = new DebugDraw();
        draw.Capsule(Vector3.Zero, Vector3.UnitY * 2f, 0.5f, Color4.White);

        Assert.True(draw.Count > 0);

        foreach (var line in draw.Lines) {
            Assert.False(line.From.IsNaN, "a vertex is NaN, so the perpendicular basis collapsed");
            Assert.False(line.To.IsNaN, "a vertex is NaN, so the perpendicular basis collapsed");
        }
    }

    /// <summary>The same, on each of the three axes, because only one of them is the seed.</summary>
    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    public void ACapsuleAlongAnyAxisIsNotDegenerate(float x, float y, float z) {
        var draw = new DebugDraw();
        draw.Capsule(Vector3.Zero, new(x, y, z), 0.5f, Color4.White);

        foreach (var line in draw.Lines) {
            Assert.False(line.From.IsNaN);
            Assert.False(line.To.IsNaN);
        }
    }

    /// <summary>A capsule whose caps have met is a sphere, not a division by zero.</summary>
    [Fact]
    public void ACapsuleOfNoLengthIsASphere() {
        var draw = new DebugDraw();
        draw.Capsule(Vector3.Zero, Vector3.Zero, 1f, Color4.White);

        var sphere = new DebugDraw();
        sphere.Sphere(new(Vector3.Zero, 1f), Color4.White);

        Assert.Equal(sphere.Count, draw.Count);
    }

    [Fact]
    public void AnArrowHasAShaftAndAHead() {
        var draw = new DebugDraw();
        draw.Arrow(Vector3.Zero, Vector3.UnitZ, Color4.White);

        // One shaft and four head lines, all of which start or end at the tip.
        Assert.Equal(5, draw.Count);
    }

    /// <summary>
    ///     A very long arrow's head stays a sensible size. A head scaled purely by length would be
    ///     twenty metres wide on a hundred-metre velocity vector, filling the screen and hiding the
    ///     thing being pointed at.
    /// </summary>
    [Fact]
    public void ALongArrowDoesNotGrowAGiantHead() {
        var draw = new DebugDraw();
        draw.Arrow(Vector3.Zero, Vector3.UnitZ * 100f, Color4.White);

        var widest = 0f;

        foreach (var line in draw.Lines) {
            widest = MathF.Max(widest, MathF.Max(MathF.Abs(line.To.X), MathF.Abs(line.To.Y)));
        }

        Assert.True(widest < 1f, $"the head is {widest} across");
    }

    [Fact]
    public void AZeroLengthArrowIsJustTheDegenerateShaft() {
        var draw = new DebugDraw();
        draw.Arrow(Vector3.One, Vector3.One, Color4.White);

        Assert.Equal(1, draw.Count);
    }

    /// <summary>A frustum is its twelve edges, and they are the view volume's own corners.</summary>
    [Fact]
    public void AFrustumIsTwelveEdgesOverEightCorners() {
        var projection = Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverFour, 1.6f, 0.1f, 100f);
        var frustum = new BoundingFrustum(projection);

        var draw = new DebugDraw();
        draw.Frustum(frustum, Color4.White);

        Assert.Equal(12, draw.Count);

        Span<Vector3> corners = stackalloc Vector3[BoundingFrustum.CornerCount];
        frustum.GetCorners(corners);

        // Every endpoint is one of the eight, so nothing was interpolated or averaged on the way.
        foreach (var line in draw.Lines) {
            Assert.Contains(corners.ToArray(), corner => Vector3.NearEqual(corner, line.From, 1e-3f));
            Assert.Contains(corners.ToArray(), corner => Vector3.NearEqual(corner, line.To, 1e-3f));
        }
    }

    [Fact]
    public void AConeIsARingAndFourSides() {
        var draw = new DebugDraw();
        draw.Cone(Vector3.Zero, Vector3.UnitZ * 3f, 1f, Color4.White);

        Assert.True(draw.Count > 4);

        // The apex is where it was asked for: four lines start there and none of them is anywhere
        // else, which is what says the direction was taken as apex-to-base rather than the reverse.
        var fromApex = 0;

        foreach (var line in draw.Lines) {
            if (Vector3.NearEqual(line.From, Vector3.Zero, 1e-4f)) {
                fromApex++;
            }
        }

        Assert.Equal(4, fromApex);
    }

    [Fact]
    public void ScreenGeometryIsCountedApartFromTheWorld() {
        var draw = new DebugDraw();
        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.White);
        draw.ScreenRect(new(10f, 10f), new(100f, 50f), Color4.White);

        Assert.Equal(1, draw.Count);
        Assert.Equal(4, draw.ScreenCount);
    }

    /// <summary>A fill is scanlines, one per unit of spacing, inside the rectangle.</summary>
    [Fact]
    public void AFillIsScanlinesInsideTheRectangle() {
        var draw = new DebugDraw();
        draw.ScreenFill(new(10f, 20f), new(100f, 10f), Color4.White, spacing: 2f);

        Assert.Equal(5, draw.ScreenCount);

        foreach (var line in draw.ScreenLines) {
            Assert.Equal(10f, line.From.X);
            Assert.Equal(110f, line.To.X);
            Assert.InRange(line.From.Y, 20f, 30f);
        }
    }

    [Fact]
    public void ScreenTextBecomesScreenLinesImmediately() {
        var draw = new DebugDraw();
        draw.ScreenText(new(4f, 8f), "AB", Color4.White, size: 10f);

        Assert.Equal(DebugFont.SegmentCount("AB"), draw.ScreenCount);
        Assert.Equal(0, draw.TextCount);
    }

    /// <summary>World text stays text, because facing it needs a camera the call site has not got.</summary>
    [Fact]
    public void WorldTextStaysAsText() {
        var draw = new DebugDraw();
        draw.Text(Vector3.Zero, "AB", Color4.White);

        Assert.Equal(1, draw.TextCount);
        Assert.Equal(0, draw.Count);
    }

    /// <summary>Ageing reaches all three lists, not only the one the first version had.</summary>
    [Fact]
    public void AgeingClearsEveryList() {
        var draw = new DebugDraw();
        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.White);
        draw.ScreenLine(Vector2.Zero, Vector2.One, Color4.White);
        draw.Text(Vector3.Zero, "hello", Color4.White);

        draw.Advance(1f / 60f);

        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.ScreenCount);
        Assert.Equal(0, draw.TextCount);
    }

    [Fact]
    public void TimedScreenGeometryAndLabelsOutliveAFrame() {
        var draw = new DebugDraw();
        draw.ScreenLine(Vector2.Zero, Vector2.One, Color4.White, seconds: 1f);
        draw.Text(Vector3.Zero, "hello", Color4.White, seconds: 1f);

        draw.Advance(1f / 60f);

        Assert.Equal(1, draw.ScreenCount);
        Assert.Equal(1, draw.TextCount);

        draw.Advance(2f);

        Assert.Equal(0, draw.ScreenCount);
        Assert.Equal(0, draw.TextCount);
    }

    [Fact]
    public void DisabledDrawsNothingAnywhere() {
        var draw = new DebugDraw { Enabled = false };

        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.White);
        draw.Capsule(Vector3.Zero, Vector3.UnitY, 1f, Color4.White);
        draw.ScreenText(Vector2.Zero, "hello", Color4.White);
        draw.Text(Vector3.Zero, "hello", Color4.White);

        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.ScreenCount);
        Assert.Equal(0, draw.TextCount);
    }
}
