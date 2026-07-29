// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Rendering;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>The rubber-band: the rectangle, what it takes, and where it parts from a click.</summary>
public class MarqueeTests : IDisposable {
    const int Width = 1000;
    const int Height = 800;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-marquee-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly ScenePicker picker;
    readonly TransformSystem transforms = new();

    public MarqueeTests() {
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

    static EditorCamera Camera() => new() { Pivot = Vector3.Zero, Distance = 12f };

    Entity Shape(PrimitiveKind kind, Vector3 position) {
        var entity = scene.CreateShape(kind, LocalTransform.At(position));

        transforms.Resolve(world);
        world.AdvanceVersion();

        return entity;
    }

    /// <summary>A band round a world point, in render pixels.</summary>
    static Marquee Around(EditorCamera camera, Vector3 world, float radius, bool additive = false) {
        var screen = camera.Project(world, Width, Height);
        var centre = new Vector2(screen.X, screen.Y);

        return new Marquee(centre - new Vector2(radius), centre + new Vector2(radius), additive);
    }

    [Fact]
    public void A_band_is_the_same_rectangle_whichever_way_it_was_dragged() {
        var forwards = new Marquee(new Vector2(10f, 20f), new Vector2(110f, 220f), false);
        var backwards = new Marquee(new Vector2(110f, 220f), new Vector2(10f, 20f), false);

        // ⚠ The whole reason two corners are stored rather than an origin and a size: a drag goes in
        // any direction, and the consumer that forgets to cope with a negative width is the hit test
        // rather than the drawing — so a band dragged up and to the left selects nothing while
        // looking exactly like one that works.
        Assert.Equal(forwards.Left, backwards.Left);
        Assert.Equal(forwards.Top, backwards.Top);
        Assert.Equal(forwards.Width, backwards.Width);
        Assert.Equal(forwards.Height, backwards.Height);
    }

    [Fact]
    public void A_band_smaller_than_the_threshold_is_a_click() {
        var still = new Marquee(new Vector2(50f, 50f), new Vector2(51f, 51f), false);
        var wide = new Marquee(new Vector2(50f, 50f), new Vector2(50f + Marquee.MinimumSize, 51f), false);

        Assert.False(still.IsBand);

        // ⚠ Either dimension, not both. Dragging along a row of objects is a band a few pixels tall
        // and several hundred wide, and requiring both would turn that gesture back into a click —
        // which deselects everything the user was in the middle of gathering.
        Assert.True(wide.IsBand);
    }

    [Fact]
    public void A_band_takes_what_it_touches_and_leaves_what_it_does_not() {
        var camera = Camera();

        var inside = Shape(PrimitiveKind.Cube, Vector3.Zero);
        var outside = Shape(PrimitiveKind.Cube, new Vector3(8f, 0f, 0f));

        List<Entity> taken = [];
        picker.Within(Around(camera, Vector3.Zero, 60f), camera, Width, Height, taken);

        Assert.Contains(inside, taken);
        Assert.DoesNotContain(outside, taken);
    }

    [Fact]
    public void A_band_takes_an_object_it_only_clips() {
        var camera = Camera();
        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);

        var screen = camera.Project(Vector3.Zero, Width, Height);

        // A band that starts well to the left and stops just past the cube's left edge. Touching,
        // not containing: a rule that only took what it fully enclosed cannot select anything larger
        // than the pane, so the gesture stops working precisely where a scene gets big.
        var band = new Marquee(new Vector2(screen.X - 200f, screen.Y - 200f), new Vector2(screen.X, screen.Y), false);

        List<Entity> taken = [];
        picker.Within(band, camera, Width, Height, taken);

        Assert.Contains(cube, taken);
    }

