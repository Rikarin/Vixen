// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Cut, copy and paste in a text field, over a clipboard that is not the editor's.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What these asserted before the wire existed: nothing, because there was nothing to
///         ask.</b> <c>IClipboard</c> had real backends on macOS, Windows and Linux and no caller
///         above <c>Vixen.Platform</c> at all, so ⌘C in a Vixen text box put nothing anywhere — in
///         every application including the editor, whose own <c>PropertyClipboard</c> is an
///         in-process object store that never reaches the OS pasteboard.
///     </para>
///     <para>
///         <b>And no <c>Editor/</c> assembly is referenced here</b>, which is the point of the gate:
///         this is a <c>Vixen.Ui.Controls</c> test project, so anything passing is available to an
///         application that has only the control set.
///     </para>
/// </remarks>
public class ClipboardTests {
    /// <summary>A clipboard with no operating system behind it.</summary>
    /// <remarks>
    ///     ⚠ Counts its writes, because "the field put the right string somewhere" and "the field
    ///     wrote once" are two different claims and a cut that copied twice would pass the first.
    /// </remarks>
    sealed class FakeClipboard : IUiClipboard {
        public string? Text { get; set; }

        public int Writes { get; private set; }

        public bool Refuse { get; set; }

        public bool HasText => Text is { Length: > 0 };

        public bool TryGetText([NotNullWhen(true)] out string? text) {
            text = Text;

            return HasText;
        }

        public bool SetText(string text) {
            if (Refuse) {
                return false;
            }

            Text = text;
            Writes++;

            return true;
        }
    }

    static (ControlFixture Fixture, TextBox Field, FakeClipboard Clipboard) Field(string value) {
        var fixture = new ControlFixture();
        var clipboard = new FakeClipboard();

        fixture.Document.Clipboard = clipboard;

        var field = fixture.Add<TextBox>();
        field.Value = value;

        fixture.Document.Focus(field);
        fixture.Update();

        return (fixture, field, clipboard);
    }

    [Fact]
    public void A_document_with_no_head_has_no_clipboard_and_the_verbs_are_not_available() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Value = "abc";
        field.SelectAll();

        Assert.False(fixture.Document.HasClipboard);
        Assert.False(field.CanCopy);
        Assert.False(field.CanPaste);
        Assert.False(field.Copy());
        Assert.False(field.Cut());

