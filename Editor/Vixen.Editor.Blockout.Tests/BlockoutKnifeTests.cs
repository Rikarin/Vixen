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
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>doc 24 § P3's knife: the gesture, the verb, and the modality round both.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gesture is driven with rays rather than with points, because the snap is the
///         half that can be wrong.</b> A test that handed <c>BlockoutKnife</c> a point already on an
///         edge would be testing <c>MeshOperations.Knife</c> a second time and would stay green over
///         a snap that put every click in the middle of the face — which is the state in which the
///         kernel refuses every cut and the tool silently does nothing.
///     </para>
///     <para>
///         ⚠ <b>And the commit goes through the mode's own key handling.</b> "The gesture builds a
///         list of cuts" and "pressing Enter cuts the mesh" are two statements, and the second is what
///         a designer has; a knife whose <c>Enter</c> never reached the verb would pass every
///         assertion about <c>Cuts()</c>.
///     </para>
/// </remarks>
public sealed class BlockoutKnifeTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-knife-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly List<IDisposable> owned = [];

    CommandRegistry? commands;

    public BlockoutKnifeTests() {
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

    // ── The snap ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A ray at the middle of a face resolves to the nearest point of its boundary, never to
    ///     where it hit.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion the whole tool rests on.</b> The kernel primitive is only
    ///     defined for points on a face's rim, so a snap that returned the hit point would produce a
    ///     stroke every cut of which the kernel refuses — a tool that draws a preview and then does
    ///     nothing, which is the worst of the shapes this could take.
    /// </remarks>
    [Fact]
    public void A_click_inside_a_face_lands_on_its_boundary() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        // Straight down at the middle of the top face, from above.
        var hit = BlockoutKnife.Resolve(mesh, Matrix4x4.Identity, Down(new Vector3(0.02f, 0f, 0.06f)));

        Assert.NotNull(hit);

        var loop = mesh.CornersOf(hit.Value.Face).ToArray();

        // On the rim: the point is within a rounding of some edge of the face it named.
        Assert.True(
            Distance(mesh, loop, hit.Value.Point) < 1e-4f,
            $"the snapped point is {Distance(mesh, loop, hit.Value.Point)} from the face's boundary"
        );
    }

    /// <summary>A click near a corner takes the corner, and one near the middle of an edge its midpoint.</summary>
    /// <remarks>
    ///     ⚠ <b>Corners are tested before midpoints and the order is load-bearing.</b> Reversing them
    ///     makes the midpoint of a short edge swallow both its ends, so a designer aiming at a corner
    ///     gets the middle — which looks like a snap that is simply inaccurate.
    /// </remarks>
    [Fact]
    public void A_click_near_a_corner_takes_the_corner_and_one_near_the_middle_the_midpoint() {
        var mesh = MeshShapes.Create(ShapeKind.Box);
        var face = FaceOf(mesh, BlockoutKnife.Resolve(mesh, Matrix4x4.Identity, Down(Vector3.Zero)));
        var loop = mesh.CornersOf(face).ToArray();
        var corner = mesh.Positions[loop[0]];
        var next = mesh.Positions[loop[1]];
        var middle = Vector3.Lerp(corner, next, 0.5f);
        var centre = Centre(mesh, loop);

        // A tenth of the way in from the corner towards the face's middle: nearer that corner than
        // anything else, and inside the snap fraction.
        var near = BlockoutKnife.Resolve(mesh, Matrix4x4.Identity, Down(Vector3.Lerp(corner, centre, 0.1f)));

        Assert.NotNull(near);
        Assert.True(Vector3.Distance(near.Value.Point, corner) < 1e-4f, $"took {near.Value.Point}, wanted {corner}");

        var mid = BlockoutKnife.Resolve(mesh, Matrix4x4.Identity, Down(Vector3.Lerp(middle, centre, 0.1f)));

        Assert.NotNull(mid);
        Assert.True(Vector3.Distance(mid.Value.Point, middle) < 1e-4f, $"took {mid.Value.Point}, wanted {middle}");
    }

    /// <summary>A ray that misses the mesh resolves to nothing rather than to the nearest face.</summary>
    [Fact]
    public void A_ray_that_misses_resolves_to_nothing() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        Assert.Null(BlockoutKnife.Resolve(mesh, Matrix4x4.Identity, Down(new Vector3(40f, 0f, 40f))));
    }

    // ── The stroke ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A stroke of one point is no cuts, and a segment across a boundary is two candidates.</summary>
    /// <remarks>
    ///     ⚠ <b>The second half is the one that makes a stroke across an edge work at all.</b> A click
    ///     on a shared edge belongs to the face on either side of it, and which one the picker
    ///     answered with is not a choice the designer made — so both are offered and the kernel
    ///     refuses whichever the segment does not cross.
    /// </remarks>
    [Fact]
    public void A_segment_across_a_face_boundary_offers_both_faces() {
        var knife = new BlockoutKnife();
        var mesh = MeshShapes.Create(ShapeKind.Box);

        Assert.Empty(knife.Cuts());

        var first = Place(knife, mesh, new Vector3(0f, 0f, 0.35f));

        Assert.True(first);
        Assert.Empty(knife.Cuts());

        Assert.True(Place(knife, mesh, new Vector3(0f, 0f, -0.35f)));

        var cuts = knife.Cuts();

        Assert.NotEmpty(cuts);

        // One cut per face the segment could belong to: one when both ends named the same face, two
        // when they did not.
        var faces = knife.Points.Select(point => point.Face).Distinct().Count();

        Assert.Equal(faces, cuts.Count);
    }

    /// <summary>Cancelling throws the stroke away and disarms the tool.</summary>
    [Fact]
    public void Cancelling_throws_the_stroke_away() {
        var knife = new BlockoutKnife { IsArmed = true };
        var mesh = MeshShapes.Create(ShapeKind.Box);

        Assert.True(Place(knife, mesh, Vector3.Zero));
        Assert.NotEmpty(knife.Points);

        knife.Cancel();

        Assert.Empty(knife.Points);
        Assert.Null(knife.Hover);
        Assert.False(knife.IsArmed);
    }

    // ── The verb ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stroke committed cuts the mesh, as one undo entry that restores it.</summary>
    /// <remarks>
    ///     ⚠ <b>The undo is the half a topology verb gets wrong.</b> A cut replaces the face table and
    ///     adds positions, so an entry that tried to describe what happened restores nothing — doc
    ///     24's D3, and the reason <c>EditMeshCommand.Rebuilt</c> records the mesh as it was.
    /// </remarks>
    [Fact]
    public void A_committed_stroke_is_one_undo_entry_that_restores_the_mesh() {
        var (mode, editing) = Editing();

        // ⚠ The demotion first, and counted out of the measurement. A parametric box collapses the
        // first time any verb edits it — one entry of its own, by `MeshEdit.Demote` — so a test that
        // measured across the first edit would be asserting two entries and calling it one. What the
        // verb owes is a single entry for the cut, which is the second one.
        Assert.True(editing.Demote());

        var mesh = editing.Mesh!;
        var before = mesh.FaceCount;
        var depth = scene.Stack.History.Count;

        var knife = mode.Knife;

        knife.IsArmed = true;

        Assert.True(Place(knife, mesh, new Vector3(0f, 0f, 0.35f)));
        Assert.True(Place(knife, mesh, new Vector3(0f, 0f, -0.35f)));
        Assert.True(BlockoutGeometry.Knife(editing, knife.Cuts()));

        Assert.Equal(before + 1, editing.Mesh!.FaceCount);
        Assert.Equal(depth + 1, scene.Stack.History.Count);
        Assert.False(editing.Selection.IsEmpty);

        scene.Stack.Undo();

        Assert.Equal(before, scene.MeshOf(editing.Target)!.FaceCount);
    }

    /// <summary>A stroke with nothing in it changes nothing and records nothing.</summary>
    [Fact]
    public void An_empty_stroke_records_nothing() {
        var (_, editing) = Editing();
        var depth = scene.Stack.History.Count;

        Assert.False(BlockoutGeometry.Knife(editing, []));
        Assert.Equal(depth, scene.Stack.History.Count);
    }

    // ── The modality ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The whole gesture through the mode: the key arms it, clicks place points, the preview is
    ///     drawn, and Enter cuts.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every step here is one nothing else asserts.</b> A knife whose command did not arm,
    ///     whose presses reached the pane's rubber-band instead of the stroke, whose preview never
    ///     drew, or whose <c>Enter</c> never reached the verb would each leave the gesture tests
    ///     above green and the tool unusable.
    /// </remarks>
    [Fact]
    public void The_mode_arms_places_previews_and_commits() {
        var (mode, editing) = Editing();
        var pane = Pane(mode, editing);
        var before = editing.Mesh!.FaceCount;

        Assert.True(commands!.Execute(BlockoutMode.KnifeCommand));
        Assert.True(mode.Knife.IsArmed);

        // Two clicks on the top face, either side of its middle. The presses are taken by the mode,
        // which is what keeps them out of the pane's rubber-band.
        Assert.True(mode.Pointer(pane, Press(360f, 300f)));
        Assert.True(mode.Pointer(pane, Press(440f, 300f)));
        Assert.Equal(2, mode.Knife.Points.Count);

        // The stroke is on screen before it is committed to.
        var drawn = Overlay(pane).Count(vertex => vertex.Colour == mode.KnifeColour);

        Assert.True(drawn >= 2, $"the placed segment drew {drawn} vertices");

        Assert.True(mode.Key(pane, new KeyEvent { Key = InputKey.Enter, Action = KeyAction.Pressed }));

        Assert.False(mode.Knife.IsArmed);
        Assert.Empty(mode.Knife.Points);
        Assert.True(editing.Mesh!.FaceCount > before, "the stroke cut nothing");
    }

    /// <summary>Escape throws the stroke away and leaves the mesh alone.</summary>
    [Fact]
    public void Escape_abandons_the_stroke() {
        var (mode, editing) = Editing();
        var pane = Pane(mode, editing);
        var before = editing.Mesh!.FaceCount;

        Assert.True(commands!.Execute(BlockoutMode.KnifeCommand));
        Assert.True(mode.Pointer(pane, Press(360f, 300f)));
        Assert.NotEmpty(mode.Knife.Points);

        Assert.True(mode.Key(pane, new KeyEvent { Key = InputKey.Escape, Action = KeyAction.Pressed }));

        Assert.False(mode.Knife.IsArmed);
        Assert.Empty(mode.Knife.Points);
        Assert.Equal(before, editing.Mesh!.FaceCount);
        Assert.DoesNotContain(Overlay(pane), vertex => vertex.Colour == mode.KnifeColour);
    }

    // ============================================================ Harness

    /// <summary>A ray pointing straight down at a point, which meets a unit box's top face.</summary>
    static Ray Down(Vector3 at) => new(new Vector3(at.X, 10f, at.Z), -Vector3.UnitY);

    static int FaceOf(EditMesh mesh, KnifePoint? point) {
        Assert.NotNull(point);

        return point.Value.Face;
    }

    static Vector3 Centre(EditMesh mesh, int[] loop) {
        var total = Vector3.Zero;

        foreach (var corner in loop) {
            total += mesh.Positions[corner];
        }

        return total / loop.Length;
    }

    /// <summary>How far a point is from a face's boundary.</summary>
    static float Distance(EditMesh mesh, int[] loop, Vector3 point) {
        var best = float.MaxValue;

        for (var corner = 0; corner < loop.Length; corner++) {
            var a = mesh.Positions[loop[corner]];
            var b = mesh.Positions[loop[(corner + 1) % loop.Length]];
            var along = b - a;
            var length = along.LengthSquared();

            if (length <= 0f) {
                continue;
            }

            var t = Math.Clamp(Vector3.Dot(point - a, along) / length, 0f, 1f);

            best = MathF.Min(best, Vector3.Distance(a + (along * t), point));
        }

        return best;
    }

    static bool Place(BlockoutKnife knife, EditMesh mesh, Vector3 at) =>
        knife.Track(mesh, Matrix4x4.Identity, Down(at)) && knife.Place();

    /// <summary>A mode over a box whose mesh is a quad box, in Face mode, with a shell behind it.</summary>
    (BlockoutMode Mode, MeshEdit Editing) Editing() {
        var shell = new EditorShell(1280f, 800f);
        var mode = new BlockoutMode();

        owned.Add(shell);
        commands = shell.Commands;

        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(mode);
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";
        shell.Modes.Activate(BlockoutMode.ModeId);

        var editing = new MeshEdit(scene);

        mode.Editing = editing;

        var entity = BlockoutCreate.Shape(
            scene,
            new ShapeParameters { Kind = ShapeKind.Box, Size = Vector3.One },
            Vector3.Zero
        );

        new TransformSystem().Resolve(world);
        world.AdvanceVersion();
        scene.Selection.Set(entity);

        Assert.True(commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Face)));

        // ⚠ A quad box over the demoted primitive: a knife works on any polygon, but a box of six
        // quads is the fixture whose face count is arithmetic rather than a measurement.
        scene.SetMesh(editing.Target, MeshShapes.Create(ShapeKind.Box));
        editing.Reconcile();

        Assert.True(editing.IsActive);

        return (mode, editing);
    }

    /// <summary>A pane looking straight down at the box, so a click near the middle is on its top.</summary>
    SceneViewport Pane(BlockoutMode mode, MeshEdit editing) {
        var document = new UiDocument(800f, 600f);

        document.Load("root { width: 800px; height: 600px; } viewport { width: 800px; height: 600px; }");

        var control = document.Root.Add<ViewportControl>();

        document.Update();
        control.Refresh();

        var pane = new SceneViewport(control, new Selection<Entity>()) { Show = SceneShow.None };

        pane.Editing = editing;
        pane.Camera.Pivot = Vector3.Zero;
        pane.Camera.Distance = 6f;
        pane.Camera.Pitch = -EditorCamera.PitchLimit;
        pane.Camera.Yaw = 0f;

        owned.Add(pane);
        owned.Add(document);

        // The pointer has to have been in the pane for the mode to have set its cursor on it, which
        // is the same rule the hover previews follow.
        mode.Pointer(pane, new PointerEvent { X = 400f, Y = 300f, Action = PointerAction.Moved });

        return pane;
    }

    static PointerEvent Press(float x, float y) =>
        new() { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary };

    IReadOnlyList<LineVertex> Overlay(SceneViewport pane) {
        var lines = new SceneLines();

        lines.Build(scene, pane, 600);

        return lines.Overlay;
    }
}
