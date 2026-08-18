// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Rendering;
using Vixen.Ui;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>
///     The seam: a mode pushes a cursor into a pane and it comes out of the geometry both presenters
///     read — [docs/plan/31 § T3], and <see cref="SceneViewport.Cursor" /> for why it is a push.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything here goes through <see cref="SceneLines.Build" /> rather than calling the
///     mode's drawing directly.</b> <c>Build</c> is what <c>ScenePresenter</c> and
///     <c>FramePresenter</c> both call, so a ring asserted anywhere else would be a ring asserted in
///     a place no frame goes through — and the thing most likely to break is the wiring, not the
///     circle.
/// </remarks>
public sealed class TerrainCursorSeamTests : IDisposable {
    const int Height = 600;

    /// <summary>The middle of <see cref="Ground.Shape" />, in world space.</summary>
    static readonly Vector3 Centre = new(31f, 0f, 31f);

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-terrain-cursor-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly List<IDisposable> owned = [];

    public TerrainCursorSeamTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        foreach (var thing in owned) {
            thing.Dispose();
        }

        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }
    }

    /// <summary>A pane the size of a window, aimed down at the middle of the terrain.</summary>
    /// <remarks>
    ///     ⚠ <b>Show flags off and no gizmo targets, so the overlay channel is the cursor and only the
    ///     cursor.</b> Asserting "the overlay grew" against a pane also drawing a transform gizmo
    ///     would pass for a ring of zero segments.
    /// </remarks>
    SceneViewport Pane() {
        var document = new UiDocument(800f, 600f);

        document.Load("root { width: 800px; height: 600px; } viewport { width: 800px; height: 600px; }");

        var control = document.Root.Add<ViewportControl>();

        document.Update();
        control.Refresh();

        var pane = new SceneViewport(control, new Selection<Entity>()) { Show = SceneShow.None };

        pane.Camera.Pivot = Centre;
        pane.Camera.Distance = 40f;
        pane.Camera.Pitch = -MathF.PI / 4f;

        owned.Add(pane);
        owned.Add(document);

        return pane;
    }

    /// <summary>A mode over a flat terrain, entered.</summary>
    static (TerrainMode Mode, TerrainMap Terrain) Armed() {
        var terrain = Ground.Terrain();
        var mode = new TerrainMode();

        mode.Activated();
        mode.Editing.Terrain = terrain;
        mode.Editing.Tools.Metres = 20f;

        return (mode, terrain);
    }

    static PointerEvent Move(float x = 400f, float y = 300f) =>
        new() { X = x, Y = y, Action = PointerAction.Moved, Button = PointerButton.None };

    static PointerEvent Press(float x = 400f, float y = 300f) =>
        new() { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary };

    /// <summary>The overlay segments a frame would carry, through the call both presenters make.</summary>
    IReadOnlyList<LineVertex> Overlay(SceneViewport pane) {
        var lines = new SceneLines();

        lines.Build(scene, pane, Height);

        return lines.Overlay;
    }

    /// <summary>How wide the drawn footprint is, in metres, from the brush's own centre.</summary>
    static float Reach(IReadOnlyList<LineVertex> overlay, Vector2 ground) =>
        overlay.Max(vertex => new Vector2(vertex.Position.X - ground.X, vertex.Position.Z - ground.Y).Length());

    // ── The cursor arrives ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pane_nobody_has_hovered_draws_no_cursor() {
        var pane = Pane();

        // The seam is a nullable delegate, so the editor as it was is the null.
        Assert.Null(pane.Cursor);
        Assert.Empty(Overlay(pane));
    }

    [Fact]
    public void Hovering_with_the_brush_armed_draws_a_ring_without_taking_the_move() {
        var pane = Pane();
        var (mode, _) = Armed();

        // ⚠ `false`, and it is load-bearing: the pane's own hover highlight runs on what this
        // returns. A mode that swallowed the move to draw a ring would turn the highlight off for
        // the whole time the terrain mode is active.
        Assert.False(mode.Pointer(pane, Move()));

        var ground = Assert.NotNull(mode.Hover);

        Assert.Equal(Centre.X, ground.X, 1);
        Assert.Equal(Centre.Z, ground.Y, 1);

        var overlay = Overlay(pane);

        Assert.NotEmpty(overlay);
        Assert.Equal(mode.Editing.Brush.Radius, Reach(overlay, ground), 2);
    }

    [Fact]
    public void The_ring_is_in_the_overlay_channel_rather_than_the_depth_tested_one() {
        var pane = Pane();
        var (mode, _) = Armed();

        mode.Pointer(pane, Move());

        var lines = new SceneLines();

        lines.Build(scene, pane, Height);

        // ⚠ A ring conformed to the ground is coplanar with the ground. In `World` it would be
        // depth-tested against the surface it is lying on and would z-fight in and out along its
        // length as the camera moved — see SceneViewport.Cursor.
        Assert.NotEmpty(lines.Overlay);
        Assert.Empty(lines.World);
    }

    [Fact]
    public void A_mode_with_no_terrain_draws_nothing_and_claims_no_pane() {
        var pane = Pane();
        var mode = new TerrainMode();

        mode.Activated();

        Assert.False(mode.Pointer(pane, Move()));
        Assert.Null(pane.Cursor);
        Assert.Empty(Overlay(pane));
    }

    // ── And leaves again ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Aiming_off_the_terrain_takes_the_ring_away_but_leaves_the_cursor_attached() {
        var pane = Pane();
        var (mode, _) = Armed();

        mode.Pointer(pane, Move());
        Assert.NotEmpty(Overlay(pane));

        // The top of the frame, which at this pitch looks past the far edge of a 62 m terrain.
        Assert.False(mode.Pointer(pane, Move(400f, 2f)));

        Assert.Null(mode.Hover);
        Assert.NotNull(pane.Cursor);
        Assert.Empty(Overlay(pane));
    }

    [Fact]
    public void Leaving_the_pane_takes_the_ring_away() {
        var pane = Pane();
        var (mode, _) = Armed();

        mode.Pointer(pane, Move());
        Assert.NotEmpty(Overlay(pane));

        // ⚠ A ring left at the last place the pointer was inside the viewport says the next click
        // lands there, which it does not — and that is the state somebody is in for the whole time
        // they are using the panel rather than the viewport.
        Assert.False(mode.Pointer(pane, new() { X = 400f, Y = 300f, Action = PointerAction.Exited }));

        Assert.Null(mode.Hover);
        Assert.Empty(Overlay(pane));
    }

    [Fact]
    public void Leaving_the_mode_takes_the_cursor_off_the_pane() {
        var pane = Pane();
        var (mode, _) = Armed();

        mode.Pointer(pane, Move());
        Assert.NotNull(pane.Cursor);

        mode.Deactivated();

        // A delegate the pane holds until something replaces it would draw a brush ring over the
        // blockout tools, in a mode with no brush.
        Assert.Null(pane.Cursor);
        Assert.Null(mode.Hover);
        Assert.Empty(Overlay(pane));
    }

    [Fact]
    public void Only_the_pane_the_pointer_is_in_draws_one() {
        var first = Pane();
        var second = Pane();
        var (mode, _) = Armed();

        mode.Pointer(first, Move());
        Assert.NotEmpty(Overlay(first));

        mode.Pointer(second, Move());

        // Two panes are two cameras looking at one terrain, and a ring left in the pane the pointer
        // has left says the brush is in two places.
        Assert.Null(first.Cursor);
        Assert.Empty(Overlay(first));
        Assert.NotEmpty(Overlay(second));
    }

    // ── What the ring is a picture of ───────────────────────────────────────────────────────────

    [Fact]
    public void The_ring_follows_the_radius_between_strokes() {
        var pane = Pane();
        var (mode, _) = Armed();

        mode.Pointer(pane, Move());

        var ground = Assert.NotNull(mode.Hover);

        mode.Editing.Brush.Radius = 3f;
        Assert.Equal(3f, Reach(Overlay(pane), ground), 2);

        // The panel is the answer between strokes, so a hand on the radius slider is watched.
        mode.Editing.Brush.Radius = 17f;
        Assert.Equal(17f, Reach(Overlay(pane), ground), 2);
    }

    [Fact]
    public void A_stroke_draws_the_brush_it_is_stamping_with_and_not_the_one_on_the_panel() {
        var pane = Pane();
        var (mode, _) = Armed();

        mode.Editing.Brush.Radius = 6f;

        Assert.True(mode.Pointer(pane, Press()));
        Assert.True(mode.Editing.IsStroking);

        var ground = Assert.NotNull(mode.Hover);

        // ⚠ The brush is snapshotted at `Begin` and not read again, so a ring reading the panel
        // mid-drag would grow under a hand on the slider while the ground being written did not.
        mode.Editing.Brush.Radius = 20f;

        Assert.Equal(6f, Reach(Overlay(pane), ground), 2);

        mode.Pointer(pane, new() { X = 400f, Y = 300f, Action = PointerAction.Released, Button = PointerButton.Primary });

        Assert.False(mode.Editing.IsStroking);
        Assert.Equal(20f, Reach(Overlay(pane), ground), 2);
    }

    [Fact]
    public void The_ring_is_still_drawn_while_the_stroke_is_being_dragged() {
        var pane = Pane();
        var (mode, terrain) = Armed();

        Assert.True(mode.Pointer(pane, Press()));

        // A drag a little to the left, which the mode takes.
        Assert.True(mode.Pointer(pane, Move(360f, 300f)));

        var ground = Assert.NotNull(mode.Hover);
        var overlay = Overlay(pane);

        Assert.NotEmpty(overlay);

        // And the ring is at the ground the stroke is extending to, not where it began.
        Assert.True(ground.X < Centre.X - 1f, "the drag did not move the brush");
        Assert.Equal(mode.Editing.HeldBrush.Radius, Reach(overlay, ground), 2);

        Assert.True(Ground.HeightAt(terrain, 31, 31) > 0.5f, "the stroke wrote nothing");
    }
}
