// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Rendering.Water;
using Vixen.Ui;
using Vixen.Water;
using Xunit;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.Water.Tests;

/// <summary>
///     The Profile tool, driven the way an author drives it — [docs/plan/35 § W9].
/// </summary>
/// <remarks>
///     <para>
///         <b>The handles have been arithmetic with no caller since they were written.</b>
///         <c>WaterEdit.Grab</c>, <c>Drag</c> and <c>HandlesOf</c> had tests and no user, and
///         <c>WaterMode.Pointer</c> answered <see langword="false" /> for every tool but Draw — so the
///         second of doc 35's three verbs was a name in a tool strip. What is asserted here is the
///         whole route: a press lands on a handle, a move widens the channel, a release is one undo
///         entry, and Escape puts it back.
///     </para>
///     <para>
///         ⚠ <b>A real pane and a real camera, because the hit test is in render pixels.</b> Neither
///         needs a device — a <c>UiDocument</c> with one box rule is enough to give the viewport a
///         size, which is <c>FlightTests.Pane</c>'s arrangement and its reason.
///     </para>
/// </remarks>
public sealed class WaterProfileTests {
    /// <summary>A river along +X, ten metres to a bank and three deep.</summary>
    /// <remarks>
    ///     ⚠ <b>The side of a curve running along +X is −Z</b>, which is
    ///     <c>cross(up, tangent)</c> and not a convention this test may pick: it is what
    ///     <see cref="WaterEdit.SideAt" /> answers and what the handles are therefore drawn on.
    /// </remarks>
    static Spline River() => new(Spline.SmoothTangents([new(0f, 0f, 0f), new(100f, 0f, 0f)]), closed: false);

    static WaterBodyComponent Body() =>
        WaterBodyComponent.Default with { Kind = WaterBodyKind.River, Spline = "river", HalfWidth = 10f, Depth = 3f };

    // --- The arithmetic -----------------------------------------------------

    /// <summary>The curve-and-profile overload is the body one, and is what a hit test may call.</summary>
    [Fact]
    public void The_two_HandlesOf_overloads_agree() {
        var spline = River();
        var component = Body();
        var body = new WaterBody(WaterBodyKind.River, spline, defaults: component.Profile);

        var fromBody = WaterEdit.HandlesOf(body, 0);
        var fromCurve = WaterEdit.HandlesOf(spline, component.Profile, 0);

        Assert.Equal(fromBody.Left, fromCurve.Left);
        Assert.Equal(fromBody.Right, fromCurve.Right);
        Assert.Equal(fromBody.Depth, fromCurve.Depth);
    }

    /// <summary>A ray aimed at a point on the axis puts the handle at that point.</summary>
    [Fact]
    public void A_handle_slides_to_where_the_pointer_aims_along_its_axis() {
        var handle = new Vector3(0f, 0f, -10f);
        var axis = new Vector3(0f, 0f, -1f);
        var target = new Vector3(0f, 0f, -15f);

        // Straight down at the target, which is the well-conditioned case a top view gives.
        var ray = new Ray(target + new Vector3(0f, 50f, 0f), new(0f, -1f, 0f));
        var moved = WaterEdit.OnAxis(ray, handle, axis);

        Assert.Equal(-15f, moved.Z, 3);
        Assert.Equal(5f, Vector3.Dot(moved - handle, axis), 3);
    }

    /// <summary>A camera looking straight down the axis holds the handle still.</summary>
    /// <remarks>
    ///     ⚠ The denominator goes to zero as the ray and the axis line up, and an unchecked divide
    ///     there is a half-width that jumps to a kilometre on the frame the author orbited past.
    /// </remarks>
    [Fact]
    public void An_edge_on_axis_does_not_fling_the_handle() {
        var handle = new Vector3(0f, 0f, -10f);
        var axis = new Vector3(0f, 0f, -1f);
        var ray = new Ray(new(0f, 0f, 40f), new(0f, 0f, -1f));

        Assert.Equal(handle, WaterEdit.OnAxis(ray, handle, axis));
    }

    // --- The gesture --------------------------------------------------------

    /// <summary>Press a bank handle, drag it out, let go: one wider river and one undo entry.</summary>
    [Fact]
    public void Dragging_a_bank_widens_the_river_and_commits_one_entry() {
        using var scene = new Harness();

        var handles = WaterEdit.HandlesOf(scene.Curve, Body().Profile, 0);

        Assert.True(scene.Press(handles.Right), "the press should have landed on the right bank's handle.");
        Assert.Equal(WaterHandle.WidthRight, scene.Mode.Editing.Holding);

        // Five metres further out along the same axis, which is the bank at fifteen.
        scene.Move(new(0f, 0f, -15f));

        Assert.Equal(15f, scene.Component.HalfWidth, 1);

        // ⚠ Applied as it happens and *not* pushed as it happens: forty pointer moves is forty undo
        // entries nobody can walk back through.
        Assert.Equal(0, scene.Document.Stack.Depth.Value);

        scene.Release();

        Assert.Equal(WaterHandle.None, scene.Mode.Editing.Holding);
        Assert.Equal(1, scene.Document.Stack.Depth.Value);
        Assert.Equal("Edit Water Profile", scene.Document.Stack.UndoName.Value);

        Assert.True(scene.Document.Stack.Undo());
        Assert.Equal(10f, scene.Component.HalfWidth, 3);
    }

