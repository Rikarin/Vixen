// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class DebugDrawTests {
    [Fact]
    public void ALineLastsExactlyOneFrameByDefault() {
        var draw = new DebugDraw();

        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red);
        Assert.Equal(1, draw.Count);

        draw.Advance(1f / 60f);
        Assert.Equal(0, draw.Count);
    }

    [Fact]
    public void ATimedLineSurvivesUntilItsTimeIsUp() {
        var draw = new DebugDraw();

        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red, seconds: 0.1f);

        for (var frame = 0; frame < 5; frame++) {
            draw.Advance(1f / 60f);
        }

        Assert.Equal(1, draw.Count);

        draw.Advance(1f);
        Assert.Equal(0, draw.Count);
    }

    /// <summary>
    ///     Timed and one-frame lines are interleaved in one list, and ageing removes by swap-back.
    ///     Getting the index bookkeeping wrong there skips entries, which looks like a line that
    ///     lingers for ever.
    /// </summary>
    [Fact]
    public void AgeingAMixOfLifetimesKeepsExactlyTheSurvivors() {
        var draw = new DebugDraw();

        for (var index = 0; index < 20; index++) {
            draw.Line(Vector3.Zero, new(index, 0, 0), Color4.White, index % 2 == 0 ? 0f : 10f);
        }

        draw.Advance(1f / 60f);

        Assert.Equal(10, draw.Count);

        foreach (var line in draw.Lines) {
            Assert.True(line.Remaining > 0f);
            Assert.Equal(1, (int)line.To.X % 2);
        }
    }

    [Fact]
    public void ABoxIsItsTwelveEdges() {
        var draw = new DebugDraw();

        draw.Box(new(new(-1, -1, -1), new(1, 1, 1)), Color4.Green);

        Assert.Equal(12, draw.Count);
    }

    [Fact]
    public void AxesAreRedGreenAndBlueInThatOrder() {
        var draw = new DebugDraw();

        draw.Axes(Matrix4x4.FromTranslation(new(5, 0, 0)), length: 2f);

        Assert.Equal(3, draw.Count);
        Assert.Equal(Color4.Red, draw.Lines[0].Colour);
        Assert.Equal(Color4.Green, draw.Lines[1].Colour);
        Assert.Equal(Color4.Blue, draw.Lines[2].Colour);
        Assert.Equal(new Vector3(7, 0, 0), draw.Lines[0].To);
    }

    [Fact]
    public void ASphereIsThreeRings() {
        var draw = new DebugDraw();

        draw.Sphere(new(Vector3.Zero, 1f), Color4.Blue);

        Assert.Equal(72, draw.Count);
    }

    [Fact]
    public void AnEmptySphereDrawsNothing() {
        var draw = new DebugDraw();

        draw.Sphere(new(Vector3.Zero, -1f), Color4.Blue);

        Assert.Equal(0, draw.Count);
    }

    [Fact]
    public void TurningItOffCostsNothingAndRecordsNothing() {
        var draw = new DebugDraw { Enabled = false };

        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red);
        draw.Box(new(Vector3.Zero, Vector3.One), Color4.Red);
        draw.Sphere(new(Vector3.Zero, 1f), Color4.Red);
        draw.Axes(Matrix4x4.Identity);

        Assert.Equal(0, draw.Count);
    }

    /// <summary>
    ///     Ageing in <c>PostRender</c> and nowhere else: a line asked for during a frame has to
    ///     survive until a renderer could have drained it.
    /// </summary>
    [Fact]
    public void TheSystemAgesTheGeometryOncePerFrame() {
        var draw = new DebugDraw();
        using var loop = new EngineLoop(registerDefaultSystems: false);
        loop.Add(new DebugDrawSystem(draw));

        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red, seconds: 0.02f);

        loop.Frame(TimeSpan.FromMilliseconds(16));
        Assert.Equal(1, draw.Count);

        loop.Frame(TimeSpan.FromMilliseconds(16));
        Assert.Equal(0, draw.Count);
    }
}
