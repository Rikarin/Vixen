// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>An editor mode's first refusal on a pane's input — doc 20's A1, from the pane's end.</summary>
public class ViewportInputTests {
    /// <summary>Something that takes whatever it is told to and remembers what it saw.</summary>
    sealed class Owner : IViewportInput {
        public bool ClaimsPointer { get; set; }
        public bool ClaimsKey { get; set; }

        public List<PointerAction> Pointers { get; } = [];
        public List<InputKey> Keys { get; } = [];

        public bool Pointer(SceneViewport pane, PointerEvent args) {
            Pointers.Add(args.Action);
            return ClaimsPointer;
        }

        public bool Key(SceneViewport pane, KeyEvent args) {
            Keys.Add(args.Key);
            return ClaimsKey;
        }
    }

    /// <summary>A point some way along the gizmo's x arm, in render pixels.</summary>
    static Vector2 AlongX(Pane pane) {
        var scale = pane.Viewport.Gizmo.WorldPerPixel(pane.Camera, pane.Control.RenderHeight)
            * pane.Viewport.Gizmo.HandleLength;

        return pane.Screen(new Vector3(scale * 0.6f, 0f, 0f));
    }

    [Fact]
    public void A_pane_with_nothing_attached_reads_every_event_itself() {
        using var pane = new Pane();

        pane.Targets.Add(new StubTarget());
        pane.Frame();

        pane.Press(PointerButton.Primary, AlongX(pane));

        // Null `Input` is the editor as it shipped, and every other test in this assembly relies on
        // it: a seam that changed the default would have changed the viewport.
        Assert.Null(pane.Viewport.Input);
        Assert.True(pane.Viewport.Gizmo.IsDragging);
    }

    [Fact]
    public void An_owner_that_declines_leaves_the_pane_exactly_as_it_was() {
        using var pane = new Pane();
        var owner = new Owner();

        pane.Viewport.Input = owner;
        pane.Targets.Add(new StubTarget());
        pane.Frame();

        pane.Press(PointerButton.Primary, AlongX(pane));

        // Doc 24's P0 ships a Blockout mode that declines everything, so this is the shipped path
        // rather than a degenerate one.
        Assert.Contains(PointerAction.Pressed, owner.Pointers);
        Assert.True(pane.Viewport.Gizmo.IsDragging);
    }

    [Fact]
    public void An_owner_that_claims_a_press_takes_it_off_the_gizmo() {
        using var pane = new Pane();
        var owner = new Owner { ClaimsPointer = true };

        pane.Viewport.Input = owner;
        pane.Targets.Add(new StubTarget());
        pane.Frame();

        pane.Press(PointerButton.Primary, AlongX(pane));

        Assert.False(pane.Viewport.Gizmo.IsDragging);
    }

    [Fact]
    public void A_drag_already_running_is_not_taken_away_mid_gesture() {
        using var pane = new Pane();
        var owner = new Owner();
        var target = new StubTarget();

        pane.Viewport.Input = owner;
        pane.Targets.Add(target);
        pane.Frame();

        var grab = AlongX(pane);

        pane.Press(PointerButton.Primary, grab);
        Assert.True(pane.Viewport.Gizmo.IsDragging);

        // ⚠ The mode changing its mind mid-drag. Refusal is over what a press *starts*: an owner that
        // could take the release of a drag it did not begin would leave the gizmo holding the object,
        // with no event ever arriving to let go.
        owner.ClaimsPointer = true;

        pane.Move(grab + new Vector2(80f, 0f));
        pane.Release(PointerButton.Primary, grab + new Vector2(80f, 0f));

        Assert.False(pane.Viewport.Gizmo.IsDragging);
        Assert.True(target.Position.X > 0f);
    }

    [Fact]
    public void Keys_are_offered_during_a_drag_because_that_is_what_numeric_entry_needs() {
        using var pane = new Pane();
        var owner = new Owner { ClaimsKey = true };

        pane.Viewport.Input = owner;
        pane.Targets.Add(new StubTarget());
        pane.Frame();

        var grab = AlongX(pane);
        pane.Press(PointerButton.Primary, grab);

        // Doc 24's `G X 5 ⏎`: X means "along X" only because a drag is in flight, so a hook that
        // stood down for the duration of one could not carry the feature it exists for.
        Assert.True(pane.Key(InputKey.X, KeyAction.Pressed));
        Assert.Contains(InputKey.X, owner.Keys);
    }

    // ── Crossings ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A pane that is not the whole window, so the pointer has somewhere else to be.</summary>
    /// <remarks>
    ///     <see cref="Pane" /> fills its document, which is right for everything above and useless
    ///     here: a pointer that cannot leave the viewport never crosses its edge.
    /// </remarks>
    sealed class Half : IDisposable {
        public Half() {
            Document = new(800f, 600f);
            Document.Load("root { width: 800px; height: 600px; } viewport { width: 800px; height: 400px; }");

            Control = Document.Root.Add<Vixen.Ui.Controls.Advanced.Viewport>();
            Document.Update();
            Control.Refresh();

            Viewport = new(Control, new Selection<Vixen.Core.Entity>()) { TargetsFactory = () => Targets };
        }

