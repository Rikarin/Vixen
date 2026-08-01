// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 24's P0, from the editor: a grid you can move, a step you can halve, a tape measure.</summary>
public class WorkPlaneCommandTests {
    [Fact]
    public void Every_pane_draws_the_same_work_plane() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Run("scene.panes-quad");
        fixture.Frames(2);

        var panes = fixture.Viewports;

        Assert.Equal(4, panes.Count);

        // ⚠ Doc 24's D5. The work plane is the thing you move onto a wall and then build in; four
        // panes disagreeing about where that is would make "on the grid" mean four things at once.
        Assert.All(panes, pane => Assert.Same(panes[0].Grid.Plane, pane.Grid.Plane));
    }

    [Fact]
    public void The_step_doubles_and_halves_and_the_snap_follows_it() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var pane = fixture.Viewport!;
        var plane = pane.Grid.Plane;

        Assert.Null(plane.Step);

        fixture.Run("scene.grid-step-double");

        var doubled = plane.Step!.Value;

        fixture.Run("scene.grid-step-halve");
        Assert.Equal(doubled * 0.5f, plane.Step!.Value, 4);

        // ⚠ D5's last sentence: "the grid I can see" and "the grid I snap to" are one number. They are
        // two in more than one shipping editor and it is a bug people never manage to describe.
        Assert.Equal(plane.Step!.Value, pane.Gizmo.Snap.GridStep, 4);
        Assert.Equal(plane.Step!.Value, pane.Grid.Spacing(pane.Camera, pane.Control.RenderHeight), 4);

        fixture.Run("scene.grid-step-auto");
        Assert.Null(plane.Step);
    }

    [Fact]
    public void The_plane_can_be_raised_along_its_own_normal_and_put_back() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var plane = fixture.Viewport!.Grid.Plane;

        Assert.True(plane.IsGround);
        Assert.False(fixture.CanRun("scene.work-plane-to-world"));

        fixture.Run("scene.grid-step-double");
        fixture.Run("scene.work-plane-raise");

        var step = plane.Step!.Value;

        Assert.Equal(step, plane.Origin.Y, 4);
        Assert.True(fixture.CanRun("scene.work-plane-to-world"));

        fixture.Run("scene.work-plane-lower");
        Assert.Equal(0f, plane.Origin.Y, 4);

        fixture.Run("scene.work-plane-to-world");
        Assert.True(plane.IsGround);
    }

    [Fact]
    public void Work_plane_to_selection_puts_it_where_the_selection_is() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        // Somewhere that is not the origin, or "the plane went to the selection" and "the plane never
        // moved" are the same assertion.
        var cube = fixture.Scene.Add("Cube", LocalTransform.At(new Vector3(3f, 2f, 1f)));

        fixture.Scene.Selection.Set([cube]);
        fixture.Frames(2);

        Assert.True(fixture.CanRun("scene.work-plane-to-selection"));

        fixture.Run("scene.work-plane-to-selection");

        var plane = fixture.Viewport!.Grid.Plane;

        Assert.True(Vector3.NearEqual(plane.Origin, new Vector3(3f, 2f, 1f), 1e-3f), $"at {plane.Origin}");

        // ⚠ Level, not tilted onto whatever the entity's own rotation is. Until there are faces to
        // select, "to selection" is a statement about *where* you are building and not about which way
        // — and a plane that inherited an arbitrary rotation would be one nobody could predict.
        Assert.True(Vector3.NearEqual(plane.Normal, Vector3.UnitY, 1e-4f));
    }

    [Fact]
    public void The_tape_measure_is_a_toggle_that_clears_itself() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var pane = fixture.Viewport!;

        Assert.False(pane.Measure.IsActive);
        Assert.False(fixture.CanRun("scene.measure-clear"));

        fixture.Run("scene.measure");
        Assert.True(pane.Measure.IsActive);

        pane.Measure.Add(Vector3.Zero);
        pane.Measure.Add(new Vector3(0f, 0f, 6f));

        Assert.Equal("6.00 m", pane.Measure.Describe());
        Assert.True(fixture.CanRun("scene.measure-clear"));

        // Turning it off takes the tape with it: a measurement left drawn over a scene nobody is
        // measuring is an annotation the editor will not stop showing.
        fixture.Run("scene.measure");

        Assert.False(pane.Measure.IsActive);
        Assert.Empty(pane.Measure.Points);
    }

    [Fact]
    public void A_scale_reference_is_drawn_and_is_not_an_entity() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var pane = fixture.Viewport!;
        var before = fixture.Scene.Entities.Count();

        fixture.Run(EditorApplication.ViewportIds.Reference(ReferenceVolumes.Person));

        Assert.Single(pane.References.Placed);
        Assert.Equal(1.8f, pane.References.Placed[0].Volume.Size.Y, 4);

        // ⚠ Doc 24's own words: "drawn and not shipped". Nothing to select, nothing to save, and
        // nothing to leave in a level by accident — which is the whole difference between this and
        // the cube everybody scales to 1.8 and then forgets about.
        Assert.Equal(before, fixture.Scene.Entities.Count());

        fixture.Run("scene.reference-clear");
        Assert.True(pane.References.IsEmpty);
    }

    [Fact]
    public void All_four_references_are_reachable_and_all_of_them_have_a_size() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        foreach (var volume in ReferenceVolumes.All) {
            var id = EditorApplication.ViewportIds.Reference(volume);

            Assert.True(fixture.Shell.Commands.TryGet(id, out _), id);
            Assert.True(volume.Size.X > 0f && volume.Size.Y > 0f && volume.Size.Z > 0f, volume.Name);
        }
    }
}
