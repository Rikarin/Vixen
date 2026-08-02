// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 24's D5: the grid is a plane with a transform, and the step is one number.</summary>
public class WorkPlaneTests {
    const int Height = 800;

    static EditorCamera Camera(float distance = 10f) => new() { Distance = distance };

    [Fact]
    public void A_plane_nobody_moved_is_the_ground_and_the_grid_is_what_it_was() {
        var plane = new WorkPlane();
        var grid = new SceneGrid();
        var camera = Camera();

        Assert.True(plane.IsGround);
        Assert.True(Vector3.NearEqual(plane.Normal, Vector3.UnitY, 1e-5f));

        // ⚠ The whole compatibility claim in one assertion. D5 says the adaptive spacing, the
        // emphasis and the reach all stay and become a *view* of a plane; a default plane has to
        // leave the grid producing exactly what it produced before, or the change is a rewrite
        // wearing a refactor's clothes.
        var before = grid.Build(camera, Height);

        grid.Plane = plane;

        Assert.Equal(before.Count, grid.Build(camera, Height).Count);
        Assert.Equal(before[0].From, grid.Build(camera, Height)[0].From);
    }

    [Fact]
    public void Setting_it_to_a_surface_turns_the_grid_onto_that_surface() {
        var plane = new WorkPlane();

        plane.SetTo(new Vector3(2f, 1f, 0f), Vector3.UnitX);

        Assert.False(plane.IsGround);
        Assert.True(Vector3.NearEqual(plane.Normal, Vector3.UnitX, 1e-4f));

        // A point in the plane's own floor is on the wall in the world: its local Y is out of it.
        var onWall = plane.ToWorld(new Vector3(3f, 0f, 4f));

        Assert.Equal(2f, onWall.X, 4);
    }

    [Fact]
    public void The_grid_is_drawn_in_the_planes_own_directions() {
        var grid = new SceneGrid { Plane = new WorkPlane() };
        var camera = Camera();

        grid.Plane.SetTo(new Vector3(5f, 0f, 0f), Vector3.UnitX);

        var lines = grid.Build(camera, Height);

        Assert.NotEmpty(lines);

        // Every line lies in the wall, so every end of every one of them is at x = 5.
        Assert.All(lines, line => Assert.Equal(5f, line.From.X, 3));
        Assert.All(lines, line => Assert.Equal(5f, line.To.X, 3));
    }

    [Fact]
    public void Offsetting_goes_along_the_normal_rather_than_along_world_up() {
        var plane = new WorkPlane();

        plane.SetTo(Vector3.Zero, Vector3.UnitX);
        plane.Offset(3f);

        // ⚠ D5's third gesture is "the second floor at three metres without doing arithmetic", and on
        // a wall the same verb is "one wall-thickness further along". An offset along world Y would
        // slide the plane *within* the wall and look like it did nothing.
        Assert.True(Vector3.NearEqual(plane.Origin, new Vector3(3f, 0f, 0f), 1e-4f));
    }

    [Fact]
    public void Doubling_and_halving_are_powers_of_two_from_whatever_is_on_screen() {
        var plane = new WorkPlane();

        // Nothing chosen yet, so the first press has to be told what the grid was drawing — see
        // `Coarsen`. Two presses from a metre is four, not two.
        Assert.Equal(2f, plane.Coarsen(1f), 4);
        Assert.Equal(4f, plane.Coarsen(1f), 4);

        Assert.Equal(2f, plane.Refine(1f), 4);
        Assert.Equal(1f, plane.Refine(1f), 4);
        Assert.Equal(0.5f, plane.Refine(1f), 4);

        // ⚠ Every level a sub-lattice of the last: a quarter-metre object is still on the four-metre
        // grid's lines, which a step of a third would never be again.
        Assert.Equal(0.25f, plane.Refine(1f), 4);
    }

    [Fact]
    public void A_chosen_step_is_what_the_grid_draws_however_far_away_the_camera_is() {
        var grid = new SceneGrid { Plane = new WorkPlane() };

        grid.Plane.Coarsen(grid.Spacing(Camera(), Height));

        var chosen = grid.Plane.Step!.Value;

        // ⚠ The point of choosing one. A level blocked out at four metres has to stay at four metres
        // while the camera moves; the adaptive sequence is what is right until somebody says
        // otherwise, and this is them saying otherwise.
        Assert.Equal(chosen, grid.Spacing(Camera(0.5f), Height), 4);
        Assert.Equal(chosen, grid.Spacing(Camera(5000f), Height), 4);

        grid.Plane.Auto();

        Assert.NotEqual(grid.Spacing(Camera(0.5f), Height), grid.Spacing(Camera(5000f), Height));
    }

    [Fact]
    public void The_grid_I_can_see_and_the_grid_I_snap_to_are_one_number() {
        var grid = new SceneGrid { Plane = new WorkPlane() };
        var snap = new SnapContext { Plane = grid.Plane, SnapPosition = true };

        grid.Plane.Step = 4f;

        Assert.Equal(4f, snap.GridStep, 4);
        Assert.Equal(4f, snap.Position(3.1f), 4);

        grid.Plane.Refine(4f);

        // ⚠ Doc 24's D5's own complaint: "they are two in more than one shipping editor and it is a
        // bug people never manage to describe" — you halve the grid, the lines get closer, and the
        // drag still moves by the old amount.
        Assert.Equal(2f, snap.GridStep, 4);
        Assert.Equal(2f, snap.Position(2.4f), 4);
    }

    [Fact]
    public void A_context_with_no_plane_keeps_its_own_step() {
        var snap = new SnapContext { GridStep = 0.25f, SnapPosition = true };

        Assert.Equal(0.25f, snap.GridStep, 4);
        Assert.Equal(0.5f, snap.Position(0.4f), 4);
    }

    [Fact]
    public void Resetting_puts_it_back_on_the_ground_with_an_automatic_step() {
        var plane = new WorkPlane();

        plane.SetTo(new Vector3(1f, 2f, 3f), Vector3.UnitZ);
        plane.Coarsen(1f);

        Assert.False(plane.IsGround);

        plane.Reset();

        Assert.True(plane.IsGround);
        Assert.Null(plane.Step);
    }

    [Fact]
    public void Every_change_says_so_once() {
        var plane = new WorkPlane();
        var changes = 0;

        plane.Changed += _ => changes++;

        plane.SetTo(Vector3.One, Vector3.UnitZ);
        plane.Offset(1f);
        plane.Coarsen(1f);
        plane.Reset();

        Assert.Equal(4, changes);

        // An offset of nothing is not a change, or a command bound to a key would redraw the grid
        // every time somebody leant on it.
        plane.Offset(0f);
        plane.Auto();

        Assert.Equal(4, changes);
    }

    [Fact]
    public void Halving_stops_before_the_grid_becomes_a_sheet() {
        var plane = new WorkPlane();

        for (var index = 0; index < 200; index++) {
            plane.Refine(1f);
        }

        // A floor rather than a limit anybody meets: halving without one reaches denormals, and the
        // grid becomes a solid sheet nothing can be aimed at.
        Assert.Equal(WorkPlane.MinimumStep, plane.Step!.Value, 6);
    }
}