        public UiDocument Document { get; }

        public Vixen.Ui.Controls.Advanced.Viewport Control { get; }

        public SceneViewport Viewport { get; }

        public List<IGizmoTarget> Targets { get; } = [];

        /// <summary>A move, dispatched into the document so that the crossings are worked out.</summary>
        public void Move(float x, float y) =>
            Document.Dispatch(new PointerEvent { X = x, Y = y, Action = PointerAction.Moved });

        public void Press(float x, float y) =>
            Document.Dispatch(
                new PointerEvent { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary }
            );

        public void Dispose() {
            Viewport.Dispose();
            Document.Dispose();
        }
    }

    /// <summary>
    ///     ⚠ <b>The one thing a synthetic <see cref="PointerAction.Exited" /> handed straight to an
    ///     owner cannot say.</b> Crossings are never fed in from outside: the document works them out
    ///     from where the pointer is and delivers them <see cref="RoutingStrategy.Direct" />, and
    ///     <c>UiElement.Invoke</c> matches handlers on the strategy they registered with — so the
    ///     bubble listener that hears every move hears no crossing at all. Anything a mode draws under
    ///     the pointer depends on this arriving.
    /// </summary>
    [Fact]
    public void An_owner_is_told_when_the_pointer_leaves_the_pane() {
        using var pane = new Half();
        var owner = new Owner();

        pane.Viewport.Input = owner;

        pane.Move(400f, 200f);

        Assert.Contains(PointerAction.Entered, owner.Pointers);
        Assert.Contains(PointerAction.Moved, owner.Pointers);

        owner.Pointers.Clear();
        pane.Move(400f, 550f);

        Assert.Contains(PointerAction.Exited, owner.Pointers);

        // And no move, because the pointer is no longer in the pane for one to be about.
        Assert.DoesNotContain(PointerAction.Moved, owner.Pointers);
    }

    [Fact]
    public void A_crossing_leaves_the_panes_own_reading_of_the_pointer_alone() {
        using var pane = new Half();

        pane.Viewport.Input = new Owner();
        pane.Move(400f, 200f);

        var inside = pane.Viewport.PointerPosition;

        pane.Move(400f, 550f);

        // ⚠ An `Exited` carries the position that took the pointer out of the pane, which is a point
        // outside it. Writing it here would leave the gizmo's hover test aiming at a pixel that is not
        // in the viewport.
        Assert.Equal(inside, pane.Viewport.PointerPosition);
    }

    [Fact]
    public void A_crossing_is_not_offered_while_a_drag_is_running() {
        using var pane = new Half();
        var owner = new Owner();
        var target = new StubTarget();

        pane.Viewport.Input = owner;
        pane.Targets.Add(target);
        pane.Viewport.Update(TimeSpan.FromSeconds(1f / 60f));

        var scale = pane.Viewport.Gizmo.WorldPerPixel(pane.Viewport.Camera, pane.Control.RenderHeight)
            * pane.Viewport.Gizmo.HandleLength;

        var projected = pane.Viewport.Camera.Project(
            new Vector3(scale * 0.6f, 0f, 0f),
            pane.Control.RenderWidth,
            pane.Control.RenderHeight
        );

        pane.Press(projected.X, projected.Y);
        Assert.True(pane.Viewport.Gizmo.IsDragging);

        owner.Pointers.Clear();
        pane.Move(400f, 550f);

        // The pointer leaving does not end a drag — the drag is captured — and an owner told the
        // pointer was gone would put its tools away in the middle of one.
        Assert.DoesNotContain(PointerAction.Exited, owner.Pointers);
        Assert.True(pane.Viewport.Gizmo.IsDragging);

        // ⚠ The control, without which this asserts nothing: a pane that forwarded no crossing at all
        // would pass the line above. Let the drag go and the very same move is delivered.
        pane.Document.Dispatch(
            new PointerEvent { X = 400f, Y = 550f, Action = PointerAction.Released, Button = PointerButton.Primary }
        );

        Assert.False(pane.Viewport.Gizmo.IsDragging);

        pane.Move(400f, 200f);
        owner.Pointers.Clear();
        pane.Move(400f, 550f);

        Assert.Contains(PointerAction.Exited, owner.Pointers);
    }

    [Fact]
    public void Escape_is_still_the_drags_own_way_out() {
        using var pane = new Pane();
        var owner = new Owner { ClaimsKey = true };
        var target = new StubTarget();

        pane.Viewport.Input = owner;
        pane.Targets.Add(target);
        pane.Frame();

        var grab = AlongX(pane);

        pane.Press(PointerButton.Primary, grab);
        pane.Move(grab + new Vector2(120f, 0f));

        Assert.True(pane.Key(InputKey.Escape, KeyAction.Pressed));

        // Cancelled by the pane rather than swallowed by the owner: a drag with no way out is one
        // people finish by dragging back to roughly where they started.
        Assert.False(pane.Viewport.Gizmo.IsDragging);
        Assert.DoesNotContain(InputKey.Escape, owner.Keys);
        Assert.Equal(0f, target.Position.X, 4);
    }
}
