// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The buffer on its own: the four edits, and where a word starts.</summary>
public class CodeBufferTests {
    [Fact]
    public void An_empty_buffer_is_one_empty_line() {
        var buffer = new CodeBuffer();

        Assert.Equal(1, buffer.LineCount);
        Assert.Equal(string.Empty, buffer[0]);
    }

    [Fact]
    public void Inserting_a_newline_splits_the_line_around_the_caret() {
        var buffer = new CodeBuffer("hello world");
        var end = buffer.Insert(new TextPosition(0, 5), "\n");

        Assert.Equal(["hello", " world"], buffer.Lines);
        Assert.Equal(new TextPosition(1, 0), end);
    }

    [Fact]
    public void Inserting_several_lines_reports_where_the_last_one_ends() {
        var buffer = new CodeBuffer("ab");
        var end = buffer.Insert(new TextPosition(0, 1), "1\n22\n333");

        Assert.Equal(["a1", "22", "333b"], buffer.Lines);
        Assert.Equal(new TextPosition(2, 3), end);
    }

    [Fact]
    public void Deleting_across_lines_joins_them() {
        var buffer = new CodeBuffer("one\ntwo\nthree");
        var at = buffer.Delete(new TextPosition(0, 1), new TextPosition(2, 2));

        Assert.Equal(["oree"], buffer.Lines);
        Assert.Equal(new TextPosition(0, 1), at);
    }

    [Fact]
    public void A_slice_carries_the_newlines_it_crossed() {
        var buffer = new CodeBuffer("one\ntwo\nthree");

        Assert.Equal("ne\ntwo\nth", buffer.Slice(new TextPosition(0, 1), new TextPosition(2, 2)));

        // Either way round, because a selection is dragged in both directions.
        Assert.Equal("ne\ntwo\nth", buffer.Slice(new TextPosition(2, 2), new TextPosition(0, 1)));
    }

    [Fact]
    public void Word_navigation_stops_at_each_change_of_character_class() {
        var buffer = new CodeBuffer("foo.bar(baz)");
        var end = new TextPosition(0, 12);

        // ⚠ Not "back to the last space", which would jump the whole line. Every editor stops at the
        // bracket, then the word, then the dot.
        var first = buffer.WordStart(end);
        Assert.Equal(11, first.Column);

        Assert.Equal(8, buffer.WordStart(first).Column);
        Assert.Equal(7, buffer.WordStart(new TextPosition(0, 8)).Column);
    }

    /// <summary>And inside a run of letters it stops where UAX #29 says a word ends.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The class rule alone navigates Japanese as one word, and it is
    ///         <c>char.IsLetterOrDigit</c> that makes it.</b> That predicate is true of every Han,
    ///         Kana and Thai codepoint, so <c>編集するためのボタン</c> — a language that puts no space
    ///         between words — was a single run of the word class and Ctrl-Left jumped the whole
    ///         clause. The literals below are UAX #29's answer for this string: a break after each
    ///         ideograph and each hiragana, and <b>none</b> inside <c>ボタン</c>, because WB13 holds
    ///         a katakana run together.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written as positions rather than as "whatever <c>WordBreaker</c> says", which
    ///         would be a tautology.</b> The implementation calls that breaker; a test that asked it
    ///         the same question would agree with itself on the day the subdivision was deleted, so
    ///         long as the deletion also went through it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Word_navigation_divides_a_run_of_letters_where_the_language_does() {
        var buffer = new CodeBuffer("編集するためのボタン");

        // Ctrl-Left from the end: ボタン is one stop, then one per kana and per ideograph.
        var stops = new List<int>();

        for (var at = new TextPosition(0, 10); at.Column > 0;) {
            at = buffer.WordStart(at);
            stops.Add(at.Column);
        }

        Assert.Equal([7, 6, 5, 4, 3, 2, 1, 0], stops);

        // And Ctrl-Right from the start, which is the same set read the other way.
        stops.Clear();

