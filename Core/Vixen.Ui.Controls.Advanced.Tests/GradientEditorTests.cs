// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The gradient on its own: two lists of stops, three interpolation spaces.</summary>
public class GradientTests {
    [Fact]
    public void An_empty_gradient_is_transparent_black_rather_than_a_crash() =>
        Assert.Equal(default, new Gradient().Evaluate(0.5f));

    [Fact]
    public void The_ends_hold_outside_the_first_and_last_stop() {
        var gradient = new Gradient(new Color4(1f, 0f, 0f, 1f), new Color4(0f, 0f, 1f, 1f));

        Assert.Equal(new Color4(1f, 0f, 0f, 1f), gradient.Evaluate(-5f));
        Assert.Equal(new Color4(0f, 0f, 1f, 1f), gradient.Evaluate(5f));
    }

    [Fact]
    public void Alpha_is_a_list_of_its_own() {
        var gradient = new Gradient(new Color4(1f, 0f, 0f, 1f), new Color4(1f, 0f, 0f, 1f));

        gradient.AddAlphaStop(0.5f, 0f);

        // ⚠ The whole reason the two lists are separate: fading out in the middle took one stop
        // rather than a copy of every colour stop with an alpha attached.
        Assert.Equal(2, gradient.ColorStops.Count);
        Assert.Equal(0f, gradient.Evaluate(0.5f).A, 3);
        Assert.Equal(1f, gradient.Evaluate(0.5f).R, 3);
    }

    [Fact]
    public void Stops_stay_in_order_however_they_arrive() {
        var gradient = new Gradient();

        gradient.AddColorStop(0.9f, Color4.White);
        gradient.AddColorStop(0.1f, Color4.Black);
        gradient.AddColorStop(0.5f, Color4.Red);

        Assert.Equal([0.1f, 0.5f, 0.9f], gradient.ColorStops.Select(static stop => stop.Position));
    }

    [Fact]
    public void The_last_stop_cannot_be_deleted() {
        var gradient = new Gradient();
        var only = gradient.AddColorStop(0.5f, Color4.Red);

        Assert.NotNull(only);

        // ⚠ Otherwise a user deletes their way to a black bar with no way back.
        Assert.False(gradient.Remove(only));
        Assert.Single(gradient.ColorStops);
    }

    [Fact]
    public void A_gradient_will_not_take_more_stops_than_it_can_carry() {
        var gradient = new Gradient();

        for (var i = 0; i < Gradient.MaximumStops; i++) {
            Assert.NotNull(gradient.AddColorStop(i / 10f, Color4.White));
        }

        Assert.Null(gradient.AddColorStop(0.95f, Color4.White));
    }

    [Fact]
    public void The_three_spaces_give_three_different_midpoints() {
        var from = new Color4(0f, 0f, 1f, 1f);
        var to = new Color4(1f, 1f, 0f, 1f);

        var srgb = new Gradient(from, to) { Interpolation = GradientInterpolation.Srgb }.Evaluate(0.5f);
        var linear = new Gradient(from, to) { Interpolation = GradientInterpolation.Linear }.Evaluate(0.5f);
        var oklab = new Gradient(from, to) { Interpolation = GradientInterpolation.Oklab }.Evaluate(0.5f);

        // ⚠ They disagree visibly, which is why the choice is recorded rather than assumed. Linear
        // light is the brightest of the three at the midpoint of a blue-to-yellow fade.
        Assert.NotEqual(srgb, linear);
        Assert.NotEqual(srgb, oklab);

        Assert.True(linear.R > srgb.R);
    }

    [Fact]
    public void Alpha_is_mixed_straight_whatever_the_colour_space_is() {
        var gradient = new Gradient(new Color4(0f, 0f, 0f, 0f), new Color4(1f, 1f, 1f, 1f)) {
            Interpolation = GradientInterpolation.Oklab
        };

        // Opacity is coverage rather than light, and a perceptual curve on it makes a linear fade
        // look as though it pauses in the middle.
        Assert.Equal(0.25f, gradient.Evaluate(0.25f).A, 3);
        Assert.Equal(0.5f, gradient.Evaluate(0.5f).A, 3);
    }