    [Fact]
    public void A_band_skips_what_is_hidden_or_locked() {
        var camera = Camera();

        var hidden = Shape(PrimitiveKind.Cube, new Vector3(-1.5f, 0f, 0f));
        var locked = Shape(PrimitiveKind.Cube, new Vector3(1.5f, 0f, 0f));

        scene.SetHidden(hidden, true);
        scene.SetLocked(locked, true);

        List<Entity> taken = [];
        picker.Within(Around(camera, Vector3.Zero, 300f), camera, Width, Height, taken);

        // ⚠ The same rule a click follows, and a band is the gesture that most easily takes something
        // the user cannot see. A marquee and a click disagreeing about what is selectable is worse
        // than either rule on its own.
        Assert.Empty(taken);
    }

    [Fact]
    public void An_object_behind_the_camera_is_not_in_every_band() {
        var camera = Camera();

        // Behind the eye, which for a perspective divide answers with a real pixel position mirrored
        // through the middle of the pane — a rectangle stretched across the whole viewport, and an
        // object that lands in every band anybody drags.
        Shape(PrimitiveKind.Cube, new Vector3(0f, 0f, 40f));

        List<Entity> taken = [];
        picker.Within(new Marquee(new Vector2(10f, 10f), new Vector2(60f, 60f), false), camera, Width, Height, taken);

        Assert.Empty(taken);
    }

    [Fact]
    public void Dragging_a_band_selects_and_a_press_that_does_not_move_picks() {
        using var pane = new Pane();

        var band = new Marquee(new Vector2(100f, 100f), new Vector2(400f, 400f), false);

        pane.Viewport.BeginSelect(band.Anchor, false);
        Assert.NotNull(pane.Viewport.Selecting);

        pane.Viewport.DragSelect(band.Corner);
        Assert.True(pane.Viewport.Selecting!.Value.IsBand);

        pane.Viewport.EndSelect();

        // ⚠ Cleared on the way out however it ended. A band left running resolves on the next release
        // anywhere in the pane, which selects a rectangle nobody drew.
        Assert.Null(pane.Viewport.Selecting);
    }

    [Fact]
    public void A_press_in_empty_space_starts_a_band_and_the_release_ends_it() {
        using var pane = new Pane();

        pane.Press(PointerButton.Primary, new Vector2(120f, 120f));
        Assert.NotNull(pane.Viewport.Selecting);

        pane.Move(new Vector2(320f, 300f));
        Assert.True(pane.Viewport.Selecting!.Value.IsBand);

        pane.Release(PointerButton.Primary, new Vector2(320f, 300f));
        Assert.Null(pane.Viewport.Selecting);
    }

    [Fact]
    public void Escape_abandons_a_band_without_selecting_anything() {
        using var pane = new Pane();

        pane.Press(PointerButton.Primary, new Vector2(120f, 120f));
        pane.Move(new Vector2(320f, 300f));

        Assert.True(pane.Key(InputKey.Escape, KeyAction.Pressed));
        Assert.Null(pane.Viewport.Selecting);
    }

    [Fact]
    public void An_empty_band_clears_the_selection_and_an_additive_one_does_not() {
        using var pane = new Pane();
        var entity = new Entity();

        pane.Selection.Set(entity);
        pane.Viewport.Picker = new NothingPicker();

        pane.Viewport.BeginSelect(new Vector2(10f, 10f), additive: false);
        pane.Viewport.DragSelect(new Vector2(200f, 200f));
        pane.Viewport.EndSelect();

        Assert.Empty(pane.Selection);

        pane.Selection.Set(entity);

        pane.Viewport.BeginSelect(new Vector2(10f, 10f), additive: true);
        pane.Viewport.DragSelect(new Vector2(200f, 200f));
        pane.Viewport.EndSelect();

        // ⚠ The same rule a miss follows: shift-clicking empty space must not deselect, because that
        // is the end of a band that grabbed nothing.
        Assert.Single(pane.Selection);
    }

    /// <summary>A picker that answers nothing, for the two cases that are about the miss.</summary>
    sealed class NothingPicker : IScenePicker {
        public Entity Under(Ray ray, EditorCamera camera, int width, int height) => Entity.Null;

        public void Within(Marquee marquee, EditorCamera camera, int width, int height, List<Entity> into) { }
    }
}
