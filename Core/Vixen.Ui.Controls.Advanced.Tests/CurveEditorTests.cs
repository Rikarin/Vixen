// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Curves;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The curve on its own: evaluation, tangent modes and what happens outside the keys.</summary>
public class AnimationCurveTests {
    [Fact]
    public void An_empty_curve_evaluates_to_nothing_rather_than_throwing() {
        Assert.Equal(0f, new AnimationCurve().Evaluate(0.5f));
    }

    [Fact]
    public void A_linear_curve_is_a_straight_line() {
        var curve = AnimationCurve.Linear();

        Assert.Equal(0f, curve.Evaluate(0f), 4);
        Assert.Equal(0.25f, curve.Evaluate(0.25f), 4);
        Assert.Equal(0.5f, curve.Evaluate(0.5f), 4);
        Assert.Equal(1f, curve.Evaluate(1f), 4);
    }

    [Fact]
    public void Outside_the_keys_the_value_holds() {
        var curve = AnimationCurve.Linear();

        // ⚠ Not extrapolated. A cubic run past its last key reaches infinity within a second, and an
        // animation sampled one frame past its end would send whatever it drives into the next
        // county.
        Assert.Equal(0f, curve.Evaluate(-100f));
        Assert.Equal(1f, curve.Evaluate(100f));
    }

    [Fact]
    public void A_constant_key_holds_until_the_next_one() {
        var curve = AnimationCurve.Step();

        Assert.Equal(0f, curve.Evaluate(0f));
        Assert.Equal(0f, curve.Evaluate(0.99f));
        Assert.Equal(1f, curve.Evaluate(1f));
    }

    [Fact]
    public void An_ease_in_out_starts_and_ends_flat() {
        var curve = AnimationCurve.EaseInOut();

        // Flat at both ends, and it passes through the middle at the middle.
        Assert.Equal(0.5f, curve.Evaluate(0.5f), 3);
        Assert.True(curve.Evaluate(0.1f) < 0.1f);
        Assert.True(curve.Evaluate(0.9f) > 0.9f);
    }

    [Fact]
    public void An_automatic_key_at_the_top_of_a_hump_is_flat() {
        var curve = new AnimationCurve(
            new CurveKey(0f, 0f),
            new CurveKey(1f, 1f),
            new CurveKey(2f, 0f)
        );

        // ⚠ Averaging the two chords rather than taking the one to the far neighbour is what makes
        // this true — and what stops a smooth curve overshooting past every local extreme.
        Assert.True(curve.Evaluate(1f) >= curve.Evaluate(0.9f));
        Assert.True(curve.Evaluate(1f) >= curve.Evaluate(1.1f));
        Assert.Equal(1f, curve.Evaluate(1f), 4);
    }

    [Fact]
    public void Keys_stay_in_time_order_however_they_arrive() {
        var curve = new AnimationCurve(new CurveKey(2f, 0f), new CurveKey(0f, 1f), new CurveKey(1f, 2f));

        Assert.Equal([0f, 1f, 2f], curve.Keys.Select(static key => key.Time));
    }

    [Fact]
    public void A_key_dragged_past_its_neighbour_changes_places_with_it() {
        var curve = new AnimationCurve(new CurveKey(0f, 0f), new CurveKey(1f, 1f));
        var first = curve.Keys[0];

        curve.Move(first, 2f, 0f);

        // ⚠ Rather than being clamped between its neighbours, which is what makes a curve editor
        // feel stuck — and reordering is exactly what dragging one key over another means.
        Assert.Same(first, curve.Keys[1]);
    }

    [Fact]
    public void Every_edit_is_announced() {
        var curve = new AnimationCurve();
        var changes = 0;

        curve.Changed += _ => changes++;

        var key = curve.Add(0f, 0f);
        curve.Move(key, 1f, 1f);
        curve.Remove(key);

        Assert.Equal(3, changes);
    }
}

/// <summary>The control: picking keys, dragging them, aiming handles and framing.</summary>
public class CurveEditorTests {
    static CurveEditor Editor(AdvancedFixture fixture, AnimationCurve? curve = null) {
        var editor = fixture.Add<CurveEditor>();

        if (curve is not null) {
            editor.Curve = curve;
        }

        fixture.Update();
        fixture.Document.Focus(editor);

        return editor;
    }

    [Fact]
    public void The_value_axis_points_up() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var low = editor.ToScreen(0f, 0f);
        var high = editor.ToScreen(0f, 1f);

