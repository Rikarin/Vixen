// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>What the viewport draws, as geometry rather than as pixels.</summary>
public class GizmoGeometryTests {
    const int Height = 800;

    static (TransformGizmo Gizmo, StubTarget Target, EditorCamera Camera) One(GizmoMode mode) {
        var target = new StubTarget();
        var gizmo = new TransformGizmo { Mode = mode };

        gizmo.Attach([target]);

        return (gizmo, target, new EditorCamera { Distance = 10f });
    }

    [Fact]
    public void Nothing_selected_draws_nothing() {
        List<LineVertex> into = [];

        Assert.Equal(0, GizmoGeometry.Build(new TransformGizmo(), new EditorCamera(), Height, into));
        Assert.Empty(into);
    }

    [Fact]
    public void The_mode_decides_what_is_drawn() {
        List<LineVertex> translate = [];
        List<LineVertex> rotate = [];

        var (moving, _, camera) = One(GizmoMode.Translate);
        var (turning, _, _) = One(GizmoMode.Rotate);

        GizmoGeometry.Build(moving, camera, Height, translate);
        GizmoGeometry.Build(turning, camera, Height, rotate);

        Assert.NotEmpty(translate);

        // Three rings of thirty-two segments each is a lot more than three arms and their heads, so
        // this also says the two paths are not quietly the same one.
        Assert.Equal(3 * GizmoGeometry.RingSegments * 2, rotate.Count);
    }

    [Fact]
    public void Every_segment_is_a_pair() {
        List<LineVertex> into = [];
        var (gizmo, _, camera) = One(GizmoMode.Translate);

        GizmoGeometry.Build(gizmo, camera, Height, into);

        // The topology pairs them. An odd count is a segment joined to whatever came next, which
        // draws a line across the viewport from the last arm to the first thing after it.
        Assert.Equal(0, into.Count % 2);
    }

    [Fact]
    public void The_handles_are_the_size_the_hit_test_uses() {
        List<LineVertex> into = [];
        var (gizmo, target, camera) = One(GizmoMode.Translate);

        target.Position = Vector3.Zero;
        GizmoGeometry.Build(gizmo, camera, Height, into);

        var expected = gizmo.WorldPerPixel(camera, Height) * gizmo.HandleLength;
        var longest = into.Max(vertex => vertex.Position.Length());

        // Drawn from the same call the hit test uses, so an arm that looks grabbable is. A gizmo
        // drawn larger than it is tested has a few dead pixels at the end of every arm.
        Assert.Equal(expected, longest, 3);
    }

    [Fact]
    public void The_hovered_arm_is_a_different_colour() {
        List<LineVertex> plain = [];
        List<LineVertex> hovered = [];

        var (gizmo, _, camera) = One(GizmoMode.Translate);
        GizmoGeometry.Build(gizmo, camera, Height, plain);

        gizmo.Hovered = GizmoHandle.AxisX;
        GizmoGeometry.Build(gizmo, camera, Height, hovered);

        Assert.Equal(plain.Count, hovered.Count);
        Assert.NotEqual(plain[0].Colour, hovered[0].Colour);
    }

    [Fact]
    public void A_drag_keeps_the_arm_highlighted_even_when_the_pointer_has_left_it() {
        List<LineVertex> into = [];
        var (gizmo, _, camera) = One(GizmoMode.Translate);

        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(new Vector2(500f, 400f), 1000, Height), camera);
        gizmo.Hovered = GizmoHandle.None;

        GizmoGeometry.Build(gizmo, camera, Height, into);

        // The pointer leaves the arm within the first few pixels of any drag; an arm that stopped
        // being highlighted then looks like the gizmo let go.
        Assert.Equal(GizmoGeometry.AxisColour(0, highlighted: true), into[0].Colour);
    }

    [Fact]
    public void The_axes_are_the_three_colours_everybody_expects() {
        // Red, green, blue for x, y, z — the convention the corner axis cross already uses.
        Assert.True(GizmoGeometry.AxisColour(0).R > GizmoGeometry.AxisColour(0).B);
        Assert.True(GizmoGeometry.AxisColour(1).G > GizmoGeometry.AxisColour(1).R);
        Assert.True(GizmoGeometry.AxisColour(2).B > GizmoGeometry.AxisColour(2).R);
    }
}