        for (var at = new TextPosition(0, 0); at.Column < 10;) {
            at = buffer.WordEnd(at);
            stops.Add(at.Column);
        }

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 10], stops);
    }

    /// <summary>And it did not swap one rule for the other: code still navigates by class run.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the half that says the fix is a subdivision and not a replacement.</b>
    ///     <c>WordBreaker</c> on its own would stop <i>between</i> <c>foo</c>, <c>.</c> and
    ///     <c>bar</c> too, but it would also refuse to treat <c>(</c> as its own stop the way an
    ///     editor does — and, more to the point, the run rule is what a code editor exists for.
    ///     Identifiers survive because UAX #29 already keeps them whole: <c>_</c> is
    ///     <c>ExtendNumLet</c>, and digits join letters, so <c>fooBar</c>, <c>foo_bar</c> and
    ///     <c>abc123</c> are one word to it as well as one run to the class rule.
    /// </remarks>
    [Fact]
    public void Word_navigation_still_stops_at_the_brackets_and_keeps_identifiers_whole() {
        var buffer = new CodeBuffer("foo.bar(");

        Assert.Equal(7, buffer.WordStart(new TextPosition(0, 8)).Column);
        Assert.Equal(4, buffer.WordStart(new TextPosition(0, 7)).Column);
        Assert.Equal(3, buffer.WordStart(new TextPosition(0, 4)).Column);
        Assert.Equal(0, buffer.WordStart(new TextPosition(0, 3)).Column);

        foreach (var identifier in (string[]) ["fooBar", "foo_bar", "abc123", "_private"]) {
            var one = new CodeBuffer(identifier);

            Assert.Equal(identifier.Length, one.WordEnd(new TextPosition(0, 0)).Column);
            Assert.Equal(0, one.WordStart(new TextPosition(0, identifier.Length)).Column);
        }
    }

    [Fact]
    public void Stepping_back_from_column_zero_lands_at_the_end_of_the_line_above() {
        var buffer = new CodeBuffer("abc\nde");

        Assert.Equal(new TextPosition(0, 3), buffer.Back(new TextPosition(1, 0)));
        Assert.Equal(new TextPosition(1, 0), buffer.Forward(new TextPosition(0, 3)));
    }

    [Fact]
    public void The_word_before_a_position_is_what_a_completion_filters_on() {
        var buffer = new CodeBuffer("var stre");

        Assert.Equal("stre", buffer.WordBefore(new TextPosition(0, 8)));
        Assert.Equal(string.Empty, buffer.WordBefore(new TextPosition(0, 4)));
    }

    [Fact]
    public void Carriage_returns_do_not_survive_into_the_lines() {
        var buffer = new CodeBuffer("a\r\nb\r\nc");

        Assert.Equal(["a", "b", "c"], buffer.Lines);
    }
}

/// <summary>The tokenizers, including the one state that crosses a line.</summary>
public class CodeTokenizerTests {
    static List<CodeToken> Tokens(CStyleTokenizer tokenizer, string line, int state = 0) {
        var into = new List<CodeToken>();
        tokenizer.Tokenize(line, state, into);

        return into;
    }

    [Fact]
    public void Keywords_and_types_are_told_apart() {
        var kinds = Tokens(CStyleTokenizer.Raven, "let x: float = 1.5;")
            .Where(static token => token.Kind != CodeTokenKind.Plain)
            .Select(static token => token.Kind)
            .ToArray();

        Assert.Equal(
            [
                CodeTokenKind.Keyword, CodeTokenKind.Operator, CodeTokenKind.Type,
                CodeTokenKind.Operator, CodeTokenKind.Number, CodeTokenKind.Operator
            ],
            kinds
        );
    }

    [Fact]
    public void A_block_comment_carries_across_the_line_boundary() {
        var opened = CStyleTokenizer.CSharp.Tokenize("var a = 1; /* start", 0, []);

        Assert.NotEqual(0, opened);

        // ⚠ The claim the state exists for: line two is a comment because line one ended in one.
        var inside = Tokens(CStyleTokenizer.CSharp, "still a comment", opened);
        Assert.Equal(CodeTokenKind.Comment, Assert.Single(inside).Kind);

        var closed = CStyleTokenizer.CSharp.Tokenize("end */ var b = 2;", opened, []);
        Assert.Equal(0, closed);
    }

