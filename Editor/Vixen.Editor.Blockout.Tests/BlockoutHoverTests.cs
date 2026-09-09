// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Input;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>
///     doc 24 § P4's candidate cell and § Geometry's loop preview: what the pointer promises, drawn
///     before the click that commits it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion that matters here reads the overlay <c>SceneLines</c> would carry,
///         not a field the mode set.</b> "The mode computed a cell" and "a designer can see where the
///         next click lands" are two statements, and the second is the whole of what
///         <a href="https://github.com/Rikarin/Vixen/issues/374">#374</a> asked for — a preview whose
///         entire content is what it looks like.
///     </para>
///     <para>
///         ⚠ <b>The loop preview is pinned to the operation rather than eyeballed.</b>
///         <see cref="Every_previewed_point_is_a_position_the_cut_would_insert" /> runs the real
///         <c>MeshOperations.LoopCut</c> on a copy and asks whether the drawing and the cut agree
///         about where the loop goes — which is the failure a picture cannot rule out, because a cut
///         drawn a tenth of a cell off looks exactly like a cut drawn correctly.
///     </para>
/// </remarks>
public sealed class BlockoutHoverTests : IDisposable {
    /// <summary>How tall the pane is in render pixels, which is what <c>SceneLines.Build</c> takes.</summary>
    const int Height = 600;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-hover-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly List<IDisposable> owned = [];

    CommandRegistry? commands;

    public BlockoutHoverTests() {
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

        GC.SuppressFinalize(this);
    }

    // ── The cube grid ───────────────────────────────────────────────────────────────────────────

    /// <summary>A pane nobody has hovered draws nothing, which is the editor as it was.</summary>
    [Fact]
    public void A_pane_nobody_has_hovered_draws_no_preview() {
        var pane = Pane();

        // The seam is a nullable delegate, so "before this change" is exactly the null.
        Assert.Null(pane.Cursor);
        Assert.Empty(Overlay(pane));
    }

    /// <summary>Hovering in Object mode names the cell under the pointer and draws it.</summary>
    /// <remarks>
    ///     ⚠ <b>A closed-form oracle rather than "something was drawn".</b> The camera looks straight
    ///     down at a known point, so the ray through the middle pixel meets the ground there and the
    ///     cell is arithmetic — and a wire box is exactly twelve segments, so a preview that drew a
    ///     cross or half a box fails on the count rather than passing on "not empty".
    /// </remarks>
    [Fact]
    public void Hovering_in_object_mode_draws_the_cell_under_the_pointer() {
        var (mode, pane) = Hovered(new Vector3(3.5f, 0f, -2.5f));

        Assert.Equal(new GridBox(3, 0, -3, 1, 1, 1), mode.HoverCell);

        var overlay = Overlay(pane);

        // Twelve edges of a cube, two vertices apiece.
        Assert.Equal(24, overlay.Count);

        // And the box is the cell: its corners span exactly one step in each direction, from the
        // cell's own corner.
        Assert.Equal(3f, overlay.Min(vertex => vertex.Position.X), 3);
        Assert.Equal(4f, overlay.Max(vertex => vertex.Position.X), 3);
        Assert.Equal(-3f, overlay.Min(vertex => vertex.Position.Z), 3);
        Assert.Equal(-2f, overlay.Max(vertex => vertex.Position.Z), 3);
    }

