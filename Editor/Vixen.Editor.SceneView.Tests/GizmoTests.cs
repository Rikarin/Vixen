// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>A transform the gizmo can move, with no world behind it.</summary>
sealed class StubTarget : IGizmoTarget {
    public Vector3 Position { get; set; }

    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    public Vector3 Scale { get; set; } = Vector3.One;

    public Matrix4x4 ParentToWorld { get; set; } = Matrix4x4.Identity;
}

/// <summary>Hit-testing, dragging, snapping and the spaces.</summary>
public class GizmoTests {
    const int Width = 1000;
    const int Height = 800;

    static EditorCamera Camera() => new() { Pivot = Vector3.Zero, Distance = 10f };

    static (TransformGizmo Gizmo, StubTarget Target) One(GizmoMode mode = GizmoMode.Translate) {
        var target = new StubTarget();
        var gizmo = new TransformGizmo { Mode = mode };

        gizmo.Attach([target]);

        return (gizmo, target);
    }

    static Vector2 Screen(EditorCamera camera, Vector3 world) {
        var projected = camera.Project(world, Width, Height);

        return new(projected.X, projected.Y);
    }

    [Fact]
    public void The_origin_is_the_last_clicked_object_or_the_middle_of_them() {
        var first = new StubTarget { Position = new(0f, 0f, 0f) };
        var second = new StubTarget { Position = new(10f, 0f, 0f) };

        var gizmo = new TransformGizmo();
        gizmo.Attach([first, second]);

        gizmo.Pivot = PivotMode.Pivot;
        Assert.Equal(second.Position, gizmo.Origin);

        gizmo.Pivot = PivotMode.Center;
        Assert.Equal(new Vector3(5f, 0f, 0f), gizmo.Origin);
    }

    [Fact]
    public void Pointing_at_an_arm_grabs_it_and_pointing_at_nothing_does_not() {
        var camera = Camera();
        var (gizmo, _) = One();

        var scale = gizmo.WorldPerPixel(camera, Height) * gizmo.HandleLength;
        var alongX = Screen(camera, new Vector3(scale * 0.8f, 0f, 0f));

        Assert.Equal(GizmoHandle.AxisX, gizmo.HitTest(alongX, camera, Width, Height));
        Assert.Equal(GizmoHandle.None, gizmo.HitTest(new Vector2(5f, 5f), camera, Width, Height));
    }

    [Fact]
    public void The_handles_are_the_same_size_on_screen_however_far_away_they_are() {
        var near = new EditorCamera { Distance = 2f };
        var far = new EditorCamera { Distance = 200f };
        var (gizmo, _) = One();

        var nearEnd = Screen(near, new Vector3(gizmo.WorldPerPixel(near, Height) * gizmo.HandleLength, 0f, 0f));
        var farEnd = Screen(far, new Vector3(gizmo.WorldPerPixel(far, Height) * gizmo.HandleLength, 0f, 0f));

        var nearLength = Vector2.Distance(nearEnd, Screen(near, Vector3.Zero));
        var farLength = Vector2.Distance(farEnd, Screen(far, Vector3.Zero));

        Assert.Equal(nearLength, farLength, 0);
    }

    [Fact]
    public void Dragging_an_arm_moves_along_it_and_along_nothing_else() {
        var camera = Camera();
        var (gizmo, target) = One();

        var start = Screen(camera, Vector3.Zero);
        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(start, Width, Height), camera);

        var moved = start + new Vector2(120f, 45f);
        gizmo.Drag(camera.PickingRay(moved, Width, Height), camera);