    [Fact]
    public void An_unterminated_string_ends_at_the_line_rather_than_eating_the_file() {
        var tokens = Tokens(CStyleTokenizer.CSharp, "var s = \"half typed");

        Assert.Equal(CodeTokenKind.String, tokens[^1].Kind);
        Assert.Equal(0, CStyleTokenizer.CSharp.Tokenize("var s = \"half typed", 0, []));
    }

    [Fact]
    public void Every_character_of_the_line_is_covered_exactly_once() {
        const string Line = "  if (a >= 0) { /* c */ return \"x\"; } // tail";

        var tokens = Tokens(CStyleTokenizer.CSharp, Line);
        var at = 0;

        // The property the whole rendering rests on: the spans are the line, in order, with no gaps
        // and no overlap — a hole would silently drop characters out of the picture.
        foreach (var token in tokens) {
            Assert.Equal(at, token.Start);
            at += token.Length;
        }

        Assert.Equal(Line.Length, at);
    }
}

/// <summary>The control: virtualisation, editing, folding, diagnostics and completion.</summary>
public class CodeEditorTests {
    static CodeEditor Editor(AdvancedFixture fixture, string source, CStyleTokenizer? tokenizer = null) {
        var editor = fixture.Add<CodeEditor>();

        if (tokenizer is not null) {
            editor.Tokenizer = tokenizer;
        }

        editor.Source = source;

        fixture.Update();
        editor.Refresh();
        fixture.Update();

        fixture.Document.Focus(editor);
        return editor;
    }

    [Fact]
    public void A_huge_file_realises_only_the_lines_that_fit() {
        using var fixture = new AdvancedFixture();

        var editor = Editor(fixture, string.Join('\n', Enumerable.Range(0, 50_000).Select(static i => $"line {i}")));

        Assert.Equal(50_000, editor.Rows.Count);
        Assert.True(editor.Pool.Count < 60, $"realised {editor.Pool.Count} lines");
        Assert.Equal(0, editor.Pool[0].Index);
    }

    [Fact]
    public void Scrolling_rebinds_the_lines_rather_than_making_new_ones() {
        using var fixture = new AdvancedFixture();

        var editor = Editor(fixture, string.Join('\n', Enumerable.Range(0, 2_000).Select(static i => $"line {i}")));

        var before = editor.Pool.Count;
        var element = editor.Pool[0];

        editor.Scroller.ScrollTop = editor.RowHeight * 500f;
        fixture.Update();

        Assert.Equal(before, editor.Pool.Count);
        Assert.Same(element, editor.Pool[0]);
        Assert.Equal(498, editor.Pool[0].Index);
    }