    [Fact]
    public void Changing_the_space_is_announced() {
        var gradient = new Gradient(Color4.Black, Color4.White);
        var changes = 0;

        gradient.Changed += _ => changes++;

        gradient.Interpolation = GradientInterpolation.Oklab;
        Assert.Equal(1, changes);

        gradient.Interpolation = GradientInterpolation.Oklab;
        Assert.Equal(1, changes);
    }
}

/// <summary>The control: two rails, dragging, adding, deleting and the picker beside them.</summary>
public class GradientEditorTests {
    static GradientEditor Editor(AdvancedFixture fixture, Gradient? gradient = null) {
        var editor = fixture.Add<GradientEditor>();

        if (gradient is not null) {
            editor.Gradient = gradient;
        }

        fixture.Update();
        fixture.Document.Focus(editor);

        return editor;
    }

    [Fact]
    public void Clicking_a_colour_marker_selects_it_and_shows_the_picker() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var stop = editor.Gradient.ColorStops[1];
        var x = editor.ToScreen(stop.Position);
        var y = AdvancedFixture.Centre(editor.ColorRail).Y;

        fixture.Press(x - 2f, y);
        fixture.Release(x - 2f, y);

        Assert.Same(stop, editor.SelectedColorStop);
        Assert.False(editor.Picker.HasClass("hidden"));
        Assert.Equal(stop.Color, editor.Picker.Value);
    }

    [Fact]
    public void The_picker_writes_the_selected_stop() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var stop = editor.Gradient.ColorStops[0];
        editor.Select(stop);

        editor.Picker.Value = new Color4(0.2f, 0.6f, 0.9f, 1f);

        Assert.Equal(0.2f, stop.Color.R, 3);
        Assert.Equal(0.9f, stop.Color.B, 3);
    }

    [Fact]
    public void Selecting_a_stop_does_not_walk_its_colour() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var stop = editor.Gradient.ColorStops[0];
        stop.Color = new Color4(0.31f, 0.62f, 0.93f, 1f);

        // ⚠ Writing the picker raises the picker's own change, which writes the stop back. Without
        // the guard a rounding difference between the two moves the colour a little every click.
        for (var i = 0; i < 5; i++) {
            editor.Select(stop);
            editor.Select((GradientColorStop?) null);
        }

        Assert.Equal(0.31f, stop.Color.R, 4);
        Assert.Equal(0.62f, stop.Color.G, 4);
        Assert.Equal(0.93f, stop.Color.B, 4);
    }

    [Fact]
    public void Dragging_a_marker_moves_its_stop() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var stop = editor.Gradient.ColorStops[0];
        var y = AdvancedFixture.Centre(editor.ColorRail).Y;

        fixture.Press(editor.ToScreen(stop.Position), y);
        fixture.Move(editor.ToScreen(0.4f), y);
        fixture.Release(editor.ToScreen(0.4f), y);

        Assert.Equal(0.4f, stop.Position, 2);
    }

    [Fact]
    public void A_stop_dragged_past_its_neighbour_changes_places_with_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        editor.Gradient.AddColorStop(0.5f, new Color4(0f, 1f, 0f, 1f));
        fixture.Update();

        var first = editor.Gradient.ColorStops[0];
        var y = AdvancedFixture.Centre(editor.ColorRail).Y;

        fixture.Press(editor.ToScreen(first.Position) + 1f, y);
        fixture.Move(editor.ToScreen(0.8f), y);
        fixture.Release(editor.ToScreen(0.8f), y);

        Assert.Same(first, editor.Gradient.ColorStops[1]);
        Assert.Equal(0.5f, editor.Gradient.ColorStops[0].Position, 3);
    }

    [Fact]
    public void A_double_click_on_the_rail_adds_a_stop_of_the_colour_that_was_there() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, new Gradient(new Color4(1f, 0f, 0f, 1f), new Color4(0f, 0f, 1f, 1f)));

        var x = editor.ToScreen(0.5f);
        var y = AdvancedFixture.Centre(editor.ColorRail).Y;

        var expected = editor.Gradient.Evaluate(0.5f);

        fixture.Press(x, y);
        fixture.Release(x, y);
        fixture.Press(x, y);
        fixture.Release(x, y);

        Assert.Equal(3, editor.Gradient.ColorStops.Count);
        Assert.Same(editor.Gradient.ColorStops[1], editor.SelectedColorStop);

        // Added where it was clicked and the colour it already was, so the picture does not jump.
        Assert.Equal(expected.R, editor.Gradient.ColorStops[1].Color.R, 3);
    }

    [Fact]
    public void A_double_click_on_a_marker_takes_it_away() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        editor.Gradient.AddColorStop(0.5f, new Color4(0f, 1f, 0f, 1f));
        fixture.Update();

        var x = editor.ToScreen(0.5f);
        var y = AdvancedFixture.Centre(editor.ColorRail).Y;

        fixture.Press(x, y);
        fixture.Release(x, y);
        fixture.Press(x, y);
        fixture.Release(x, y);

        Assert.Equal(2, editor.Gradient.ColorStops.Count);
        Assert.Null(editor.SelectedColorStop);
    }

    [Fact]
    public void The_alpha_rail_edits_the_other_list() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var stop = editor.Gradient.AlphaStops[0];
        var y = AdvancedFixture.Centre(editor.AlphaRail).Y;

        fixture.Press(editor.ToScreen(stop.Position) + 1f, y);
        fixture.Release(editor.ToScreen(stop.Position) + 1f, y);

        Assert.Same(stop, editor.SelectedAlphaStop);
        Assert.Null(editor.SelectedColorStop);
        Assert.False(editor.Opacity.HasClass("hidden"));

        editor.Opacity.Value = 0.25f;
        Assert.Equal(0.25f, stop.Alpha, 3);
    }

    [Fact]
    public void Delete_removes_whichever_stop_is_selected() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        editor.Gradient.AddColorStop(0.5f, Color4.Red);
        editor.Select(editor.Gradient.ColorStops[1]);

        fixture.Type(InputKey.Delete);

        Assert.Equal(2, editor.Gradient.ColorStops.Count);
        Assert.Null(editor.SelectedColorStop);
    }

    [Fact]
    public void Choosing_a_space_changes_how_the_gradient_mixes() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, new Gradient(new Color4(0f, 0f, 1f, 1f), new Color4(1f, 1f, 0f, 1f)));

        var before = editor.Gradient.Evaluate(0.5f);

        editor.Space.Value = nameof(GradientInterpolation.Oklab);

        Assert.Equal(GradientInterpolation.Oklab, editor.Gradient.Interpolation);
        Assert.NotEqual(before, editor.Gradient.Evaluate(0.5f));
    }

    [Fact]
    public void Every_edit_is_announced_once() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var changes = 0;
        editor.GradientChanged += _ => changes++;

        editor.Gradient.AddColorStop(0.5f, Color4.Red);
        Assert.Equal(1, changes);

        editor.Gradient.Move(editor.Gradient.ColorStops[1], 0.6f);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Changing_gradient_drops_the_selection_with_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        editor.Select(editor.Gradient.ColorStops[0]);
        Assert.NotNull(editor.SelectedColorStop);

        editor.Gradient = new Gradient(Color4.Black, Color4.White);

        // Otherwise the picker edits a stop belonging to a gradient nobody is looking at.
        Assert.Null(editor.SelectedColorStop);
    }
}