    /// <summary>Escape mid-drag puts the profile back and leaves no entry.</summary>
    [Fact]
    public void Escape_abandons_a_handle_drag() {
        using var scene = new Harness();

        var handles = WaterEdit.HandlesOf(scene.Curve, Body().Profile, 0);

        Assert.True(scene.Press(handles.Right));
        scene.Move(new(0f, 0f, -20f));

        Assert.True(scene.Mode.Key(scene.Viewport, new KeyEvent { Key = InputKey.Escape }));

        Assert.Equal(WaterHandle.None, scene.Mode.Editing.Holding);
        Assert.Equal(10f, scene.Component.HalfWidth, 3);
        Assert.Equal(0, scene.Document.Stack.Depth.Value);
    }

    /// <summary>A press in empty space is the pane's, so the body can still be selected.</summary>
    [Fact]
    public void A_press_that_is_not_on_a_handle_is_left_to_the_pane() {
        using var scene = new Harness();

        Assert.False(scene.Press(new(0f, 0f, 40f)));
        Assert.Equal(WaterHandle.None, scene.Mode.Editing.Holding);
    }

    /// <summary>Without a curve source there are no handles, and nothing is taken.</summary>
    /// <remarks>
    ///     A body names its curve by name, and a mode with nothing to ask cannot know where the banks
    ///     are — so it must not swallow the press it cannot answer.
    /// </remarks>
    [Fact]
    public void With_no_curve_source_the_tool_takes_nothing() {
        using var scene = new Harness();

        var handles = WaterEdit.HandlesOf(scene.Curve, Body().Profile, 0);

        scene.Mode.Curves = null;

        Assert.False(scene.Press(handles.Right));
    }

    /// <summary>Hovering arms the pane's cursor, and the handles are what it draws.</summary>
    [Fact]
    public void Hovering_puts_the_handles_in_the_panes_cursor() {
        using var scene = new Harness();

        Assert.Null(scene.Viewport.Cursor);

        // A hover is tracked and *not* taken — the pane's own highlight has to survive the tool.
        Assert.False(scene.Pointer(PointerAction.Moved, PointerButton.None, new(0f, 0f, 0f)));
        Assert.NotNull(scene.Viewport.Cursor);

        var lines = new List<Vixen.Rendering.LineVertex>();

        scene.Viewport.Cursor!.Invoke(new(lines));

        Assert.NotEmpty(lines);

        // And leaving the pane takes them back off, so nothing says the tool is aimed where it is not.
        scene.Pointer(PointerAction.Exited, PointerButton.None, new(0f, 0f, 0f));

        Assert.Null(scene.Viewport.Cursor);
    }

    /// <summary>⚠ The centre line is drawn too, and not only the markers at the control points.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>SplineOverlay</c> was complete, tested and called by nothing but its own tests
    ///         — GitHub #118.</b> This is its first caller: the Profile tool draws the body's curve
    ///         before the handles that hang off it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The discriminator is a vertex <i>between</i> the control points.</b> The river's
    ///         two points are at x = 0 and x = 100, and every marker <c>WaterProfileHandles</c> emits
    ///         sits at one of them — so a line vertex strictly inside that span can only have come
    ///         from the sampled curve. Asserting the list merely grew would have stayed green with
    ///         the handles alone, which is what the test above already covers.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_hover_cursor_draws_the_curve_the_handles_hang_off() {
        using var scene = new Harness();

        Assert.False(scene.Pointer(PointerAction.Moved, PointerButton.None, new(0f, 0f, 0f)));

        var lines = new List<Vixen.Rendering.LineVertex>();

        scene.Viewport.Cursor!.Invoke(new(lines));