    [Fact]
    public void Typing_goes_in_at_the_caret() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "ac");

        editor.Move(new TextPosition(0, 1));
        fixture.TypeText("b");

        Assert.Equal("abc", editor.Source);
        Assert.Equal(new TextPosition(0, 2), editor.Caret);
    }

    [Fact]
    public void Enter_copies_the_indent_of_the_line_it_left() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "    let x = 1;");

        editor.Move(new TextPosition(0, 14));
        fixture.Type(InputKey.Enter);

        Assert.Equal(["    let x = 1;", "    "], editor.Buffer.Lines);
        Assert.Equal(new TextPosition(1, 4), editor.Caret);
    }

    [Fact]
    public void Backspace_at_column_zero_joins_the_line_to_the_one_above() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "one\ntwo");

        editor.Move(new TextPosition(1, 0));
        fixture.Type(InputKey.Backspace);

        Assert.Equal("onetwo", editor.Source);
        Assert.Equal(new TextPosition(0, 3), editor.Caret);
    }

    [Fact]
    public void Tab_indents_every_selected_line_and_shift_tab_takes_it_back() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "a\nb\nc");

        editor.Move(default);
        editor.Move(new TextPosition(2, 1), extend: true);

        fixture.Type(InputKey.Tab);
        Assert.Equal(["    a", "    b", "    c"], editor.Buffer.Lines);

        fixture.Type(InputKey.Tab, ModifierKeys.Shift);
        Assert.Equal(["a", "b", "c"], editor.Buffer.Lines);
    }

    [Fact]
    public void Tab_with_no_selection_inserts_spaces() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        editor.Move(default);
        fixture.Type(InputKey.Tab);

        Assert.Equal("    x", editor.Source);
    }

    [Fact]
    public void Shift_and_an_arrow_drag_the_selection() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "hello");

        editor.Move(default);

        fixture.Type(InputKey.Right, ModifierKeys.Shift);
        fixture.Type(InputKey.Right, ModifierKeys.Shift);

        Assert.True(editor.HasSelection);
        Assert.Equal("he", editor.SelectedText);

        // And typing over a selection replaces it, which is the one thing every text control does.
        fixture.TypeText("H");

        Assert.Equal("Hllo", editor.Source);
        Assert.False(editor.HasSelection);
    }

    [Fact]
    public void Select_all_and_delete_empties_the_file() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "one\ntwo\nthree");

        fixture.Type(InputKey.A, ModifierKeys.Control);
        Assert.Equal("one\ntwo\nthree", editor.SelectedText);

        fixture.Type(InputKey.Delete);

        Assert.Equal(string.Empty, editor.Source);
        Assert.Equal(1, editor.Buffer.LineCount);
    }

    [Fact]
    public void Home_stops_at_the_indent_before_it_stops_at_the_margin() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "    value");

        editor.Move(new TextPosition(0, 9));

        fixture.Type(InputKey.Home);
        Assert.Equal(4, editor.Caret.Column);

        fixture.Type(InputKey.Home);
        Assert.Equal(0, editor.Caret.Column);
    }

    [Fact]
    public void A_read_only_editor_refuses_every_edit() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "locked");

        editor.ReadOnly = true;
        editor.Move(default);

        fixture.TypeText("x");
        fixture.Type(InputKey.Delete);
        fixture.Type(InputKey.Enter);

        Assert.Equal("locked", editor.Source);
    }

    [Fact]
    public void A_line_is_coloured_by_the_tokenizer() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "let x = 1;", CStyleTokenizer.Raven);

        var spans = editor.Pool[0].Spans.Where(static span => !span.HasClass("parked")).ToArray();

        Assert.Equal(CodeTokenKind.Keyword, spans[0].Kind);
        Assert.Equal("let", spans[0].Text);
        Assert.True(spans[0].HasClass("tok-keyword"));

        Assert.Contains(spans, static span => span.Kind == CodeTokenKind.Number && span.Text == "1");
    }

    [Fact]
    public void Opening_a_block_comment_recolours_the_lines_below_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "one\ntwo\nthree", CStyleTokenizer.CSharp);

        static CodeTokenKind KindOf(CodeEditor editor, int line) =>
            editor.Pool.First(row => row.Index == line).Spans[0].Kind;

        Assert.Equal(CodeTokenKind.Plain, KindOf(editor, 2));

        // ⚠ The cache's whole reason for being invalidated downwards: an edit on line 0 changes what
        // line 2 is, and a highlighter that only re-ran the edited line would leave it uncoloured.
        editor.Move(default);
        fixture.TypeText("/* ");

        Assert.Equal(CodeTokenKind.Comment, KindOf(editor, 2));
    }

    [Fact]
    public void A_fold_hides_its_lines_and_toggling_brings_them_back() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "outer\n    inner1\n    inner2\nafter");

        Assert.Equal(new CodeFold(0, 2), Assert.Single(editor.Folds));
        Assert.Equal(4, editor.Rows.Count);

        Assert.True(editor.ToggleFold(0));

        Assert.Equal([0, 3], editor.Rows);
        Assert.True(editor.IsCollapsed(0));

        editor.ToggleFold(0);
        Assert.Equal(4, editor.Rows.Count);
    }

    [Fact]
    public void The_caret_comes_out_of_a_region_that_is_collapsed_under_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "outer\n    inner1\n    inner2\nafter");

        editor.Move(new TextPosition(2, 4));
        editor.ToggleFold(0);

        // ⚠ Otherwise the caret has no row, every arrow key moves something invisible, and the
        // editor looks frozen.
        Assert.Equal(0, editor.Caret.Line);
        Assert.True(editor.RowOf(editor.Caret.Line) >= 0);
    }

    [Fact]
    public void Down_steps_over_a_collapsed_region_rather_than_into_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "outer\n    inner1\n    inner2\nafter");

        editor.ToggleFold(0);
        editor.Move(new TextPosition(0, 0));

        fixture.Type(InputKey.Down);

        Assert.Equal(3, editor.Caret.Line);
    }

    [Fact]
    public void A_diagnostic_marks_its_line_and_its_gutter() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "fine\nbroken\nfine");

        editor.SetDiagnostics(new CodeDiagnostic(1, 0, 6, CodeSeverity.Error, "no such thing"));
        fixture.Update();

        Assert.True(editor.Pool.First(static row => row.Index == 1).HasClass("has-error"));
        Assert.False(editor.Pool.First(static row => row.Index == 0).HasClass("has-error"));
    }

    [Fact]
    public void The_completion_popup_filters_moves_and_accepts() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, string.Empty);

        editor.CompletionProvider = static (_, _) => [
            new CompletionItem("Strength", "float"),
            new CompletionItem("Stretch", "float"),
            new CompletionItem("Other")
        ];

        editor.Move(default);
        fixture.TypeText("St");

        fixture.Type(InputKey.Space, ModifierKeys.Control);

        Assert.True(editor.IsCompleting);
        Assert.Equal(2, editor.Completions.Count);

        fixture.Type(InputKey.Down);
        Assert.Equal(1, editor.CompletionIndex);

        var accepted = default(CompletionItem);
        editor.CompletionAccepted += (_, item) => accepted = item;

        fixture.Type(InputKey.Enter);

        // ⚠ The prefix is replaced rather than the remainder appended, so a differently-cased
        // prefix does not give `stStretch`.
        Assert.Equal("Stretch", editor.Source);
        Assert.Equal("Stretch", accepted.Label);
        Assert.False(editor.IsCompleting);
    }

    [Fact]
    public void Escape_takes_the_popup_down_and_leaves_the_text_alone() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, string.Empty);

        editor.CompletionProvider = static (_, _) => [new CompletionItem("Strength")];

        editor.Move(default);
        fixture.TypeText("St");

        fixture.Type(InputKey.Space, ModifierKeys.Control);
        Assert.True(editor.IsCompleting);

        fixture.Type(InputKey.Escape);

        Assert.False(editor.IsCompleting);
        Assert.Equal("St", editor.Source);
    }

    [Fact]
    public void A_click_puts_the_caret_where_it_landed() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "abcdefgh\nijklmnop\nqrstuvwx");

        var target = new TextPosition(1, 4);
        var point = editor.ToScreen(target);

        // Half a cell in, so the rounding lands on the character rather than on the boundary.
        fixture.Press(point.X + (editor.CharacterWidth * 0.2f), point.Y + (editor.RowHeight * 0.5f));
        fixture.Release(point.X + (editor.CharacterWidth * 0.2f), point.Y + (editor.RowHeight * 0.5f));

        Assert.Equal(target, editor.Caret);
    }

    [Fact]
    public void Dragging_selects_between_the_two_points() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "abcdefgh\nijklmnop");

        var from = editor.ToScreen(new TextPosition(0, 2));
        var to = editor.ToScreen(new TextPosition(1, 3));

        fixture.Press(from.X, from.Y + (editor.RowHeight * 0.5f));
        fixture.Move(to.X, to.Y + (editor.RowHeight * 0.5f));
        fixture.Release(to.X, to.Y + (editor.RowHeight * 0.5f));

        Assert.Equal("cdefgh\nijk", editor.SelectedText);
    }

    [Fact]
    public void A_buffer_edited_from_outside_is_taken_as_a_new_file() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "one\ntwo", CStyleTokenizer.CSharp);

        editor.Move(new TextPosition(1, 3));

        // What a formatter, a refactor or a hot reload does. Nothing on `Changed` says which line
        // moved, so every cached state has to go — and the caret has to be brought back inside.
        editor.Buffer.Text = "x";

        Assert.Equal(1, editor.Buffer.LineCount);
        Assert.Equal(new TextPosition(0, 1), editor.Caret);
        Assert.Equal(0, Assert.Single(editor.Rows));
    }


    // ────────────────────────────────────────────────────────────── word wrap

    /// <summary>How many characters the viewport holds, which is what the wrap column is.</summary>
    /// <remarks>
    ///     Read off the control rather than written down, because the cell width comes from the test
    ///     font. A test asserting "three rows" against a hard-coded column count would be asserting
    ///     what the font happens to measure.
    /// </remarks>
    static int Columns(CodeEditor editor) => (int) (editor.Scroller.Width / editor.CharacterWidth);

    [Fact]
    public void A_line_too_long_for_the_viewport_becomes_several_rows() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        var columns = Columns(editor);
        Assert.True(columns > 4, $"the viewport holds {columns} characters, which is too few to wrap in");

        editor.Source = new string('x', columns * 3);
        fixture.Update();

        // One row until it is asked for, which is what keeps a column number meaning what a compiler
        // says it means.
        Assert.Equal(0, Assert.Single(editor.Rows));

        editor.WordWrap = true;
        fixture.Update();

        Assert.Equal([0, 0, 0], editor.Rows);
        Assert.Equal([0, columns, columns * 2], editor.RowStarts);
    }

    [Fact]
    public void Turning_wrapping_off_puts_every_row_back_on_its_own_line() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        editor.Source = new string('x', Columns(editor) * 3);
        editor.WordWrap = true;
        fixture.Update();

        Assert.Equal(3, editor.Rows.Count);

        editor.WordWrap = false;
        fixture.Update();

        Assert.Equal(0, Assert.Single(editor.Rows));
        Assert.Equal(0, Assert.Single(editor.RowStarts));
    }

    /// <summary>
    ///     ⚠ <b>A word is never cut while there is a space to break at</b>, which in a code editor is
    ///     the one thing wrapping must not do to an identifier. The trailing space stays on the row it
    ///     ended, so the next row does not begin with a blank cell.
    /// </summary>
    [Fact]
    public void A_row_breaks_after_a_space_rather_than_inside_a_word() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        var line = string.Join(' ', Enumerable.Repeat("alpha", 60));

        editor.Source = line;
        editor.WordWrap = true;
        fixture.Update();

        Assert.True(editor.Rows.Count > 2, $"expected several rows, got {editor.Rows.Count}");

        foreach (var start in editor.RowStarts.Skip(1)) {
            Assert.Equal(' ', line[start - 1]);
            Assert.NotEqual(' ', line[start]);
        }
    }

    /// <summary>And a word with nowhere to break is cut, because a row cannot be wider than its box.</summary>
    [Fact]
    public void A_word_longer_than_the_viewport_is_cut_at_the_column() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        var columns = Columns(editor);

        editor.Source = new string('a', columns + 5);
        editor.WordWrap = true;
        fixture.Update();

        Assert.Equal([0, columns], editor.RowStarts);
    }

    /// <summary>
    ///     ⚠ <b>Numbered once, on the row the line starts on.</b> Numbering every visual row prints
    ///     the same number three times down the margin; numbering them consecutively makes the gutter
    ///     disagree with every compiler error in the file.
    /// </summary>
    [Fact]
    public void The_gutter_numbers_a_wrapped_line_once() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        editor.Source = new string('x', Columns(editor) * 3) + "\nsecond";
        editor.WordWrap = true;
        fixture.Update();

        var numbered = editor.Gutter.Children
            .OfType<CodeGutterRow>()
            .Where(row => row.Index >= 0)
            .Select(row => row.Number.Text ?? string.Empty)
            .ToArray();

        Assert.Equal(["1", string.Empty, string.Empty, "2"], numbered);
    }

    /// <summary>
    ///     Down moves one <i>visual</i> row, keeping the offset within it — which is the whole reason
    ///     the caret's row is looked up in the row list rather than worked out from the line number.
    /// </summary>
    [Fact]
    public void Down_moves_one_visual_row_and_keeps_the_offset_within_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        var columns = Columns(editor);

        editor.Source = new string('x', columns * 3);
        editor.WordWrap = true;
        fixture.Update();

        editor.Move(new TextPosition(0, 3));
        fixture.Type(InputKey.Down);

        Assert.Equal(new TextPosition(0, columns + 3), editor.Caret);

        fixture.Type(InputKey.Up);
        Assert.Equal(new TextPosition(0, 3), editor.Caret);
    }

    /// <summary>
    ///     ⚠ <b>A click on the second visual row lands on the character under it, not on the one that
    ///     many columns from the start of the line.</b> The x is an offset into the row, so the
    ///     column is the row's own start plus it — the same arithmetic <c>ToScreen</c> undoes.
    /// </summary>
    [Fact]
    public void A_click_on_a_wrapped_row_lands_on_the_character_under_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        var columns = Columns(editor);

        editor.Source = new string('x', columns * 3);
        editor.WordWrap = true;
        fixture.Update();

        var target = new TextPosition(0, columns + 4);
        var point = editor.ToScreen(target);

        fixture.Press(point.X + (editor.CharacterWidth * 0.2f), point.Y + (editor.RowHeight * 0.5f));
        fixture.Release(point.X + (editor.CharacterWidth * 0.2f), point.Y + (editor.RowHeight * 0.5f));

        Assert.Equal(target, editor.Caret);
    }

    /// <summary>
    ///     ⚠ <b>And a click past the end of a wrapped row stays on that row.</b> Clamping only to the
    ///     line's length would put the caret two rows down, on a character the pointer was nowhere
    ///     near.
    /// </summary>
    [Fact]
    public void A_click_past_the_end_of_a_wrapped_row_stays_on_it() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x");

        var columns = Columns(editor);
        var line = string.Join(' ', Enumerable.Repeat("alpha", 60));

        editor.Source = line;
        editor.WordWrap = true;
        fixture.Update();

        var start = editor.ToScreen(new TextPosition(0, 0));

        // Far to the right of the first row, which is shorter than the viewport because it broke at
        // a space.
        fixture.Press(start.X + (editor.CharacterWidth * (columns - 0.5f)), start.Y + (editor.RowHeight * 0.5f));
        fixture.Release(start.X + (editor.CharacterWidth * (columns - 0.5f)), start.Y + (editor.RowHeight * 0.5f));

        Assert.True(
            editor.Caret.Column < editor.RowStarts[1],
            $"the caret landed at {editor.Caret.Column}, past the row's break at {editor.RowStarts[1]}"
        );
    }

    /// <summary>
    ///     ⚠ <b>A wrapped row is coloured by the whole line's tokens, not by re-tokenizing from its
    ///     own start.</b> A string that crosses a break would otherwise come back plain on the second
    ///     row — the state a tokenizer carries along a line is exactly what a slice does not have.
    /// </summary>
    [Fact]
    public void A_string_that_crosses_a_wrap_is_still_a_string_on_the_second_row() {
        using var fixture = new AdvancedFixture();
        var editor = Editor(fixture, "x", CStyleTokenizer.CSharp);

        var columns = Columns(editor);

        editor.Source = "var s = \"" + new string('a', columns * 2) + "\";";
        editor.WordWrap = true;
        fixture.Update();

        Assert.True(editor.Rows.Count >= 2, $"expected the line to wrap, got {editor.Rows.Count} rows");

        var second = editor.Pool[1];

        Assert.Equal(0, second.Index);
        Assert.Equal(CodeTokenKind.String, second.Spans[0].Kind);

        // And the slice is exactly the row — every character once, which is the same property the
        // tokenizer's own coverage test asserts one level down.
        var from = editor.RowStarts[1];
        var to = editor.Rows.Count > 2 && editor.Rows[2] == 0 ? editor.RowStarts[2] : editor.Source.Length;

        Assert.Equal(
            editor.Source[from..to],
            string.Concat(second.Spans.Where(static span => !span.HasClass("parked")).Select(static span => span.Text))
        );
    }
}
