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

        // Four rings — the three axes and the screen-facing one — of up to thirty-two segments each,
        // every segment drawn as many parallel strokes as the gizmo is pixels thick. A lot more than
        // three arms and their heads, so this also says the two paths are not quietly the same one.
        //
        // ⚠ A range rather than a count, and the range is the reason: the three axis rings are cut
        // to the half facing the camera, and which sample lands exactly on the horizon is a cosine
        // compared against zero. Asserting the count to the segment is asserting the sign of a float
        // that is 1e−8 either way.
        var strokes = (int) MathF.Round(turning.Thickness);
        var full = 4 * GizmoGeometry.RingSegments * strokes * 2;

        Assert.InRange(rotate.Count, full / 2, full - (strokes * 2));
    }

    [Fact]
    public void The_far_half_of_a_rotation_ring_is_not_drawn() {
        List<LineVertex> into = [];
        var (gizmo, _, camera) = One(GizmoMode.Rotate);

        GizmoGeometry.Build(gizmo, camera, Height, into);

        var radius = gizmo.WorldPerPixel(camera, Height) * gizmo.HandleLength;
        var slack = radius * 0.05f;

        // Nothing at ring radius sits on the far side of the gizmo. Three full circles about one
        // point cross each other twelve times, and every crossing is a click that lands on the back
        // of one ring while aiming at the front of another. The screen-facing ring is a whole circle
        // and is further out, so it is excluded by radius rather than by being special-cased.
        Assert.DoesNotContain(
            into,
            vertex => vertex.Position.Length() <= radius + slack && vertex.Position.Z < -slack
        );
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
        var start = pixel * gizmo.HandleLength * gizmo.ArmStart;

        // The first five segments are the x arm's five strokes. They run parallel to the axis and
        // are spread across it, which is what makes the arm look five pixels wide from every angle
        // rather than one pixel wide from the angle it was built for.
        var origins = into.Where((_, index) => index % 2 == 0).Take(5).Select(vertex => vertex.Position).ToArray();

        // ⚠ At `ArmStart` along the axis rather than at the origin. The arms are drawn from where the
        // hit test starts testing them, so the middle of the gizmo belongs to the centre handle in
        // the picture as well as in the arithmetic.
        Assert.All(origins, position => Assert.Equal(start, position.X, 5));

        var spread = origins.Max(position => MathF.Abs(position.Y));

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
    public void Both_the_scale_and_the_translate_gizmo_draw_the_middle_box() {
        List<LineVertex> resizing = [];
        List<LineVertex> moving = [];
        List<LineVertex> turning = [];

        var (scale, _, camera) = One(GizmoMode.Scale);
        var (translate, _, _) = One(GizmoMode.Translate);
        var (rotate, _, _) = One(GizmoMode.Rotate);

        GizmoGeometry.Build(scale, camera, Height, resizing);
        GizmoGeometry.Build(translate, camera, Height, moving);
        GizmoGeometry.Build(rotate, camera, Height, turning);

        var pixel = scale.WorldPerPixel(camera, Height);

        // The corners of a square whose half-side is the radius `HitTest` answers a middle handle
        // within. One square, two meanings: uniform scale where there is a scale to do, and a drag in
        // the view plane where there is not — and in both cases it is the thing the arms cannot do.
        // Neither was drawn, and translate did not offer one at all, so the middle of a translate
        // gizmo answered with whichever arm the loop reached first.
        var corner = MathF.Sqrt(2f) * pixel * scale.CentreRadius;
        var tolerance = pixel * scale.Thickness;

        Assert.Contains(resizing, vertex => MathF.Abs(vertex.Position.Length() - corner) <= tolerance);
        Assert.Contains(moving, vertex => MathF.Abs(vertex.Position.Length() - corner) <= tolerance);

        // Rotate's middle is the screen-facing ring's business, and a box inside three rings is a
        // fourth thing to aim at in the one place there is no room for it.
        Assert.DoesNotContain(turning, vertex => MathF.Abs(vertex.Position.Length() - corner) <= tolerance);
    }

    [Fact]
    public void An_arm_pointing_at_the_eye_is_not_drawn() {
        List<LineVertex> into = [];
        var (gizmo, _, camera) = One(GizmoMode.Translate);

        // Looking straight down z, so the z arm projects to a dot in the middle of the gizmo. Drawn,
        // it is a smudge over the other two; offered, it wins every click in the middle and then
        // drags along a line that has no direction on screen.
        GizmoGeometry.Build(gizmo, camera, Height, into);

        var scale = gizmo.WorldPerPixel(camera, Height) * gizmo.HandleLength;

        Assert.False(gizmo.IsAxisVisible(Vector3.UnitZ, camera));
        Assert.True(gizmo.IsAxisVisible(Vector3.UnitX, camera));

        Assert.DoesNotContain(into, vertex => MathF.Abs(vertex.Position.Z) > scale * 0.5f);
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