        // ⚠ The one place in the interface where the mathematical convention wins. A graph with its
        // value axis upside down is unreadable.
        Assert.True(high.Y < low.Y);
    }

    [Fact]
    public void Screen_and_curve_coordinates_are_inverses() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var point = editor.ToScreen(0.3f, 0.7f);
        var back = editor.ToCurve(point.X, point.Y);

        Assert.Equal(0.3f, back.X, 3);
        Assert.Equal(0.7f, back.Y, 3);
    }

    [Fact]
    public void Clicking_a_key_selects_it_and_clicking_the_background_clears_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, AnimationCurve.Linear());

        var point = editor.ToScreen(0f, 0f);

        fixture.Press(point.X, point.Y);
        fixture.Release(point.X, point.Y);

        Assert.Same(editor.Curve.Keys[0], Assert.Single(editor.Selection));
        Assert.Same(editor.Curve.Keys[0], editor.Active);

        var empty = editor.ToScreen(0.5f, -0.15f);

        fixture.Press(empty.X, empty.Y);
        fixture.Release(empty.X, empty.Y);

        Assert.Empty(editor.Selection);
    }

    [Fact]
    public void Dragging_a_key_moves_it_in_time_and_value() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, AnimationCurve.Linear());

        var key = editor.Curve.Keys[1];
        var from = editor.ToScreen(key.Time, key.Value);
        var to = editor.ToScreen(0.8f, 0.4f);

        fixture.Press(from.X, from.Y);
        fixture.Move(to.X, to.Y);
        fixture.Release(to.X, to.Y);

        Assert.Equal(0.8f, key.Time, 2);
        Assert.Equal(0.4f, key.Value, 2);
    }

    [Fact]
    public void A_dragged_key_can_be_snapped_to_the_grid() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, AnimationCurve.Linear());

        editor.SnapToGrid = true;
        editor.TimeStep = 0.25f;
        editor.ValueStep = 0.5f;

        var key = editor.Curve.Keys[1];
        var from = editor.ToScreen(key.Time, key.Value);
        var to = editor.ToScreen(0.68f, 0.44f);

        fixture.Press(from.X, from.Y);
        fixture.Move(to.X, to.Y);
        fixture.Release(to.X, to.Y);

        Assert.Equal(0.75f, key.Time, 3);
        Assert.Equal(0.5f, key.Value, 3);
    }

    [Fact]
    public void Dragging_one_of_several_selected_keys_moves_all_of_them() {
        using var fixture = new AdvancedFixture();

        var curve = new AnimationCurve(new CurveKey(0f, 0f), new CurveKey(0.5f, 0.5f), new CurveKey(1f, 1f));
        var editor = Editor(fixture, curve);

        fixture.Type(InputKey.A, ModifierKeys.Control);
        Assert.Equal(3, editor.Selection.Count);

        var middle = curve.Keys[1];
        var from = editor.ToScreen(middle.Time, middle.Value);
        var to = editor.ToScreen(middle.Time, 0.8f);

        fixture.Press(from.X, from.Y);
        fixture.Move(to.X, to.Y);
        fixture.Release(to.X, to.Y);

        Assert.Equal(0.3f, curve.Keys[0].Value, 2);
        Assert.Equal(0.8f, curve.Keys[1].Value, 2);
        Assert.Equal(1.3f, curve.Keys[2].Value, 2);
    }

    [Fact]
    public void Aiming_a_free_handle_moves_both_sides_and_a_broken_one_does_not() {
        using var fixture = new AdvancedFixture();

        var curve = new AnimationCurve(
            new CurveKey(0f, 0f, TangentMode.Free),
            new CurveKey(1f, 1f, TangentMode.Free)
        );

        var editor = Editor(fixture, curve);
        var key = curve.Keys[0];

        var centre = editor.ToScreen(key.Time, key.Value);

        fixture.Press(centre.X, centre.Y);
        fixture.Release(centre.X, centre.Y);

        Assert.Same(key, editor.Active);

        var handle = editor.HandlePoint(key, outgoing: true);

        fixture.Press(handle.X, handle.Y);
        fixture.Move(handle.X, handle.Y - 40f);
        fixture.Release(handle.X, handle.Y - 40f);

        Assert.True(key.OutTangent > 0.2f, $"out tangent is {key.OutTangent}");
        Assert.Equal(key.OutTangent, key.InTangent, 4);

        key.Mode = TangentMode.Broken;

        var incoming = key.InTangent;
        var again = editor.HandlePoint(key, outgoing: true);

        fixture.Press(again.X, again.Y);
        fixture.Move(again.X, again.Y + 60f);
        fixture.Release(again.X, again.Y + 60f);

        // ⚠ The whole difference between the two modes, and it is about what a drag means rather
        // than about what a curve is — which is why it lives in the editor and not in the model.
        Assert.Equal(incoming, key.InTangent, 4);
        Assert.NotEqual(incoming, key.OutTangent, 4);
    }

    [Fact]
    public void A_double_click_on_nothing_adds_a_key_and_on_a_key_removes_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, AnimationCurve.Linear());

        var empty = editor.ToScreen(0.5f, 0.2f);

        fixture.Press(empty.X, empty.Y);
        fixture.Release(empty.X, empty.Y);
        fixture.Press(empty.X, empty.Y);
        fixture.Release(empty.X, empty.Y);

        Assert.Equal(3, editor.Curve.Keys.Count);
        Assert.Equal(0.5f, editor.Curve.Keys[1].Time, 2);

        // ⚠ Otherwise the next two presses are taps three and four rather than one and two.
        fixture.Rest();

        var added = editor.ToScreen(0.5f, 0.2f);

        fixture.Press(added.X, added.Y);
        fixture.Release(added.X, added.Y);
        fixture.Press(added.X, added.Y);
        fixture.Release(added.X, added.Y);

        Assert.Equal(2, editor.Curve.Keys.Count);
    }

    [Fact]
    public void Delete_removes_the_selection() {
        using var fixture = new AdvancedFixture();

        var curve = new AnimationCurve(new CurveKey(0f, 0f), new CurveKey(0.5f, 1f), new CurveKey(1f, 0f));
        var editor = Editor(fixture, curve);

        var middle = editor.ToScreen(0.5f, 1f);

        fixture.Press(middle.X, middle.Y);
        fixture.Release(middle.X, middle.Y);

        fixture.Type(InputKey.Delete);

        Assert.Equal(2, curve.Keys.Count);
        Assert.Empty(editor.Selection);
        Assert.Null(editor.Active);
    }

    [Fact]
    public void Framing_fits_every_key_including_a_flat_curve() {
        using var fixture = new AdvancedFixture();

        var curve = new AnimationCurve(new CurveKey(-4f, 7f), new CurveKey(9f, 7f));
        var editor = Editor(fixture, curve);

        editor.Frame();

        // ⚠ A flat curve has no height, and framing it is exactly what somebody does before they
        // start editing one — so a minimum span keeps the division alive.
        Assert.True(editor.View.Width > 0f && editor.View.Height > 0f);

        foreach (var key in curve.Keys) {
            Assert.True(key.Time > editor.View.X && key.Time < editor.View.Right);
            Assert.True(key.Value > editor.View.Y && key.Value < editor.View.Bottom);
        }
    }

    [Fact]
    public void The_wheel_zooms_about_the_pointer() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, AnimationCurve.Linear());

        var before = editor.ToCurve(300f, 200f);

        fixture.Document.Dispatch(new WheelEvent { X = 300f, Y = 200f, DeltaY = -400f, Timestamp = TimeSpan.Zero });
        fixture.Update();

        var after = editor.ToCurve(300f, 200f);

        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.True(editor.View.Width < 1.2f);
    }

    [Fact]
    public void A_preset_is_copied_into_the_curve_the_caller_is_holding() {
        using var fixture = new AdvancedFixture();

        var curve = AnimationCurve.Linear();
        var editor = Editor(fixture, curve);

        editor.Apply(AnimationCurve.Step());

        // ⚠ Everything bound to a curve holds the object, so replacing it would leave a material, a
        // particle system and an inspector all pointing at the previous one.
        Assert.Same(curve, editor.Curve);
        Assert.Equal(TangentMode.Constant, curve.Keys[0].Mode);
        Assert.Equal(0f, curve.Evaluate(0.9f));
    }

    [Fact]
    public void The_tangent_mode_of_the_selection_can_be_set_at_once() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, AnimationCurve.Linear());

        fixture.Type(InputKey.A, ModifierKeys.Control);
        editor.SetTangentMode(TangentMode.Constant);

        Assert.All(editor.Curve.Keys, static key => Assert.Equal(TangentMode.Constant, key.Mode));
    }

    [Fact]
    public void A_secondary_drag_pans_the_graph() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, AnimationCurve.Linear());

        var before = editor.View;

        fixture.Press(300f, 200f, PointerButton.Secondary);
        fixture.Move(200f, 200f);
        fixture.Release(200f, 200f, PointerButton.Secondary);

        Assert.True(editor.View.X > before.X);
        Assert.Equal(before.Width, editor.View.Width, 4);
    }
}
