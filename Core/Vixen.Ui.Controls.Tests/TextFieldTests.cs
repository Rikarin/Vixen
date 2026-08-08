// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Caret motion, selection, editing, and the numeric field's arithmetic.</summary>
public class TextFieldTests {
    [Fact]
    public void Typing_inserts_at_the_caret() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        fixture.Document.Focus(field);

        fixture.TypeText("ab");
        fixture.TypeText("c");

        Assert.Equal("abc", field.Value);
        Assert.Equal(3, field.CaretIndex);
    }

    [Fact]
    public void The_arrows_move_the_caret_and_shift_extends_the_selection() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "abcd";
        field.MoveCaret(4);

        fixture.Document.Focus(field);

        fixture.Type(InputKey.Left);
        Assert.Equal(3, field.CaretIndex);
        Assert.False(field.HasSelection);

        fixture.Type(InputKey.Left, ModifierKeys.Shift);
        Assert.Equal(2, field.CaretIndex);
        Assert.Equal(3, field.SelectionAnchor);
        Assert.Equal("c", field.SelectedText);
    }

    [Fact]
    public void Home_and_end_go_to_the_ends() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "abcd";
        fixture.Document.Focus(field);

        fixture.Type(InputKey.End);
        Assert.Equal(4, field.CaretIndex);

        fixture.Type(InputKey.Home);
        Assert.Equal(0, field.CaretIndex);
    }

    [Fact]
    public void Backspace_removes_the_grapheme_before_the_caret() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        fixture.Document.Focus(field);

        // ⚠ An emoji is a surrogate pair. A caret that moved by one UTF-16 unit would delete half of
        // it and leave an unpaired surrogate in the string.
        fixture.TypeText("a😀");
        Assert.Equal(3, field.Value!.Length);

        fixture.Type(InputKey.Backspace);
        Assert.Equal("a", field.Value);

        fixture.Type(InputKey.Backspace);
        Assert.Equal(string.Empty, field.Value);

        fixture.Type(InputKey.Backspace);
        Assert.Equal(string.Empty, field.Value);
    }

    [Fact]
    public void Delete_removes_the_grapheme_after_it() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "abc";
        field.MoveCaret(1);

        fixture.Document.Focus(field);
        fixture.Type(InputKey.Delete);

        Assert.Equal("ac", field.Value);
        Assert.Equal(1, field.CaretIndex);
    }

    [Fact]
    public void Typing_over_a_selection_replaces_it() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "hello";
        fixture.Document.Focus(field);

        field.SelectAll();
        Assert.Equal("hello", field.SelectedText);

        fixture.TypeText("x");

        Assert.Equal("x", field.Value);
        Assert.False(field.HasSelection);
    }

    [Fact]
    public void Select_all_anchors_at_the_start() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "hello";
        fixture.Document.Focus(field);

        field.SelectAll();
        fixture.Type(InputKey.Right);

        // Pressing Right after Ctrl-A puts the caret at the end of the text, which is what every
        // editor does. Anchoring the other way would put it at the start.
        Assert.Equal(5, field.CaretIndex);
    }

    [Fact]
    public void Ctrl_a_selects_everything() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "hello";
        fixture.Document.Focus(field);

        fixture.Type(InputKey.A, ModifierKeys.Control);

        Assert.Equal("hello", field.SelectedText);
    }

    [Fact]
    public void A_length_limit_truncates_a_paste_rather_than_refusing_it() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.MaxLength = 4;
        fixture.Document.Focus(field);

        fixture.TypeText("abcdefgh");

        Assert.Equal("abcd", field.Value);

        fixture.TypeText("z");
        Assert.Equal("abcd", field.Value);
    }

    [Fact]
    public void A_read_only_field_still_takes_the_focus_and_takes_no_text() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "fixed";
        field.ReadOnly = true;

        fixture.Document.Focus(field);
        Assert.True(field.IsFocused);

        fixture.TypeText("x");
        Assert.Equal("fixed", field.Value);
    }

    [Fact]
    public void The_placeholder_shows_only_while_it_is_empty() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Placeholder = "Name";
        fixture.Update();

        Assert.True(field.HasClass("empty"));

        fixture.Document.Focus(field);
        fixture.TypeText("a");

        Assert.False(field.HasClass("empty"));

        fixture.Type(InputKey.Backspace);
        Assert.True(field.HasClass("empty"));
    }

    [Fact]
    public void Enter_submits_without_changing_the_value() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "query";
        fixture.Document.Focus(field);

        var submitted = 0;
        field.Submitted += _ => submitted++;

        fixture.Type(InputKey.Enter);

        Assert.Equal(1, submitted);
        Assert.Equal("query", field.Value);
    }

    /// <summary>
    ///     ⚠ <b>The prompt has to be where the answer will be, and twice it was not.</b> It is
    ///     absolutely positioned so that a long prompt cannot decide how wide the box has to be —
    ///     which means the field itself has to be its containing block (<c>position: relative</c>,
    ///     since <c>static</c> is the initial and an ancestor several panels up would otherwise
    ///     claim it), and it means the offset has to clear the magnifying glass a search box has and
    ///     the other fields do not. Getting either wrong draws "Search assets" over the icon.
    /// </summary>
    [Fact]
    public void A_search_box_puts_its_placeholder_where_the_text_will_go() {
        using var fixture = new ControlFixture();

        var search = fixture.Add<SearchBox>();
        search.Placeholder = "Search assets";
        fixture.Update();

        var placeholder = Descendants(search).First(child => child.Tag == "field-placeholder");
        var text = Descendants(search).First(child => child.Tag == "field-text");

        Assert.True(placeholder.Width > 0f, "the placeholder is not being drawn, so this proves nothing.");

        // Clear of the icon on its left, and lined up with the value that replaces it. A pixel of
        // slack, because both are snapped to the device's grid independently.
        Assert.True(
            placeholder.AbsoluteLeft >= search.SearchIcon.AbsoluteLeft + search.SearchIcon.Width,
            $"the placeholder starts at {placeholder.AbsoluteLeft}, over an icon that runs to "
            + $"{search.SearchIcon.AbsoluteLeft + search.SearchIcon.Width}."
        );

        Assert.Equal(text.AbsoluteLeft, placeholder.AbsoluteLeft, 1f);
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    [Fact]
    public void A_search_box_clears_itself() {
        using var fixture = new ControlFixture();

        var search = fixture.Add<SearchBox>();
        search.Value = "mesh";
        fixture.Update();

        Assert.False(search.HasClass("empty"));

        fixture.Click(search.ClearButton);

        Assert.Equal(string.Empty, search.Value);
        Assert.True(search.HasClass("empty"));
        Assert.True(search.IsFocused);
    }

    [Fact]
    public void A_numeric_field_refuses_letters_and_keeps_half_typed_numbers() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        fixture.Document.Focus(field);

        field.Value = string.Empty;

        fixture.TypeText("-");
        Assert.Equal("-", field.Value);

        fixture.TypeText("1");
        fixture.TypeText(".");
        Assert.Equal("-1.", field.Value);

        // A field that rewrote `1.` to `1` would delete the decimal point the moment it was typed.
        fixture.TypeText("banana");
        Assert.Equal("-1.", field.Value);
    }

    [Fact]
    public void Enter_commits_a_half_typed_number() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        fixture.Document.Focus(field);

        field.Value = "007";
        fixture.Type(InputKey.Enter);

        Assert.Equal(7d, field.Number);
        Assert.Equal("7", field.Value);
    }

    [Fact]
    public void The_arrows_step_it_and_the_modifiers_scale_the_step() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Step = 2d;
        field.Number = 10d;

        fixture.Document.Focus(field);

        fixture.Type(InputKey.Up);
        Assert.Equal(12d, field.Number);

        fixture.Type(InputKey.Down, ModifierKeys.Shift);
        Assert.Equal(-8d, field.Number);
    }

    [Fact]
    public void A_number_is_clamped_to_its_range() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Minimum = 0d;
        field.Maximum = 10d;

        field.Number = 50d;
        Assert.Equal(10d, field.Number);

        field.Number = -50d;
        Assert.Equal(0d, field.Number);

        // Moving the ceiling under the value brings it back inside.
        field.Number = 10d;
        field.Maximum = 4d;
        Assert.Equal(4d, field.Number);
    }

    [Fact]
    public void Dragging_an_unfocused_numeric_field_scrubs_it() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Step = 1d;
        field.Number = 0d;
        fixture.Update();

        var bounds = field.Bounds;
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(bounds.X + 5f, y);
        fixture.MovePointer(bounds.X + 15f, y);
        fixture.Release(bounds.X + 15f, y);

        Assert.Equal(10d, field.Number);
        Assert.False(field.IsFocused);
    }

    [Fact]
    public void A_press_that_never_moved_focuses_it_instead() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Number = 3d;
        fixture.Update();

        fixture.Click(field);

        Assert.True(field.IsFocused);
        Assert.Equal(3d, field.Number);
        Assert.Equal(field.Value, field.SelectedText);
    }

    [Fact]
    public void A_double_click_selects_the_word_under_it_and_a_third_takes_the_lot() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "hello brave world";
        fixture.Update();

        // Into the middle word rather than at its start, because a double click selects the word the
        // pointer is inside and a test that clicked on a boundary would pass against one that only
        // ever grew rightwards.
        var part = field.Children.Single(child => child.Text == field.Value);
        var bounds = part.Bounds;
        var y = bounds.Y + (bounds.Height * 0.5f);
        var x = bounds.X + part.Block()!.Lines[0].CaretOffset(8);

        fixture.Click(x, y);
        Assert.False(field.HasSelection);

        fixture.Click(x, y);
        Assert.Equal("brave", field.SelectedText);
        Assert.Equal(11, field.CaretIndex);

        fixture.Click(x, y);
        Assert.Equal("hello brave world", field.SelectedText);
    }

    [Fact]
    public void A_double_click_on_a_numeric_field_takes_the_whole_number() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 2;
        field.Number = 12.5d;
        fixture.Update();

        fixture.Click(field);
        fixture.Click(field);

        // A number is one thing to whoever is editing it, and the word breaker would have handed
        // back "12".
        Assert.Equal("12.50", field.SelectedText);
    }

    [Fact]
    public void Clicking_away_from_a_field_takes_the_focus_off_it() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        fixture.Update();

        fixture.Click(field);
        Assert.True(field.IsFocused);

        // The document's background, which holds no focus of its own.
        fixture.Click(400f, 500f);

        Assert.False(field.IsFocused);
        Assert.Null(fixture.Document.Focused);
    }

    [Fact]
    public void Dragging_a_scrollbar_does_not_take_the_focus_off_a_field() {
        using var fixture = new ControlFixture();

        var view = fixture.Add<ScrollView>();
        var field = view.Content.Add<TextBox>();

        // Enough content for the bar to have somewhere to go, or the press below is a press on a
        // scrollbar with nothing to drag.
        view.Content.Add("div").SetStyle("height", "4000px");
        fixture.Update();

        fixture.Click(field);
        Assert.True(field.IsFocused);

        var bar = view.VerticalBar.Bounds;
        var x = bar.X + (bar.Width * 0.5f);

        fixture.Press(x, bar.Y + 10f);
        fixture.MovePointer(x, bar.Y + 60f);
        fixture.Release(x, bar.Y + 60f);

        // A scrollbar cannot hold the focus and does not want it. Taking the caret and the selection
        // out of a field because the panel around it was scrolled is the bug this exempts.
        Assert.True(view.ScrollTop > 0f);
        Assert.True(field.IsFocused);
    }

    [Fact]
    public void A_selection_drag_that_leaves_the_field_keeps_it() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "hello";
        fixture.Update();

        var bounds = field.Bounds;
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(bounds.X + 5f, y);
        fixture.MovePointer(bounds.X + bounds.Width + 200f, y);

        // A drag that has captured the pointer is not a click on whatever is under it, and the field
        // it started in must not lose the focus part-way through a selection.
        Assert.True(field.IsFocused);

        fixture.Release(bounds.X + bounds.Width + 200f, y);
        Assert.True(field.IsFocused);
    }

    [Fact]
    public void A_focused_field_draws_a_caret_and_a_selection_band() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "abc";
        fixture.Update();

        var before = fixture.Document.Drawing.Commands.Count;

        fixture.Document.Focus(field);
        field.SelectAll();
        fixture.Update();

        // One rectangle for the band and one for the caret. Drawn on the field rather than on the
        // text element, so the band lands under the glyphs it highlights.
        Assert.Equal(before + 2, fixture.Document.Drawing.Commands.Count);
    }

    /// <summary>
    ///     ⚠ <b>A caret is a promise that the next keystroke lands here, and on a read-only field
    ///     that promise is false.</b> An inspector row over a member with no setter blinked exactly
    ///     like one you could edit, and the only way to find out was to type into it and watch
    ///     nothing happen.
    /// </summary>
    [Fact]
    public void A_read_only_field_takes_the_focus_and_draws_no_caret() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "abc";
        field.ReadOnly = true;
        fixture.Update();

        var before = fixture.Document.Drawing.Commands.Count;

        fixture.Document.Focus(field);
        fixture.Update();

        // Still a tab stop and still focusable — that is the whole difference from `Disabled`, and
        // it is what lets somebody select the value and copy it.
        Assert.True(field.IsFocused);
        Assert.Equal(before, fixture.Document.Drawing.Commands.Count);

        // And the selection band is still drawn, because selecting and copying is what a read-only
        // field is for. It is the insertion point that is the lie, not the highlight.
        field.SelectAll();
        fixture.Update();

        Assert.Equal(before + 1, fixture.Document.Drawing.Commands.Count);
    }

    /// <summary>The same for a disabled field, which cannot be typed into either.</summary>
    /// <remarks>
    ///     ⚠ <b>Focused by hand rather than by a click.</b> A disabled control refuses every input
    ///     route — which is the point of it — so the only way to get a focused disabled field is to
    ///     ask the document for one, and the only way this could ever be seen in an application is a
    ///     field disabled while it already had the focus.
    /// </remarks>
    [Fact]
    public void A_disabled_field_draws_no_caret_either() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "abc";
        fixture.Update();

        var before = Painted(fixture);

        fixture.Document.Focus(field);
        field.Disabled = true;
        fixture.Update();

        Assert.Equal(before, Painted(fixture));
    }

    /// <summary>How many commands actually paint something, ignoring the brackets.</summary>
    /// <remarks>
    ///     ⚠ <b>The total will not do, because disabling a control now opens a group.</b>
    ///     <c>:disabled</c> is <c>opacity: 0.55</c> and an element below one opacity is composited
    ///     rather than faded in place, so the act of disabling adds a
    ///     <see cref="DrawCommandKind.LayerPush" /> and its pop — two commands that draw nothing. The
    ///     claim here is about a caret, so what it has to count is <i>paint</i>; counting the raw list
    ///     would make this test fail for the one reason it is not about.
    /// </remarks>
    static int Painted(ControlFixture fixture) =>
        fixture.Document.Drawing.Commands.Count(
            command => command.Kind
                is not (DrawCommandKind.LayerPush
                or DrawCommandKind.LayerPop
                or DrawCommandKind.ClipPush
                or DrawCommandKind.ClipPop)
        );
}
