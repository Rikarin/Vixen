// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 24's P2 selection verbs, driven the way the mode drives them.</summary>
public class BlockoutSelectionTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-blockout-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly MeshEdit editing;
    readonly TransformSystem transforms = new();

    public BlockoutSelectionTests() {
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

    /// <summary>A cylinder, which is the primitive with quads to walk loops and rings through.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a cube, and it is the same reason <c>MeshShapes</c> in the kernel's tests is
    ///     quads.</b> A primitive arrives as a triangle soup, so a cube's every face is a triangle and
    ///     every corner has more than four edges — which is exactly what an edge loop stops on. A
    ///     cylinder's side is where a designer's loop actually runs.
    /// </remarks>
    Entity Cylinder() {
        var entity = scene.CreateShape(
            PrimitiveKind.Cylinder,
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }
        );

        transforms.Resolve(world);
        world.AdvanceVersion();

        scene.Selection.Set(entity);
        editing.Enter(MeshElementKind.Face);

        return entity;
    }

    [Fact]
    public void Select_all_takes_every_element_of_the_mode_and_none_deselects() {
        Cylinder();

        Assert.True(BlockoutSelection.All(editing));
        Assert.Equal(editing.Mesh!.FaceCount, editing.Selection.Count);

        Assert.True(BlockoutSelection.None(editing));
        Assert.True(editing.Selection.IsEmpty);
    }

    [Fact]
    public void Inverting_a_selection_of_one_leaves_everything_else() {
        var mesh = Meshed();

        editing.Selection.Set(0);

        Assert.True(BlockoutSelection.Invert(editing));
        Assert.Equal(mesh.FaceCount - 1, editing.Selection.Count);
    }

    [Fact]
    public void Growing_and_shrinking_a_face_selection_settle_on_a_closed_mesh() {
        var mesh = Meshed();

        BlockoutSelection.All(editing);
        Assert.True(BlockoutSelection.Grow(editing));
        Assert.Equal(mesh.FaceCount, editing.Selection.Count);
    }

    [Fact]
    public void Selecting_the_group_takes_the_whole_wall_rather_than_one_triangle() {
        var mesh = Meshed();

        editing.Selection.Set(0);

        Assert.True(BlockoutSelection.Group(editing));

        var group = mesh.Faces[0].Group;

        Assert.All(editing.Selection.Indices, face => Assert.Equal(group, mesh.Faces[face].Group));
        Assert.True(editing.Selection.Count >= 2, "a cylinder's cap is more than one triangle");
    }

    [Fact]
    public void Selecting_coplanar_takes_a_flat_cap_and_stops_at_its_rim() {
        var mesh = Meshed();

        // The face whose normal points most nearly straight up, which on a cylinder is the top cap.
        var cap = 0;

        for (var face = 1; face < mesh.FaceCount; face++) {
            if (mesh.Normal(face).Y > mesh.Normal(cap).Y) {
                cap = face;
            }
        }

        editing.Selection.Set(cap);

        Assert.True(BlockoutSelection.Coplanar(editing));
        Assert.All(editing.Selection.Indices, face => Assert.True(mesh.Normal(face).Y > 0.99f));
        Assert.True(editing.Selection.Count < mesh.FaceCount, "the sides are not coplanar with the cap");
    }

    [Fact]
    public void Selecting_linked_takes_the_whole_shell() {
        var mesh = Meshed();

        editing.Selection.Set(0);

        Assert.True(BlockoutSelection.Linked(editing));
        Assert.Equal(mesh.FaceCount, editing.Selection.Count);
    }

    [Fact]
    public void A_loop_asked_for_in_face_mode_converts_to_edges_rather_than_declining() {
        Meshed();

        editing.Selection.Set(0);

        Assert.Equal(MeshElementKind.Face, editing.Element);

        BlockoutSelection.Loop(editing);

        // ⚠ "Select loop" is a statement about edges whatever mode you are in. A key that did nothing
        // in three of the four modes is a key people conclude is broken.
        Assert.Equal(MeshElementKind.Edge, editing.Element);
    }

    [Fact]
    public void A_ring_through_an_edge_of_a_quad_strip_crosses_it() {
        var mesh = Quads();

        // The edge running across the strip between the first two quads.
        var across = mesh.EdgeBetween(1, 5);

        editing.Element = MeshElementKind.Edge;
        editing.Selection.Set(across);

        Assert.True(BlockoutSelection.Ring(editing));
        Assert.True(editing.Selection.Count >= 2, "a ring crosses more than the edge it started on");
        Assert.Contains(across, editing.Selection.Indices);
    }

    [Fact]
    public void Every_verb_declines_quietly_when_there_is_nothing_to_act_on() {
        // No entity, no mesh, no selection. Each of these is a key press somebody made, none is a
        // mistake, and a command that threw would take the editor down over a keystroke.
        Assert.False(BlockoutSelection.All(editing));
        Assert.False(BlockoutSelection.None(editing));
        Assert.False(BlockoutSelection.Invert(editing));
        Assert.False(BlockoutSelection.Grow(editing));
        Assert.False(BlockoutSelection.Shrink(editing));
        Assert.False(BlockoutSelection.Loop(editing));
        Assert.False(BlockoutSelection.Ring(editing));
        Assert.False(BlockoutSelection.Group(editing));
        Assert.False(BlockoutSelection.Coplanar(editing));
        Assert.False(BlockoutSelection.Linked(editing));
    }

    [Fact]
    public void The_mode_drives_the_editing_state_it_is_given() {
        using var shell = new EditorShell(1280f, 800f);

        var mode = new BlockoutMode { Editing = editing };

        shell.Modes.Add(mode);
        shell.Modes.Activate(BlockoutMode.ModeId);

        // ⚠ What `EditorApplication.RegisterModes` wires: entering a mode is a claim about the
        // viewport, and only the application knows that. Without it the mode's scoped commands are
        // out of scope and every one of them declines.
        shell.Context = BlockoutMode.BlockoutContext;

        Cylinder();
        editing.Exit();

        shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Edge));

        Assert.Equal(BlockoutElement.Edge, mode.Element);
        Assert.Equal(MeshElementKind.Edge, editing.Element);
        Assert.True(editing.IsActive);

        shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Object));

        Assert.False(editing.IsActive);
    }

    EditMesh Meshed() {
        Cylinder();
        return editing.Mesh!;
    }

    /// <summary>A strip of quads, put on the entity directly because no primitive is one.</summary>
    EditMesh Quads() {
        var entity = Cylinder();
        var mesh = new EditMesh();

        for (var z = 0; z < 2; z++) {
            for (var x = 0; x < 4; x++) {
                mesh.AddPosition(new Vector3(x, 0f, z));
            }
        }

        for (var x = 0; x < 3; x++) {
            mesh.AddFace([x, x + 1, x + 5, x + 4]);
        }

        scene.SetMesh(entity, mesh);
        editing.Reconcile();

        return editing.Mesh!;
    }
}
