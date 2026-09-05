// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The gradient's stop rails, operated without a pointer — the fourth of #420's six.</summary>
/// <remarks>
///     <para>
///         <b>Keyboard first, role second, in one change.</b> A rail announced before it could be
///         operated would convert "this control is not available to me" into "this control is
///         available and does nothing", which is the failure a screen-reader user cannot diagnose.
///         So every test here asserts a keystroke <i>moved something</i> beside asserting the role —
///         ⚠ and #420's own comment records that the coverage sweep cannot catch that ordering
///         being broken, because its rule is "roleless and focusable is an offender" and says
///         nothing about a role with no keyboard. These tests are what catches it.
///     </para>
///     <para>
///         ⚠ <b>Two axes and two meanings.</b> Left and Right move the selected stop; Up and Down
///         choose a different one. Both are asserted, because a rail that only moved would leave a
///         keyboard user with whichever stop the mouse last touched and no way to reach the others
///         — which is the same "available and does nothing" failure one level in.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class GradientRailKeyboardTests {
    static GradientEditor Editor(AdvancedFixture fixture) {
        var editor = fixture.Add<GradientEditor>();
        fixture.Update();

        return editor;
    }

    [Fact]
    public void The_colour_rail_is_a_named_group_that_says_which_stop_of_how_many() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        Assert.Equal(AccessibleRole.Group, editor.ColorRail.Role);
        Assert.Equal(ControlStrings.GradientEditorColorStops.Text, editor.ColorRail.AccessibleName);
        Assert.Equal(ControlStrings.GradientEditorAlphaStops.Text, editor.AlphaRail.AccessibleName);

        // ⚠ Null rather than a position, and it is what a bridge should hear: nothing is selected,
        // so there is no stop whose place could be announced.
        Assert.Null(editor.ColorRail.AccessibleValue);

        editor.Select(editor.Gradient.ColorStops[1]);
        Assert.Equal("2 of 2 at 1", editor.ColorRail.AccessibleValue);

        // And the other rail keeps quiet while the selection is on this one — the editor holds a
        // single selection across both, so an alpha rail that answered would be announcing a stop
        // nobody chose.
        Assert.Null(editor.AlphaRail.AccessibleValue);
    }

    [Fact]
    public void The_arrows_move_the_selected_stop_and_page_and_the_ends_go_further() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var stop = editor.Gradient.AddColorStop(0.5f, Color4.Red)!;
        editor.Select(stop);

        Assert.True(fixture.Document.Focus(editor.ColorRail));

        fixture.Type(InputKey.Right);
        Assert.Equal(0.51f, stop.Position, 3);

        fixture.Type(InputKey.Left);
        fixture.Type(InputKey.Left);
        Assert.Equal(0.49f, stop.Position, 3);

        fixture.Type(InputKey.PageUp);
        Assert.Equal(0.59f, stop.Position, 3);

        fixture.Type(InputKey.PageDown);
        Assert.Equal(0.49f, stop.Position, 3);

        fixture.Type(InputKey.Home);
        Assert.Equal(0f, stop.Position, 3);

        fixture.Type(InputKey.End);
        Assert.Equal(1f, stop.Position, 3);
    }

    /// <summary>Up and Down choose a stop, which is the half that makes the arrows reach all of them.</summary>
    [Fact]
    public void Up_and_down_walk_the_stops_and_stop_at_the_ends() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        editor.Gradient.AddColorStop(0.5f, Color4.Red);
        editor.Select(editor.Gradient.ColorStops[0]);

        Assert.True(fixture.Document.Focus(editor.ColorRail));
        Assert.Equal(0, editor.ColorRail.SelectedIndex);

        fixture.Type(InputKey.Down);
        Assert.Equal(1, editor.ColorRail.SelectedIndex);

        fixture.Type(InputKey.Down);
        Assert.Equal(2, editor.ColorRail.SelectedIndex);

        // Clamped rather than wrapping: a list that wraps gives a user pressing Down no way to know
        // they have reached the end, which is the one thing the announcement cannot tell them twice.
        fixture.Type(InputKey.Down);
        Assert.Equal(2, editor.ColorRail.SelectedIndex);

        fixture.Type(InputKey.Up);
        fixture.Type(InputKey.Up);
        fixture.Type(InputKey.Up);
        Assert.Equal(0, editor.ColorRail.SelectedIndex);
    }

    /// <summary>The state a keyboard user actually arrives in: focused, with nothing selected.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the whole feature is unreachable by keyboard alone.</b> Tab moves the
    ///     focus and selects nothing, the editor's selection starts null, and every key above needs a
    ///     selection to act on — so a rail that answered nothing here would be operable only after a
    ///     mouse had already chosen a stop, which is precisely the state #420 refuses to give a role
    ///     to.
    /// </remarks>
    [Fact]
    public void A_first_press_on_a_rail_nobody_has_clicked_selects_a_stop_rather_than_doing_nothing() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        Assert.Null(editor.SelectedColorStop);
        Assert.True(fixture.Document.Focus(editor.ColorRail));

        fixture.Type(InputKey.Right);

        Assert.Equal(0, editor.ColorRail.SelectedIndex);
        Assert.NotNull(editor.SelectedColorStop);

        // ⚠ And the press that selected did not also move: a first Right that both chose a stop and
        // shifted it would edit the gradient of anyone who merely tabbed through the control.
        Assert.Equal(0f, editor.Gradient.ColorStops[0].Position, 3);
    }

    /// <summary>A stop arrowed past its neighbour changes places with it, as a dragged one does.</summary>
    /// <remarks>
    ///     ⚠ <b>The reason <c>MoveSelected</c> goes through <c>Gradient.Move</c>.</b> Writing
    ///     <c>Position</c> would leave the list unsorted, and the bar — which walks it in order —
    ///     would draw a gradient that runs backwards between two of its stops.
    /// </remarks>
    [Fact]
    public void A_stop_arrowed_past_its_neighbour_changes_places_with_it_and_stays_selected() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        var moving = editor.Gradient.AddColorStop(0.5f, Color4.Red)!;
        var neighbour = editor.Gradient.AddColorStop(0.51f, Color4.Blue)!;

        editor.Select(moving);
        Assert.True(fixture.Document.Focus(editor.ColorRail));

        Assert.Equal(1, editor.ColorRail.SelectedIndex);

        fixture.Type(InputKey.PageUp);

        Assert.Same(moving, editor.SelectedColorStop);
        Assert.Equal(2, editor.ColorRail.SelectedIndex);
        Assert.Same(neighbour, editor.Gradient.ColorStops[1]);
        Assert.True(moving.Position > neighbour.Position);
    }

    /// <summary>Delete still belongs to the editor, even though the focus is now on a part of it.</summary>
    /// <remarks>
    ///     ⚠ The rail deliberately does not answer Delete: an unhandled key bubbles to the editor,
    ///     which already owns it. A second implementation there could disagree with the first about
    ///     which stop is selected, and this is the assertion that the bubble actually happens.
    /// </remarks>
    [Fact]
    public void Delete_still_reaches_the_editor_from_a_focused_rail() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture);

        editor.Gradient.AddColorStop(0.5f, Color4.Red);
        editor.Select(editor.Gradient.ColorStops[1]);

        Assert.True(fixture.Document.Focus(editor.ColorRail));

        fixture.Type(InputKey.Delete);

        Assert.Equal(2, editor.Gradient.ColorStops.Count);
        Assert.Null(editor.SelectedColorStop);
    }
}
