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

        // Shafts and plane outlines for a translate gizmo.
        Assert.NotEmpty(translate);

        // ⚠ And nothing at all in the wire list for a rotate one. Its rings are tubes through
        // `BuildSolid`, and a polyline down the middle of a five-pixel tube is a hairline over a
        // solid shape — visible as a seam on the near side of every ring and as nothing on the far
        // side, which reads as the ring being drawn twice at slightly different radii.
        Assert.Empty(rotate);

        // Four tubes — the three axes and the screen-facing one — of up to thirty-two cross-sections
        // each, six sides to a cross-section. A lot more than three arms and their heads, so this
        // also says the two paths are not quietly the same one.
        var (vertices, triangles, _) = Solid(turning, camera);
        var full = 4 * (GizmoGeometry.RingSegments + 1) * GizmoGeometry.TubeSides;

        // ⚠ A range rather than a count, and the range is the reason: the three axis rings are cut to
        // the half facing the camera, and which sample lands exactly on the horizon is a cosine
        // compared against zero. Asserting the count to the cross-section is asserting the sign of a
        // float that is 1e−8 either way.
        Assert.InRange(vertices.Count, full / 2, full);
        Assert.Equal(0, triangles.Count % 3);
    }

    [Fact]
    public void The_far_half_of_a_rotation_ring_is_not_drawn() {
        var (gizmo, _, camera) = One(GizmoMode.Rotate);
        var (vertices, _, radius) = Solid(gizmo, camera);

        var slack = radius * 0.05f;

        // Nothing at ring radius sits on the far side of the gizmo. Three full circles about one
        // point cross each other twelve times, and every crossing is a click that lands on the back
        // of one ring while aiming at the front of another. The screen-facing ring is a whole circle
        // and is further out, so it is excluded by radius rather than by being special-cased.
        Assert.NotEmpty(vertices);

        Assert.DoesNotContain(
            vertices,
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

        // ⚠ The shaft stops half a head short of the arm's end and the head covers the rest of it.
        // A stroke is offset *across* its segment by a few pixels and a cone is only a few pixels
        // wide near its point, so a shaft drawn all the way to the tip shows up as a needle sticking
        // out of the arrowhead — three of them, at every camera angle.
        Assert.Equal(expected * (1f - (gizmo.HeadLength * 0.5f)), tip.X, 3);

        // What has to reach `HandleLength` is the handle, and the cone's point does. Drawn from the
        // same call the hit test uses, so an arm that looks grabbable is: a gizmo drawn longer than
        // it is tested has a few dead pixels at the end of every arm, and one drawn shorter has a few
        // that look dead and are not.
        var solid = Solid(gizmo, camera).Vertices;

        Assert.Equal(expected, solid.Max(vertex => vertex.Position.X), 3);
    }

    [Fact]
    public void The_strokes_of_an_arm_are_shaded_across_it_like_a_cylinder() {
        List<LineVertex> into = [];
        var (gizmo, _, camera) = One(GizmoMode.Translate);

        GizmoGeometry.Build(gizmo, camera, Height, into);

        var axis = GizmoGeometry.AxisColour(0);
        var strokes = into.Take((int) MathF.Round(gizmo.Thickness) * 2)
            .Where((_, index) => index % 2 == 0)
            .Select(vertex => vertex.Colour)
            .ToArray();

        Assert.Equal((int) MathF.Round(gizmo.Thickness), strokes.Length);

        // ⚠ Every stroke the axis colour scaled, never the axis colour tinted. Which stroke a pixel
        // is on is where round a cylinder it would be, so the shade is a lighting term and a lighting
        // term multiplies — a red arm that went pink towards its edge would be saying "hovered".
        foreach (var colour in strokes) {
            var shade = colour.R / axis.R;

            Assert.InRange(shade, GizmoGeometry.Ambient - 1e-3f, 1f + 1e-3f);
            Assert.Equal(axis.G * shade, colour.G, 4);
            Assert.Equal(axis.B * shade, colour.B, 4);
        }

        // And they are not all the same shade, which is the whole point: a flat ribbon of six
        // one-pixel lines reads as a painted stripe, and the same six lit as the surface of a
        // cylinder read as a shaft with the same solidity as the cone on the end of it.
        Assert.True(
            strokes.Max(colour => colour.R) - strokes.Min(colour => colour.R) > 0.1f * axis.R,
            "the strokes of an arm are all one shade, so the shaft is drawn flat"
        );
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
        var (gizmo, _, camera) = One(GizmoMode.Rotate);
        var (vertices, _, _) = Solid(gizmo, camera);

        var pixel = gizmo.WorldPerPixel(camera, Height);
        var expected = pixel * gizmo.HandleLength * gizmo.ScreenRingScale;
        var outermost = vertices.Max(vertex => vertex.Position.Length());

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
    public void Both_the_scale_and_the_translate_gizmo_draw_a_solid_middle_handle() {
        var (scale, _, camera) = One(GizmoMode.Scale);
        var (translate, _, _) = One(GizmoMode.Translate);
        var (rotate, _, _) = One(GizmoMode.Rotate);

        var pixel = scale.WorldPerPixel(camera, Height);


        // One handle, two meanings: uniform scale where there is a scale to do, and a drag in the
        // view plane where there is not — and in both cases it is the thing the arms cannot do.
        // Neither was drawn, and translate did not offer one at all, so the middle of a translate
        // gizmo answered with whichever arm the loop reached first.
        Assert.True(Middle(Solid(scale, camera).Vertices, Ball(scale, pixel)));
        Assert.True(Middle(Solid(translate, camera).Vertices, Ball(translate, pixel)));

        // Rotate's middle is the screen-facing ring's business, and a ball inside three rings is a
        // fourth thing to aim at in the one place there is no room for it. Its solid list is four
        // tubes and nothing at the centre radius.
        Assert.False(Middle(Solid(rotate, camera).Vertices, Ball(rotate, pixel)));
    }

    [Fact]
    public void The_middle_handle_is_a_ball_the_size_of_the_circle_that_grabs_it() {
        var (gizmo, _, camera) = One(GizmoMode.Scale);
        var (vertices, _, _) = Solid(gizmo, camera);

        var radius = gizmo.WorldPerPixel(camera, Height) * gizmo.CentreRadius;
        var near = vertices.Where(vertex => vertex.Position.Length() < radius * 1.5f).ToArray();

        Assert.NotEmpty(near);

        // ⚠ Three dimensions, not two, and that is what the flat square held square to the camera
        // was not: a sticker on the front of a solid object, flat because a *square* on the object's
        // own axes is one you have to orbit to see square.
        Assert.Contains(near, vertex => MathF.Abs(vertex.Position.X) > 1e-4f);
        Assert.Contains(near, vertex => MathF.Abs(vertex.Position.Y) > 1e-4f);
        Assert.Contains(near, vertex => MathF.Abs(vertex.Position.Z) > 1e-4f);

        // ⚠ Every vertex the same distance out, and that distance is exactly the radius `HitTest`
        // answers within — which no shape before it could be. The square's half-side *was* the
        // radius, so its four corners stuck out to √2 × it and did not answer clicks; the cube that
        // followed had to be divided by √3 to pull its corners back in, which left it drawn at a bit
        // over half the region it stood for. A sphere's silhouette *is* the circle: no ring of pixels
        // that looks like the handle and is not, and none that answers for it and looks like nothing.
        Assert.All(near, vertex => Assert.Equal(radius, vertex.Position.Length(), 4));
    }

    [Fact]
    public void The_arms_run_through_the_middle_rather_than_stopping_at_it() {
        var (gizmo, _, camera) = One(GizmoMode.Translate);
        List<LineVertex> into = [];

        GizmoGeometry.Build(gizmo, camera, Height, into);

        // ⚠ `ArmStart` is zero: the shafts begin at the origin and the ball is drawn under all three,
        // so the gizmo reads as axes crossing at a marked point rather than as handles arranged
        // around an obstacle. What makes that safe is that `HitTest` takes the centre circle before
        // it looks at an arm at all — so nothing is drawn over a region that answers for something
        // else, which is the oldest gizmo complaint there is.
        Assert.Equal(0f, into[0].Position.X, 4);

        // And the drawn arm is still the tested arm: same fraction, same call.
        Assert.Equal(0f, gizmo.ArmStart);
    }

    [Fact]
    public void The_middle_wins_a_click_the_arms_are_drawn_across() {
        var (gizmo, _) = Grabbed();
        var camera = new EditorCamera { Distance = 10f };
        var centre = Screen(camera, Vector3.Zero);

        // A point well inside the ball and squarely on the x arm, which is now drawn right through
        // it. The centre check runs first, so what answers is the handle the ball stands for.
        var on = centre + new Vector2(gizmo.CentreRadius * 0.5f, 0f);

        Assert.Equal(GizmoHandle.Screen, gizmo.HitTest(on, camera, Width, Height));

        // Just outside it, the same arm answers for itself.
        var past = centre + new Vector2(gizmo.CentreRadius + 8f, 0f);

        Assert.Equal(GizmoHandle.AxisX, gizmo.HitTest(past, camera, Width, Height));
    }

    const int Width = 1000;

    static (TransformGizmo Gizmo, StubTarget Target) Grabbed() {
        var target = new StubTarget();
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Attach([target]);

        return (gizmo, target);
    }

    static Vector2 Screen(EditorCamera camera, Vector3 world) {
        var projected = camera.Project(world, Width, Height);

        return new(projected.X, projected.Y);
    }

    /// <summary>How far out the middle handle's surface is.</summary>
    static float Ball(TransformGizmo gizmo, float pixel) => pixel * gizmo.CentreRadius;

    /// <summary>Whether a solid list holds a shell about the origin at a given radius.</summary>
    static bool Middle(IEnumerable<MeshVertex> vertices, float radius) =>
        vertices.Any(vertex => MathF.Abs(vertex.Position.Length() - radius) < radius * 0.05f);

    /// <summary>
    ///     ⚠ <b>You could see inside the middle box, and the cause is that the handles are drawn with
    ///     no depth test and no culling.</b> Nothing decided which of a solid shape's own faces was in
    ///     front of which, so the answer was "whichever was appended last" — which for a cube is the
    ///     far side, drawn over the near one. The heads had the same fault and hid it better, because
    ///     a cone's far half is mostly behind its near half from any angle.
    ///     <para>
    ///         The cull is done here, against the vertex normals, rather than by the rasteriser:
    ///         which winding the hardware calls front depends on the API, on the projection's
    ///         handedness and on whether the viewport flips Y, which is why every pipeline in the
    ///         engine is two-sided in the first place.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(GizmoMode.Scale)]
    [InlineData(GizmoMode.Translate)]
    public void No_solid_handle_shows_a_face_pointing_away_from_the_camera(GizmoMode mode) {
        var (gizmo, _, camera) = One(mode);
        var (vertices, triangles, _) = Solid(gizmo, camera);

        Assert.NotEmpty(triangles);

        for (var index = 0; index + 2 < triangles.Count; index += 3) {
            var a = vertices[(int) triangles[index]];
            var b = vertices[(int) triangles[index + 1]];
            var c = vertices[(int) triangles[index + 2]];

            // ⚠ The plane quads are exempt and have to be: a translucent square that vanished when
            // you orbited past its plane would be a handle that is grabbable and invisible from one
            // side. Their alpha is what tells them apart from a head — `PlaneFillAlpha` is the only
            // thing in this list that is not opaque.
            if (a.Colour.A < 1f) {
                continue;
            }

            var centroid = (a.Position + b.Position + c.Position) / 3f;
            var facing = Vector3.Dot(a.Normal + b.Normal + c.Normal, GizmoGeometry.TowardsEye(camera, centroid));

            Assert.True(facing > 0f, $"a solid handle keeps a triangle at {centroid} whose face points away");
        }
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
        //
        // ⚠ Proportional to the highlight colour rather than equal to it. The strokes of an arm are
        // shaded across its width as though it were a cylinder — see `GizmoGeometry.Segment` — so
        // what says "highlighted" is the hue and not the value.
        var highlight = GizmoGeometry.AxisColour(0, highlighted: true);
        var drawn = into[0].Colour;
        var shade = drawn.R / highlight.R;

        Assert.InRange(shade, GizmoGeometry.Ambient - 1e-3f, 1f + 1e-3f);
        Assert.Equal(highlight.G * shade, drawn.G, 4);
        Assert.Equal(highlight.B * shade, drawn.B, 4);
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

    /// <summary>The solid vertices that belong to an arm's head rather than to a plane quad.</summary>
    /// <remarks>
    ///     ⚠ <b>A filter, because the solid list is no longer only heads.</b> The plane handles are
    ///     filled quads in the same list and they sit at <c>PlaneOffset</c> along <i>two</i> axes at
    ///     once, so anything that took the list's first vertex or its extreme along one axis would be
    ///     measuring a quad's corner and calling it an arrowhead.
    /// </remarks>
    static MeshVertex[] Head(List<MeshVertex> vertices, int axis, float scale) =>
        [
            .. vertices.Where(vertex => {
                var position = vertex.Position;

                var along = axis switch {
                    0 => position.X,
                    1 => position.Y,
                    _ => position.Z
                };

                var across = (position - (Axis(axis) * along)).Length();

                return along > scale * 0.5f && across < scale * 0.2f;
            })
        ];

    static Vector3 Axis(int index) =>
        index switch {
            0 => Vector3.UnitX,
            1 => Vector3.UnitY,
            _ => Vector3.UnitZ
        };

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

        var head = Head(vertices, 0, scale);

        var tip = head.Max(vertex => vertex.Position.X);
        var back = head.Min(vertex => vertex.Position.X);

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
    public void Scale_gets_cubes_and_rotate_gets_tubes() {
        var (scale, _, camera) = One(GizmoMode.Scale);
        var (rotate, _, _) = One(GizmoMode.Rotate);
        var (translate, _, _) = One(GizmoMode.Translate);

        var cubes = Solid(scale, camera);
        var cones = Solid(translate, camera);

        // A cube is eight corners' worth of faces and a cone is a fan of twelve, so the two modes
        // cannot have quietly become the same shape.
        Assert.NotEmpty(cubes.Vertices);
        Assert.NotEqual(cones.Vertices.Count, cubes.Vertices.Count);

        // ⚠ A scale gizmo has no plane handles, so its solid list is heads and the middle box and
        // nothing else. A translate one has three quads' worth of vertices on top of its heads — of
        // which one is drawn from this angle and two are edge-on and skipped.
        Assert.All(cubes.Vertices, vertex => Assert.Equal(1f, vertex.Colour.A));
        Assert.Contains(cones.Vertices, vertex => vertex.Colour.A < 1f);

        // A rotate gizmo is four tubes and nothing else: every vertex of it is out at ring radius,
        // give or take the tube's own thickness. Nothing is at the middle, where a solid lump would
        // be a fourth thing to aim at in the one place there is no room for it, and nothing is
        // between — which is what says the arms and the heads really are gone rather than merely
        // being somewhere else.
        var rings = Solid(rotate, camera);

        Assert.NotEmpty(rings.Vertices);

        Assert.All(
            rings.Vertices,
            vertex => Assert.InRange(vertex.Position.Length(), rings.Scale * 0.97f, rings.Scale * 1.2f)
        );
    }

    [Fact]
    public void A_hovered_arm_s_head_changes_colour_with_its_shaft() {
        var (gizmo, _, camera) = One(GizmoMode.Translate);
        var plain = Solid(gizmo, camera);

        gizmo.Hovered = GizmoHandle.AxisX;

        var hovered = Solid(gizmo, camera);

        // ⚠ Read off the head rather than off the list's first vertex: the plane quads are in the
        // same list and are appended in front of the heads, so vertex zero belongs to a quad.
        var before = Head(plain.Vertices, 0, plain.Scale);
        var after = Head(hovered.Vertices, 0, hovered.Scale);

        Assert.Equal(plain.Vertices.Count, hovered.Vertices.Count);
        Assert.NotEmpty(after);
        Assert.All(after, vertex => Assert.Equal(GizmoGeometry.AxisColour(0, highlighted: true), vertex.Colour));
        Assert.NotEqual(before[0].Colour, after[0].Colour);
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
    public void A_handle_under_the_pointer_goes_darker_rather_than_paler() {
        for (var axis = 0; axis < 3; axis++) {
            var plain = GizmoGeometry.AxisColour(axis);
            var hovered = GizmoGeometry.AxisColour(axis, highlighted: true);

            // ⚠ Darker, and by a scale rather than a blend. Every pixel of a handle is its colour
            // times a shading term, so brightness is the channel that says which way a surface faces
            // — an arm lightened towards white under the pointer is an arm that has lost its shading,
            // and it reads as a differently lit object rather than as this one being pointed at.
            Assert.True(
                hovered.R + hovered.G + hovered.B < plain.R + plain.G + plain.B,
                $"axis {axis} gets lighter under the pointer, not darker"
            );

            // And the hue survives it: a blend towards a dark colour would drag all three arms
            // towards that colour, and saying which axis is the whole job of an axis colour.
            Assert.Equal(plain.R * GizmoGeometry.HighlightShade, hovered.R, 4);
            Assert.Equal(plain.G * GizmoGeometry.HighlightShade, hovered.G, 4);
            Assert.Equal(plain.B * GizmoGeometry.HighlightShade, hovered.B, 4);
        }

        var ball = GizmoGeometry.NeutralColour();
        var grabbed = GizmoGeometry.NeutralColour(highlighted: true);

        Assert.True(grabbed.R < ball.R, "the middle handle gets lighter under the pointer, not darker");
    }

    [Fact]
    public void The_axes_are_the_three_colours_everybody_expects() {
        // Red, green, blue for x, y, z — the convention the corner axis cross already uses.
        Assert.True(GizmoGeometry.AxisColour(0).R > GizmoGeometry.AxisColour(0).B);
        Assert.True(GizmoGeometry.AxisColour(1).G > GizmoGeometry.AxisColour(1).R);
        Assert.True(GizmoGeometry.AxisColour(2).B > GizmoGeometry.AxisColour(2).R);
    }
}
