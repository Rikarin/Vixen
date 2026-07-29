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

        // Four rings — the three axes and the screen-facing one — of thirty-two segments each, every
        // segment drawn as many parallel strokes as the gizmo is pixels thick. A lot more than three
        // arms and their heads, so this also says the two paths are not quietly the same one.
        var strokes = (int) MathF.Round(turning.Thickness);

        Assert.Equal(4 * GizmoGeometry.RingSegments * strokes * 2, rotate.Count);
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

        // ⚠ The far end of the x shaft — `into[1]` — rather than whichever vertex is furthest out.
        // Thickening offsets each stroke across its own segment, so the outline of the arrow head
        // leans a pixel past the tip, and taking the maximum would assert the thickness instead of
        // the length.
        var tip = into[1].Position;

        // Drawn from the same call the hit test uses, so an arm that looks grabbable is. A gizmo
        // drawn larger than it is tested has a few dead pixels at the end of every arm.
        Assert.Equal(expected, tip.X, 3);
    }

    [Fact]
    public void A_thicker_gizmo_is_more_lines_and_the_same_size() {
        List<LineVertex> thin = [];
        List<LineVertex> thick = [];

        var (gizmo, _, camera) = One(GizmoMode.Translate);

        gizmo.Thickness = 1f;
        GizmoGeometry.Build(gizmo, camera, Height, thin);

        gizmo.Thickness = 6f;
        GizmoGeometry.Build(gizmo, camera, Height, thick);

        // The renderer draws one-pixel lines and will not draw anything else — see LineRenderer — so
        // a thick arm is six thin ones. Six times the vertices and not one segment longer.
        Assert.Equal(thin.Count * 6, thick.Count);
        Assert.Equal(thin[1].Position.X, thick[1].Position.X, 4);
    }

    [Fact]
    public void The_strokes_of_an_arm_are_a_pixel_apart_across_it_and_not_along_it() {
        List<LineVertex> into = [];
        var (gizmo, _, camera) = One(GizmoMode.Translate);

        gizmo.Thickness = 5f;
        GizmoGeometry.Build(gizmo, camera, Height, into);

        var pixel = gizmo.WorldPerPixel(camera, Height);

        // The first five segments are the x arm's five strokes. They run parallel to the axis and
        // are spread across it, which is what makes the arm look five pixels wide from every angle
        // rather than one pixel wide from the angle it was built for.
        var origins = into.Where((_, index) => index % 2 == 0).Take(5).Select(vertex => vertex.Position).ToArray();

        Assert.All(origins, position => Assert.Equal(0f, position.X, 5));

        var spread = origins.Max(position => position.Length());

        Assert.Equal(2f * pixel, spread, 5);
    }

    [Fact]
    public void The_rotate_gizmo_draws_the_screen_ring_the_hit_test_answers_for() {
        List<LineVertex> into = [];
        var (gizmo, _, camera) = One(GizmoMode.Rotate);

        GizmoGeometry.Build(gizmo, camera, Height, into);

        var pixel = gizmo.WorldPerPixel(camera, Height);
        var expected = pixel * gizmo.HandleLength * gizmo.ScreenRingScale;
        var outermost = into.Max(vertex => vertex.Position.Length());

        // `HitTest` has always answered `Screen` for a circle out here and nothing drew it, so a
        // rotate gizmo had a band of pixels outside its three rings that turned the selection about
        // the view axis with no picture saying it would. Within the thickness, because the outermost
        // stroke of a thick ring is half of it further out than the ring itself.
        Assert.True(
            MathF.Abs(outermost - expected) <= pixel * gizmo.Thickness,
            $"the outermost ring is at {outermost}, and the screen ring should be at {expected}"
        );
    }

    [Fact]
    public void The_scale_gizmo_draws_the_middle_box_and_the_translate_one_does_not() {
        List<LineVertex> resizing = [];
        List<LineVertex> moving = [];

        var (scale, _, camera) = One(GizmoMode.Scale);
        var (translate, _, _) = One(GizmoMode.Translate);

        GizmoGeometry.Build(scale, camera, Height, resizing);
        GizmoGeometry.Build(translate, camera, Height, moving);

        var pixel = scale.WorldPerPixel(camera, Height);

        // The corners of a square whose half-side is the radius `HitTest` answers `Uniform` within.
        // The most-used handle of a scale gizmo, and it was invisible: a click in the middle scaled
        // everything at once and only somebody who tried it would know.
        var corner = MathF.Sqrt(2f) * pixel * scale.CentreRadius;
        var tolerance = pixel * scale.Thickness;

        Assert.Contains(resizing, vertex => MathF.Abs(vertex.Position.Length() - corner) <= tolerance);
        Assert.DoesNotContain(moving, vertex => MathF.Abs(vertex.Position.Length() - corner) <= tolerance);
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
