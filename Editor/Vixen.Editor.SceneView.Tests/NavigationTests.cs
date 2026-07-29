// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Which button does what, and what the two orbit pivots mean.</summary>
/// <remarks>
///     <see cref="CameraTests" /> proves the arithmetic of a turn; this proves that a drag with a
///     given button and a given modifier reaches the right one of them.
/// </remarks>
public class NavigationTests {
    [Theory]
    [InlineData(PointerButton.Middle, ModifierKeys.None, NavigationAction.Orbit)]
    [InlineData(PointerButton.Middle, ModifierKeys.Shift, NavigationAction.Pan)]
    [InlineData(PointerButton.Middle, ModifierKeys.Control, NavigationAction.Dolly)]
    public void The_middle_button_is_blender_s_three_gestures(
        PointerButton button,
        ModifierKeys modifiers,
        NavigationAction expected
    ) {
        // ⚠ The middle button used to pan whatever was held with it — two branches written for one
        // answer — so there was no orbit on it at all and somebody arriving from Blender found the
        // button that turns the view slides it instead.
        Assert.Equal(expected, SceneViewport.Interpret(button, modifiers));
    }

    [Theory]
    [InlineData(PointerButton.Primary, NavigationAction.Orbit)]
    [InlineData(PointerButton.Middle, NavigationAction.Pan)]
    [InlineData(PointerButton.Secondary, NavigationAction.Dolly)]
    public void Alt_gives_maya_s_three_on_the_same_three_buttons(PointerButton button, NavigationAction expected) {
        // They do not collide with Blender's because Blender's use no modifier where these use Alt.
        Assert.Equal(expected, SceneViewport.Interpret(button, ModifierKeys.Alt));
    }

    [Fact]
    public void The_left_button_drives_the_gizmo_unless_alt_takes_it() {
        Assert.Equal(NavigationAction.Manipulate, SceneViewport.Interpret(PointerButton.Primary, ModifierKeys.None));

        // Checked before the plain button, so holding Alt takes the left button away from the gizmo
        // rather than grabbing a handle and swinging the camera at the same time.
        Assert.Equal(NavigationAction.Orbit, SceneViewport.Interpret(PointerButton.Primary, ModifierKeys.Alt));
    }

    [Fact]
    public void Control_and_shift_together_resolve_to_one_action() {
        // Both held is one gesture and it has to mean one thing. Either answer would do; what must
        // not happen is a branch that matches neither and leaves the drag doing nothing.
        Assert.Equal(
            NavigationAction.Dolly,
            SceneViewport.Interpret(PointerButton.Middle, ModifierKeys.Control | ModifierKeys.Shift)
        );
    }

    [Fact]
    public void The_right_button_still_orbits_and_still_flies() {
        // Flight is held on the right button and flight *is* orbiting from where you are, so the two
        // are one gesture rather than two modes — see SceneViewport's own remarks.
        Assert.Equal(NavigationAction.Orbit, SceneViewport.Interpret(PointerButton.Secondary, ModifierKeys.None));
        Assert.True(SceneViewport.Flies(PointerButton.Secondary));
        Assert.False(SceneViewport.Flies(PointerButton.Middle));
    }

    [Fact]
    public void Orbiting_around_the_view_leaves_the_pivot_where_it_is() {
        using var pane = new Pane();

        pane.Targets.Add(new StubTarget { Position = new Vector3(6f, 0f, 0f) });
        pane.Frame();

        var pivot = pane.Camera.Pivot;
        pane.Viewport.Orbit(90f, 30f);

        // The default, and the reason it is the default: the thing in the middle of the pane stays
        // in the middle of the pane, whatever happens to be selected.
        Assert.True(Vector3.NearEqual(pane.Camera.Pivot, pivot, 1e-4f));
    }

    [Fact]
    public void Orbiting_around_the_selection_swings_about_what_is_selected() {
        using var pane = new Pane();
        var anchor = new Vector3(6f, 0f, 0f);

        pane.Targets.Add(new StubTarget { Position = anchor });
        pane.Frame();

        pane.Viewport.OrbitAround = OrbitPivot.Selection;

        var before = (pane.Camera.Position - anchor).Length();
        pane.Viewport.Orbit(90f, 30f);

        // Blender's "orbit around selection". The pivot has to move, because the camera can only
        // orbit its own pivot — what stays fixed is the distance to the thing being worked on.
        Assert.Equal(before, (pane.Camera.Position - anchor).Length(), 3);
        Assert.False(Vector3.NearEqual(pane.Camera.Pivot, Vector3.Zero, 1e-3f));
    }

    [Fact]
    public void Orbiting_around_the_selection_with_nothing_selected_falls_back_to_the_view() {
        using var pane = new Pane();

        pane.Frame();
        pane.Viewport.OrbitAround = OrbitPivot.Selection;

        var pivot = pane.Camera.Pivot;
        pane.Viewport.Orbit(90f, 30f);

        // An empty selection has no anchor, and a preference that stopped the view turning until
        // something was clicked would read as the middle button having broken.
        Assert.Null(pane.Viewport.OrbitAnchor);
        Assert.True(Vector3.NearEqual(pane.Camera.Pivot, pivot, 1e-4f));
    }

    [Fact]
    public void The_wheel_zooms_at_the_middle_until_it_is_told_to_zoom_at_the_pointer() {
        using var pane = new Pane();
        var corner = new Vector2(120f, 90f);

        pane.Viewport.Wheel(-pane.Viewport.WheelNotch, corner);
        Assert.True(Vector3.NearEqual(pane.Camera.Pivot, Vector3.Zero, 1e-4f));

        pane.Viewport.ZoomToCursor = true;
        pane.Viewport.Wheel(-pane.Viewport.WheelNotch, corner);

        // Off, the wheel is exactly what it was. On, the view leans towards the pointer, which is the
        // whole of what makes approaching something off-centre one gesture instead of four.
        Assert.False(Vector3.NearEqual(pane.Camera.Pivot, Vector3.Zero, 1e-3f));
    }
}
