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

/// <summary>Clicking an object in the scene, and clicking away from one.</summary>
public class PickingTests : IDisposable {
    const int Width = 1000;
    const int Height = 800;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-picking-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly ScenePicker picker;
    readonly TransformSystem transforms = new();

    public PickingTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
        picker = new(scene);
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A camera at +Z looking at the origin, which is where every fixture below aims from.</summary>
    static EditorCamera Camera() => new() { Pivot = Vector3.Zero, Distance = 10f };

    /// <summary>The ray through a world point, which is what a click on that point produces.</summary>
    static Ray At(EditorCamera camera, Vector3 world) {
        var screen = camera.Project(world, Width, Height);

        return camera.PickingRay(new Vector2(screen.X, screen.Y), Width, Height);
    }

    /// <summary>Brings the world transforms up to date, which the editor's own loop does per frame.</summary>
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

    Entity Empty(Vector3 position) {
        var entity = scene.Add("Empty", LocalTransform.At(position));

        transforms.Resolve(world);
        world.AdvanceVersion();

        return entity;
    }

    [Fact]
    public void A_ray_through_a_cube_finds_it() {
        var camera = Camera();
        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);

        Assert.Equal(cube, picker.Under(At(camera, Vector3.Zero), camera, Width, Height));
    }

    [Fact]
    public void A_ray_past_everything_finds_nothing() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        // Well off to the side, where there is nothing but grid. Clicking there is how anybody
        // deselects, so "nothing" has to be an answer rather than the nearest thing to the pointer.
        Assert.True(picker.Under(At(camera, new Vector3(40f, 0f, 0f)), camera, Width, Height).IsNull);
    }

    [Fact]
    public void The_nearest_of_two_along_one_ray_wins() {
        var camera = Camera();

        var far = Shape(PrimitiveKind.Cube, new Vector3(0f, 0f, -4f));
        var near = Shape(PrimitiveKind.Cube, new Vector3(0f, 0f, 4f));

        // Both are on the axis the camera looks down, so the ray goes through both. Picking the far
        // one is the failure that reads as "it selects things behind what I clicked".
        Assert.Equal(near, picker.Under(At(camera, Vector3.Zero), camera, Width, Height));
        Assert.NotEqual(far, picker.Under(At(camera, Vector3.Zero), camera, Width, Height));
    }

    [Fact]
    public void A_scaled_shape_is_hit_where_it_looks_like_it_is() {
        var camera = Camera();
        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero, scale: 4f);

        // A unit cube reaches to 0.5 and this one to 2. ⚠ The ray goes into the shape's local space
        // rather than the shape coming out of it, so the scale has to survive the inverse — a picker
        // that tested the untransformed unit cube would miss everything but the middle.
        Assert.Equal(cube, picker.Under(At(camera, new Vector3(1.6f, 1.6f, 0f)), camera, Width, Height));

        // ⚠ The miss is aimed past the *front face*, not past the middle. The camera is ten units out
        // and the cube's front is at two, so a ray through a point just outside the cube at z = 0 is
        // still inside it where it crosses z = 2 — which is perspective doing what it is for, and a
        // fixture that aimed at the middle would pass whether or not the scale was honoured.
        Assert.True(picker.Under(At(camera, new Vector3(4f, 4f, 2f)), camera, Width, Height).IsNull);
    }

    [Fact]
    public void A_sphere_is_round_rather_than_a_box() {
        var camera = Camera();

        Shape(PrimitiveKind.Sphere, Vector3.Zero);

        // The corner of a sphere's bounding box is empty, and a picker that tested bounds would
        // answer for it — which is what makes clicking beside a ball select the ball.
        Assert.True(picker.Under(At(camera, new Vector3(0.48f, 0.48f, 0f)), camera, Width, Height).IsNull);
    }

    [Fact]
    public void An_entity_with_no_shape_is_pickable_by_its_marker() {
        var camera = Camera();
        var empty = Empty(new Vector3(2f, 1f, 0f));

        // A cross has no area to hit, so the marker is a sphere sized in *pixels* — a light two
        // hundred metres away is a handful of them and has to stay clickable.
        Assert.Equal(empty, picker.Under(At(camera, new Vector3(2f, 1f, 0f)), camera, Width, Height));
    }

    [Fact]
    public void A_marker_stays_the_same_size_on_screen_however_far_away_it_is() {
        var near = new EditorCamera { Distance = 2f };
        var far = new EditorCamera { Distance = 200f };

        var empty = Empty(Vector3.Zero);

        foreach (var camera in new[] { near, far }) {
            var centre = camera.Project(Vector3.Zero, Width, Height);
            var inside = new Vector2(centre.X + (picker.MarkerRadius * 0.6f), centre.Y);
            var outside = new Vector2(centre.X + (picker.MarkerRadius * 2f), centre.Y);

            Assert.Equal(empty, picker.Under(camera.PickingRay(inside, Width, Height), camera, Width, Height));
            Assert.True(picker.Under(camera.PickingRay(outside, Width, Height), camera, Width, Height).IsNull);
        }
    }

    [Fact]
    public void A_shape_scaled_to_nothing_is_skipped_rather_than_thrown_over() {
        var camera = Camera();

        Shape(PrimitiveKind.Cube, Vector3.Zero, scale: 0f);

        // A zero scale has no surface and no invertible matrix. An entity can be scaled to nothing
        // and back, and a picker that threw would take the editor down with it.
        Assert.True(picker.Under(At(camera, Vector3.Zero), camera, Width, Height).IsNull);
    }

    [Fact]
    public void A_click_selects_and_a_click_on_nothing_deselects() {
        using var pane = new Pane();

        var camera = pane.Viewport.Camera;
        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);

        pane.Viewport.Picker = picker;
        pane.Frame();

        // ⚠ The regression. `Pick` needed a `PickingBuffer` the application never set, so it returned
        // false and did nothing at all: the only way to select an entity was the hierarchy panel, and
        // clicking empty space did not even deselect.
        Assert.True(pane.Viewport.Pick(pane.Screen(Vector3.Zero)));
        Assert.Equal([cube], pane.Selection);

        Assert.True(pane.Viewport.Pick(pane.Screen(new Vector3(40f, 0f, 0f))));
        Assert.Empty(pane.Selection);
    }

    [Fact]
    public void An_additive_click_extends_the_selection_and_clicking_again_takes_it_back_out() {
        using var pane = new Pane();

        var first = Shape(PrimitiveKind.Cube, new Vector3(-3f, 0f, 0f));
        var second = Shape(PrimitiveKind.Cube, new Vector3(3f, 0f, 0f));

        pane.Viewport.Picker = picker;
        pane.Frame();

        pane.Viewport.Pick(pane.Screen(new Vector3(-3f, 0f, 0f)));
        pane.Viewport.Pick(pane.Screen(new Vector3(3f, 0f, 0f)), additive: true);

        Assert.Equal([first, second], pane.Selection);

        // Additive *toggles*, so the same modifier that extends a selection is the one that takes
        // something back out of it — two gestures for the two halves of one idea is what makes people
        // click an already-selected object and wonder why nothing happened.
        pane.Viewport.Pick(pane.Screen(new Vector3(-3f, 0f, 0f)), additive: true);

        Assert.Equal([second], pane.Selection);
    }

    [Fact]
    public void An_additive_click_on_nothing_keeps_what_was_selected() {
        using var pane = new Pane();
        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);

        pane.Viewport.Picker = picker;
        pane.Frame();

        pane.Viewport.Pick(pane.Screen(Vector3.Zero));
        pane.Viewport.Pick(pane.Screen(new Vector3(40f, 0f, 0f)), additive: true);

        // Clicking empty space deselects and shift-clicking it must not: that is the miss at the end
        // of a rubber-band that grabbed nothing.
        Assert.Equal([cube], pane.Selection);
    }

    [Fact]
    public void A_pane_with_neither_a_buffer_nor_a_picker_cannot_answer() {
        using var pane = new Pane();

        Shape(PrimitiveKind.Cube, Vector3.Zero);
        pane.Frame();

        // Still the honest answer for a host that has set up neither, and it is now reachable only
        // by being one.
        Assert.False(pane.Viewport.Pick(pane.Screen(Vector3.Zero)));
    }
}
