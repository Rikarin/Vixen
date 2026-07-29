// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Orbit, pan, zoom, focus and the axis views.</summary>
public class CameraTests {
    [Fact]
    public void An_unturned_camera_looks_down_negative_z() {
        var camera = new EditorCamera();

        // The engine's forward (Conventions.md § Handedness). A camera whose zero yaw looked the
        // other way would make every numpad view the opposite of what it is labelled.
        Assert.True(Vector3.NearEqual(camera.Forward, Vector3.Forward, 1e-5f));
        Assert.True(Vector3.NearEqual(camera.Position, new Vector3(0f, 0f, 10f), 1e-4f));
    }

    [Fact]
    public void Dragging_up_tips_the_scene_towards_you_and_puts_the_eye_below_it() {
        var camera = new EditorCamera { Distance = 10f };

        camera.Orbit(0f, -100f);

        // The turntable: a drag up grabs the front of the thing and pulls it up, so its underside
        // comes into view and the eye ends up beneath it. It is the same gesture as the horizontal
        // axis below, which is the whole point — the vertical used to carry the *camera* instead, so
        // one diagonal drag turned the scene one way and the eye the other.
        Assert.True(camera.Position.Y < camera.Pivot.Y);
        Assert.True(camera.Forward.Y > 0f);

        camera.Orbit(0f, 200f);

        Assert.True(camera.Position.Y > camera.Pivot.Y);
        Assert.True(camera.Forward.Y < 0f);
    }

    [Fact]
    public void Inverting_y_swaps_the_vertical_and_leaves_the_horizontal_alone() {
        var plain = new EditorCamera { Distance = 10f };
        var inverted = new EditorCamera { Distance = 10f, InvertOrbitY = true };

        plain.Orbit(80f, -60f);
        inverted.Orbit(80f, -60f);

        // The setting people arriving from Unity and Unreal reach for. It is one axis: reversing both
        // is a different preference, and a setting that quietly did the second when asked for the
        // first is one nobody can describe.
        Assert.Equal(plain.Yaw, inverted.Yaw, 5);
        Assert.Equal(-plain.Pitch, inverted.Pitch, 5);
    }

    [Fact]
    public void Dragging_sideways_spins_the_scene_the_way_the_pointer_went() {
        var camera = new EditorCamera { Distance = 10f };

        // The horizontal axis carries the scene rather than the camera: dragging right swings the
        // camera left, which is what makes what you are looking at appear to follow the pointer.
        camera.Orbit(100f, 0f);

        Assert.True(camera.Position.X < camera.Pivot.X);
    }

    [Fact]
    public void Orbiting_around_an_anchor_keeps_the_eye_the_same_distance_from_it() {
        var camera = new EditorCamera { Pivot = Vector3.Zero, Distance = 10f };
        var anchor = new Vector3(4f, 1f, -2f);

        var before = (camera.Position - anchor).Length();
        var pivot = (camera.Pivot - anchor).Length();

        camera.OrbitAround(anchor, 120f, -70f);

        // The whole rig turns about the anchor, so both the eye and the pivot keep their distance
        // from it — which is what "orbit around the selection" means and what a camera that only
        // knows how to orbit its own pivot cannot do without moving that pivot too.
        Assert.Equal(before, (camera.Position - anchor).Length(), 3);
        Assert.Equal(pivot, (camera.Pivot - anchor).Length(), 3);
    }

    [Fact]
    public void Orbiting_around_the_pivot_itself_is_an_ordinary_orbit() {
        var anchored = new EditorCamera { Pivot = new Vector3(3f, 0f, 1f), Distance = 7f };
        var plain = new EditorCamera { Pivot = new Vector3(3f, 0f, 1f), Distance = 7f };

        anchored.OrbitAround(anchored.Pivot, 40f, 25f);
        plain.Orbit(40f, 25f);

        Assert.Equal(plain.Yaw, anchored.Yaw, 5);
        Assert.Equal(plain.Pitch, anchored.Pitch, 5);
        Assert.True(Vector3.NearEqual(plain.Pivot, anchored.Pivot, 1e-3f));
    }

    [Fact]
    public void Orbiting_around_an_anchor_at_the_pitch_limit_does_not_drift() {
        var camera = new EditorCamera { Distance = 10f };
        var anchor = new Vector3(5f, 0f, 0f);

        // Held past the top of its travel, which is where the requested rotation and the applied one
        // stop being the same thing. Rebuilding the pivot from the requested one slides it a little
        // further on every frame the drag is held there.
        camera.OrbitAround(anchor, 0f, 400f);

        var pivot = camera.Pivot;

        for (var frame = 0; frame < 20; frame++) {
            camera.OrbitAround(anchor, 0f, 40f);
        }

        Assert.True(Vector3.NearEqual(camera.Pivot, pivot, 1e-3f), $"the pivot drifted to {camera.Pivot}");
    }

