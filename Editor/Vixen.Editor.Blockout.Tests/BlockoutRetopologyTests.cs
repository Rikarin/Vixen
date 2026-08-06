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
using Vixen.Geometry.Uv;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>docs/plan/41 § D16's blockout row and docs/plan/42 § D13's panel, against a real scene.</summary>
public class BlockoutRetopologyTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-retopo-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly TransformSystem transforms = new();

    public BlockoutRetopologyTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Retopologizing_a_box_leaves_quads_on_the_entity() {
        var entity = Box();

        Assert.Equal(1, BlockoutRetopology.Run(scene, new() { TargetQuads = 200 }));

        var mesh = scene.MeshOf(entity)!;

        Assert.True(mesh.FaceCount > 0);

        for (var face = 0; face < mesh.FaceCount; face++) {
            Assert.Equal(4, mesh.Faces[face].Count);
        }
    }

    /// <summary>Doc 24's D3: a topology change records the whole mesh, because a boolean has no inverse.</summary>
    /// <remarks>
    ///     <b>The failure class this test exists for is an undo entry that does not restore.</b> A
    ///     retopology replaces every vertex and every face, so an entry that recorded anything less
    ///     than the mesh it replaced would come back as a mesh that is nearly the original — which is
    ///     the worst possible outcome, because it looks like it worked.
    /// </remarks>
    [Fact]
    public void One_undo_puts_the_original_mesh_back_exactly() {
        var entity = Box();
        var before = new EditMesh(scene.MeshOf(entity)!);

        Assert.Equal(1, BlockoutRetopology.Run(scene, new() { TargetQuads = 200 }));
        Assert.NotEqual(before.FaceCount, scene.MeshOf(entity)!.FaceCount);

        scene.Stack.Undo();

        var after = scene.MeshOf(entity)!;

        Assert.Equal(before.PositionCount, after.PositionCount);
        Assert.Equal(before.FaceCount, after.FaceCount);
        Assert.Equal(before.CornerCount, after.CornerCount);

        for (var index = 0; index < before.PositionCount; index++) {
            Assert.Equal(before.Positions[index], after.Positions[index]);
        }

        for (var index = 0; index < before.CornerCount; index++) {
            Assert.Equal(before.Corners[index], after.Corners[index]);
        }
    }

    /// <summary>One entry for the whole verb, however many entities it touched.</summary>
    [Fact]
    public void Two_entities_are_one_undo_entry() {
        var first = Box();
        var second = Box(new(3f, 0f, 0f));

        scene.Selection.Set([first, second]);

        var depth = scene.Stack.History.Count;

        Assert.Equal(2, BlockoutRetopology.Run(scene, new() { TargetQuads = 150 }));
        Assert.Equal(depth + 1, scene.Stack.History.Count);
    }

    /// <summary>The result is what is selected afterwards, which is § D16's third clause.</summary>
    [Fact]
    public void The_result_is_selected() {
        var entity = Box();

        BlockoutRetopology.Run(scene, new() { TargetQuads = 200 });

        Assert.Equal(entity, Assert.Single(scene.Selection.Items));
    }

    /// <summary>Nothing selected is nothing done, and no undo entry either.</summary>
    [Fact]
    public void An_empty_selection_leaves_the_stack_alone() {
        Box();
        scene.Selection.Clear();

        var depth = scene.Stack.History.Count;

        Assert.Equal(0, BlockoutRetopology.Run(scene, new() { TargetQuads = 200 }));
        Assert.Equal(depth, scene.Stack.History.Count);
    }

    /// <summary>The verb is registered on the mode, so a keymap and a menu can find it.</summary>
    [Fact]
    public void The_verb_is_one_of_the_handoff_commands() =>
        Assert.Contains(BlockoutMode.RetopologizeCommand, BlockoutMode.HandoffCommands);

    /// <summary>docs/plan/42 § D13's panel: the three verbs separately, over one mesh.</summary>
    [Fact]
    public void The_panel_runs_the_three_stages_and_describes_the_islands() {
        var panel = new BlockoutUvPanel {
            Mesh = MeshShapes.Create(ShapeKind.Box),
            Packing = new() { Resolution = 512, Margin = 2 }
        };

        Assert.True(panel.Chart());
        Assert.NotEmpty(panel.Charts);
        Assert.Empty(panel.Islands);

        Assert.True(panel.Flatten());
        Assert.NotEmpty(panel.Islands);
        Assert.Empty(panel.Placements);

        Assert.True(panel.Pack());
        Assert.NotEmpty(panel.Placements);
        Assert.Equal(panel.Islands.Count, panel.Views.Count);

        // Every island lands inside the atlas's unit square, which is what a packed layout means.
        foreach (var view in panel.Views) {
            Assert.InRange(view.Minimum.X, -1e-3f, 1.001f);
            Assert.InRange(view.Minimum.Y, -1e-3f, 1.001f);
            Assert.True(view.Maximum.X >= view.Minimum.X);
            Assert.True(view.Maximum.Y >= view.Minimum.Y);
        }
    }

    /// <summary>Packing on its own charts and flattens first, because it needs islands to place.</summary>
    [Fact]
    public void Packing_alone_runs_what_it_needs() {
        var panel = new BlockoutUvPanel { Mesh = MeshShapes.Create(ShapeKind.Box) };

        Assert.True(panel.Pack());
        Assert.NotEmpty(panel.Charts);
        Assert.NotEmpty(panel.Islands);
    }

    /// <summary>A seam is an edge whose two faces went into different charts, plus every open rim.</summary>
    [Fact]
    public void Seams_come_off_the_chart_assignment() {
        var panel = new BlockoutUvPanel { Mesh = MeshShapes.Create(ShapeKind.Box) };

        Assert.Empty(panel.Seams());
        Assert.True(panel.Chart());
        Assert.NotEmpty(panel.Seams());
    }

    /// <summary>Changing the mesh clears everything that described the last one.</summary>
    /// <remarks>
    ///     ⚠ <b>The stale-state failure a panel has.</b> Chart indices are per face and island corners
    ///     are per corner, so keeping either across a mesh change is keeping an index into an array
    ///     that is now a different length — which draws as an atlas belonging to the previous
    ///     selection.
    /// </remarks>
    [Fact]
    public void Setting_a_new_mesh_clears_the_derived_state() {
        var panel = new BlockoutUvPanel { Mesh = MeshShapes.Create(ShapeKind.Box) };

        Assert.True(panel.Pack());

        panel.Mesh = MeshShapes.Create(ShapeKind.Cylinder);

        Assert.Empty(panel.Charts);
        Assert.Empty(panel.Islands);
        Assert.Empty(panel.Placements);
        Assert.Empty(panel.Views);
    }

    /// <summary>No mesh is a message rather than an exception.</summary>
    [Fact]
    public void A_panel_with_no_mesh_says_so() {
        var panel = new BlockoutUvPanel();

        Assert.False(panel.Chart());
        Assert.NotEmpty(panel.Messages);
    }

    /// <summary>The heat ramp is anchored at "no distortion" rather than at what this atlas happens to have.</summary>
    [Fact]
    public void The_heat_ramp_starts_at_one() {
        Assert.Equal(0f, BlockoutUvPanel.Heat(new(0, Vector2.Zero, Vector2.One, 1f, 0)));
        Assert.True(BlockoutUvPanel.Heat(new(0, Vector2.Zero, Vector2.One, BlockoutUvPanel.BadDistortion, 0)) > 0f);
        Assert.Equal(1f, BlockoutUvPanel.Heat(new(0, Vector2.Zero, Vector2.One, 100f, 0)));
    }

    /// <summary>A flipped triangle is bad however low the stretch is.</summary>
    [Fact]
    public void A_flipped_triangle_makes_an_island_bad() {
        Assert.False(new UvIslandView(0, Vector2.Zero, Vector2.One, 1f, 0).IsBad);
        Assert.True(new UvIslandView(0, Vector2.Zero, Vector2.One, 1f, 1).IsBad);
    }

    Entity Box(Vector3 at = default) {
        var entity = BlockoutCreate.Shape(
            scene,
            new ShapeParameters { Kind = ShapeKind.Box, Size = Vector3.One },
            at
        );

        transforms.Resolve(world);
        world.AdvanceVersion();
        scene.Selection.Set(entity);

        return entity;
    }
}
