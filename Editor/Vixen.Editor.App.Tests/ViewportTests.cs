// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's E2 exit criteria, driven through the shell rather than through the model.</summary>
public class ViewportTests {
    [Fact]
    public void Four_panes_have_four_cameras_and_four_view_modes() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Run("scene.panes-quad");
        fixture.Frames(2);

        var panes = fixture.Viewports;

        Assert.Equal(4, panes.Count);

        // ⚠ The classic four rather than four of the same. A layout that came up as four identical
        // perspective views is one every user then configures by hand — and it is also the layout in
        // which a presenter shared between panes is invisible, because all four would agree.
        Assert.True(panes.Take(3).All(pane => pane.Camera.IsOrthographic));
        Assert.False(panes[3].Camera.IsOrthographic);

        Assert.Equal(4, panes.Select(pane => (pane.Camera.Yaw, pane.Camera.Pitch)).Distinct().Count());

        // A view mode is stage state, so a pane that wanted its own needs its own everything — see
        // `ViewModes.ApplyTo` for what sharing less than the whole pane costs.
        panes[0].Modes.Current = ViewMode.Wireframe;

        Assert.Equal(ViewMode.Wireframe, panes[0].Modes.Current);
        Assert.All(panes.Skip(1), pane => Assert.Equal(ViewMode.Shaded, pane.Modes.Current));
    }

    [Fact]
    public void A_scene_command_acts_on_the_focused_pane_and_not_its_neighbours() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Run("scene.panes-side-by-side");
        fixture.Frames(2);

        var panes = fixture.Viewports;

        Assert.Equal(2, panes.Count);
        Assert.Same(panes[0], fixture.Viewport);

        fixture.Run("scene.view-mode-wireframe");

        Assert.Equal(ViewMode.Wireframe, panes[0].Modes.Current);
        Assert.Equal(ViewMode.Shaded, panes[1].Modes.Current);

        // Clicking in a pane focuses it, and the next command follows the work rather than the
        // arrangement's reading order.
        fixture.Document.Focus(panes[1].Control);
        fixture.Frames(1);

        Assert.Same(panes[1], fixture.Viewport);

        fixture.Run("scene.view-mode-unlit");

        Assert.Equal(ViewMode.Wireframe, panes[0].Modes.Current);
        Assert.Equal(ViewMode.Unlit, panes[1].Modes.Current);
    }

    /// <summary>
    ///     Maximise gives the Scene panel the whole window and gives the window back, splits and all.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The panel, not the pane count, and the difference is why this command did nothing at
    ///     all.</b> It used to set the arrangement to Single and remember what it had been — so in a
    ///     single-pane layout, which is the default and what nearly everyone is in, it asked the
    ///     arrangement to become what it already was and the setter returned. The button was inert in
    ///     exactly the case it is pressed.
    /// </remarks>
    [Fact]
    public void Maximise_gives_the_scene_the_window_and_gives_it_back() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Open("hierarchy");
        fixture.Frames(2);

        Assert.True(fixture.Shell.Workspace.IsOpen("hierarchy"));

        fixture.Run("scene.maximise");
        fixture.Frames(2);

        // Everything but the Scene tab has gone, and the pane count inside it is untouched.
        Assert.True(fixture.Shell.Workspace.IsOpen("scene"));
        Assert.False(fixture.Shell.Workspace.IsOpen("hierarchy"));

        fixture.Run("scene.maximise");
        fixture.Frames(2);

        // ⚠ Back to what was there rather than to a preset. A maximise that returned by re-applying
        // the default layout would silently throw away every splitter the user had dragged.
        Assert.True(fixture.Shell.Workspace.IsOpen("scene"));
        Assert.True(fixture.Shell.Workspace.IsOpen("hierarchy"));
    }

    /// <summary>And it does something in the single-pane layout, which is where it did nothing.</summary>
    [Fact]
    public void Maximise_works_in_the_layout_that_is_already_one_pane() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Open("inspector");
        fixture.Frames(2);

        Assert.Single(fixture.Viewports);

        fixture.Run("scene.maximise");
        fixture.Frames(2);

        Assert.False(fixture.Shell.Workspace.IsOpen("inspector"));
    }

    [Fact]
    public void The_two_view_modes_the_tool_renderer_cannot_draw_are_disabled_with_a_reason() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(1);

        // Doc 20's first bar: a verb that is not implemented is *visibly* not implemented rather than
        // absent. For a view mode the alternative is worse than absence — a mode with no compositor
        // falls back to shaded, so the line would draw the picture of the line above it.
        Assert.True(fixture.CanRun("scene.view-mode-normal"));
        Assert.False(fixture.CanRun("scene.view-mode-overdraw"));
        Assert.False(fixture.CanRun("scene.view-mode-light-complexity"));

        // ✅ And roughness has come off that list, which is what the enablement being derived from
        // `ViewShading.IsSupported` rather than written out here is worth: the viewport can read a
        // material now, so the menu line enabled itself.
        Assert.True(fixture.CanRun("scene.view-mode-roughness"));

        Assert.True(fixture.Shell.Commands.TryGet("scene.view-mode-overdraw", out var command));
        Assert.False(string.IsNullOrWhiteSpace(command!.Unavailable.Text));
    }

    [Fact]
    public void The_show_flags_are_per_pane_and_the_grid_has_exactly_one_command() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Run("scene.panes-side-by-side");
        fixture.Frames(2);

        var panes = fixture.Viewports;

        fixture.Run("scene.toggle-grid");

        Assert.Equal(SceneShow.None, panes[0].Show & SceneShow.Grid);
        Assert.Equal(SceneShow.Grid, panes[1].Show & SceneShow.Grid);

        // ⚠ One command over the grid's flag and not two. A second id registered only so the Show
        // menu could be built in a loop is two menu lines that can disagree about what is on.
        Assert.False(fixture.Shell.Commands.TryGet("scene.show-grid", out _));

        fixture.Run("scene.show-bounds");

        Assert.Equal(SceneShow.Bounds, panes[0].Show & SceneShow.Bounds);
        Assert.Equal(SceneShow.None, panes[1].Show & SceneShow.Bounds);
    }

    [Fact]
    public void A_bookmark_is_saved_by_one_command_and_recalled_by_another() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(1);

        var pane = fixture.Viewport!;

        // ⚠ Recall is disabled until something has been saved. A key that does nothing and a key that
        // does nothing *yet* look identical while you are pressing it.
        Assert.False(fixture.CanRun("scene.bookmark-go-1"));

        pane.Camera.Pivot = new Vector3(7f, 1f, 2f);
        fixture.Run("scene.bookmark-set-1");

        Assert.True(fixture.CanRun("scene.bookmark-go-1"));

        pane.Camera.Pivot = Vector3.Zero;
        fixture.Run("scene.bookmark-go-1");

        Assert.Equal(new Vector3(7f, 1f, 2f), pane.Camera.Pivot);
    }

    [Fact]
    public void Snap_to_floor_drops_the_selection_onto_what_is_under_it_and_undoes() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(1);

        var scene = fixture.Scene;
        var crate = scene.Entities.First(entity => scene.NameOf(entity) == "Crate");

        scene.Selection.Set([crate]);
        fixture.Frames(1);

        var before = fixture.Scene.World.Read<Vixen.Engine.Transforms.LocalTransform>(crate).Position;

        Assert.True(fixture.CanRun("entity.snap-to-floor"));
        fixture.Run("entity.snap-to-floor");
        fixture.Frames(1);

        var after = fixture.Scene.World.Read<Vixen.Engine.Transforms.LocalTransform>(crate).Position;

        // Nothing is under the seeded crate but the ground plane, so it lands on zero — a verb that
        // only worked once there was floor geometry would be one nobody would find out worked.
        Assert.Equal(0f, after.Y, 4);
        Assert.NotEqual(before.Y, after.Y);

        fixture.Run("edit.undo");
        fixture.Frames(1);

        Assert.Equal(before.Y, fixture.Scene.World.Read<Vixen.Engine.Transforms.LocalTransform>(crate).Position.Y, 4);
    }

    [Fact]
    public void Move_to_view_puts_the_selection_where_the_camera_is_looking() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(1);

        var scene = fixture.Scene;
        var crate = scene.Entities.First(entity => scene.NameOf(entity) == "Crate");

        scene.Selection.Set([crate]);
        fixture.Frames(1);

        fixture.Viewport!.Camera.Pivot = new Vector3(12f, 3f, -4f);

        Assert.True(fixture.CanRun("entity.move-to-view"));
        fixture.Run("entity.move-to-view");
        fixture.Frames(1);

        var position = fixture.Scene.World.Read<Vixen.Engine.Transforms.WorldTransform>(crate).Value.Translation;

        // ⚠ At the pivot, not at the eye. Moving something to the eye puts it inside the near plane,
        // which reads as the object having vanished.
        Assert.True(Vector3.NearEqual(position, new Vector3(12f, 3f, -4f), 1e-3f));
    }

    /// <summary>
    ///     ⚠ <b>Every toggle in the Show menu reset on every launch.</b> The flags live on the pane,
    ///     the pane is rebuilt whenever the panel is reopened, and nothing wrote them anywhere — so
    ///     somebody who works with the grid off turned it off again every morning.
    /// </summary>
    [Fact]
    public void What_a_pane_is_drawing_survives_a_restart() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(1);

        var pane = fixture.Viewport!;

        Assert.True((pane.Show & SceneShow.Grid) != 0, "the grid should start on");

        // ⚠ `scene.toggle-grid` rather than `scene.show-grid`. The grid's toggle predates the
        // generated ones and is deliberately not registered twice — see `ShowFlagCommands`.
        fixture.Run("scene.toggle-grid");
        fixture.Frames(2);

        Assert.True((fixture.Viewport!.Show & SceneShow.Grid) == 0);

        fixture.Restart();
        fixture.Open("scene");
        fixture.Frames(2);

        Assert.True(
            (fixture.Viewport!.Show & SceneShow.Grid) == 0,
            "the grid came back on after being turned off and the editor restarted"
        );

        // ⚠ And the rest of the set is untouched, which is what says the whole bitset round-tripped
        // rather than one flag being remembered by name.
        Assert.True((fixture.Viewport!.Show & SceneShow.Gizmos) != 0);
    }

    /// <summary>The view mode goes the same way, through the same entry.</summary>
    [Fact]
    public void So_does_the_view_mode() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(1);

        fixture.Viewport!.Modes.Current = ViewMode.Wireframe;
        fixture.Frames(2);

        fixture.Restart();
        fixture.Open("scene");
        fixture.Frames(2);

        Assert.Equal(ViewMode.Wireframe, fixture.Viewport!.Modes.Current);
    }
}