    [Fact]
    public void Zooming_at_a_point_keeps_that_point_where_it_was_on_screen() {
        var camera = new EditorCamera { Distance = 10f };
        var target = new Vector3(2f, 1.5f, 0f);
        var before = camera.Project(target, 1000, 800);

        camera.ZoomTowards(target, 3f);

        var after = camera.Project(target, 1000, 800);

        // What "zoom to mouse position" has to mean: the thing under the pointer is still under the
        // pointer afterwards. A zoom that only scaled the distance moves it towards the middle of the
        // pane, which is why approaching anything off-centre is otherwise zoom, pan, zoom, pan.
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
        Assert.True(camera.Distance < 10f);
    }

    [Fact]
    public void Zooming_at_a_point_stops_dragging_the_view_once_the_distance_is_floored() {
        var camera = new EditorCamera { Distance = EditorCamera.MinimumDistance };
        var target = new Vector3(50f, 0f, 0f);

        camera.ZoomTowards(target, 200f);

        // The distance cannot fall any further, so nothing may move. Scaling the pivot by the factor
        // that was *asked* for instead of the one that happened is a view that keeps sliding towards
        // the pointer for as long as the wheel is turned at the bottom of the zoom.
        Assert.Equal(EditorCamera.MinimumDistance, camera.Distance);
        Assert.True(Vector3.NearEqual(camera.Pivot, Vector3.Zero, 1e-4f));
    }

    [Fact]
    public void A_point_behind_the_eye_has_no_screen_position() {
        var camera = new EditorCamera { Distance = 10f };

        Assert.True(camera.TryProject(Vector3.Zero, 1000, 800, out var middle));
        Assert.Equal(500f, middle.X, 2);

        // Behind the camera, which sits at +10 on z looking down −z. A perspective divide by a
        // negative w answers with a real pixel position on the wrong side of the pane, and nothing
        // downstream can tell it from a real one — see EditorCamera.TryProject.
        Assert.False(camera.TryProject(new Vector3(0f, 0f, 40f), 1000, 800, out _));
    }

    [Fact]
    public void Orthographic_projects_what_is_behind_it_too() {
        var camera = new EditorCamera { Distance = 10f, IsOrthographic = true };

        // No divide, so there is nothing to go wrong and nothing to reject. Rejecting it anyway would
        // make the gizmo unclickable in the three orthographic panes of a quad layout, where the
        // camera routinely sits inside the scene.
        Assert.True(camera.TryProject(new Vector3(0f, 0f, 40f), 1000, 800, out var behind));
        Assert.Equal(500f, behind.X, 2);
    }

    [Fact]
    public void Pitch_is_held_short_of_vertical() {
        var camera = new EditorCamera();

        camera.Orbit(0f, -100000f);
        Assert.True(camera.Pitch <= EditorCamera.PitchLimit);

        camera.Orbit(0f, 200000f);
        Assert.True(camera.Pitch >= -EditorCamera.PitchLimit);

        // And the basis stays well conditioned at the limit, which is the whole reason for it.
        Assert.True(camera.Up.Length() > 0.5f);
        Assert.True(camera.Right.Length() > 0.5f);
    }

    [Fact]
    public void Zoom_is_multiplicative_so_it_never_punches_through() {
        var camera = new EditorCamera { Distance = 100f };

        for (var notch = 0; notch < 200; notch++) {
            camera.Zoom(1f);
        }

        // Two hundred notches in and still in front of whatever it was looking at. A fixed step per
        // notch would have gone through it and out the other side.
        Assert.True(camera.Distance > 0f);
        Assert.True(camera.Distance < 1f);
    }

    [Fact]
    public void One_click_of_the_wheel_is_one_notch_rather_than_a_notch_a_pixel() {
        var camera = new EditorCamera { Distance = 10f };

        // What the wheel actually delivers: a line height of pixels, negative because the scroll's
        // positive direction is down a document. Fed to Zoom unconverted it was forty-eight notches
        // — a single click of the wheel from ten units to two thousandths of one.
        camera.Zoom(SceneViewport.Notches(-48f, 48f));

        Assert.Equal(10f * (1f - camera.ZoomSpeed), camera.Distance, 4);

        camera.Zoom(SceneViewport.Notches(48f, 48f));

        Assert.Equal(10f, camera.Distance, 4);
    }

    [Fact]
    public void Distance_never_reaches_zero() {
        var camera = new EditorCamera { Distance = -5f };

        Assert.Equal(EditorCamera.MinimumDistance, camera.Distance);
    }