    /// <summary>Moving off the plane takes the preview away rather than leaving the last cell.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a preview usually gets wrong.</b> A cell that stayed where the pointer last
    ///     met the plane is a promise about a click that would land somewhere else — and it is the
    ///     state a designer is in whenever they look up at the sky.
    /// </remarks>
    [Fact]
    public void A_pointer_that_leaves_the_plane_leaves_no_cell() {
        var (mode, pane) = Hovered(Vector3.Zero);

        Assert.NotNull(mode.HoverCell);

        // A nearly level camera and a pixel near the top of the pane: that ray points above the
        // horizon and never meets the ground. Asserted rather than assumed, because a test whose
        // premise had quietly stopped holding would be asserting that a cell it still had was gone.
        pane.Camera.Pitch = -0.15f;

        var away = Move(400f, 4f);
        var ray = pane.Ray(pane.Control.ToRender(away.X, away.Y));

        Assert.True(!ray.Intersects(new Plane(Vector3.UnitY, 0f), out var distance) || distance <= 0f);

        mode.Pointer(pane, away);

        Assert.Null(mode.HoverCell);
        Assert.Empty(Overlay(pane));
    }

    /// <summary>Leaving the mode takes the cursor off the pane, so it does not outlive the tool.</summary>
    [Fact]
    public void Deactivating_takes_the_preview_off_the_pane() {
        var (mode, pane) = Hovered(Vector3.Zero);

        Assert.NotNull(pane.Cursor);

        mode.Deactivated();

        Assert.Null(pane.Cursor);
        Assert.Null(mode.HoverCell);
        Assert.Empty(Overlay(pane));
    }

    /// <summary>
    ///     The cube-grid verb builds where the preview said, which is the half that makes it a tool
    ///     rather than a decoration.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A preview and a verb that disagreed would be worse than no preview.</b> The verb used
    ///     to build at the work plane's origin unconditionally — <c>BlockoutMode.Cell</c> — so drawing
    ///     a candidate cell and leaving that alone would have shown one answer and committed another.
    /// </remarks>
    [Fact]
    public void The_cube_grid_verb_builds_at_the_hovered_cell() {
        var (mode, _) = Hovered(new Vector3(5.5f, 0f, 7.5f));

        Assert.True(commands!.Execute(BlockoutMode.CubeGridCommand));

        var entity = Assert.Single(scene.Selection.Items);
        var placed = new Transform(world, entity).Position;

        // `BlockoutCubeGrid.Create` puts a one-cell box at the middle of its footprint and its floor,
        // so the cell (5, 0, 7) is the box at (5.5, 0, 7.5) — the point the pointer was over.
        Assert.Equal(5.5f, placed.X, 3);
        Assert.Equal(0f, placed.Y, 3);
        Assert.Equal(7.5f, placed.Z, 3);
    }

    // ── The loop cut ────────────────────────────────────────────────────────────────────────────

    /// <summary>One segment per quad the ring crosses, and the ring of a box crosses four.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is the oracle and the cut is what confirms it.</b> A loop cut through one
    ///     edge of a cube turns four of its six quads into two apiece, so the mesh goes from six faces
    ///     to ten — n + 2 per crossed quad — and the preview must have exactly one segment per one of
    ///     those.
    /// </remarks>
    [Fact]
    public void The_preview_has_one_segment_per_quad_the_ring_crosses() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        List<(Vector3 A, Vector3 B)> segments = [];

        Assert.True(BlockoutHover.LoopCut(mesh, 0, 1, 0.5f, segments));
        Assert.Equal(4, segments.Count);

        var before = mesh.FaceCount;

        MeshOperations.LoopCut(mesh, 0, 1, 0.5f);