        Assert.Contains(
            lines,
            vertex => vertex.Colour == SplineOverlay.CurveColour
                && vertex.Position.X > 1f
                && vertex.Position.X < 99f
        );
    }

    /// <summary>Preview is a state and not a gesture, so it takes no pointer event at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole point of the tool is being able to fly around and look</b>, which a mode
    ///     that claimed the pointer would make impossible.
    /// </remarks>
    [Fact]
    public void Preview_takes_no_pointer_events() {
        using var scene = new Harness();

        scene.Mode.Tool = WaterTool.Preview;

        var handles = WaterEdit.HandlesOf(scene.Curve, Body().Profile, 0);

        Assert.False(scene.Press(handles.Right));
        Assert.False(scene.Pointer(PointerAction.Moved, PointerButton.None, handles.Right));
        Assert.False(scene.Pointer(PointerAction.Released, PointerButton.Primary, handles.Right));
        Assert.Null(scene.Viewport.Cursor);
    }

    /// <summary>The preview flag is something a listener hears rather than a field nobody reads.</summary>
    [Fact]
    public void The_carve_preview_is_announced_and_restored_when_the_mode_is_left() {
        using var scene = new Harness();

        var seen = new List<bool>();

        scene.Mode.Editing.CarvePreviewChanged += shown => seen.Add(shown);

        scene.Mode.Editing.CarvePreview = false;

        Assert.Equal([false], seen);

        // Set to what it already is, which is not a change and must not be announced as one.
        scene.Mode.Editing.CarvePreview = false;

        Assert.Equal([false], seen);

        // ⚠ Leaving the mode puts it back, because TerrainEditLayer.IsVisible is *saved*: an author
        // who left with the preview off would reopen a project whose riverbeds are invisible.
        scene.Mode.Deactivated();

        Assert.Equal([false, true], seen);
        Assert.True(scene.Mode.Editing.CarvePreview);
    }

    /// <summary>Changing tool takes the handles off the pane that was drawing them.</summary>
    [Fact]
    public void Changing_tool_takes_the_handles_off() {
        using var scene = new Harness();

        scene.Pointer(PointerAction.Moved, PointerButton.None, new(0f, 0f, 0f));

        Assert.NotNull(scene.Viewport.Cursor);

        scene.Mode.Tool = WaterTool.Draw;

        Assert.Null(scene.Viewport.Cursor);
    }

    // --- The harness --------------------------------------------------------

    /// <summary>A pane looking straight down at one river, with the mode aimed at it.</summary>
    sealed class Harness : IDisposable {
        readonly UiDocument ui;
        readonly ViewportControl control;
        readonly World world;
        readonly EditorProject project;
        readonly string root;
        readonly Entity entity;

        public Harness() {
            ui = new(800f, 600f);
            ui.Load("root { width: 800px; height: 600px; } viewport { width: 800px; height: 600px; }");

            control = ui.Root.Add<ViewportControl>();
            ui.Update();
            control.Refresh();

            root = Path.Combine(Path.GetTempPath(), "vixen-water-profile", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets"));

            project = new(new ProjectPaths(root));
            project.Open();

            world = new("Water profile");
            Document = new(project, world, AssetId.Empty, "Scene");
            Viewport = new(control, Document.Selection);

            // Straight down, so the bank axis lies in the screen plane and the drag is well
            // conditioned — see OnAxis's remarks about a camera looking down the axis.
            Viewport.Camera.LookFrom(ViewDirection.Top);
            Viewport.Camera.Pivot = Vector3.Zero;
            Viewport.Camera.Distance = 80f;

            Curve = River();

            entity = Document.Add("River", default);
            world.Add(entity, Body());

            // `SceneDocument.Add` gives an entity both transforms already, so this is a Set — adding
            // one twice is a structural change and the world refuses it.
            world.Set(entity, new WorldTransform { Value = Matrix4x4.Identity });
            Document.Selection.Set(entity);

            Mode = new() { Document = Document, Curves = new Source(Curve), Tool = WaterTool.Profile };
        }

        public SceneDocument Document { get; }

        public SceneViewport Viewport { get; }

        public WaterMode Mode { get; }

        public Spline Curve { get; }

        /// <summary>What the scene says about the river right now.</summary>
        public WaterBodyComponent Component => world.Read<WaterBodyComponent>(entity);

        /// <summary>Sends a pointer event aimed at a world point.</summary>
        public bool Pointer(PointerAction action, PointerButton button, Vector3 at) {
            var screen = Viewport.Camera.Project(at, control.RenderWidth, control.RenderHeight);

            return Mode.Pointer(
                Viewport,
                new PointerEvent { Action = action, Button = button, X = screen.X, Y = screen.Y }
            );
        }

        public bool Press(Vector3 at) => Pointer(PointerAction.Pressed, PointerButton.Primary, at);

        public void Move(Vector3 at) => Pointer(PointerAction.Moved, PointerButton.None, at);

        public void Release() => Pointer(PointerAction.Released, PointerButton.Primary, Vector3.Zero);

        public void Dispose() {
            Viewport.Dispose();
            ui.Dispose();
            world.Dispose();

            try {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            } catch (IOException) {
                // A temporary directory a virus scanner still has open. Not this test's business.
            }
        }

        /// <summary>One curve, whatever it is asked for.</summary>
        sealed class Source(Spline spline) : IWaterScene {
            public Spline? SplineFor(string name, in Matrix4x4 placement) => spline;

            public WaterWaveSpectrum? SpectrumFor(string name) => null;

            public float GroundAt(Vector2 ground) => 0f;
        }
    }
}
