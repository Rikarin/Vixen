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

/// <summary>Doc 24's D4: one snapping service, with an element, a base and three modifiers.</summary>
public class SnapContextTests : IDisposable {
    const int Width = 1000;
    const int Height = 800;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-snapctx-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly SceneProbe probe;
    readonly TransformSystem transforms = new();

    public SnapContextTests() {
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

    /// <summary>A camera near enough that a unit cube is a few hundred pixels across.</summary>
    static EditorCamera Camera() => new() { Pivot = Vector3.Zero, Distance = 4f };

    static Vector2 Screen(EditorCamera camera, Vector3 world) {
        var projected = camera.Project(world, Width, Height);

        return new Vector2(projected.X, projected.Y);
    }

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

    /// <summary>Asks the probe from a pointer aimed at a world point.</summary>
    bool Snap(SnapContext snap, EditorCamera camera, Vector3 at, out SnapHit hit, Vector3? origin = null) {
        var pointer = Screen(camera, at);

        return probe.TrySnap(
            camera.PickingRay(pointer, Width, Height),
            pointer,
            camera,
            Width,
            Height,
            snap,
            origin ?? at,
            [],
            out hit
        );
    }

    // ── The model ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_old_booleans_are_views_over_the_element_set_rather_than_second_state() {
        var snap = new SnapContext();

        // ⚠ Both directions. Two writers for one fact is how a toolbar toggle and a settings panel
        // end up disagreeing about whether snapping is on, and doc 24's D4 asks for the four booleans
        // to keep working while the set becomes the model.
        snap.SnapToVertex = true;
        Assert.True(snap.Has(SnapElements.Vertex));

        snap.Elements |= SnapElements.Face;
        Assert.True(snap.SnapToSurface);

        snap.SnapPosition = true;
        Assert.Equal(SnapElements.Vertex | SnapElements.Face | SnapElements.Increment, snap.Elements);

        snap.SnapToVertex = false;
        Assert.False(snap.Has(SnapElements.Vertex));
        Assert.True(snap.SnapToSurface);
    }

    [Fact]
    public void Rounding_and_the_steps_are_exactly_what_they_were() {
        var snap = new SnapContext { SnapPosition = true, GridStep = 0.5f, SnapRotation = true, AngleStep = 45f };

        Assert.Equal(1.5f, snap.Position(1.4f), 4);
        Assert.Equal(MathUtil.DegreesToRadians(45f), snap.Rotation(MathUtil.DegreesToRadians(50f)), 4);

        // Off is the identity, which is what everything that does not snap relies on.
        Assert.Equal(1.4f, new SnapContext().Position(1.4f), 4);
    }

    [Fact]
    public void Nothing_geometric_is_on_by_default() {
        var snap = new SnapContext();

        Assert.False(snap.SnapsToGeometry);
        Assert.Equal(SnapBase.Origin, snap.Base);

        // ⚠ AlignToTarget is on and it is deliberate: a drop onto a surface has stood the dropped
        // thing up since before this type existed, and one context is what stops a drop and a drag
        // disagreeing. It is unreachable until somebody turns Face on, which is off.
        Assert.True(snap.Is(SnapModifiers.AlignToTarget));
        Assert.True(snap.Is(SnapModifiers.ProjectFromView));
        Assert.True(snap.Is(SnapModifiers.IgnoreSelf));
    }

    // ── The elements ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_vertex_snap_lands_on_a_welded_corner_rather_than_near_it() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        var corner = new Vector3(0.5f, 0.5f, 0.5f);
        var snap = new SnapContext { SnapToVertex = true };

        Assert.True(Snap(snap, camera, corner, out var hit));

        Assert.Equal(SnapElements.Vertex, hit.Element);
        Assert.True(Vector3.NearEqual(hit.Point, corner, 1e-4f));