    [Fact]
    public void Panning_moves_by_roughly_what_the_pointer_moved() {
        var camera = new EditorCamera { Distance = 10f };
        var height = 800;
        var before = camera.Pivot;

        camera.Pan(100f, 0f, height);

        var moved = (camera.Pivot - before).Length();
        var expected = camera.OrthographicHeight / height * 100f;

        Assert.Equal(expected, moved, 3);
    }

    [Fact]
    public void Focus_keeps_the_angle_and_moves_the_pivot() {
        var camera = new EditorCamera();
        camera.Orbit(120f, 40f);

        var yaw = camera.Yaw;
        var pitch = camera.Pitch;

        camera.Focus(new BoundingBox(new Vector3(4f, 4f, 4f), new Vector3(6f, 6f, 6f)));

        Assert.Equal(yaw, camera.Yaw);
        Assert.Equal(pitch, camera.Pitch);
        Assert.True(Vector3.NearEqual(camera.Pivot, new Vector3(5f, 5f, 5f), 1e-4f));
    }

    [Fact]
    public void Focusing_something_with_no_size_still_gets_a_usable_distance() {
        var camera = new EditorCamera();

        // A light, an empty, a camera: things people focus on constantly and which have no bounds.
        camera.Focus(new BoundingBox(Vector3.Zero, Vector3.Zero));

        Assert.True(camera.Distance >= EditorCamera.MinimumDistance);
        Assert.True(float.IsFinite(camera.Distance));
    }

    [Theory]
    [InlineData(ViewDirection.Front, 0f, 0f, -1f)]
    [InlineData(ViewDirection.Back, 0f, 0f, 1f)]
    [InlineData(ViewDirection.Right, -1f, 0f, 0f)]
    [InlineData(ViewDirection.Left, 1f, 0f, 0f)]
    public void The_axis_views_look_the_way_they_are_named(ViewDirection direction, float x, float y, float z) {
        var camera = new EditorCamera();
        camera.LookFrom(direction);

        Assert.True(Vector3.NearEqual(camera.Forward, new Vector3(x, y, z), 1e-4f));
    }

    [Fact]
    public void Top_and_bottom_look_along_y_without_reaching_the_pole() {
        var camera = new EditorCamera();

        camera.LookFrom(ViewDirection.Top);
        Assert.True(camera.Forward.Y < -0.999f);

        camera.LookFrom(ViewDirection.Bottom);
        Assert.True(camera.Forward.Y > 0.999f);
    }

    [Fact]
    public void Switching_projection_does_not_change_how_big_anything_looks() {
        var camera = new EditorCamera { Distance = 25f };
        var perspective = camera.Project(new Vector3(1f, 0f, 0f), 1000, 800);

        camera.IsOrthographic = true;
        var orthographic = camera.Project(new Vector3(1f, 0f, 0f), 1000, 800);

        // The orthographic height is derived from the distance for exactly this: a point at the
        // pivot's depth lands in the same place either way.
        Assert.Equal(perspective.X, orthographic.X, 1);
        Assert.Equal(perspective.Y, orthographic.Y, 1);
    }

    [Fact]
    public void A_bookmark_goes_back_to_where_it_was_taken() {
        var camera = new EditorCamera();
        camera.Orbit(37f, -12f);
        camera.Zoom(3f);
        camera.Pivot = new(1f, 2f, 3f);

        var bookmark = camera.Bookmark("Kitchen");

        camera.Orbit(400f, 200f);
        camera.Zoom(-8f);
        camera.Restore(bookmark);

        Assert.Equal(bookmark.Yaw, camera.Yaw);
        Assert.Equal(bookmark.Pitch, camera.Pitch);
        Assert.Equal(bookmark.Distance, camera.Distance);
        Assert.Equal(bookmark.Pivot, camera.Pivot);
    }

    [Fact]
    public void A_picking_ray_through_the_middle_goes_where_the_camera_looks() {
        var camera = new EditorCamera();
        var ray = camera.PickingRay(new Vector2(500f, 400f), 1000, 800);

        Assert.True(Vector3.NearEqual(Vector3.Normalize(ray.Direction), camera.Forward, 1e-3f));
    }

    [Fact]
    public void Projecting_and_unprojecting_the_same_point_agree() {
        var camera = new EditorCamera();
        var world = new Vector3(2f, 1f, -4f);
        var screen = camera.Project(world, 1000, 800);
        var ray = camera.PickingRay(new Vector2(screen.X, screen.Y), 1000, 800);

        // The point has to be somewhere along the ray that comes back through its own pixel.
        var toPoint = world - ray.Origin;
        var along = Vector3.Dot(toPoint, Vector3.Normalize(ray.Direction));
        var offset = (toPoint - (Vector3.Normalize(ray.Direction) * along)).Length();

        Assert.True(offset < 0.01f, $"the point was {offset} off its own ray");
    }
}
