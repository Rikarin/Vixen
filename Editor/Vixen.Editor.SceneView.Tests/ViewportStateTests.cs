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

/// <summary>Show flags, view modes, the selection outline, the stats and the bookmark slots.</summary>
/// <remarks>
///     Every one of these is a per-pane answer, which is what makes a four-pane layout mean something
///     — see <c>ViewportLayout</c> for the one piece of state the panes must not share.
/// </remarks>
public class ViewportStateTests : IDisposable {
    const int Height = 800;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-viewport-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly TransformSystem transforms = new();

    public ViewportStateTests() {
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

    Entity Shape(PrimitiveKind kind, Vector3 position) {
        var entity = scene.CreateShape(kind, LocalTransform.At(position));

        transforms.Resolve(world);
        world.AdvanceVersion();

        return entity;
    }

    // ── Show flags ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pane_comes_up_showing_everything_but_the_bounds() {
        using var pane = new Pane();

        Assert.Equal(SceneShow.Default, pane.Viewport.Show);

        // A box round every object is the one flag that makes a busy scene less legible rather than
        // more, which is why it is the one thing off by default.
        Assert.Equal(SceneShow.None, pane.Viewport.Show & SceneShow.Bounds);
    }

    [Fact]
    public void Turning_the_grid_off_removes_the_grid_and_nothing_else() {
        using var pane = new Pane();
        var lines = new SceneLines();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        lines.Build(scene, pane.Viewport, Height);
        var all = lines.World.Count;

        pane.Viewport.Show &= ~SceneShow.Grid;
        lines.Build(scene, pane.Viewport, Height);

        Assert.True(lines.World.Count < all);

        // The marker cross is still there: six vertices for its three arms.
        Assert.NotEmpty(lines.World);
    }

    [Fact]
    public void Turning_every_flag_off_draws_nothing_at_all() {
        using var pane = new Pane();
        var lines = new SceneLines();

        Shape(PrimitiveKind.Cube, Vector3.Zero);
        pane.Targets.Add(new StubTarget());
        pane.Frame();

        pane.Viewport.Show = SceneShow.None;
        lines.Build(scene, pane.Viewport, Height);

        Assert.Empty(lines.World);
        Assert.Empty(lines.Overlay);
        Assert.True(lines.Handles.IsEmpty);
    }

    [Fact]
    public void The_bounds_flag_draws_a_box_the_size_of_the_shape() {
        using var pane = new Pane();
        var lines = new SceneLines();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        pane.Viewport.Show = SceneShow.Bounds;
        lines.Build(scene, pane.Viewport, Height);

        // Twelve edges, two vertices each. A unit cube's corners are half a unit out on every axis —
        // the shape's own bounds through the entity's matrix, not a world-aligned box round it.
        Assert.Equal(24, lines.World.Count);
        Assert.All(lines.World, vertex => Assert.Equal(0.5f, MathF.Abs(vertex.Position.X), 4));
    }

    [Fact]
    public void Turning_the_gizmo_off_leaves_the_handles_undrawn() {
        using var pane = new Pane();
        var lines = new SceneLines();

        pane.Targets.Add(new StubTarget());
        pane.Frame();

        lines.Build(scene, pane.Viewport, Height);
        Assert.NotEmpty(lines.Overlay);

        pane.Viewport.Show &= ~SceneShow.Gizmos;
        lines.Build(scene, pane.Viewport, Height);

        Assert.Empty(lines.Overlay);
        Assert.True(lines.Handles.IsEmpty);
    }

    [Fact]
    public void Every_flag_has_a_name_and_a_slug() {
        // The Show menu and the viewport's popover are both generated from `ShowFlags.All`, so a flag
        // added to the enum and not to the list appears in neither — which is the failure that shows
        // up immediately rather than the one that does not.
        Assert.All(ShowFlags.All, flag => Assert.False(string.IsNullOrWhiteSpace(ShowFlags.NameOf(flag))));
        Assert.All(ShowFlags.All, flag => Assert.False(string.IsNullOrWhiteSpace(ShowFlags.SlugOf(flag))));

        Assert.Equal(ShowFlags.All.Count, ShowFlags.All.Select(ShowFlags.SlugOf).Distinct().Count());
    }

    // ── View modes ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_wireframe_view_draws_edges_and_no_surfaces() {
        using var pane = new Pane();
        var meshes = new SceneMeshes();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        pane.Viewport.Modes.Current = ViewMode.Wireframe;
        meshes.Build(scene, pane.Viewport);

        Assert.Equal(0, meshes.Triangles);

        // The wireframe is the same instance again with the edge index range, which is a batch of its
        // own because a topology cannot change within a draw.
        var batch = Assert.Single(meshes.Batches);