        Assert.Equal(before + segments.Count, mesh.FaceCount);
    }

    /// <summary>
    ///     Every point the preview draws is a position the cut inserts, at the slide it was asked
    ///     for — which is what stops the drawing and the verb drifting apart.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Off-centre on purpose.</b> At a slide of a half every candidate formula agrees, so a
    ///     preview that ignored the parameter entirely would pass a mid-ring test and mislead a
    ///     designer at every other value — which is the one thing "slide before committing" is for.
    /// </remarks>
    [Theory]
    [InlineData(1, 0.25f)]
    [InlineData(3, 0.5f)]
    public void Every_previewed_point_is_a_position_the_cut_would_insert(int cuts, float slide) {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var cut = new EditMesh(mesh);
        List<(Vector3 A, Vector3 B)> segments = [];

        Assert.True(BlockoutHover.LoopCut(mesh, 0, cuts, slide, segments));
        Assert.Equal(4 * cuts, segments.Count);

        MeshOperations.LoopCut(cut, 0, cuts, slide);

        var made = cut.Positions.ToArray();

        foreach (var (a, b) in segments) {
            Assert.Contains(made, position => Vector3.Distance(position, a) < 1e-4f);
            Assert.Contains(made, position => Vector3.Distance(position, b) < 1e-4f);
        }

        // ⚠ And every segment is the same length, which is what catches the pairing.
        // A box is six unit squares, so a loop across one of its rings is a set of parallel segments
        // of equal length — pair cut k on one side with cut k counted from the *other* end and the
        // outer segments become diagonals while the middle one stays put. Every point is still a
        // point the cut inserts, so the membership check above passes over it: this is the assertion
        // that does not.
        var width = Vector3.Distance(segments[0].A, segments[0].B);

        Assert.True(width > 1e-3f);
        Assert.All(segments, segment => Assert.Equal(width, Vector3.Distance(segment.A, segment.B), 3));
    }

    /// <summary>
    ///     And it reaches the overlay: hovering an edge in Edge mode draws the loop the cut would
    ///     make, in the pane, without anything else asking it to.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion the two above cannot make.</b> A preview function nothing calls
    ///     is this repository's commonest defect, and every test of <c>BlockoutHover.LoopCut</c> stays
    ///     green over a mode that never draws it. Filtered by colour rather than counted, because the
    ///     element cage is writing every edge of the mesh into the same channel — an unfiltered count
    ///     would be a count of the cage.
    /// </remarks>
    [Fact]
    public void Hovering_an_edge_draws_the_loop_the_cut_would_make() {
        var (mode, pane) = Hovered(Vector3.Zero);
        var editing = mode.Editing!;

        var entity = scene.CreateShape(
            PrimitiveKind.Cube,
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }
        );

        new TransformSystem().Resolve(world);
        world.AdvanceVersion();

        scene.Selection.Set(entity);
        pane.Editing = editing;

        Assert.True(commands!.Execute(BlockoutMode.ElementCommand(BlockoutElement.Edge)));
        Assert.True(editing.IsActive);

        // ⚠ A quad box over the demoted primitive, because a ring is only defined across quads and
        // `PrimitiveKind.Cube` demotes to triangles. A loop cut on a triangulated mesh is a verb with
        // nothing to do, which is a true statement about the kernel and not what this test is asking.
        scene.SetMesh(editing.Target, MeshShapes.Create(ShapeKind.Box));
        editing.Reconcile();

        // Nothing hovered: the cage is drawn and the loop is not.
        Assert.Empty(Loop(pane, mode));

        editing.Hover = new SubObject(SubObjectKind.Edge, 0);

        List<(Vector3 A, Vector3 B)> expected = [];

        Assert.True(BlockoutHover.LoopCut(editing.Mesh!, 0, mode.LoopCuts, mode.LoopSlide, expected));

        // Two vertices per segment, in the mode's own colour, and nothing else in that colour.
        Assert.Equal(expected.Count * 2, Loop(pane, mode).Count);
    }

    /// <summary>A preview of a ring that goes nowhere is nothing, rather than a single point.</summary>
    [Fact]
    public void An_edge_with_no_ring_previews_nothing() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        List<(Vector3 A, Vector3 B)> segments = [];

        Assert.False(BlockoutHover.LoopCut(mesh, mesh.Edges.Count + 5, 1, 0.5f, segments));
        Assert.Empty(segments);
    }

    // ── The retopology artefacts ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     docs/plan/41 § D1's stages reach the viewport: a capture draws, and turning every stage off
    ///     draws nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion <c>RemeshDumpTests</c> cannot make.</b> <c>RemeshDump</c> had a
    ///     full suite over its artefacts and no consumer under <c>Editor/</c> at all
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/413">#413</a>), and every one of those
    ///     tests stays green over a tree where nothing draws them — which is the whole of what was
    ///     wrong, since § D1's argument for making each stage an artefact is that a remesher is judged
    ///     by a picture.
    ///     <para>
    ///         ⚠ <b>And both directions, because only the pair says the switches are wired.</b> An
    ///         overlay that ignored them and always drew would pass the first half.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_captured_dump_reaches_the_overlay_and_the_switches_take_it_away() {
        var (mode, pane) = Hovered(Vector3.Zero);

        var entity = Box();
        var before = Overlay(pane).Count;

        Assert.False(mode.RemeshDebug.IsVisible);
        Assert.True(commands!.Execute(BlockoutMode.RemeshDebugCommand));

        Assert.NotNull(mode.RemeshDebug.Dump);
        Assert.Equal(entity, mode.RemeshDebug.Target);
        Assert.True(mode.RemeshDebug.IsVisible);

        var drawn = Overlay(pane).Count;

        Assert.True(drawn > before, $"the capture drew nothing: {drawn} segments against {before}");

        // Every switch off: the capture is still there and the picture is not, which is the half that
        // says the switches are read rather than the capture being drawn unconditionally.
        mode.RemeshDebug.ShowConditioned = false;
        mode.RemeshDebug.ShowFeatures = false;
        mode.RemeshDebug.ShowField = false;
        mode.RemeshDebug.ShowSingularities = false;
        mode.RemeshDebug.ShowPatches = false;
        mode.RemeshDebug.ShowQuantization = false;

        Assert.False(mode.RemeshDebug.IsVisible);
        Assert.Equal(before, Overlay(pane).Count);
        Assert.NotNull(mode.RemeshDebug.Dump);

        // And the command is a toggle: pressed again it throws the capture away.
        Assert.True(commands.Execute(BlockoutMode.RemeshDebugCommand));
        Assert.Null(mode.RemeshDebug.Dump);
        Assert.True(mode.RemeshDebug.Target.IsNull);
    }

    /// <summary>
    ///     Each stage is drawn by its own switch, so what is on screen is the stage a person asked
    ///     for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A missing arm in a drawing switch does not fail to build; it silently never draws.</b>
    ///     Six booleans read in one method is exactly the shape where one of them gets left out, and
    ///     "the overlay is not empty" cannot see it — so each is turned on alone and counted.
    /// </remarks>
    [Fact]
    public void Every_stage_switch_draws_something_of_its_own() {
        var (mode, pane) = Hovered(Vector3.Zero);

        Box();

        Assert.True(mode.RemeshDebug.Capture(scene, mode.Retopology.ToRemeshSettings()));

        var debug = mode.RemeshDebug;
        Action<bool>[] switches = [
            on => debug.ShowConditioned = on,
            on => debug.ShowFeatures = on,
            on => debug.ShowField = on,
            on => debug.ShowSingularities = on,
            on => debug.ShowPatches = on,
            on => debug.ShowQuantization = on
        ];

        foreach (var stage in switches) {
            stage(false);
        }

        var baseline = Overlay(pane).Count;

        var drew = 0;

        foreach (var stage in switches) {
            stage(true);

            if (Overlay(pane).Count > baseline) {
                drew++;
            }

            stage(false);
        }

        // ⚠ All six, not "at least one", and measured rather than hoped for: a unit box conditions to
        // a mesh with features, a field, singularities, a patch partition and labelled arcs, so every
        // stage has something to say about it. A count that allowed one to be silent would be a count
        // that could not see the switch somebody forgot to read.
        Assert.Equal(6, switches.Length);
        Assert.Equal(switches.Length, drew);
    }

    // ============================================================ Harness

    /// <summary>A pane the size of a window, looking straight down at a point on the ground.</summary>
    /// <remarks>
    ///     ⚠ <b>Show flags off and no gizmo, so the overlay is the preview and only the preview.</b>
    ///     Asserting "the overlay grew" against a pane also drawing a transform gizmo would pass for a
    ///     box of no segments at all.
    /// </remarks>
    SceneViewport Pane(Vector3 at) {
        var document = new UiDocument(800f, 600f);

        document.Load("root { width: 800px; height: 600px; } viewport { width: 800px; height: 600px; }");

        var control = document.Root.Add<ViewportControl>();

        document.Update();
        control.Refresh();

        var pane = new SceneViewport(control, new Selection<Entity>()) { Show = SceneShow.None };

        pane.Camera.Pivot = at;
        pane.Camera.Distance = 30f;

        // ⚠ `PitchLimit` rather than a right angle, and `EditorCamera`'s own remarks say why: at
        // exactly ninety degrees the view direction and the up vector are parallel and the view
        // matrix is degenerate. A thousandth off vertical costs nothing here — the ray through the
        // middle pixel *is* the view direction and therefore passes through the pivot whatever the
        // pitch, so the cell under the pointer stays arithmetic rather than a measurement.
        pane.Camera.Pitch = -EditorCamera.PitchLimit;
        pane.Camera.Yaw = 0f;

        owned.Add(pane);
        owned.Add(document);

        return pane;
    }

    SceneViewport Pane() => Pane(Vector3.Zero);

    /// <summary>A blockout mode in a shell, hovering the middle of a pane aimed at a point.</summary>
    (BlockoutMode Mode, SceneViewport Pane) Hovered(Vector3 at) {
        var shell = new EditorShell(1280f, 800f);
        var mode = new BlockoutMode();

        owned.Add(shell);
        commands = shell.Commands;

        // ⚠ Select first, and it is not decoration: the first mode added becomes the active one, so
        // adding blockout alone would make `Activate` a no-op that never raises `Changed` — and the
        // shell's context would stay empty, which puts every scoped verb this mode registers out of
        // scope. `BlockoutModeTests.Built` has the same two lines for the same reason.
        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(mode);
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";
        shell.Modes.Activate(BlockoutMode.ModeId);

        mode.Editing = new MeshEdit(scene);

        var pane = Pane(at);

        mode.Pointer(pane, Move());

        return (mode, pane);
    }

    /// <summary>A parametric box in the scene, selected — which is what a retopology verb takes.</summary>
    /// <remarks>
    ///     ⚠ <c>BlockoutCreate.Shape</c> and not <c>SceneDocument.CreateShape</c>: only the first gives
    ///     the entity a mesh <c>MeshOf</c> can read, and a capture over the second finds nothing to
    ///     condition and reports no artefacts — silently, since a refusal here is an empty dump.
    /// </remarks>
    Entity Box() {
        var entity = BlockoutCreate.Shape(
            scene,
            new ShapeParameters { Kind = ShapeKind.Box, Size = Vector3.One },
            Vector3.Zero
        );

        new TransformSystem().Resolve(world);
        world.AdvanceVersion();
        scene.Selection.Set(entity);

        return entity;
    }

    static PointerEvent Move(float x = 400f, float y = 300f) =>
        new() { X = x, Y = y, Action = PointerAction.Moved, Button = PointerButton.None };

    /// <summary>Just the loop preview's vertices, told apart from the element cage by their colour.</summary>
    IReadOnlyList<LineVertex> Loop(SceneViewport pane, BlockoutMode mode) =>
        [.. Overlay(pane).Where(vertex => vertex.Colour == mode.LoopColour)];

    /// <summary>The overlay segments a frame would carry, through the call the presenter makes.</summary>
    IReadOnlyList<LineVertex> Overlay(SceneViewport pane) {
        var lines = new SceneLines();

        lines.Build(scene, pane, Height);

        return lines.Overlay;
    }
}
