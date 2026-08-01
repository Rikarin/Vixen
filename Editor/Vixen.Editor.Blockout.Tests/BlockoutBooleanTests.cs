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
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 24's P6 and P7 against a scene: derived geometry, and geometry leaving the editor.</summary>
public class BlockoutBooleanTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-csg-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly MeshEdit editing;
    readonly TransformSystem transforms = new();

    public BlockoutBooleanTests() {
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

    Entity Box(Vector3 size, Vector3 at = default) {
        var entity = BlockoutCreate.Shape(scene, new ShapeParameters { Kind = ShapeKind.Box, Size = size }, at);

        Settle();

        return entity;
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

    [Fact]
    public void A_subtract_makes_a_derived_entity_whose_operands_survive_as_hidden_children() {
        var wall = Box(new(4f, 3f, 0.5f));
        var hole = Box(new(1f, 2f, 2f));

        scene.Selection.Set([wall, hole]);

        var result = BlockoutBoolean.Subtract(scene);

        Settle();

        Assert.NotEqual(Entity.Null, result);
        Assert.True(scene.IsDerived(result));

        // ⚠ The operands are still there, still editable, and merely not drawn — which is the whole
        // of what "non-destructive" buys and is why the phase is worth having.
        var operands = SceneCsg.Operands(scene, result);

        Assert.Equal([wall, hole], operands);
        Assert.All(operands, entity => Assert.True(scene.IsHidden(entity)));
        Assert.All(operands, entity => Assert.NotNull(scene.MeshOf(entity)));

        Assert.Equal((4f * 3f * 0.5f) - (1f * 2f * 0.5f), Volume(scene.MeshOf(result)!), 2);
    }

    [Fact]
    public void Moving_an_operand_re_evaluates_the_result() {
        var wall = Box(new(4f, 3f, 0.5f));
        var hole = Box(new(1f, 2f, 2f));

        scene.Selection.Set([wall, hole]);

        var result = BlockoutBoolean.Subtract(scene);

        Settle();

        var before = Volume(scene.MeshOf(result)!);

        // Slide the cutter half out of the wall. Nothing about its geometry changed — only where it
        // is — which is most of what dragging a boolean's operand is.
        world.Get<LocalTransform>(hole).Position = new(2f, 0f, 0f);

        Settle();

        Assert.Equal(1, SceneCsg.Refresh(scene));

        var after = Volume(scene.MeshOf(result)!);

        Assert.True(after > before, $"the hole should have shrunk: {before} then {after}");

        // ⚠ And a frame that changed nothing costs nothing, which is what makes this pulled rather
        // than pushed.
        Assert.Equal(0, SceneCsg.Refresh(scene));
    }

    [Fact]
    public void Widening_an_operands_parameters_re_evaluates_the_result() {
        var wall = Box(new(4f, 3f, 0.5f));
        var hole = Box(new(1f, 2f, 2f));

        scene.Selection.Set([wall, hole]);

        var result = BlockoutBoolean.Subtract(scene);

        Settle();

        var before = Volume(scene.MeshOf(result)!);

        // ⚠ "That corridor should be a metre wider" reaching all the way through a boolean, which is
        // the sentence doc 24's D6 and P6 are both written about.
        Assert.True(BlockoutCreate.Resize(scene, hole, scene.ShapeOf(hole)!.Value with { Size = new(2f, 2f, 2f) }));

        Settle();
        SceneCsg.Refresh(scene);

        Assert.Equal(before - (1f * 2f * 0.5f), Volume(scene.MeshOf(result)!), 2);
    }

    [Fact]
    public void Applying_a_boolean_keeps_the_geometry_and_deletes_the_operands() {
        var wall = Box(new(4f, 3f, 0.5f));
        var hole = Box(new(1f, 2f, 2f));

        scene.Selection.Set([wall, hole]);

        var result = BlockoutBoolean.Subtract(scene);

        Settle();

        var before = Volume(scene.MeshOf(result)!);

        Assert.True(BlockoutBoolean.Collapse(scene, result));

        Settle();

        Assert.False(scene.IsDerived(result));
        Assert.False(world.IsAlive(wall));
        Assert.False(world.IsAlive(hole));
        Assert.Equal(before, Volume(scene.MeshOf(result)!), 3);

        // One undo for the whole apply, which is one thing somebody did.
        scene.Stack.Undo();
        Settle();

        Assert.True(scene.IsDerived(result));
        Assert.Equal(2, SceneCsg.Operands(scene, result).Count);
    }

    [Fact]
    public void Editing_a_face_of_a_derived_mesh_collapses_the_boolean_first() {
        var wall = Box(new(4f, 3f, 0.5f));
        var hole = Box(new(1f, 2f, 2f));

        scene.Selection.Set([wall, hole]);

        var result = BlockoutBoolean.Subtract(scene);

        Settle();
        scene.Selection.Set(result);
        editing.Enter(MeshElementKind.Face);
        editing.Selection.Set(0);

        Assert.True(BlockoutGeometry.Extrude(editing, 1f));

        // ⚠ The same one-way door the parametric shapes have, for the same reason: an edit to derived
        // geometry is an edit the next re-evaluation would overwrite without saying so.
        Assert.False(scene.IsDerived(result));
    }

    [Fact]
    public void A_derived_entity_writes_its_operation_and_not_its_mesh_and_rebuilds_on_load() {
        var wall = Box(new(4f, 3f, 0.5f));
        var hole = Box(new(1f, 2f, 2f));

        scene.Selection.Set([wall, hole]);

        var result = BlockoutBoolean.Subtract(scene);

        Settle();

        var file = SceneSerializer.ToFile(scene);
        var written = file.Roots.Single();

        Assert.Equal("Difference", written.Boolean);
        Assert.Null(written.Mesh);
        Assert.Equal(2, written.Children.Count);

        using var second = new World("Reload");

        var reopened = new SceneDocument(project, second, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reopened, SceneFile.FromYaml(file.ToYaml()));

        transforms.Resolve(second);
        second.AdvanceVersion();

        var made = reopened.Roots.Single();

        Assert.True(reopened.IsDerived(made));

        // ⚠ Rebuilt rather than read, which is also what makes a scene whose boolean has been improved
        // since it was saved come back improved rather than stale.
        Assert.Equal(1, SceneCsg.Refresh(reopened));
        Assert.Equal(Volume(scene.MeshOf(result)!), Volume(reopened.MeshOf(made)!), 2);
    }

    [Fact]
    public void A_plane_cut_takes_the_selection_apart_and_is_one_undo_entry() {
        var box = Box(new(4f, 4f, 4f));

        scene.Selection.Set(box);

        var steps = scene.Stack.History.Count;

        Assert.Equal(1, BlockoutBoolean.PlaneCut(scene, new Plane(Vector3.UnitY, -2f)));

        Assert.Equal(4f * 2f * 4f, Volume(scene.MeshOf(box)!), 2);
        Assert.Equal(steps + 1, scene.Stack.History.Count);

        // And it demoted, because a cut box is not a box's three extents.
        Assert.True(scene.IsPlainMesh(box));

        scene.Stack.Undo();

        Assert.True(scene.IsParametric(box));
        Assert.Equal(64f, Volume(scene.MeshOf(box)!), 2);
    }

    [Fact]
    public void A_cut_is_square_to_a_wall_that_has_been_rotated() {
        var wall = Box(new(4f, 4f, 4f));

        world.Get<LocalTransform>(wall).Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI * 0.25f);

        Settle();
        scene.Selection.Set(wall);

        // ⚠ The plane arrives in world space and is taken into the mesh's own. A caller that skipped
        // that conversion would cut this wall on a diagonal and every unrotated one correctly, which
        // is the shape of a bug nobody attributes to the rotation.
        Assert.Equal(1, BlockoutBoolean.PlaneCut(scene, new Plane(Vector3.UnitY, -2f)));
        Assert.Equal(4f * 2f * 4f, Volume(scene.MeshOf(wall)!), 2);
        Assert.Equal(2f, scene.MeshOf(wall)!.Bounds.Maximum.Y, 3);
    }

    [Fact]
    public void A_trim_removes_the_material_and_the_cutter() {
        var wall = Box(new(6f, 4f, 0.5f));
        var cutter = Box(new(2f, 2f, 4f));

        scene.Selection.Set([wall, cutter]);

        Assert.True(BlockoutBoolean.Trim(scene));

        Settle();

        Assert.False(world.IsAlive(cutter));
        Assert.False(scene.MeshOf(wall)!.Validate().IsClosed, "a trim leaves the opening bare");
    }

    // ── P7 ──────────────────────────────────────────────────────────────────────────────────────

    sealed class Baker : IMeshBaker {
        public string? Content { get; private set; }

        public string? Extension { get; private set; }

        public AssetReference Made { get; } = new(AssetId.New());

        public AssetReference Bake(string name, string extension, string content) {
            Content = content;
            Extension = extension;

            return Made;
        }
    }

    sealed class Source(AssetReference reference, MeshData mesh) : IMeshSource {
        public bool TryGet(AssetReference asked, out MeshData data) {
            data = mesh;

            return asked == reference;
        }
    }

    [Fact]
    public void Baking_writes_an_obj_in_the_entitys_own_space_and_points_it_at_the_asset() {
        var box = Box(new(2f, 2f, 2f), new(17f, 0f, -9f));

        scene.Selection.Set(box);

        var baker = new Baker();

        Assert.Equal(1, BlockoutHandoff.Bake(scene, baker));

        Assert.Equal(".obj", baker.Extension);
        Assert.Contains("v 1 2 1", baker.Content);

        // ⚠ In the entity's own space, so nothing in the file mentions where in the level it was
        // standing. An export centred on the world would give a mesh that arrives seventeen metres out.
        Assert.DoesNotContain("17", baker.Content);

        Assert.Null(scene.MeshOf(box));
        Assert.True(MeshRenderables.TryGet(world, box, out var renderable));
        Assert.Equal(baker.Made, renderable.Mesh);

        // And an undo puts the geometry back, because the command recorded it.
        scene.Stack.Undo();
        Assert.NotNull(scene.MeshOf(box));
    }

    [Fact]
    public void A_baked_asset_comes_back_editable_and_the_same_shape() {
        var box = Box(new(2f, 3f, 4f));

        scene.Selection.Set(box);

        var before = Volume(scene.MeshOf(box)!);
        var data = scene.MeshOf(box)!.ToMeshData();
        var baker = new Baker();

        Assert.Equal(1, BlockoutHandoff.Bake(scene, baker));

        Assert.Equal(1, BlockoutHandoff.Editable(scene, new Source(baker.Made, data)));

        // ⚠ P7's exit criterion in one assertion: the geometry crossed the boundary twice and the
        // level did not change shape.
        Assert.Equal(before, Volume(scene.MeshOf(box)!), 3);
        Assert.True(scene.MeshOf(box)!.Validate().IsClosed, "a mesh made editable is still a solid");
    }

    [Fact]
    public void An_obj_export_of_two_entities_counts_its_vertices_across_the_whole_file() {
        var one = Box(new(2f, 2f, 2f), new(-4f, 0f, 0f));
        var other = Box(new(2f, 2f, 2f), new(4f, 0f, 0f));

        scene.Selection.Set([one, other]);

        var text = BlockoutHandoff.Export(scene);

        Assert.Equal(2, text.Split("\no ").Length - 1);

        // ⚠ OBJ counts vertices from one across the whole document, and the second object's faces
        // naming the first object's vertices is the single most common way a hand-written exporter is
        // wrong — in a way that produces a file which opens. Two boxes are sixteen vertices and twelve
        // normals, so the last face has to name numbers in the second half of both.
        Assert.Contains("//7", text);
        Assert.Contains("16//", text);
        Assert.DoesNotContain("17//", text);
    }

    [Fact]
    public void A_gltf_export_is_one_self_contained_document() {
        var box = Box(new(2f, 2f, 2f));

        scene.Selection.Set(box);

        var text = BlockoutHandoff.Export(scene, ".gltf");

        Assert.Contains("\"version\":\"2.0\"", text);
        Assert.Contains("data:application/octet-stream;base64,", text);
        Assert.Contains("\"POSITION\"", text);

        // A POSITION accessor is the one glTF requires bounds on, because a reader frames the scene
        // from them before it has looked at a vertex.
        Assert.Contains("\"min\":", text);
        Assert.Contains("\"max\":", text);
    }

    [Fact]
    public void Collision_is_a_box_per_shell_and_the_mesh_when_that_is_not_enough() {
        var one = Box(new(2f, 2f, 2f));
        var other = Box(new(2f, 2f, 2f), new(6f, 0f, 0f));

        scene.Selection.Set([one, other]);

        var joined = BlockoutBoolean.Union(scene);

        Settle();

        List<BoundingBox> boxes = [];

        // Two boxes that do not touch are two shells, so two colliders — both of which can be given
        // to a body that moves, where one mesh collider could not.
        Assert.Equal(2, BlockoutHandoff.Collision(scene, joined, boxes));
        Assert.All(boxes, box => Assert.Equal(new Vector3(2f, 2f, 2f), box.Maximum - box.Minimum));

        var (positions, indices) = BlockoutHandoff.CollisionMesh(scene, joined);

        Assert.NotEmpty(positions);
        Assert.Equal(0, indices.Length % 3);
    }
}