        Assert.True(batch.Edges);
        Assert.Equal(1, batch.Count);
        Assert.Equal(meshes.WireColour, meshes.Instances[batch.First].Colour);
    }

    [Fact]
    public void A_shaded_wireframe_draws_both() {
        using var pane = new Pane();
        var meshes = new SceneMeshes();

        Shape(PrimitiveKind.Cube, Vector3.Zero);

        pane.Viewport.Modes.Current = ViewMode.ShadedWireframe;
        meshes.Build(scene, pane.Viewport);

        Assert.True(meshes.Triangles > 0);
        Assert.Contains(meshes.Batches, batch => batch.Edges);
        Assert.Contains(meshes.Batches, batch => !batch.Edges);

        // One entity, drawn twice. The pass costs a second instance rather than a second copy of the
        // cube's vertices, which is the whole difference from the path this replaced.
        Assert.Equal(1, meshes.Count);
        Assert.Equal(2, meshes.Instances.Length);
    }

    [Fact]
    public void A_normal_view_colours_by_the_normal_and_ignores_the_selection() {
        using var pane = new Pane();
        var meshes = new SceneMeshes();

        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);
        scene.Selection.Set(cube);

        pane.Viewport.Modes.Current = ViewMode.Normal;
        meshes.Build(scene, pane.Viewport);

        // ⚠ A style lane rather than a colour per vertex, because there are no vertices here to put
        // one on: the shader remaps the world normal from −1..1 into a colour, and remaps rather than
        // clamps because half of every normal is negative and a colour is not — clamping would paint
        // three of a cube's six faces black.
        var instance = Assert.Single(meshes.Instances.ToArray());

        Assert.Equal(1f, instance.Style.W);

        // Painting the selected object orange in a view whose whole content is "this pixel's colour
        // *is* the normal" makes the one object being looked at the one the view cannot answer for.
        Assert.NotEqual(meshes.SelectedColour, instance.Colour);
    }

    [Fact]
    public void The_two_modes_the_tool_renderer_cannot_draw_say_so() {
        // ⚠ A mode with no compositor falls back to shaded, which for a menu line means drawing the
        // same picture as the line above it. `IsSupported` is what lets those two be registered as
        // declared-and-disabled with the reason instead.
        Assert.False(ViewShading.IsSupported(ViewMode.Overdraw));
        Assert.False(ViewShading.IsSupported(ViewMode.LightComplexity));

        Assert.True(ViewShading.IsSupported(ViewMode.Shaded));
        Assert.True(ViewShading.IsSupported(ViewMode.Normal));
        Assert.True(ViewShading.IsSupported(ViewMode.Wireframe));

        // ✅ Roughness was the third of them and is not any more. It was refused because "roughness
        // needs a material to read one off, and there are none: the shape colour is a constant chosen
        // by SceneMeshes" — and there are materials now. `SceneMaterialTests` asserts what it draws.
        Assert.True(ViewShading.IsSupported(ViewMode.Roughness));
    }

    [Fact]
    public void The_unlit_modes_ask_for_a_full_ambient_term_and_the_shaded_ones_do_not() {
        Assert.Equal(1f, ViewShading.AmbientFor(ViewMode.Unlit, 0.35f));
        Assert.Equal(1f, ViewShading.AmbientFor(ViewMode.Albedo, 0.35f));
        Assert.Equal(1f, ViewShading.AmbientFor(ViewMode.Normal, 0.35f));

        // A roughness view is the surface's own value as the whole picture, exactly as an albedo view
        // is — shading it would be the number multiplied by a picture of itself.
        Assert.Equal(1f, ViewShading.AmbientFor(ViewMode.Roughness, 0.35f));
        Assert.Equal(0.35f, ViewShading.AmbientFor(ViewMode.Shaded, 0.35f));
    }

    // ── Selection ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The rim is gone and the amber is what says "selected".</b> A selected shape used to be
    ///     drawn twice — itself, and an expanded hull of itself in blue — which was two answers to one
    ///     question in two colours. What is asserted here is the whole of what replaced it: one
    ///     instance, tinted.
    /// </summary>
    [Fact]
    public void A_selected_shape_is_one_tinted_instance_rather_than_a_shape_and_a_hull() {
        using var pane = new Pane();
        var meshes = new SceneMeshes();

        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);

        meshes.Build(scene, pane.Viewport);
        var plain = meshes.Triangles;

        scene.Selection.Set(cube);
        meshes.Build(scene, pane.Viewport);

        // The same geometry and the same triangle count as before it was selected: no second copy.
        Assert.Equal(plain, meshes.Triangles);

        var instance = Assert.Single(meshes.Instances.ToArray());

        Assert.Equal(meshes.SelectedColour, instance.Colour);

        // ⚠ And no expansion width in the style lane, which is what the shader reads to push a hull
        // outwards. The lanes are still there for the wireframe's flat lighting; nothing sets the
        // width any more.
        Assert.Equal(0f, instance.Style.X);
    }

    /// <summary>
    ///     ⚠ <b>The one number that reaches the shader and can be wrong quietly.</b> The hull's width
    ///     is in pixels, so the shader needs how many world units a pixel is — which it computes from
    ///     <c>EditorCamera.PixelScale</c> and the depth along the view axis. Nothing but a picture can
    ///     assert on the expansion; this asserts that the numbers it is made of are the camera's own,
    ///     in both projections, which is the half that can drift silently.
    /// </summary>
    [Fact]
    public void The_pixel_scale_the_shader_is_given_reproduces_the_cameras_own() {
        var camera = new EditorCamera { Pivot = Vector3.Zero, Distance = 12f };
        var point = new Vector3(1f, 2f, 3f);

        foreach (var orthographic in (bool[]) [false, true]) {
            camera.IsOrthographic = orthographic;

            var view = new MeshInstanceView(
                camera.ViewProjection(1.5f),
                camera.Position,
                camera.Forward,
                camera.NearPlane,
                camera.IsOrthographic,
                camera.PixelScale(Height)
            );

            Assert.Equal(camera.WorldPerPixel(point, Height), view.WorldPerPixel(point), 6);
        }
    }

    [Fact]
    public void A_wireframe_view_draws_no_solid_for_a_selection() {
        using var pane = new Pane();
        var meshes = new SceneMeshes();

        var cube = Shape(PrimitiveKind.Cube, Vector3.Zero);
        scene.Selection.Set(cube);

        pane.Viewport.Modes.Current = ViewMode.Wireframe;
        meshes.Build(scene, pane.Viewport);

        // A wireframe view has no surfaces in it, and selecting something must not put one back.
        Assert.Equal(0, meshes.Triangles);
        Assert.DoesNotContain(meshes.Instances.ToArray(), instance => instance.Style.X > 0f);
    }

    // ── Stats ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_frame_time_is_taken_whole_and_the_rest_are_blended() {
        var stats = new ViewportStats();

        stats.Sample(TimeSpan.FromMilliseconds(16d));

        // ⚠ Not eased in from zero: a frame rate climbing from infinity for the first second of every
        // session reads as the editor warming up and is an artefact of the filter.
        Assert.Equal(16f, stats.FrameMilliseconds, 3);

        stats.Sample(TimeSpan.FromMilliseconds(32d));

        Assert.True(stats.FrameMilliseconds > 16f);
        Assert.True(stats.FrameMilliseconds < 17f);
        Assert.True(stats.FramesPerSecond is > 58f and < 63f);
    }

    [Fact]
    public void A_frame_of_no_length_does_not_move_the_average() {
        var stats = new ViewportStats();

        stats.Sample(TimeSpan.FromMilliseconds(16d));
        stats.Sample(TimeSpan.Zero);

        Assert.Equal(16f, stats.FrameMilliseconds, 3);
    }

    [Fact]
    public void Clearing_the_counts_keeps_the_frame_time() {
        var stats = new ViewportStats { Triangles = 100, Entities = 4, Draws = 3, Segments = 20 };

        stats.Sample(TimeSpan.FromMilliseconds(16d));
        stats.Clear();

        Assert.Equal(0, stats.Triangles);

        // The pane still took a frame; a collapsed panel reporting zero milliseconds would be the one
        // place the readout lies about the editor being fast.
        Assert.Equal(16f, stats.FrameMilliseconds, 3);
    }

    // ── Bookmarks ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_slot_holds_a_view_until_it_is_overwritten() {
        using var pane = new Pane();

        Assert.False(pane.Viewport.HasBookmark(0));
        Assert.False(pane.Viewport.RestoreBookmark(0));

        pane.Camera.Pivot = new Vector3(5f, 0f, 0f);
        pane.Viewport.SaveBookmark(0);

        Assert.True(pane.Viewport.HasBookmark(0));

        pane.Camera.Pivot = Vector3.Zero;
        Assert.True(pane.Viewport.RestoreBookmark(0));
        Assert.Equal(new Vector3(5f, 0f, 0f), pane.Camera.Pivot);

        pane.Camera.Pivot = new Vector3(9f, 0f, 0f);
        pane.Viewport.SaveBookmark(0);

        pane.Camera.Pivot = Vector3.Zero;
        pane.Viewport.RestoreBookmark(0);

        Assert.Equal(new Vector3(9f, 0f, 0f), pane.Camera.Pivot);
    }

    [Fact]
    public void A_slot_that_is_not_one_of_the_nine_is_refused_rather_than_thrown_over() {
        using var pane = new Pane();

        Assert.Null(pane.Viewport.SaveBookmark(-1));
        Assert.Null(pane.Viewport.SaveBookmark(SceneViewport.BookmarkSlots));
        Assert.False(pane.Viewport.RestoreBookmark(SceneViewport.BookmarkSlots));
        Assert.Equal(SceneViewport.BookmarkSlots, pane.Viewport.Bookmarks.Count);
    }
}