        // ⚠ A point, so nothing to align to. A vertex says where and not which way, and averaging the
        // faces round a corner would stand things up diagonally.
        Assert.Null(hit.Normal);
    }

    [Fact]
    public void An_edge_snap_lands_anywhere_along_the_edge() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        // A quarter of the way up the vertical edge nearest the camera on the right, which is a place
        // no vertex and no edge centre is.
        var along = new Vector3(0.5f, -0.25f, 0.5f);
        var snap = new SnapContext { Elements = SnapElements.Edge };

        Assert.True(Snap(snap, camera, along, out var hit));

        Assert.Equal(SnapElements.Edge, hit.Element);
        Assert.True(Vector3.NearEqual(hit.Point, along, 0.02f), $"landed at {hit.Point}");
    }

    [Fact]
    public void An_edge_centre_snap_is_what_a_wall_is_centred_on() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        var centre = new Vector3(0.5f, 0f, 0.5f);
        var snap = new SnapContext { Elements = SnapElements.EdgeCentre };

        Assert.True(Snap(snap, camera, centre, out var hit));

        Assert.Equal(SnapElements.EdgeCentre, hit.Element);
        Assert.True(Vector3.NearEqual(hit.Point, centre, 1e-4f), $"landed at {hit.Point}");
    }

    [Fact]
    public void A_surface_snap_carries_the_normal_the_alignment_needs() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        var snap = new SnapContext { SnapToSurface = true };

        // Off the diagonal the triangulation puts across the +Z side, and well away from its corners.
        Assert.True(Snap(snap, camera, new Vector3(0.25f, -0.1f, 0.5f), out var hit));

        Assert.Equal(SnapElements.Face, hit.Element);
        Assert.NotNull(hit.Normal);
        Assert.True(Vector3.NearEqual(hit.Normal!.Value, Vector3.UnitZ, 1e-3f));
    }

    [Fact]
    public void The_smallest_element_within_reach_wins() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        var corner = new Vector3(0.5f, 0.5f, 0.5f);
        var snap = new SnapContext { Elements = SnapElements.Geometry };

        // ⚠ A corner is on three faces, three edges and is an edge centre of none — and holding all
        // four elements at once has to be strictly better than holding one, not a mode fight. Vertex
        // beats edge beats surface, which is the same innermost-wins rule sub-object picking uses.
        Assert.True(Snap(snap, camera, corner, out var hit));
        Assert.Equal(SnapElements.Vertex, hit.Element);

        // And the fall-through is the point of the set: a place with no corner near it still answers.
        Assert.True(Snap(snap, camera, new Vector3(0.25f, -0.1f, 0.5f), out var surface));
        Assert.Equal(SnapElements.Face, surface.Element);
    }

    [Fact]
    public void Nothing_within_reach_answers_nothing() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        var snap = new SnapContext { SnapToVertex = true, VertexRadius = 4f };

        // Aimed at empty space well off the cube. A snap that answered here would drag things to a
        // corner nobody was pointing at.
        Assert.False(Snap(snap, camera, new Vector3(3f, 3f, 0f), out _));
    }

    // ── The modifiers ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Turning_the_view_projection_off_searches_from_the_base_instead() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, new Vector3(0f, 0f, 0f));

        var near = new Vector3(0.5f, 0.5f, 0.5f);
        var far = new Vector3(-0.5f, -0.5f, 0.5f);

        var snap = new SnapContext { SnapToVertex = true, VertexRadius = 400f };

        snap.Toggle(SnapModifiers.ProjectFromView, false);

        // ⚠ The pointer is aimed at one corner and the base is at the other, and with the modifier
        // off it is the base that decides. That is the drag where the handle being held is a long way
        // from the geometry the object should land on.
        Assert.True(Snap(snap, camera, near, out var hit, origin: far));

        Assert.True(Vector3.NearEqual(hit.Point, far, 1e-4f), $"landed at {hit.Point}");
    }

    [Fact]
    public void The_view_projection_is_what_makes_the_pointer_decide() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        var near = new Vector3(0.5f, 0.5f, 0.5f);
        var far = new Vector3(-0.5f, -0.5f, 0.5f);

        var snap = new SnapContext { SnapToVertex = true, VertexRadius = 400f };

        // The same two points, the modifier left on: the pointer wins.
        Assert.True(Snap(snap, camera, near, out var hit, origin: far));

        Assert.True(Vector3.NearEqual(hit.Point, near, 1e-4f), $"landed at {hit.Point}");
    }

    [Fact]
    public void What_is_being_dragged_is_left_out_by_the_caller_rather_than_by_the_probe() {
        var camera = Camera();
        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);

        var corner = new Vector3(0.5f, 0.5f, 0.5f);
        var pointer = Screen(camera, corner);
        var snap = new SnapContext { SnapToVertex = true };

        // ⚠ The probe takes a list rather than reading the modifier, because what "self" is belongs to
        // whoever is dragging — a gizmo knows its targets and a placement has none.
        Assert.False(
            probe.TrySnap(
                camera.PickingRay(pointer, Width, Height),
                pointer,
                camera,
                Width,
                Height,
                snap,
                corner,
                [cube],
                out _
            )
        );
    }

    // ── The base ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_base_decides_which_part_of_the_selection_lands_on_the_point() {
        var camera = Camera();

        var first = new StubTarget { Position = new Vector3(-1f, 0f, 0f) };
        var second = new StubTarget { Position = new Vector3(1f, 0f, 0f) };

        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate, Pivot = PivotMode.Center };

        gizmo.Attach([first, second]);

        var grab = camera.PickingRay(new Vector2(500f, 400f), Width, Height);

        // Origin: the gizmo's own, which for a centred pivot is the middle. This is what the editor
        // did before the base existed, and it stays the default.
        gizmo.Begin(GizmoHandle.Screen, grab, camera);
        gizmo.SnapTo = new SnapHit(new Vector3(0f, 5f, 0f), null, SnapElements.Vertex);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(first.Position, new Vector3(-1f, 5f, 0f), 1e-4f));
        gizmo.Cancel();

        // Active: the first of the selection, so *it* lands on the point and the other keeps its
        // offset from it.
        gizmo.Snap.Base = SnapBase.Active;

        gizmo.Begin(GizmoHandle.Screen, grab, camera);
        gizmo.SnapTo = new SnapHit(new Vector3(0f, 5f, 0f), null, SnapElements.Vertex);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(first.Position, new Vector3(0f, 5f, 0f), 1e-4f));
        Assert.True(Vector3.NearEqual(second.Position, new Vector3(2f, 5f, 0f), 1e-4f));
    }

    [Fact]
    public void The_pointer_base_puts_the_corner_you_grabbed_on_the_point() {
        var camera = Camera();
        var target = new StubTarget { Position = Vector3.Zero };

        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Snap.Base = SnapBase.Pointer;
        gizmo.Attach([target]);

        // Grabbing the free handle away from the middle of the pane, so the point under the cursor is
        // not the object's origin.
        var grab = camera.PickingRay(new Vector2(600f, 300f), Width, Height);

        gizmo.Begin(GizmoHandle.Screen, grab, camera);

        var grabbed = gizmo.SnapOrigin;

        gizmo.SnapTo = new SnapHit(new Vector3(3f, 4f, 5f), null, SnapElements.Vertex);
        gizmo.Drag(camera.PickingRay(new Vector2(620f, 300f), Width, Height), camera);

        // ⚠ Doc 24's D4: "you meant the corner you grabbed". The grabbed point lands on the snap
        // point, and the object keeps its offset from it — which for a base of Origin it would not.
        Assert.NotEqual(Vector3.Zero, grabbed);
        Assert.True(
            Vector3.NearEqual(target.Position, new Vector3(3f, 4f, 5f) - grabbed, 1e-3f),
            $"landed at {target.Position} having grabbed {grabbed}"
        );
    }

    // ── Aligning ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_surface_snap_stands_what_is_dragged_up_on_it() {
        var camera = Camera();
        var target = new StubTarget { Position = Vector3.Zero };

        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        gizmo.SnapTo = new SnapHit(new Vector3(0f, 1f, 0f), Vector3.UnitX, SnapElements.Face);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        // An entity stands on its local +Y, so aligning to a wall's normal lies it on its side.
        Assert.True(
            Vector3.NearEqual(Quaternion.Transform(Vector3.UnitY, target.Rotation), Vector3.UnitX, 1e-3f),
            $"up is {Quaternion.Transform(Vector3.UnitY, target.Rotation)}"
        );
    }

    [Fact]
    public void A_vertex_snap_moves_and_does_not_turn() {
        var camera = Camera();
        var target = new StubTarget { Position = Vector3.Zero, Rotation = Quaternion.Identity };

        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        gizmo.SnapTo = new SnapHit(new Vector3(3f, 4f, 5f), null, SnapElements.Vertex);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(target.Position, new Vector3(3f, 4f, 5f), 1e-4f));
        Assert.Equal(Quaternion.Identity, target.Rotation);
    }

    [Fact]
    public void The_alignment_can_be_turned_off_without_turning_the_snap_off() {
        var camera = Camera();
        var target = new StubTarget { Position = Vector3.Zero, Rotation = Quaternion.Identity };

        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Snap.Toggle(SnapModifiers.AlignToTarget, false);
        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        gizmo.SnapTo = new SnapHit(new Vector3(0f, 1f, 0f), Vector3.UnitX, SnapElements.Face);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(target.Position, new Vector3(0f, 1f, 0f), 1e-4f));
        Assert.Equal(Quaternion.Identity, target.Rotation);
    }

    // ── One service ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_drag_in_a_pane_ends_on_a_corner_of_the_scene() {
        using var pane = new Pane();

        var target = new StubTarget { Position = Vector3.Zero };

        pane.Viewport.Surfaces = probe;
        pane.Camera.Pivot = Vector3.Zero;
        pane.Camera.Distance = 6f;

        pane.Targets.Add(target);
        pane.Frame();

        // Somewhere the object being dragged is not, so the corner is a place to go rather than where
        // it already is.
        Shape(PrimitiveKind.Cube, new Vector3(2f, 0f, 0f));

        pane.Viewport.Gizmo.Snap.SnapToVertex = true;

        var corner = new Vector3(2.5f, 0.5f, 0.5f);

        pane.Press(Vixen.Ui.PointerButton.Primary, pane.Screen(Vector3.Zero));
        pane.Move(pane.Screen(corner));

        // ⚠ The whole path: a pointer move becomes a probe query becomes a snap point becomes a
        // constrained offset. Doc 24's B5 said this was waiting for "the mesh under the pointer with
        // an indexed vertex list", and the corner it lands on is one of the eight `MeshElements`
        // welded a cube's twenty-four drawing vertices down to.
        Assert.NotNull(pane.Viewport.Gizmo.SnapTo);
        Assert.Equal(SnapElements.Vertex, pane.Viewport.Gizmo.SnapTo!.Value.Element);
        Assert.True(Vector3.NearEqual(target.Position, corner, 1e-3f), $"landed at {target.Position}");
    }

    [Fact]
    public void A_gizmo_and_a_placement_can_be_given_the_same_context() {
        var shared = new SnapContext();

        var gizmo = new TransformGizmo { Snap = shared };
        var placement = new ScenePlacement { Snap = shared };

        // ⚠ Doc 24's D4's whole argument in three lines: turning surface snapping on once turns it on
        // for the drag *and* for the drop, so the two cannot disagree about a ramp.
        gizmo.Snap.SnapToSurface = true;

        Assert.True(placement.Snap.SnapToSurface);
        Assert.Same(gizmo.Snap, placement.Snap);
    }
}
