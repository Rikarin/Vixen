// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 24's P0 precision group: typing an exact transform, measuring, and scale references.</summary>
public class PrecisionTests {
    const int Width = 1000;
    const int Height = 800;

    static EditorCamera Camera() => new() { Pivot = Vector3.Zero, Distance = 10f };

    // ── Numeric entry ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_is_typed_until_something_is_typed() {
        var entry = new NumericEntry();

        Assert.False(entry.IsActive);
        Assert.Null(entry.Typed);

        // ⚠ An axis letter on its own is not taken. X during a drag is a key some other tool may want,
        // and it only becomes a constraint once the user has said, by typing a digit, that they mean
        // an exact transform.
        Assert.False(entry.Key(InputKey.X));
        Assert.False(entry.IsActive);
    }

    [Fact]
    public void A_digit_starts_it_and_the_text_is_what_was_typed() {
        var entry = new NumericEntry();

        Assert.True(entry.Key(InputKey.Number1));
        Assert.True(entry.Key(InputKey.Period));
        Assert.True(entry.Key(InputKey.Number5));

        Assert.True(entry.IsActive);
        Assert.Equal("1.5|", entry.Text);
        Assert.Equal(1.5f, entry.Typed!.Value.Values.X, 4);
    }

    [Fact]
    public void The_number_row_and_the_keypad_agree_about_zero() {
        var row = new NumericEntry();
        var pad = new NumericEntry();

        // ⚠ `Number0` follows `Number9` and `Keypad0` follows `Keypad9`, which is the order the keys
        // are in on a keyboard and not the order the digits are. A range test from zero maps every
        // digit one place out, which is a numeric entry that types the wrong number.
        row.Key(InputKey.Number4);
        row.Key(InputKey.Number0);

        pad.Key(InputKey.Keypad4);
        pad.Key(InputKey.Keypad0);

        Assert.Equal("40|", row.Text);
        Assert.Equal(40f, row.Typed!.Value.Values.X, 4);
        Assert.Equal(40f, pad.Typed!.Value.Values.X, 4);
    }

    [Fact]
    public void An_axis_letter_constrains_and_pressing_it_again_releases() {
        var entry = new NumericEntry();

        entry.Key(InputKey.Number5);

        Assert.True(entry.Key(InputKey.X));
        Assert.Equal(0, entry.Typed!.Value.Axis);
        Assert.Equal("X 5|", entry.Text);

        Assert.True(entry.Key(InputKey.X));
        Assert.Equal(-1, entry.Typed!.Value.Axis);

        Assert.True(entry.Key(InputKey.Z));
        Assert.Equal(2, entry.Typed!.Value.Axis);
    }

    [Fact]
    public void Minus_is_a_toggle_wherever_you_are_in_the_number() {
        var entry = new NumericEntry();

        entry.Key(InputKey.Number2);
        entry.Key(InputKey.Minus);
        entry.Key(InputKey.Number5);

        // A minus in the middle of a number is not a number, and Blender's flips the sign however far
        // in you are.
        Assert.Equal(-25f, entry.Typed!.Value.Values.X, 4);

        entry.Key(InputKey.Minus);
        Assert.Equal(25f, entry.Typed!.Value.Values.X, 4);
    }

    [Fact]
    public void Tab_moves_between_components_and_shift_tab_goes_back() {
        var entry = new NumericEntry();

        entry.Key(InputKey.Number1);
        entry.Key(InputKey.Tab);
        entry.Key(InputKey.Number2);
        entry.Key(InputKey.Tab);
        entry.Key(InputKey.Number3);

        var typed = entry.Typed!.Value;

        Assert.Equal(3, typed.Count);
        Assert.True(Vector3.NearEqual(typed.Values, new Vector3(1f, 2f, 3f), 1e-4f));

        entry.Key(InputKey.Tab, ModifierKeys.Shift);
        Assert.Equal(1, entry.Component);
    }

    [Fact]
    public void Backspacing_the_last_character_out_backs_out_of_the_entry() {
        var entry = new NumericEntry();

        entry.Key(InputKey.Number7);
        Assert.True(entry.IsActive);

        entry.Key(InputKey.Backspace);

        // ⚠ Not a frozen zero. Backing out has to put the drag back on the pointer, or a mistyped key
        // leaves the object stuck at the origin with no way back except cancelling the whole drag.
        Assert.False(entry.IsActive);
        Assert.Null(entry.Typed);
    }

    [Fact]
    public void A_chord_belongs_to_somebody_else() {
        var entry = new NumericEntry();

        entry.Key(InputKey.Number1);

        // Ctrl+Z during a drag is undo and Ctrl+S is save. Taking them because they contain a letter
        // would make a drag a place where shortcuts stop working.
        Assert.False(entry.Key(InputKey.Z, ModifierKeys.Control));
        Assert.Equal(-1, entry.Typed!.Value.Axis);
    }

    [Fact]
    public void A_typed_distance_is_what_the_drag_applies() {
        var camera = Camera();
        var target = new StubTarget { Position = Vector3.Zero };
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        // `G X 5 ⏎` — the drag stops following the pointer and becomes exactly five metres along X.
        gizmo.Typed = new TypedTransform(new Vector3(5f, 0f, 0f), 1, 0);
        gizmo.Drag(camera.PickingRay(new Vector2(900f, 700f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(target.Position, new Vector3(5f, 0f, 0f), 1e-3f));
    }

    [Fact]
    public void A_typed_axis_overrides_the_handle_that_was_grabbed() {
        var camera = Camera();
        var target = new StubTarget { Position = Vector3.Zero };
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.AxisX, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        // ⚠ Grabbed X, typed Z. Projecting "three along Z" onto the arm the user happened to be
        // holding would move the object nowhere and look like the typing was ignored — and pressing a
        // letter is a more specific statement than which arrow was grabbed.
        gizmo.Typed = new TypedTransform(new Vector3(3f, 0f, 0f), 1, 2);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(target.Position, new Vector3(0f, 0f, 3f), 1e-3f));
    }

    [Fact]
    public void A_typed_number_beats_a_snap() {
        var camera = Camera();
        var target = new StubTarget { Position = Vector3.Zero };
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Snap.SnapPosition = true;
        gizmo.Snap.GridStep = 1f;

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        gizmo.SnapTo = new SnapHit(new Vector3(9f, 9f, 9f), null, SnapElements.Vertex);
        gizmo.Typed = new TypedTransform(new Vector3(2.5f, 0f, 0f), 1, 0);

        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        // ⚠ Neither rounded to the grid nor pulled onto the corner. A number somebody typed is the
        // most specific thing anybody has said about where this lands.
        Assert.True(Vector3.NearEqual(target.Position, new Vector3(2.5f, 0f, 0f), 1e-3f));
    }

    [Fact]
    public void A_typed_rotation_is_in_degrees_and_a_typed_scale_is_a_factor() {
        var camera = Camera();

        var turning = new StubTarget { Position = new Vector3(1f, 0f, 0f) };
        var rotate = new TransformGizmo { Mode = GizmoMode.Rotate };

        rotate.Attach([turning]);
        rotate.Begin(GizmoHandle.AxisY, camera.PickingRay(new Vector2(560f, 400f), Width, Height), camera);

        rotate.Typed = new TypedTransform(new Vector3(90f, 0f, 0f), 1, -1);
        rotate.Drag(camera.PickingRay(new Vector2(570f, 400f), Width, Height), camera);

        Assert.Equal(GizmoMode.Rotate, rotate.Dragged.Kind);
        Assert.Equal(90f, rotate.Dragged.Scalar, 2);

        var sized = new StubTarget { Scale = Vector3.One };
        var scale = new TransformGizmo { Mode = GizmoMode.Scale };

        scale.Attach([sized]);

        // ⚠ Away from the middle of the pane. A scale is measured as a ratio of distances from the
        // gizmo's origin, so a grab exactly on it has no distance to be a ratio of and the drag
        // refuses — which is the arithmetic being honest rather than a bug.
        scale.Begin(GizmoHandle.Uniform, camera.PickingRay(new Vector2(560f, 400f), Width, Height), camera);

        scale.Typed = new TypedTransform(new Vector3(3f, 0f, 0f), 1, -1);
        scale.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        Assert.True(Vector3.NearEqual(sized.Scale, new Vector3(3f), 1e-3f));
    }

    [Fact]
    public void The_drag_says_how_far_it_has_gone_for_a_readout_to_show() {
        var camera = Camera();
        var target = new StubTarget();
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };

        gizmo.Attach([target]);
        gizmo.Begin(GizmoHandle.Screen, camera.PickingRay(new Vector2(500f, 400f), Width, Height), camera);

        gizmo.Typed = new TypedTransform(new Vector3(2f, 0f, 0f), 1, 0);
        gizmo.Drag(camera.PickingRay(new Vector2(520f, 400f), Width, Height), camera);

        // Doc 24: "the extent in metres, on screen, while resizing. Both reference editors make you
        // read a details panel."
        Assert.Equal(GizmoMode.Translate, gizmo.Dragged.Kind);
        Assert.Equal(2f, gizmo.Dragged.Scalar, 3);

        gizmo.End();
        Assert.Equal(GizmoMode.None, gizmo.Dragged.Kind);
    }

    [Fact]
    public void Typing_reaches_the_gizmo_through_the_pane_and_only_during_a_drag() {
        using var pane = new Pane();
        var target = new StubTarget();

        pane.Targets.Add(target);
        pane.Frame();

        // Not dragging: the digit is the shell's — a view bookmark — and the pane must not eat it.
        Assert.False(pane.Key(InputKey.Number5, KeyAction.Pressed));
        Assert.False(pane.Viewport.Typing.IsActive);

        var grab = pane.Screen(Vector3.Zero);

        pane.Press(Vixen.Ui.PointerButton.Primary, grab);
        Assert.True(pane.Viewport.Gizmo.IsDragging);

        Assert.True(pane.Key(InputKey.Number2, KeyAction.Pressed));
        Assert.True(pane.Key(InputKey.X, KeyAction.Pressed));

        Assert.True(pane.Viewport.Typing.IsActive);
        Assert.True(Vector3.NearEqual(target.Position, new Vector3(2f, 0f, 0f), 1e-3f));

        // Escape abandons the typing with the drag it belonged to; a half-typed number left behind is
        // one the next drag inherits.
        pane.Key(InputKey.Escape, KeyAction.Pressed);

        Assert.False(pane.Viewport.Typing.IsActive);
        Assert.True(Vector3.NearEqual(target.Position, Vector3.Zero, 1e-3f));
    }

    // ── Measuring ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_points_are_a_distance_and_three_are_an_angle() {
        var measure = new SceneMeasure();

        Assert.False(measure.HasMeasurement);
        Assert.Null(measure.Describe());

        measure.Add(Vector3.Zero);
        measure.Add(new Vector3(3f, 4f, 0f));

        Assert.True(measure.HasMeasurement);
        Assert.Equal(5f, measure.Distance, 4);
        Assert.Null(measure.Angle);

        measure.Add(new Vector3(3f, 4f, 0f) + new Vector3(0f, 4f, 0f));

        // The angle at the *middle* point rather than at either end, which is the second question
        // anybody asks of a corner. The legs from it are (−3, −4) and (0, 4), so the cosine is −0.8.
        Assert.NotNull(measure.Angle);
        Assert.Equal(MathUtil.RadiansToDegrees(MathF.Acos(-0.8f)), measure.Angle!.Value, 2);
    }

    [Fact]
    public void A_fourth_point_starts_a_new_measurement() {
        var measure = new SceneMeasure();

        measure.Add(Vector3.Zero);
        measure.Add(Vector3.UnitX);
        measure.Add(Vector3.UnitY);
        measure.Add(new Vector3(7f, 0f, 0f));

        // The gesture after reading a measurement is measuring the next thing, and a tool that had to
        // be cleared first is one people clear by turning it off and on again.
        Assert.Single(measure.Points);
        Assert.Equal(new Vector3(7f, 0f, 0f), measure.Points[0]);
    }

    [Fact]
    public void Measuring_takes_the_click_and_does_not_select_or_drag() {
        using var pane = new Pane();
        var target = new StubTarget();

        pane.Targets.Add(target);
        pane.Frame();

        pane.Viewport.Measure.IsActive = true;

        pane.Press(Vixen.Ui.PointerButton.Primary, pane.Screen(Vector3.Zero));

        // ⚠ A click that also selected would hand the inspector away every time somebody measured a
        // wall, and one that also grabbed a handle would move the thing being measured.
        Assert.False(pane.Viewport.Gizmo.IsDragging);
        Assert.Single(pane.Viewport.Measure.Points);
    }

    // ── Reference volumes ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_person_is_one_point_eight_metres_and_stands_on_the_point() {
        var person = ReferenceVolumes.Person;

        Assert.Equal(1.8f, person.Size.Y, 4);

        // ⚠ Where the box sits relative to the point is a property of the volume: a person stands on
        // the floor, and a placement that centred them would put half of them through it.
        Assert.Equal(0.9f, person.CentreAt(Vector3.Zero).Y, 4);
    }

    [Fact]
    public void The_four_are_findable_by_name_and_nothing_else_is() {
        Assert.Equal(4, ReferenceVolumes.All.Count);
        Assert.Equal("Door", ReferenceVolumes.Find("door")!.Value.Name);
        Assert.Null(ReferenceVolumes.Find("dragon"));
    }

    [Fact]
    public void A_set_holds_what_was_placed_and_gives_it_back() {
        var set = new ReferenceVolumeSet();

        Assert.True(set.IsEmpty);

        set.Add(ReferenceVolumes.Person, Vector3.Zero);
        set.Add(ReferenceVolumes.Door, new Vector3(4f, 0f, 0f));

        Assert.Equal(2, set.Placed.Count);
        Assert.True(set.RemoveLast());
        Assert.Single(set.Placed);

        set.Clear();
        Assert.True(set.IsEmpty);
        Assert.False(set.RemoveLast());
    }
}
