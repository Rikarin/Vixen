// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The code editor's half of the pasteboard wire.</summary>
/// <remarks>
///     ⚠ <b>Two controls, so two wires, so two tests.</b> The pattern this repository meets most
///     often is a capability added to one of a pair and silently absent from the other — the game
///     renderer and the editor's, the framework host and the editor's host. A code editor whose ⌘C
///     did nothing while a text box's worked would be that shape again, and no test of
///     <c>TextField</c> can see it.
/// </remarks>
public class CodeEditorClipboardTests {
    sealed class FakeClipboard : IUiClipboard {
        public string? Text { get; set; }

        public bool HasText => Text is { Length: > 0 };

        public bool TryGetText([NotNullWhen(true)] out string? text) {
            text = Text;

            return HasText;
        }

        public bool SetText(string text) {
            Text = text;

            return true;
        }
    }

    static (CodeEditor Editor, FakeClipboard Clipboard) Editor(AdvancedFixture fixture, string source) {
        var clipboard = new FakeClipboard();
        fixture.Document.Clipboard = clipboard;

        var editor = fixture.Add<CodeEditor>();
        editor.Source = source;

        fixture.Update();
        editor.Refresh();
        fixture.Update();

        fixture.Document.Focus(editor);

        return (editor, clipboard);
    }

    [Fact]
    public void Copy_takes_the_selection_across_lines_with_its_newlines() {
        using var fixture = new AdvancedFixture();

        var (editor, clipboard) = Editor(fixture, "one\ntwo\nthree");

        editor.Move(new TextPosition(0, 1));
        editor.Move(new TextPosition(2, 2), extend: true);

        fixture.Type(InputKey.C, ModifierKeys.Control);

        Assert.Equal("ne\ntwo\nth", clipboard.Text);
        Assert.Equal("one\ntwo\nthree", editor.Source);
    }

    [Fact]
    public void Cut_removes_what_it_wrote() {
        using var fixture = new AdvancedFixture();

        var (editor, clipboard) = Editor(fixture, "one\ntwo\nthree");

        editor.Move(new TextPosition(0, 1));
        editor.Move(new TextPosition(2, 2), extend: true);

        fixture.Type(InputKey.X, ModifierKeys.Control);

        Assert.Equal("ne\ntwo\nth", clipboard.Text);
        Assert.Equal("oree", editor.Source);
    }

    /// <summary>A paste keeps its line breaks, and loses the carriage returns that came with them.</summary>
    /// <remarks>
    ///     ⚠ A \r left in the buffer is invisible on screen and survives a save, so the file grows a
    ///     stray carriage return per line that only whatever reads it back can see.
    /// </remarks>
    [Fact]
    public void Paste_keeps_the_lines_and_normalises_the_carriage_returns() {
        using var fixture = new AdvancedFixture();

        var (editor, clipboard) = Editor(fixture, string.Empty);

        clipboard.Text = "one\r\ntwo";

        fixture.Type(InputKey.V, ModifierKeys.Control);

        Assert.Equal("one\ntwo", editor.Source);
        Assert.Equal(2, editor.Buffer.LineCount);
    }

    [Fact]
    public void A_read_only_editor_copies_and_neither_cuts_nor_pastes() {
        using var fixture = new AdvancedFixture();

        var (editor, clipboard) = Editor(fixture, "one\ntwo");

        editor.ReadOnly = true;
        editor.SelectAll();

        Assert.True(editor.Copy());
        Assert.Equal("one\ntwo", clipboard.Text);

        Assert.False(editor.Cut());
        Assert.False(editor.CanPaste);
        Assert.Equal("one\ntwo", editor.Source);
    }

    [Fact]
    public void The_editor_answers_the_edit_verbs_through_the_command_route() {
        using var fixture = new AdvancedFixture();

        var (editor, clipboard) = Editor(fixture, "one\ntwo");

        Assert.False(CommandRoute.CanExecute(fixture.Document, "edit.copy"));

        Assert.True(CommandRoute.Execute(fixture.Document, "edit.select-all"));
        Assert.True(CommandRoute.Execute(fixture.Document, "edit.cut"));

        Assert.Equal("one\ntwo", clipboard.Text);
        Assert.Equal(string.Empty, editor.Source);

        Assert.True(CommandRoute.Execute(fixture.Document, "edit.paste"));
        Assert.Equal("one\ntwo", editor.Source);
    }
}
