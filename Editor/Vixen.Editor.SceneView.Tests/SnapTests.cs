// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>What a ray meets, which vertex is nearest, and what a drag does with either answer.</summary>
public class SnapTests : IDisposable {
    const int Width = 1000;
    const int Height = 800;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-snap-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly SceneProbe probe;
    readonly TransformSystem transforms = new();

    public SnapTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
        probe = new(scene);
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A camera at +Z looking at the origin.</summary>
    static EditorCamera Camera() => new() { Pivot = Vector3.Zero, Distance = 10f };

    Entity Shape(PrimitiveKind kind, Vector3 position, float scale = 1f) {
        var entity = scene.CreateShape(kind, new LocalTransform {
            Position = position,
            Rotation = Quaternion.Identity,
            Scale = new(scale)
        });

        transforms.Resolve(world);
        world.AdvanceVersion();

        return entity;
    }

    // ── The surface half ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_ray_down_onto_a_plate_lands_on_its_top_face() {
        Shape(PrimitiveKind.Cube, new Vector3(0f, -2f, 0f), scale: 4f);

        Assert.True(probe.Raycast(new Ray(new Vector3(0f, 10f, 0f), -Vector3.UnitY), out var hit));

        // The cube is four units across centred two below the origin, so its top face is at zero.
        Assert.Equal(0f, hit.Point.Y, 3);
        Assert.True(Vector3.NearEqual(hit.Normal, Vector3.UnitY, 1e-3f));
    }

    [Fact]
    public void A_normal_always_faces_the_ray_it_was_found_by() {
        Shape(PrimitiveKind.Cube, Vector3.Zero, scale: 2f);

        // ⚠ From below. The geometry is two-sided in the picture and a snap is not: a normal pointing
        // away puts the dropped object underneath the surface it was dropped on.
        Assert.True(probe.Raycast(new Ray(new Vector3(0f, -10f, 0f), Vector3.UnitY), out var hit));
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) < 0f);
    }

    [Fact]
    public void The_nearest_surface_along_a_ray_wins() {
        var near = Shape(PrimitiveKind.Cube, new Vector3(0f, 2f, 0f));

        Shape(PrimitiveKind.Cube, new Vector3(0f, -2f, 0f));

        Assert.True(probe.Raycast(new Ray(new Vector3(0f, 10f, 0f), -Vector3.UnitY), out var hit));
        Assert.Equal(2.5f, hit.Point.Y, 3);
        Assert.NotEqual(Entity.Null, near);
    }

    [Fact]
    public void An_ignored_entity_does_not_answer() {
        var floor = Shape(PrimitiveKind.Cube, new Vector3(0f, -2f, 0f), scale: 4f);
        var crate = Shape(PrimitiveKind.Cube, new Vector3(0f, 2f, 0f));

        // ⚠ Without the exclusion the crate answers at once, which is a Snap To Floor that never
        // moves anything: the pointer is over the object being dragged for the whole of every drag.
        Assert.True(probe.Raycast(new Ray(new Vector3(0f, 10f, 0f), -Vector3.UnitY), [crate], out var hit));

        Assert.Equal(0f, hit.Point.Y, 3);
        Assert.NotEqual(Entity.Null, floor);
    }

    [Fact]
    public void A_hidden_or_locked_entity_does_not_answer_either() {
        var floor = Shape(PrimitiveKind.Cube, new Vector3(0f, -2f, 0f), scale: 4f);

        scene.SetHidden(floor, true);

        Assert.False(probe.Raycast(new Ray(new Vector3(0f, 10f, 0f), -Vector3.UnitY), out _));
    }

    // ── The vertex half ─────────────────────────────────────────────────────────────────────────

    /// <summary>Where a world point lands in the pane, in render pixels.</summary>
    static Vector2 Screen(EditorCamera camera, Vector3 world) {
        var projected = camera.Project(world, Width, Height);

        return new Vector2(projected.X, projected.Y);
    }

    [Fact]
    public void The_vertex_nearest_the_pointer_is_the_one_it_is_aimed_at() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero, scale: 2f);

        var corner = new Vector3(1f, 1f, 1f);

        Assert.True(
            probe.TryNearestVertex(Screen(camera, corner), camera, Width, Height, 24f, [], out var found)
        );

        Assert.True(Vector3.NearEqual(found, corner, 1e-3f));
    }

    [Fact]
    public void Nothing_within_the_radius_is_no_answer_rather_than_the_nearest_thing() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero, scale: 2f);

        // Well off to the side. A snap that always answered would drag the selection to the far
        // corner of whatever happened to be in the scene the moment the setting was turned on.
        Assert.False(
            probe.TryNearestVertex(new Vector2(20f, 20f), camera, Width, Height, 12f, [], out _)
        );
    }

    [Fact]
    public void The_dragged_object_s_own_corners_are_not_offered() {
        var camera = Camera();
        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero, scale: 2f);

        var corner = new Vector3(1f, 1f, 1f);

        Assert.False(
            probe.TryNearestVertex(Screen(camera, corner), camera, Width, Height, 24f, [cube], out _)
        );
    }

    // ── What a drag does with the answer ────────────────────────────────────────────────────────

    [Fact]
    public void A_snapped_free_drag_lands_exactly_on_the_point() {
        var target = new StubTarget { Position = Vector3.Zero };
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };
        var camera = Camera();

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        gizmo.SnapTo = new Vector3(3f, 4f, 5f);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(target.Position, new Vector3(3f, 4f, 5f), 1e-4f));
    }

    [Fact]
    public void A_snapped_axis_drag_keeps_the_other_two_components() {
        var target = new StubTarget { Position = Vector3.Zero };
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };
        var camera = Camera();

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        // ⚠ "Snap to that corner" and "keep it on this axis" compose rather than the last one written
        // winning. The x is the corner's and the other two are where the drag started.
        gizmo.SnapTo = new Vector3(3f, 4f, 5f);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(target.Position, new Vector3(3f, 0f, 0f), 1e-4f));
    }

    [Fact]
    public void A_snapped_plane_drag_stays_in_its_plane() {
        var target = new StubTarget { Position = Vector3.Zero };
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };
        var camera = Camera();

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.PlaneZX, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        // The quad between Z and X, so the drag has no Y.
        gizmo.SnapTo = new Vector3(3f, 4f, 5f);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(target.Position, new Vector3(3f, 0f, 5f), 1e-4f));
    }

    [Fact]
    public void The_snap_point_is_dropped_when_the_drag_ends() {
        var target = new StubTarget();
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };
        var camera = Camera();

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        gizmo.SnapTo = new Vector3(3f, 4f, 5f);
        gizmo.End();

        // ⚠ A snap point left behind is one the *next* drag begins by teleporting to, which reads as
        // the gizmo having remembered the wrong object.
        Assert.Null(gizmo.SnapTo);
    }

    [Fact]
    public void A_viewport_with_no_probe_snaps_to_nothing() {
        using var pane = new Pane();
        var target = new StubTarget();

        pane.Targets.Add(target);
        pane.Frame();

        pane.Viewport.Gizmo.Snap.SnapToVertex = true;

        // Nothing set `Surfaces`, so there is nobody to ask — and the drag is the ordinary one rather
        // than a snap to the origin.
        var grab = pane.Screen(Vector3.Zero);

        pane.Press(Vixen.Ui.PointerButton.Primary, grab);
        pane.Move(grab + new Vector2(60f, 0f));

        Assert.Null(pane.Viewport.Gizmo.SnapTo);
    }
}
