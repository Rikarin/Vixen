// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 24's P4: shapes with live parameters, the one-way door, and the verbs that repeat them.</summary>
public class BlockoutCreateTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-create-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly MeshEdit editing;
    readonly TransformSystem transforms = new();

    public BlockoutCreateTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
        editing = new(scene);
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    void Settle() {
        transforms.Resolve(world);
        world.AdvanceVersion();
    }

    [Fact]
    public void A_created_shape_has_a_mesh_and_the_parameters_that_made_it() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Stairs);

        Assert.True(scene.IsParametric(entity));
        Assert.False(scene.IsPlainMesh(entity));

        var mesh = scene.MeshOf(entity);

        Assert.NotNull(mesh);
        Assert.True(mesh.Validate().IsClosed, "a created staircase is closed");

        // ⚠ The mesh is there from the moment the shape is, which is what P4 changed about P2's
        // demotion timing: an element mode has a cage to draw on a parametric entity, so entering one
        // costs it nothing.
        Assert.Equal("Stairs", scene.NameOf(entity));
    }

    [Fact]
    public void Changing_a_parameter_rebuilds_the_mesh_and_undoes_in_one_step() {
        var entity = BlockoutCreate.Shape(scene, new ShapeParameters { Kind = ShapeKind.Box, Size = new(2f, 2f, 2f) });

        var before = scene.MeshOf(entity)!.Bounds;
        var steps = scene.Stack.History.Count;

        Assert.True(BlockoutCreate.Resize(scene, entity, scene.ShapeOf(entity)!.Value with { Size = new(6f, 2f, 2f) }));

        Assert.Equal(6f, scene.MeshOf(entity)!.Bounds.Maximum.X - scene.MeshOf(entity)!.Bounds.Minimum.X, 3);
        Assert.Equal(steps + 1, scene.Stack.History.Count);

        scene.Stack.Undo();

        Assert.Equal(before.Maximum.X, scene.MeshOf(entity)!.Bounds.Maximum.X, 3);
    }

    [Fact]
    public void Two_parameter_changes_in_a_row_are_one_history_entry() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Box);
        var steps = scene.Stack.History.Count;

        for (var width = 3f; width <= 6f; width++) {
            BlockoutCreate.Resize(scene, entity, scene.ShapeOf(entity)!.Value with { Size = new(width, 2f, 2f) });
        }

        // ⚠ One entry for four frames of a drag, which is `ShapeCommand.TryMergeWith` — and undoing it
        // has to go back to where the drag started rather than to its penultimate frame.
        Assert.Equal(steps + 1, scene.Stack.History.Count);

        scene.Stack.Undo();

        Assert.Equal(2f, scene.MeshOf(entity)!.Bounds.Maximum.X - scene.MeshOf(entity)!.Bounds.Minimum.X, 3);
    }

    [Fact]
    public void Entering_an_element_mode_leaves_a_shape_parametric_and_editing_a_face_does_not() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Box);

        Settle();
        scene.Selection.Set(entity);

        // ⚠ P2 demoted here and P4 does not, because P4 is what gave a parametric entity a mesh. A
        // designer who presses 4 to look at the faces has not thrown anything away.
        editing.Enter(MeshElementKind.Face);

        Assert.True(editing.IsActive);
        Assert.True(scene.IsParametric(entity));

        editing.Selection.Set(0);
        Assert.True(BlockoutGeometry.Extrude(editing, 1f));

        // And now it is a plain mesh, which is D6's one-way door.
        Assert.False(scene.IsParametric(entity));
        Assert.True(scene.IsPlainMesh(entity));
    }

    [Fact]
    public void The_demotion_asks_once_and_a_refusal_leaves_the_shape_alone() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Box);

        Settle();
        scene.Selection.Set(entity);

        var asked = 0;

        editing.Confirm = _ => {
            asked++;
            return false;
        };

        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        // Refused: the verb declines and nothing about the shape changed.
        Assert.False(BlockoutGeometry.Extrude(editing, 1f));
        Assert.Equal(1, asked);
        Assert.True(scene.IsParametric(entity));

        editing.Confirm = _ => {
            asked++;
            return true;
        };

        Assert.True(BlockoutGeometry.Extrude(editing, 1f));
        Assert.Equal(2, asked);
        Assert.False(scene.IsParametric(entity));

        // ⚠ And never again, which is what "the first time" means. A dialog on every wall is one
        // people learn to dismiss without reading; the badge is what tells them afterwards.
        var second = BlockoutCreate.Shape(scene, ShapeKind.Box);

        Settle();
        scene.Selection.Set(second);
        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        Assert.True(BlockoutGeometry.Extrude(editing, 1f));
        Assert.Equal(2, asked);
    }

    [Fact]
    public void Undoing_the_demotion_gives_the_parameters_back() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Box);

        Settle();
        scene.Selection.Set(entity);
        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        Assert.True(BlockoutGeometry.Extrude(editing, 1f));
        Assert.False(scene.IsParametric(entity));

        // The extrude, then the demotion: two entries, because they are two things somebody did and
        // the second is the one they are entitled to step back over on its own.
        scene.Stack.Undo();
        Assert.False(scene.IsParametric(entity));

        scene.Stack.Undo();
        Assert.True(scene.IsParametric(entity));
    }

    [Fact]
    public void A_parametric_entity_writes_its_parameters_and_not_its_mesh() {
        var entity = BlockoutCreate.Shape(scene, new ShapeParameters { Kind = ShapeKind.Arch, Size = new(4f, 3f, 0.5f), Sides = 8, Thickness = 0.4f, Inner = 0.5f });

        Settle();

        var file = SceneSerializer.ToFile(scene);
        var written = file.Roots[0];

        Assert.Null(written.Mesh);
        Assert.NotNull(written.Parameters);
        Assert.Equal("Arch", written.Parameters.Kind);

        // And it comes back as the same geometry, regenerated rather than read.
        using var second = new World("Reload");

        var reloaded = new SceneDocument(project, second, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reloaded, SceneFile.FromYaml(file.ToYaml()));

        var made = reloaded.Roots.Single();

        Assert.True(reloaded.IsParametric(made));
        Assert.Equal(scene.MeshOf(entity)!.FaceCount, reloaded.MeshOf(made)!.FaceCount);
    }

    [Fact]
    public void A_duplicate_carries_the_mesh_the_parameters_and_the_materials() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Cylinder);

        scene.SetMaterial(entity, MeshShapes.GroupTop, new AssetReference(AssetId.New()));

        Settle();
        scene.Selection.Set(entity);

        Assert.Equal(1, BlockoutCreate.Duplicate(scene, new(4f, 0f, 0f)));

        var copy = scene.Selection.Items.Single();

        Assert.NotEqual(entity, copy);
        Assert.True(scene.IsParametric(copy));
        Assert.Equal(scene.MeshOf(entity)!.FaceCount, scene.MeshOf(copy)!.FaceCount);
        Assert.Single(scene.MaterialsOf(copy));

        Assert.Equal(4f, world.Read<LocalTransform>(copy).Position.X, 3);

        // One undo for the whole duplicate, which is one thing somebody did.
        scene.Stack.Undo();
        Assert.False(world.IsAlive(copy));
    }

    [Fact]
    public void A_mirrored_copy_is_on_the_far_side_and_is_not_inside_out() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Stairs, new(3f, 0f, 0f));

        Settle();
        scene.Selection.Set(entity);

        Assert.Equal(1, BlockoutCreate.Mirror(scene, new Plane(Vector3.UnitX, 0f)));

        var copy = scene.Selection.Items.Single();

        Assert.Equal(-3f, world.Read<LocalTransform>(copy).Position.X, 3);

        // ⚠ Reflected and re-wound rather than given a negative scale, so the copy is a solid in its
        // own right — which a signed volume is what notices.
        var report = scene.MeshOf(copy)!.Validate();

        Assert.True(report.IsClosed, report.Describe() ?? "closed");
        Assert.True(report.IsConsistent, report.Describe() ?? "consistent");
        Assert.True(Volume(scene.MeshOf(copy)!) > 0f, "a mirrored staircase is not inside out");

        // And it is a plain mesh now, because a reflected staircase is not a staircase's parameters.
        Assert.True(scene.IsPlainMesh(copy));
    }

    [Fact]
    public void An_array_repeats_a_shape_along_a_line_as_one_undo_entry() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Box);

        Settle();

        var steps = scene.Stack.History.Count;

        Assert.Equal(4, BlockoutCreate.Array(scene, entity, new(2f, 0f, 0f), 4));
        Assert.Equal(steps + 1, scene.Stack.History.Count);

        Settle();

        Assert.Equal(5, scene.Roots.Count);

        scene.Stack.Undo();
        Assert.Single(scene.Roots);
    }

    [Fact]
    public void A_radial_array_of_seven_puts_the_eighth_gap_where_the_original_is() {
        var entity = BlockoutCreate.Shape(scene, ShapeKind.Cylinder, new(4f, 0f, 0f));

        Settle();

        Assert.Equal(7, BlockoutCreate.Radial(scene, entity, Vector3.Zero, Vector3.UnitY, 7));

        Settle();

        // ⚠ A full circle divides by the total rather than by the gaps, so eight columns come out
        // forty-five degrees apart and none of them lands on top of the original.
        foreach (var made in scene.Roots.Where(root => root != entity)) {
            Assert.Equal(4f, world.Read<LocalTransform>(made).Position.Length(), 3);
            Assert.True((world.Read<LocalTransform>(made).Position - new Vector3(4f, 0f, 0f)).Length() > 1f);
        }
    }

    [Fact]
    public void A_poly_shape_is_an_L_shaped_room_of_the_height_it_was_dragged_to() {
        Span<Vector2> footprint = [new(0f, 0f), new(4f, 0f), new(4f, 2f), new(2f, 2f), new(2f, 5f), new(0f, 5f)];

        var entity = BlockoutCreate.Poly(scene, footprint.ToArray(), 3f);

        Assert.NotEqual(Entity.Null, entity);

        var mesh = scene.MeshOf(entity)!;

        Assert.True(mesh.Validate().IsClosed, "a poly shape is closed");
        Assert.Equal(14f * 3f, Volume(mesh), 3);

        // ⚠ Not parametric, and that is stated rather than accidental: a polygon of arbitrary length
        // is not six numbers, and what a designer does to one afterwards is move its corners.
        Assert.False(scene.IsParametric(entity));
    }

    [Fact]
    public void A_clockwise_footprint_makes_the_same_room_as_an_anticlockwise_one() {
        Vector2[] footprint = [new(0f, 0f), new(4f, 0f), new(4f, 4f), new(0f, 4f)];

        var one = BlockoutCreate.Poly(scene, footprint, 2f);

        System.Array.Reverse(footprint);

        var other = BlockoutCreate.Poly(scene, footprint, 2f);

        // Which way round a designer drags a room is not something they should have to know.
        Assert.Equal(Volume(scene.MeshOf(one)!), Volume(scene.MeshOf(other)!), 3);
        Assert.True(Volume(scene.MeshOf(other)!) > 0f, "a clockwise room is not inside out");
    }

    [Fact]
    public void A_cube_grid_box_is_a_whole_number_of_cells_and_reads_back_as_one() {
        var plane = new WorkPlane { Step = 2f };
        var entity = BlockoutCubeGrid.Create(scene, new GridBox(1, 0, -2, 3, 2, 4), plane);

        Settle();

        Assert.True(BlockoutCubeGrid.TryRead(scene, entity, plane, out var box));
        Assert.Equal(new GridBox(1, 0, -2, 3, 2, 4), box);

        // Two-metre cells, so a three-cell box is six metres.
        Assert.Equal(6f, scene.ShapeOf(entity)!.Value.Size.X, 3);
    }

    [Fact]
    public void Pushing_a_cube_grid_box_stays_parametric_and_moves_by_whole_cells() {
        var plane = new WorkPlane { Step = 1f };
        var entity = BlockoutCubeGrid.Create(scene, GridBox.At(0, 0, 0), plane);

        Settle();

        Assert.True(BlockoutCubeGrid.Push(scene, entity, axis: 1, positive: true, cells: 3, plane));

        Settle();

        Assert.True(scene.IsParametric(entity));
        Assert.True(BlockoutCubeGrid.TryRead(scene, entity, plane, out var box));
        Assert.Equal(new GridBox(0, 0, 0, 1, 4, 1), box);

        // ⚠ Pulling the near side in moves the origin as well as the extent, which is what makes
        // "pull this wall towards me" work rather than making the box grow the other way.
        Assert.True(BlockoutCubeGrid.Push(scene, entity, axis: 0, positive: false, cells: 2, plane));

        Settle();

        Assert.True(BlockoutCubeGrid.TryRead(scene, entity, plane, out var wider));
        Assert.Equal(new GridBox(-2, 0, 0, 3, 4, 1), wider);
    }

    [Fact]
    public void A_box_cannot_be_pushed_smaller_than_one_cell() {
        var plane = new WorkPlane { Step = 1f };
        var entity = BlockoutCubeGrid.Create(scene, GridBox.At(0, 0, 0), plane);

        Settle();

        Assert.False(BlockoutCubeGrid.Push(scene, entity, axis: 1, positive: true, cells: -4, plane));
        Assert.True(BlockoutCubeGrid.TryRead(scene, entity, plane, out var box));
        Assert.Equal(1, box.Height);
    }

    [Fact]
    public void Corner_mode_moves_a_corner_by_a_whole_cell_and_demotes() {
        var plane = new WorkPlane { Step = 1f };
        var entity = BlockoutCubeGrid.Create(scene, new GridBox(0, 0, 0, 2, 2, 2), plane);

        Settle();
        scene.Selection.Set(entity);
        editing.Enter(MeshElementKind.Vertex);

        var mesh = scene.MeshOf(entity)!;
        var highest = 0;

        for (var position = 1; position < mesh.PositionCount; position++) {
            if (mesh.Positions[position].Y > mesh.Positions[highest].Y) {
                highest = position;
            }
        }

        var was = mesh.Positions[highest];

        editing.Selection.Set(highest);

        Assert.True(BlockoutCubeGrid.Corner(editing, new(0f, -1f, 0f), plane));

        Assert.Equal(was.Y - 1f, scene.MeshOf(entity)!.Positions[highest].Y, 3);

        // A box with a corner pulled down is not a box's three extents any more.
        Assert.True(scene.IsPlainMesh(entity));
    }

    [Fact]
    public void A_ray_at_a_blockout_wall_selects_it_and_the_work_plane_can_land_on_it() {
        // A wall eight metres long whose entity's transform is uniform, which is what every P4 shape
        // is: the size lives in the geometry. A picker that only knew about `PrimitiveShape` was ray
        // testing a unit cube at the origin and missing everything but the very middle — clicking
        // selected nothing, while a marquee, which projects a point, worked.
        var wall = BlockoutCreate.Shape(
            scene,
            new ShapeParameters { Kind = ShapeKind.Box, Size = new(8f, 3f, 0.5f) }
        );

        Settle();

        var picker = new ScenePicker(scene);
        var camera = new EditorCamera();

        // Straight down the +Z axis at the middle of the wall, from well outside it.
        var ray = new Ray(new(3f, 1.5f, 10f), -Vector3.UnitZ);

        Assert.Equal(wall, picker.Under(ray, camera, 800, 600));

        // And the same geometry answers a surface probe, which is what "Work Plane to Face" asks —
        // it did nothing at all while the probe could only see primitives.
        var probe = new SceneProbe(scene);

        Assert.True(probe.Raycast(ray, out var hit));
        Assert.Equal(0.25f, hit.Point.Z, 3);
        Assert.Equal(1f, hit.Normal.Z, 3);

        // A point off the end of the wall misses it, so the fix is a hit test rather than a bounds
        // test that swallows the whole neighbourhood.
        Assert.Equal(Entity.Null, picker.Under(new Ray(new(20f, 1.5f, 10f), -Vector3.UnitZ), camera, 800, 600));
    }

    [Fact]
    public void The_shape_tool_drags_a_footprint_and_then_a_height() {
        var drag = new ShapeDrag(scene) { Kind = ShapeKind.Box };

        drag.Begin(new(0f, 0f, 0f));

        Assert.Equal(ShapeStage.Footprint, drag.Stage);

        drag.Drag(new(4f, 0f, 6f));

        Assert.True(drag.Settle());
        Assert.Equal(ShapeStage.Height, drag.Stage);

        drag.Raise(3f);

        var entity = drag.Commit();

        Assert.Equal(ShapeStage.Idle, drag.Stage);

        var shape = scene.ShapeOf(entity)!.Value;

        Assert.Equal(new Vector3(4f, 3f, 6f), shape.Size);

        // The origin is the middle of the footprint on the plane, so the box stands on it rather than
        // being buried half way into it.
        Assert.Equal(new Vector3(2f, 0f, 3f), world.Read<LocalTransform>(entity).Position);
        Assert.Equal(entity, scene.Selection.Items.Single());
    }

    [Fact]
    public void Cancelling_a_shape_drag_takes_the_entity_away_again() {
        var drag = new ShapeDrag(scene);

        drag.Begin(Vector3.Zero);
        drag.Drag(new(2f, 0f, 2f));

        var entity = drag.Entity;

        Assert.NotEqual(Entity.Null, entity);
        Assert.True(drag.Cancel());

        Assert.False(world.IsAlive(entity));
        Assert.Equal(ShapeStage.Idle, drag.Stage);
    }

    [Fact]
    public void A_press_and_release_with_no_drag_is_a_click_rather_than_a_shape() {
        var drag = new ShapeDrag(scene);

        drag.Begin(Vector3.Zero);

        // ⚠ Nothing was made, so settling declines — a tool that turned a click into a shape of no
        // size would make it impossible to click anything while it was armed.
        Assert.False(drag.Settle());
        Assert.Equal(ShapeStage.Idle, drag.Stage);
        Assert.Empty(scene.Roots);
    }

    /// <summary>Doc 24's P4 exit criterion, as a test rather than as a claim.</summary>
    /// <remarks>
    ///     A two-storey building with stairs between the floors, blocked out in one session, in a scene
    ///     that opens again. Every piece is a live parametric shape except the ones an edit has demoted,
    ///     the whole thing round-trips through the scene file, and the stairs reach the first floor.
    /// </remarks>
    [Fact]
    public void A_two_storey_building_with_stairs_between_the_floors_is_blocked_out_and_reopens() {
        const float storey = 3f;

        var plane = new WorkPlane { Step = 1f };

        // The ground floor and the first floor: two slabs, eight by ten, one on top of the other.
        var ground = BlockoutCubeGrid.Create(scene, new GridBox(-4, -1, -5, 8, 1, 10), plane);
        var first = BlockoutCubeGrid.Create(scene, new GridBox(-4, 3, -5, 8, 1, 10), plane);

        // Four walls round the ground floor, made by the shape tool the way a drag makes them.
        var north = BlockoutCreate.Shape(scene, new ShapeParameters { Kind = ShapeKind.Box, Size = new(8f, storey, 0.3f) }, new(0f, 0f, -5f));
        var south = BlockoutCreate.Shape(scene, new ShapeParameters { Kind = ShapeKind.DoorFrame, Size = new(8f, storey, 0.3f), Sides = 8, Thickness = 0.6f, Inner = 0.25f }, new(0f, 0f, 5f));

        // And the two long ones as one wall, mirrored — which is what a symmetrical room is for.
        var side = BlockoutCreate.Shape(scene, new ShapeParameters { Kind = ShapeKind.Box, Size = new(0.3f, storey, 10f) }, new(-4f, 0f, 0f));

        Settle();
        scene.Selection.Set(side);

        Assert.Equal(1, BlockoutCreate.Mirror(scene, new Plane(Vector3.UnitX, 0f)));

        var mirrored = scene.Selection.Items.Single();

        // The stairs, reaching exactly one storey plus the slab they land on.
        var stairs = BlockoutCreate.Shape(
            scene,
            new ShapeParameters { Kind = ShapeKind.Stairs, Size = new(1.2f, storey + 1f, 5f), Steps = 16 },
            new(2.5f, 0f, -2.5f)
        );

        // A hole in the first floor for them to come up through, which is where the block-out stops
        // being parametric — and the only place in the building that does.
        Settle();
        scene.Selection.Set(first);
        editing.Enter(MeshElementKind.Face);

        var top = Top(scene.MeshOf(first)!);

        editing.Selection.Set(top);

        Assert.True(BlockoutGeometry.Inset(editing, 3f));
        Assert.True(BlockoutGeometry.Delete(editing));

        Settle();

        // Two of the seven are plain meshes and both of them earned it: the floor somebody cut a hole
        // in, and the wall that was mirrored.
        Assert.True(scene.IsPlainMesh(first));
        Assert.True(scene.IsPlainMesh(mirrored));
        Assert.True(scene.IsParametric(ground));
        Assert.True(scene.IsParametric(stairs));
        Assert.True(scene.IsParametric(north));
        Assert.True(scene.IsParametric(south));

        // The stairs actually reach the first floor, which is the one thing "with stairs between the
        // floors" has to mean.
        Assert.True(scene.MeshOf(stairs)!.Bounds.Maximum.Y >= storey + 1f - 1e-3f);

        // Seven entities, and every one of them still a sound mesh.
        Assert.Equal(7, scene.Roots.Count);

        foreach (var entity in scene.Roots) {
            var report = scene.MeshOf(entity)!.Validate();

            Assert.True(report.IsConsistent, scene.NameOf(entity) + ": " + (report.Describe() ?? "consistent"));
        }

        // ⚠ And it opens again. A parametric piece comes back through its parameters and the edited
        // floor through its geometry, which is the whole of what the file has to get right.
        var yaml = SceneSerializer.ToYaml(scene);

        using var second = new World("Reload");

        var reopened = new SceneDocument(project, second, AssetId.Empty, "Untitled");

        Assert.Equal(7, SceneSerializer.Load(reopened, SceneFile.FromYaml(yaml)));
        Assert.Equal(5, reopened.Shapes.Count);
        Assert.Equal(7, reopened.Meshes.Count);

        // Byte-identical on the way out again, which is the format's own standing promise.
        Assert.Equal(yaml, SceneSerializer.ToYaml(reopened));
    }

    static int Top(EditMesh mesh) {
        var top = 0;

        for (var face = 1; face < mesh.FaceCount; face++) {
            if (mesh.Normal(face).Y > mesh.Normal(top).Y) {
                top = face;
            }
        }

        return top;
    }

    static float Volume(EditMesh mesh) {
        var triangles = mesh.Triangulate();
        var total = 0f;

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = mesh.Positions[triangles[index]];
            var b = mesh.Positions[triangles[index + 1]];
            var c = mesh.Positions[triangles[index + 2]];

            total += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
        }

        return total;
    }
}
