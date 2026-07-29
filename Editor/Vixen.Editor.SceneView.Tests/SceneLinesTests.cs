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
    public void Both_the_scale_and_the_translate_gizmo_draw_a_solid_middle_box() {
        var (scale, _, camera) = One(GizmoMode.Scale);
        var (translate, _, _) = One(GizmoMode.Translate);
        var (rotate, _, _) = One(GizmoMode.Rotate);

        var pixel = scale.WorldPerPixel(camera, Height);

        // One box, two meanings: uniform scale where there is a scale to do, and a drag in the view
        // plane where there is not — and in both cases it is the thing the arms cannot do. Neither
        // was drawn, and translate did not offer one at all, so the middle of a translate gizmo
        // answered with whichever arm the loop reached first.
        Assert.True(Middle(Solid(scale, camera).Vertices, pixel * scale.CentreRadius));
        Assert.True(Middle(Solid(translate, camera).Vertices, pixel * translate.CentreRadius));

        // Rotate's middle is the screen-facing ring's business, and a box inside three rings is a
        // fourth thing to aim at in the one place there is no room for it.
        Assert.Empty(Solid(rotate, camera).Vertices);
    }

    [Fact]
    public void The_middle_box_is_a_cube_and_fits_inside_the_circle_that_grabs_it() {
        var (gizmo, _, camera) = One(GizmoMode.Scale);
        var (vertices, _, _) = Solid(gizmo, camera);

        var radius = gizmo.WorldPerPixel(camera, Height) * gizmo.CentreRadius;
        var near = vertices.Where(vertex => vertex.Position.Length() < radius * 1.5f).ToArray();

        // ⚠ Three dimensions, not two, and that is the whole change: a flat square held square to the
        // camera is a sticker on the front of a solid object, and it was flat because a *square* on
        // the object's own axes is one you have to orbit to see square. A cube reads as a cube from
        // every angle.
        Assert.Contains(near, vertex => MathF.Abs(vertex.Position.X) > 1e-4f);
        Assert.Contains(near, vertex => MathF.Abs(vertex.Position.Y) > 1e-4f);
        Assert.Contains(near, vertex => MathF.Abs(vertex.Position.Z) > 1e-4f);

        // ⚠ And every corner of it is inside the circle `HitTest` answers within. The old square's
        // half-side *was* that radius, so its four corners stuck out to √2 × it and did not answer
        // clicks — the same failure `Tolerance` exists to prevent on the arms, and it fails the same
        // way: at the edges of a handle, which reads as the tool being unreliable.
        Assert.All(near, vertex => Assert.True(vertex.Position.Length() <= radius + 1e-4f));

        // Not so small that it is a dot: the corners should reach the circle rather than hide well
        // inside it.
        Assert.Contains(near, vertex => vertex.Position.Length() > radius * 0.95f);
    }

    /// <summary>Whether a solid list holds a box about the origin reaching a given corner distance.</summary>
    static bool Middle(List<MeshVertex> vertices, float corner) =>
        vertices.Any(vertex => MathF.Abs(vertex.Position.Length() - corner) < corner * 0.05f);

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

    /// <summary>Builds a gizmo's solid parts, and says how big its arms are.</summary>
    static (List<MeshVertex> Vertices, List<uint> Triangles, float Scale) Solid(
        TransformGizmo gizmo,
        EditorCamera camera
    ) {
        List<MeshVertex> vertices = [];
        List<uint> triangles = [];

        GizmoGeometry.BuildSolid(gizmo, camera, Height, vertices, triangles);

        return (vertices, triangles, gizmo.WorldPerPixel(camera, Height) * gizmo.HandleLength);
    }

    [Fact]
    public void The_arm_heads_are_solid_triangles_and_the_shafts_are_still_lines() {
        List<LineVertex> wire = [];

        var (gizmo, _, camera) = One(GizmoMode.Translate);
        var (vertices, triangles, _) = Solid(gizmo, camera);

        GizmoGeometry.Build(gizmo, camera, Height, wire);

        // ⚠ An outlined arrowhead is four ribs and a square: from the one angle it was built for it
        // reads as an arrow, and from every other it is four unrelated lines crossing near the end of
        // a shaft. It is also the part people aim at — the head is the target and the shaft only says
        // which way — so it was exactly the wrong part to draw as a hint.
        Assert.NotEmpty(vertices);
        Assert.NotEmpty(triangles);
        Assert.Equal(0, triangles.Count % 3);

        // Two arms on screen looking down z, and nothing but their shafts, the plane quads and the
        // middle box left in the wire list.
        Assert.NotEmpty(wire);
    }

    [Fact]
    public void A_head_sits_on_the_tip_of_its_arm() {
        var (gizmo, _, camera) = One(GizmoMode.Translate);
        var (vertices, _, scale) = Solid(gizmo, camera);

        var tip = vertices.Max(vertex => vertex.Position.X);
        var back = vertices.Where(vertex => vertex.Position.X > scale * 0.5f).Min(vertex => vertex.Position.X);

        // The tip lands exactly where the shaft ends, and the base a head's length behind it. A cone
        // centred on the arm's end instead would bury half of itself in the shaft and leave the arm
        // looking half a head short.
        Assert.Equal(scale, tip, 4);
        Assert.Equal(scale * (1f - gizmo.HeadLength), back, 4);
    }

    [Fact]
    public void Every_index_names_a_vertex_of_the_head_it_belongs_to() {
        var (gizmo, _, camera) = One(GizmoMode.Translate);
        var (vertices, triangles, _) = Solid(gizmo, camera);

        // ⚠ `MeshRenderer` deliberately does not offset indices — a caller building a frame knows
        // where each mesh began and it does not — so an unoffset index names another head's vertex,
        // which draws a triangle stretched between two arms.
        Assert.All(triangles, index => Assert.True(index < (uint) vertices.Count));

        var x = vertices.Where(vertex => MathF.Abs(vertex.Position.Y) < 1e-3f).ToArray();

        Assert.NotEmpty(x);
        Assert.All(vertices, vertex => Assert.Equal(1f, vertex.Normal.Length(), 3));
    }

    [Fact]
    public void An_arm_pointing_at_the_eye_has_no_head_either() {
        var (gizmo, _, camera) = One(GizmoMode.Translate);
        var (vertices, _, scale) = Solid(gizmo, camera);

        // Looking down z, so the z arm is a dot and is neither drawn nor grabbable — and a head left
        // behind on it would be a solid lump over the middle of the gizmo, hiding the handle that
        // does answer there.
        Assert.False(gizmo.IsAxisVisible(Vector3.UnitZ, camera));
        Assert.DoesNotContain(vertices, vertex => MathF.Abs(vertex.Position.Z) > scale * 0.5f);
    }

    [Fact]
    public void Scale_gets_cubes_and_rotate_gets_nothing() {
        var (scale, _, camera) = One(GizmoMode.Scale);
        var (rotate, _, _) = One(GizmoMode.Rotate);
        var (translate, _, _) = One(GizmoMode.Translate);

        var cubes = Solid(scale, camera);
        var cones = Solid(translate, camera);

        // A cube is eight corners' worth of faces and a cone is a fan of twelve, so the two modes
        // cannot have quietly become the same shape.
        Assert.NotEmpty(cubes.Vertices);
        Assert.NotEqual(cones.Vertices.Count, cubes.Vertices.Count);

        // A rotate gizmo is rings. There is no arm to put a head on, and a solid lump inside three
        // rings would be a fourth thing to aim at in the one place there is no room for it.
        Assert.Empty(Solid(rotate, camera).Vertices);
    }

    [Fact]
    public void A_hovered_arm_s_head_changes_colour_with_its_shaft() {
        var (gizmo, _, camera) = One(GizmoMode.Translate);
        var plain = Solid(gizmo, camera);

        gizmo.Hovered = GizmoHandle.AxisX;

        var hovered = Solid(gizmo, camera);

        Assert.Equal(plain.Vertices.Count, hovered.Vertices.Count);
        Assert.Equal(GizmoGeometry.AxisColour(0, highlighted: true), hovered.Vertices[0].Colour);
        Assert.NotEqual(plain.Vertices[0].Colour, hovered.Vertices[0].Colour);
    }

    [Fact]
    public void Nothing_selected_builds_no_solid_geometry() {
        List<MeshVertex> vertices = [];
        List<uint> triangles = [];

        Assert.Equal(0, GizmoGeometry.BuildSolid(new TransformGizmo(), new EditorCamera(), Height, vertices, triangles));
        Assert.Empty(vertices);
        Assert.Empty(triangles);
    }

    [Fact]
    public void The_axes_are_the_three_colours_everybody_expects() {
        // Red, green, blue for x, y, z — the convention the corner axis cross already uses.
        Assert.True(GizmoGeometry.AxisColour(0).R > GizmoGeometry.AxisColour(0).B);
        Assert.True(GizmoGeometry.AxisColour(1).G > GizmoGeometry.AxisColour(1).R);
        Assert.True(GizmoGeometry.AxisColour(2).B > GizmoGeometry.AxisColour(2).R);
    }
}
