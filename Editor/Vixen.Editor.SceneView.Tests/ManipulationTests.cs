// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Rendering;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Grabbing a handle with the mouse, which is a different question from what a drag means.</summary>
/// <remarks>
///     <para>
///         <b><see cref="GizmoTests" /> proves the arithmetic and this proves the wiring.</b> Every
///         one of those calls <c>Begin</c> and <c>Drag</c> directly, so all of them passed while the
///         gizmo was unreachable with a mouse: nothing pointed it at the selection except the press
///         that was meant to grab it, so there were no handles to draw and the press that would have
///         made some was the press that hit-tested against an empty gizmo.
///     </para>
///     <para>
///         Driven through <c>UiDocument.Dispatch</c> rather than by calling the viewport's handlers,
///         for the reason <see cref="Pane" /> gives: what is being tested is the routing.
///     </para>
/// </remarks>
public class ManipulationTests {
    /// <summary>A point some way along the x arm, in render pixels.</summary>
    static Vector2 AlongX(Pane pane, float fraction = 0.6f) {
        var scale = pane.Viewport.Gizmo.WorldPerPixel(pane.Camera, pane.Control.RenderHeight)
            * pane.Viewport.Gizmo.HandleLength;

        return pane.Screen(new Vector3(scale * fraction, 0f, 0f));
    }

    static StubTarget Selected(Pane pane) {
        var target = new StubTarget();

        pane.Targets.Add(target);
        pane.Frame();

        return target;
    }

    [Fact]
    public void A_selection_has_handles_before_anything_has_been_clicked() {
        using var pane = new Pane();
        List<LineVertex> into = [];

        Selected(pane);

        // ⚠ The regression. A gizmo attached only by the press that grabs it draws nothing until
        // something has been clicked in the viewport — so a selection made in the hierarchy panel
        // had no handles at all, which is indistinguishable from handles that cannot be dragged.
        Assert.Single(pane.Viewport.Gizmo.Targets);
        Assert.True(GizmoGeometry.Build(pane.Viewport.Gizmo, pane.Camera, pane.Control.RenderHeight, into) > 0);
    }

    [Fact]
    public void Moving_over_an_arm_lights_it_up_and_moving_away_puts_it_out() {
        using var pane = new Pane();
        Selected(pane);

        pane.Move(AlongX(pane));
        Assert.Equal(GizmoHandle.AxisX, pane.Viewport.Gizmo.Hovered);

        // A move with no button carries `PointerButton.None`, so a viewport that only looked at the
        // primary button never asked what was under the pointer at all.
        pane.Move(new Vector2(20f, 20f));
        Assert.Equal(GizmoHandle.None, pane.Viewport.Gizmo.Hovered);
    }

    [Fact]
    public void Pressing_an_arm_and_dragging_moves_what_is_selected() {
        using var pane = new Pane();
        var target = Selected(pane);
        var grab = AlongX(pane);

        pane.Press(PointerButton.Primary, grab);
        Assert.True(pane.Viewport.Gizmo.IsDragging);
        Assert.Equal(GizmoHandle.AxisX, pane.Viewport.Gizmo.Active);

        pane.Move(grab + new Vector2(80f, 25f));

        // Along the arm and along nothing else, which is the arithmetic GizmoTests already covers —
        // asserted here only to say that the pointer stream reached it.
        Assert.True(target.Position.X > 0f);
        Assert.Equal(0f, target.Position.Y, 4);

        pane.Release(PointerButton.Primary, grab + new Vector2(80f, 25f));

        Assert.False(pane.Viewport.Gizmo.IsDragging);
        Assert.True(target.Position.X > 0f);
    }

    [Fact]
    public void A_press_that_missed_every_handle_does_not_start_a_drag() {
        using var pane = new Pane();
        Selected(pane);

        // The corner of the pane, which is where a click means "select what is there" rather than
        // "grab something". A gizmo that started dragging here would move the selection on every
        // click in empty space.
        pane.Press(PointerButton.Primary, new Vector2(20f, 20f));

        Assert.False(pane.Viewport.Gizmo.IsDragging);
    }

    [Fact]
    public void Alt_takes_the_left_button_away_from_the_gizmo() {
        using var pane = new Pane();
        Selected(pane);

        var grab = AlongX(pane);

        pane.Document.Dispatch(
            new PointerEvent {
                X = grab.X,
                Y = grab.Y,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary,
                Modifiers = ModifierKeys.Alt
            }
        );

        // Alt+left orbits — the Maya convention — and a press that also grabbed a handle would move
        // the object the camera was supposed to be swinging around.
        Assert.False(pane.Viewport.Gizmo.IsDragging);
    }

    [Fact]
    public void Escape_abandons_a_drag_and_is_not_passed_on() {
        using var pane = new Pane();
        var target = Selected(pane);
        var grab = AlongX(pane);

        target.Position = new Vector3(0f, 0f, 0f);

        pane.Press(PointerButton.Primary, grab);
        pane.Move(grab + new Vector2(120f, 0f));

        Assert.True(target.Position.X > 0f);

        // ⚠ Consumed, so the shell's own Escape binding cannot also fire. Rolled back here rather
        // than through the command stack, so the viewport is redrawn from the model on the frame the
        // key was pressed — a drag with no way out is one people finish by dragging roughly back.
        Assert.True(pane.Key(InputKey.Escape, KeyAction.Pressed));

        Assert.False(pane.Viewport.Gizmo.IsDragging);
        Assert.Equal(Vector3.Zero, target.Position);
    }

    [Fact]
    public void Escape_with_no_drag_under_way_is_left_for_everything_else() {
        using var pane = new Pane();
        Selected(pane);

        // The key that closes a dialog, clears a search box and cancels a rename. A viewport that
        // ate it unconditionally would be one where none of those work while it has the focus.
        //
        // ⚠ Pressed *and released*, because a press that misses the gizmo now starts a rubber-band
        // and Escape is what abandons one. The release is what ends it — see `SceneViewport.EndSelect`
        // for why every press in empty space begins a band and the release is where a click and a
        // band part company.
        pane.Press(PointerButton.Primary, new Vector2(20f, 20f));
        pane.Release(PointerButton.Primary, new Vector2(20f, 20f));

        Assert.False(pane.Key(InputKey.Escape, KeyAction.Pressed));
    }

    [Fact]
    public void The_selection_can_change_between_frames_while_nothing_is_being_dragged() {
        using var pane = new Pane();
        Selected(pane);

        pane.Targets.Clear();
        pane.Frame();

        // An undo that deleted what was selected, or a script that did. Re-attaching every frame is
        // what makes that a gizmo with nothing in it rather than one still holding a dead entity.
        Assert.Empty(pane.Viewport.Gizmo.Targets);
        Assert.Equal(GizmoHandle.None, pane.Viewport.Gizmo.HitTest(AlongX(pane), pane.Camera, 800, 600));
    }
}
