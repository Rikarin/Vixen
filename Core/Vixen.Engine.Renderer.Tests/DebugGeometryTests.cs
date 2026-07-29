// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Xunit;

namespace Vixen.Engine.Renderer.Tests;

/// <summary>The arithmetic between an accumulator and a vertex buffer.</summary>
/// <remarks>
///     Everything here runs without a device, which is the reason <c>DebugGeometry</c> is a class of
///     its own rather than three private methods of the renderer: whether a label faces the camera
///     and whether a screen line lands where it was asked for are questions about numbers, and a
///     picture is a slow and indirect way to ask them. What a picture is needed for — whether the
///     pipeline agrees with the vertex layout — the golden suite already covers for lines.
/// </remarks>
public sealed class DebugGeometryTests {
    /// <summary>A camera at the origin looking down −Z, which is the identity view.</summary>
    static readonly DebugView Facing = new(Vector3.UnitX, Vector3.UnitY);

    [Fact]
    public void EachLineBecomesTwoVertices() {
        var draw = new DebugDraw();
        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red);
        draw.Line(Vector3.UnitY, Vector3.UnitZ, Color4.Green);

        var geometry = new DebugGeometry();
        geometry.Build(draw, Facing);

        Assert.Equal(4, geometry.World.Length);
        Assert.Equal(Vector3.Zero, geometry.World[0].Position);
        Assert.Equal(Vector3.UnitX, geometry.World[1].Position);
        Assert.Equal(Color4.Green, geometry.World[3].Colour);
    }

    /// <summary>
    ///     The colour is per vertex and both ends of a segment carry the line's, which is what makes
    ///     a fading grid line possible at all — and what a builder that wrote it once would break.
    /// </summary>
    [Fact]
    public void BothEndsCarryTheColour() {
        var draw = new DebugDraw();
        draw.Line(Vector3.Zero, Vector3.One, Color4.Blue);

        var geometry = new DebugGeometry();
        geometry.Build(draw, Facing);

        Assert.Equal(Color4.Blue, geometry.World[0].Colour);
        Assert.Equal(Color4.Blue, geometry.World[1].Colour);
    }

    [Fact]
    public void ScreenLinesGoInTheOtherBuffer() {
        var draw = new DebugDraw();
        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red);
        draw.ScreenLine(new(10f, 20f), new(30f, 40f), Color4.White);

        var geometry = new DebugGeometry();
        geometry.Build(draw, Facing);

        Assert.Equal(2, geometry.World.Length);
        Assert.Equal(2, geometry.Screen.Length);

        // Pixels, unchanged and un-projected: the projection is a matrix the renderer pushes, so a
        // builder that pre-transformed here would be projecting twice.
        Assert.Equal(new Vector3(10f, 20f, 0f), geometry.Screen[0].Position);
        Assert.Equal(new Vector3(30f, 40f, 0f), geometry.Screen[1].Position);
    }

    /// <summary>A build is a rebuild: nothing survives from the frame before.</summary>
    [Fact]
    public void BuildingTwiceDoesNotAccumulate() {
        var draw = new DebugDraw();
        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red);

        var geometry = new DebugGeometry();
        geometry.Build(draw, Facing);
        geometry.Build(draw, Facing);

        Assert.Equal(2, geometry.World.Length);
    }

    [Fact]
    public void ALabelBecomesStrokesInTheWorldBuffer() {
        var draw = new DebugDraw();
        draw.Text(Vector3.Zero, "A", Color4.White, size: 1f);

        var geometry = new DebugGeometry();
        geometry.Build(draw, Facing);

        Assert.Equal(1, geometry.LabelCount);
        Assert.Equal(DebugFont.SegmentCount("A") * 2, geometry.World.Length);
    }

    /// <summary>
    ///     Text space runs down and the camera's up axis runs up, so a label's first row has to end
    ///     up <i>above</i> its last. Adding the vertical term instead of subtracting it draws every
    ///     label upside down — which reads as a projection bug and is not one.
    /// </summary>
    [Fact]
    public void ALabelIsNotUpsideDown() {
        var draw = new DebugDraw();
        draw.Text(Vector3.Zero, "I", Color4.White, size: 1f);

        var geometry = new DebugGeometry();
        geometry.Build(draw, Facing);

        var highest = float.MinValue;
        var lowest = float.MaxValue;

        foreach (var vertex in geometry.World) {
            highest = MathF.Max(highest, vertex.Position.Y);
            lowest = MathF.Min(lowest, vertex.Position.Y);
        }

        // Centred on the point it was asked for, and a capital's worth tall.
        Assert.True(highest > 0f, "the top of the glyph is not above the anchor");
        Assert.True(lowest < 0f, "the bottom of the glyph is not below the anchor");
        Assert.Equal(1f, highest - lowest, 3);
    }

    /// <summary>A label lies in the camera's plane, whichever way the camera is turned.</summary>
    [Fact]
    public void ALabelFacesTheCamera() {
        var draw = new DebugDraw();
        draw.Text(Vector3.Zero, "H", Color4.White, size: 1f);

        // Looking down +X instead of −Z: the label's plane has to turn with it, so no vertex may
        // have an X component.
        var view = new DebugView(Vector3.UnitZ, Vector3.UnitY);

        var geometry = new DebugGeometry();
        geometry.Build(draw, view);

        Assert.NotEmpty(geometry.World.ToArray());

        foreach (var vertex in geometry.World) {
            Assert.Equal(0f, vertex.Position.X, 5);
        }
    }

    /// <summary>
    ///     The basis comes out of the view matrix's columns. Rows happen to be right for a camera at
    ///     the origin looking down −Z, which is why this one is somewhere else looking elsewhere.
    /// </summary>
    [Fact]
    public void TheBasisComesFromTheViewMatrixColumns() {
        var eye = new Vector3(4f, 3f, -7f);
        var view = Matrix4x4.LookAt(eye, new(1f, 2f, 3f), Vector3.UnitY);
        var basis = DebugView.FromView(view);

        // Both axes are unit length, perpendicular, and perpendicular to the direction of travel.
        Assert.Equal(1f, basis.Right.Length(), 4);
        Assert.Equal(1f, basis.Up.Length(), 4);
        Assert.Equal(0f, Vector3.Dot(basis.Right, basis.Up), 4);

        var forward = Vector3.Normalize(new Vector3(1f, 2f, 3f) - eye);

        Assert.Equal(0f, Vector3.Dot(basis.Right, forward), 4);
        Assert.Equal(0f, Vector3.Dot(basis.Up, forward), 4);
    }

    [Fact]
    public void ClearEmptiesBothBuffers() {
        var draw = new DebugDraw();
        draw.Line(Vector3.Zero, Vector3.UnitX, Color4.Red);
        draw.ScreenLine(Vector2.Zero, Vector2.One, Color4.White);

        var geometry = new DebugGeometry();
        geometry.Build(draw, Facing);
        geometry.Clear();

        Assert.Equal(0, geometry.World.Length);
        Assert.Equal(0, geometry.Screen.Length);
        Assert.Equal(0, geometry.LabelCount);
    }
}
