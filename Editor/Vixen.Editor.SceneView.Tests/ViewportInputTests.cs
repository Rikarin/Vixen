// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
