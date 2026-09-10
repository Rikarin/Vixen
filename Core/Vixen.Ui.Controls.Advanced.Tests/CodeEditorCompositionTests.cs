// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The code editor with an input method in front of it — issue #673's third item.</summary>
/// <remarks>
///     <para>
///         <b>The defect these were written against is invisible rather than wrong.</b>
///         <c>CodeEditor</c> registered <c>KeyEvent</c>, <c>TextInputEvent</c>, <c>PointerEvent</c>
///         and <c>TapEvent</c> and no <c>TextCompositionEvent</c>, so a Japanese, Chinese or Korean
///         pre-edit was not drawn at all until it committed: the file did not change while somebody
///         typed into it, and the candidate list floated over unchanged text. Nothing logged, no
///         counter moved and every existing test stayed green, because every existing test types
///         with <c>TypeText</c> — which is what a keyboard with no input method in front of it
///         sends.
///     </para>
///     <para>
///         ⚠ <b>What each assertion here reads is the <em>shown</em> line and not the buffer</b>,
///         because the two must disagree while a composition is running and agree at every other
///         moment. A suite that only read <c>Source</c> would pass against a control that committed
///         the pre-edit on every keystroke — the other way to be wrong, and the more damaging one,
///         since it puts provisional text through the undo stack.
///     </para>
/// </remarks>
public class CodeEditorCompositionTests {
    /// <summary>What a realised row actually shows, spans and all.</summary>
    /// <remarks>
    ///     A parked span keeps the text it last had, so the filter is not tidiness: without it a
    ///     line that shrank would read as the longer line it used to be.
    /// </remarks>
    static string Shown(CodeEditor editor, int row) =>
        string.Concat(
            editor.Pool[row].Spans
                .Where(span => !span.HasClass("parked"))
                .Select(span => span.Text ?? string.Empty)
        );

    static (AdvancedFixture Fixture, CodeEditor Editor) Editor(string source = "abc") {
        var fixture = new AdvancedFixture();
        var editor = fixture.Add<CodeEditor>();

        editor.Source = source;

        fixture.Update();
        editor.Refresh();
        fixture.Update();

        fixture.Document.Focus(editor);
        fixture.Update();

        return (fixture, editor);
    }

    /// <summary>A pre-edit is drawn where the caret is, and the buffer never sees it.</summary>
    [Fact]
    public void A_pre_edit_is_drawn_at_the_caret_and_stays_out_of_the_buffer() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        editor.Move(new TextPosition(0, 3));
        fixture.Compose("ねこ");

        Assert.Equal("abcねこ", Shown(editor, 0));

        // ⚠ The half that separates this from a control that commits every keystroke: provisional
        // text must not reach the buffer, the undo stack or `Source`.
        Assert.Equal("abc", editor.Source);
    }

    /// <summary>And in the middle of a line, not only at the end of one.</summary>
    [Fact]
    public void A_pre_edit_splices_into_the_line_at_the_caret() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        editor.Move(new TextPosition(0, 1));
        fixture.Compose("ねこ");

        Assert.Equal("aねこbc", Shown(editor, 0));
        Assert.Equal("abc", editor.Source);
    }

    /// <summary>
    ///     ⚠ An empty composition is a cancellation, and a handler that returned early on one would
    ///     leave the pre-edit drawn for ever.
    /// </summary>
    [Fact]
    public void An_empty_composition_abandons_the_pre_edit() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        editor.Move(new TextPosition(0, 3));
        fixture.Compose("ねこ");

        Assert.Equal("abcねこ", Shown(editor, 0));

        fixture.Compose(string.Empty);

        Assert.Equal("abc", Shown(editor, 0));
        Assert.Equal("abc", editor.Source);
    }

    /// <summary>The commit arrives as typed text, and it lands exactly once.</summary>
    /// <remarks>
    ///     ⚠ <b>The equality is what catches the double.</b> A control that cleared its pre-edit
    ///     after the insert rather than before it shows <c>abcねこ猫</c> for a frame and puts the
    ///     right string in the buffer, so an assertion on <c>Source</c> alone is green against it.
    /// </remarks>
    [Fact]
    public void Committing_replaces_the_pre_edit_rather_than_adding_to_it() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        editor.Move(new TextPosition(0, 3));
        fixture.Compose("ねこ");
        fixture.TypeText("猫");

        Assert.Equal("abc猫", editor.Source);
        Assert.Equal("abc猫", Shown(editor, 0));
    }

    /// <summary>A composition that starts over a selection replaces it, exactly as typing would.</summary>
    [Fact]
    public void Starting_a_composition_deletes_the_selection() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        editor.SelectAll();
        fixture.Update();

        fixture.Compose("ね");

        Assert.Equal(string.Empty, editor.Source);
        Assert.Equal("ね", Shown(editor, 0));

        // And the updates that follow replace the pre-edit rather than the file, which is what the
        // `!IsComposing` half of that guard is for.
        fixture.Compose("ねこ");

        Assert.Equal(string.Empty, editor.Source);
        Assert.Equal("ねこ", Shown(editor, 0));
    }

    /// <summary>
    ///     The caret — and so the candidate window — follows the input method's own cursor inside
    ///     the pre-edit.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Measured as a difference in columns on the same editor at the same moment</b>, not
    ///     against a number: <c>CharacterWidth</c> is the theme's font at whatever size the cascade
    ///     resolved, so an absolute x would be asserting about the test font. Dropped, the caret
    ///     sits in front of the whole pre-edit and every candidate list is placed against the wrong
    ///     character — which is the placement <c>ITextInput.SetCandidateArea</c> exists for.
    /// </remarks>
    [Fact]
    public void The_candidate_area_follows_the_cursor_inside_the_pre_edit() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        editor.Move(new TextPosition(0, 3));

        var caret = editor.CaretArea.X;

        fixture.Compose("ねこ", caret: 0);

        Assert.Equal(caret, editor.CaretArea.X, 0.01f);

        fixture.Compose("ねこ", caret: 2);

        Assert.Equal(caret + (2f * editor.CharacterWidth), editor.CaretArea.X, 0.01f);
    }

    /// <summary>
    ///     ⚠ An editor that has lost the focus is not the one the input method is talking to, so it
    ///     abandons the pre-edit itself.
    /// </summary>
    /// <remarks>
    ///     The platform sends the end of the composition to whatever <em>took</em> the focus, so
    ///     without this the pre-edit stays drawn in a file nobody is typing into — visible,
    ///     uncommittable, and belonging to an input method that has forgotten about it.
    /// </remarks>
    [Fact]
    public void Losing_the_focus_abandons_the_pre_edit() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        var other = fixture.Add<CodeEditor>();

        editor.Move(new TextPosition(0, 3));
        fixture.Compose("ねこ");

        Assert.Equal("abcねこ", Shown(editor, 0));

        fixture.Document.Focus(other);
        fixture.Update();

        Assert.Equal("abc", Shown(editor, 0));
    }

    /// <summary>A read-only editor composes nothing at all.</summary>
    [Fact]
    public void A_read_only_editor_takes_no_pre_edit() {
        var (fixture, editor) = Editor();
        using var owned = fixture;

        editor.ReadOnly = true;
        editor.Move(new TextPosition(0, 3));

        fixture.Compose("ねこ");

        Assert.Equal("abc", Shown(editor, 0));
    }
}