        // ⚠ And the cut left the value alone. A cut that deleted the selection while failing to
        // write it anywhere is data loss, and is the obvious way to get this wrong.
        Assert.Equal("abc", field.Value);
    }

    [Fact]
    public void Copy_writes_the_selection_and_leaves_the_value_alone() {
        var (fixture, field, clipboard) = Field("Lovelace");
        using var _ = fixture;

        field.MoveCaret(0);
        field.MoveCaret(4, extend: true);

        fixture.Type(InputKey.C, ModifierKeys.Control);

        Assert.Equal("Love", clipboard.Text);
        Assert.Equal(1, clipboard.Writes);
        Assert.Equal("Lovelace", field.Value);
    }

    [Fact]
    public void Cut_writes_the_selection_and_removes_it() {
        var (fixture, field, clipboard) = Field("Lovelace");
        using var _ = fixture;

        field.MoveCaret(0);
        field.MoveCaret(4, extend: true);

        fixture.Type(InputKey.X, ModifierKeys.Control);

        Assert.Equal("Love", clipboard.Text);
        Assert.Equal(1, clipboard.Writes);
        Assert.Equal("lace", field.Value);
    }

    /// <summary>A cut the clipboard refused deletes nothing.</summary>
    /// <remarks>
    ///     ⚠ The one ordering that matters. Another application can own the pasteboard and refuse
    ///     the write; a field that erased first and wrote second would have thrown the text away
    ///     with nowhere to get it back from.
    /// </remarks>
    [Fact]
    public void A_refused_write_is_not_a_cut() {
        var (fixture, field, clipboard) = Field("Lovelace");
        using var _ = fixture;

        clipboard.Refuse = true;

        field.SelectAll();

        Assert.False(field.Cut());
        Assert.Equal("Lovelace", field.Value);
    }

    [Fact]
    public void Paste_replaces_the_selection() {
        var (fixture, field, clipboard) = Field("Lovelace");
        using var _ = fixture;

        clipboard.Text = "Byron";

        field.MoveCaret(0);
        field.MoveCaret(4, extend: true);

        fixture.Type(InputKey.V, ModifierKeys.Control);

        Assert.Equal("Byronlace", field.Value);
        Assert.Equal(5, field.CaretIndex);
    }

    /// <summary>A single-line field takes a multi-line paste as one line.</summary>
    /// <remarks>
    ///     ⚠ Spaces rather than nothing: dropping the breaks welds the last word of one line to the
    ///     first of the next, which reads as a truncation bug in whatever reads the field back.
    /// </remarks>
    [Fact]
    public void A_paste_of_two_lines_into_a_one_line_field_is_flattened() {
        var (fixture, field, clipboard) = Field(string.Empty);
        using var _ = fixture;

        clipboard.Text = "Ada\r\nLovelace";

        Assert.True(field.Paste());
        Assert.Equal("Ada Lovelace", field.Value);
    }

    [Fact]
    public void A_text_area_keeps_the_lines_and_normalises_the_carriage_returns() {
        using var fixture = new ControlFixture();

        var clipboard = new FakeClipboard { Text = "Ada\r\nLovelace" };
        fixture.Document.Clipboard = clipboard;

        var area = fixture.Add<TextArea>();
        fixture.Document.Focus(area);

        Assert.True(area.Paste());
        Assert.Equal("Ada\nLovelace", area.Value);
    }

    /// <summary>A read-only field copies and refuses to cut or paste.</summary>
    [Fact]
    public void A_read_only_field_copies_but_does_not_cut() {
        var (fixture, field, clipboard) = Field("Lovelace");
        using var _ = fixture;

        field.ReadOnly = true;
        field.SelectAll();

        Assert.True(field.Copy());
        Assert.Equal("Lovelace", clipboard.Text);

        Assert.False(field.Cut());
        Assert.Equal("Lovelace", field.Value);

        clipboard.Text = "Byron";

        Assert.False(field.CanPaste);
        Assert.False(field.Paste());
        Assert.Equal("Lovelace", field.Value);
    }

    /// <summary>⌘C with nothing selected is not handled, so whatever else wanted it still gets it.</summary>
    /// <remarks>
    ///     ⚠ <b>The bug a naive wiring introduces.</b> A field that marked the chord handled
    ///     regardless would silently eat an application's own Copy for as long as a text box had the
    ///     focus — and an empty search box has the focus most of the time.
    /// </remarks>
    [Fact]
    public void An_empty_selection_leaves_the_chord_for_an_ancestor() {
        var (fixture, field, _) = Field("Lovelace");
        using var _f = fixture;

        var seen = 0;
        fixture.Document.Root.AddHandler<KeyEvent>((_, args) => {
            if (args is { Key: InputKey.C, Action: KeyAction.Pressed }) {
                seen++;
            }
        });

        field.MoveCaret(3);

        fixture.Type(InputKey.C, ModifierKeys.Control);

        Assert.Equal(1, seen);

        // And with a selection the field takes it, so the ancestor's handler is the only difference
        // between the two halves.
        field.MoveCaret(0);
        field.MoveCaret(4, extend: true);

        fixture.Type(InputKey.C, ModifierKeys.Control);

        Assert.Equal(1, seen);
    }

    /// <summary>The four verbs answer as command ids, not only as chords.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes a menu item work.</b> A <c>MenuItem</c> bound to <c>edit.copy</c>
    ///     resolves through <c>CommandRoute</c> against the focused element and greys itself out
    ///     when nothing answers — so a field that had only a key switch would draw "⌘C" beside a
    ///     permanently disabled item.
    /// </remarks>
    [Fact]
    public void The_field_answers_the_edit_verbs_through_the_command_route() {
        var (fixture, field, clipboard) = Field("Lovelace");
        using var _ = fixture;

        Assert.False(CommandRoute.CanExecute(fixture.Document, "edit.copy"));

        Assert.True(CommandRoute.Execute(fixture.Document, "edit.select-all"));
        Assert.True(CommandRoute.CanExecute(fixture.Document, "edit.copy"));
        Assert.True(CommandRoute.Execute(fixture.Document, "edit.copy"));

        Assert.Equal("Lovelace", clipboard.Text);

        Assert.True(CommandRoute.Execute(fixture.Document, "edit.cut"));
        Assert.Equal(string.Empty, field.Value);

        Assert.True(CommandRoute.CanExecute(fixture.Document, "edit.paste"));
        Assert.True(CommandRoute.Execute(fixture.Document, "edit.paste"));
        Assert.Equal("Lovelace", field.Value);
    }
}