        Assert.True(target.Position.X > 0f);
        Assert.Equal(0f, target.Position.Y, 4);
        Assert.Equal(0f, target.Position.Z, 4);
    }

    [Fact]
    public void A_drag_out_and_back_ends_exactly_where_it_started() {
        var camera = Camera();
        var (gizmo, target) = One();

        var start = Screen(camera, Vector3.Zero);
        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(start, Width, Height), camera);

        // Recomputed from the captured state every frame, so a hundred intermediate positions leave
        // no accumulated error. An implementation that added each frame's delta would drift.
        for (var step = 1; step <= 100; step++) {
            gizmo.Drag(camera.PickingRay(start + new Vector2(step * 3f, 0f), Width, Height), camera);
        }

        gizmo.Drag(camera.PickingRay(start, Width, Height), camera);
        gizmo.End();

        Assert.True(Vector3.NearEqual(target.Position, Vector3.Zero, 1e-4f));
    }

    [Fact]
    public void Snapping_lands_on_the_grid_rather_than_near_it() {
        var camera = Camera();
        var (gizmo, target) = One();

        gizmo.Snap.SnapPosition = true;
        gizmo.Snap.GridStep = 0.5f;

        var start = Screen(camera, Vector3.Zero);
        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(start, Width, Height), camera);

        // Dragged in many small steps, which is what breaks an implementation that rounds each delta.
        for (var step = 1; step <= 40; step++) {
            gizmo.Drag(camera.PickingRay(start + new Vector2(step * 4f, 0f), Width, Height), camera);
        }

        var remainder = MathF.Abs(target.Position.X / 0.5f - MathF.Round(target.Position.X / 0.5f));

        Assert.True(remainder < 1e-3f, $"landed at {target.Position.X}, which is not on a half-unit grid");
    }

    [Fact]
    public void Escape_puts_everything_back_where_it_was() {
        var camera = Camera();
        var (gizmo, target) = One();

        target.Position = new(3f, 0f, 0f);

        var start = Screen(camera, target.Position);
        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(start, Width, Height), camera);
        gizmo.Drag(camera.PickingRay(start + new Vector2(200f, 0f), Width, Height), camera);

        Assert.True(gizmo.Cancel());
        Assert.Equal(new Vector3(3f, 0f, 0f), target.Position);
        Assert.False(gizmo.IsDragging);
    }

    [Fact]
    public void Rotating_a_group_turns_it_about_the_gizmo_rather_than_in_place() {
        // Pitched down, because a rotation about Y is measured in the XZ plane and a horizontal
        // camera's rays are parallel to it — see TransformGizmo.OnPlane for what a parallel ray does.
        var camera = Camera();
        camera.Orbit(0f, 150f);
        var first = new StubTarget { Position = new(-5f, 0f, 0f) };
        var second = new StubTarget { Position = new(5f, 0f, 0f) };

        var gizmo = new TransformGizmo { Mode = GizmoMode.Rotate, Pivot = PivotMode.Center };
        gizmo.Attach([first, second]);

        var start = Screen(camera, new Vector3(0f, 0f, 5f));
        gizmo.Begin(GizmoHandle.AxisY, camera.PickingRay(start, Width, Height), camera);
        gizmo.Drag(camera.PickingRay(start + new Vector2(150f, 0f), Width, Height), camera);

        // Both moved, and they are still the same distance apart: the group turned as a group.
        Assert.NotEqual(new Vector3(-5f, 0f, 0f), first.Position);
        Assert.Equal(10f, (second.Position - first.Position).Length(), 3);
    }

    [Fact]
    public void Rotation_snapping_lands_on_whole_steps() {
        var camera = Camera();
        camera.Orbit(0f, 150f);
        var (gizmo, target) = One(GizmoMode.Rotate);

        gizmo.Snap.SnapRotation = true;
        gizmo.Snap.AngleStep = 45f;

        var start = Screen(camera, new Vector3(0f, 0f, 4f));
        gizmo.Begin(GizmoHandle.AxisY, camera.PickingRay(start, Width, Height), camera);

        for (var step = 1; step <= 30; step++) {
            gizmo.Drag(camera.PickingRay(start + new Vector2(step * 6f, 0f), Width, Height), camera);
        }

        var degrees = EulerDegrees(target.Rotation);
        var remainder = MathF.Abs(degrees / 45f - MathF.Round(degrees / 45f));

        Assert.True(remainder < 1e-2f, $"landed at {degrees}°, which is not a multiple of 45");
    }

    [Fact]
    public void Scaling_by_dragging_out_makes_it_bigger() {
        var camera = Camera();
        var (gizmo, target) = One(GizmoMode.Scale);

        var start = Screen(camera, new Vector3(1f, 0f, 0f));
        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(start, Width, Height), camera);
        gizmo.Drag(camera.PickingRay(start + new Vector2(120f, 0f), Width, Height), camera);

        Assert.True(target.Scale.X > 1f);
        Assert.Equal(1f, target.Scale.Y, 4);
        Assert.Equal(1f, target.Scale.Z, 4);
    }

    [Fact]
    public void Local_space_points_the_arms_at_the_object_s_own_axes() {
        var camera = Camera();
        var target = new StubTarget {
            Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo)
        };

        var gizmo = new TransformGizmo { Space = GizmoSpace.Local };
        gizmo.Attach([target]);

        var basis = gizmo.Basis(camera);
        var x = Vector3.Normalize(new Vector3(basis.M11, basis.M12, basis.M13));

        // The object is turned a quarter turn about Y, so its own X points along world −Z.
        Assert.True(Vector3.NearEqual(x, new Vector3(0f, 0f, -1f), 1e-4f));
    }

    [Fact]
    public void Several_objects_selected_forces_world_space() {
        var camera = Camera();

        var first = new StubTarget { Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, 1f) };
        var second = new StubTarget { Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, -1f) };

        var gizmo = new TransformGizmo { Space = GizmoSpace.Local };
        gizmo.Attach([first, second]);

        // "Local" has no answer when the selection disagrees about which way local is, and picking
        // the primary's would drag the other one along an axis that is not its own.
        Assert.Equal(Matrix4x4.Identity, gizmo.Basis(camera));
    }

    [Fact]
    public void Changing_the_selection_mid_drag_is_refused() {
        var camera = Camera();
        var (gizmo, _) = One();

        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(Screen(camera, Vector3.Zero), Width, Height), camera);

        Assert.Throws<InvalidOperationException>(() => gizmo.Attach([new StubTarget()]));
    }

    [Fact]
    public void Nothing_selected_has_nothing_to_hit() {
        var camera = Camera();
        var gizmo = new TransformGizmo();

        Assert.Equal(GizmoHandle.None, gizmo.HitTest(new Vector2(500f, 400f), camera, Width, Height));
        Assert.False(gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera));
    }

    static float EulerDegrees(Quaternion rotation) {
        var turned = Quaternion.Transform(Vector3.UnitX, rotation);

        return MathUtil.RadiansToDegrees(MathF.Atan2(-turned.Z, turned.X));
    }
}
