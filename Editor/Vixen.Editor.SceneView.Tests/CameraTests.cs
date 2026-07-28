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
