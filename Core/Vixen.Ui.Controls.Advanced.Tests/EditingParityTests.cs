// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The same verb, from each platform's own chord, answered the same way by both controls.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the only project that can ask.</b> <c>TextField</c> is in
///         <c>Vixen.Ui.Controls</c> and <c>CodeEditor</c> in <c>Vixen.Ui.Controls.Advanced</c>, so no
///         test of either assembly alone could see that the two keyboards had drifted apart — and
///         they had: the field took <c>Control || Meta</c> and the editor <c>Control</c> only, so ⌘←
///         moved by a word in one and by a single character in the other.
///     </para>
///     <para>
///         ⚠ <b>Both keymaps are named, never inferred.</b> A suite that read
///         <c>EditingCommands.Current</c> would test one keyboard on a Mac and the other in CI.
///     </para>
/// </remarks>
public class EditingParityTests {
    const string Line = "one two three";

    static TextArea Field(AdvancedFixture fixture, EditingKeymap keymap) {
        fixture.Document.EditingKeymap = keymap;

        var field = fixture.Add<TextArea>();
        field.Value = Line;

        fixture.Update();
        fixture.Document.Focus(field);

        return field;
    }

    static CodeEditor Editor(AdvancedFixture fixture, EditingKeymap keymap) {
        fixture.Document.EditingKeymap = keymap;

        var editor = fixture.Add<CodeEditor>();
        editor.Source = Line;

        fixture.Update();
        editor.Refresh();
        fixture.Update();

        fixture.Document.Focus(editor);

        return editor;
    }

    /// <summary>Word-left, from Ctrl-Left on Windows and ⌥← on a Mac, in both controls.</summary>
    /// <remarks>
    ///     ⚠ Four assertions of the same number. <c>"one two three"</c> is chosen because the two
    ///     controls break words by different rules — the field by UAX #29, the buffer by runs of
    ///     letters-and-digits — and ASCII words separated by spaces is where those two agree. Where
    ///     they do not is a separate question and a separate issue.
    /// </remarks>
    [Theory]
    [InlineData(EditingKeymap.Windows, ModifierKeys.Control)]
    [InlineData(EditingKeymap.MacOs, ModifierKeys.Alt)]
    public void Word_left_moves_to_the_same_boundary_in_both_controls(EditingKeymap keymap, ModifierKeys word) {
        using (var fixture = new AdvancedFixture()) {
            var field = Field(fixture, keymap);
            field.MoveCaret(Line.Length);

            fixture.Type(InputKey.Left, word);

            Assert.Equal(8, field.CaretIndex);
        }

        using (var fixture = new AdvancedFixture()) {
            var editor = Editor(fixture, keymap);
            editor.Move(new TextPosition(0, Line.Length));

            fixture.Type(InputKey.Left, word);

            Assert.Equal(new TextPosition(0, 8), editor.Caret);
        }
    }

    /// <summary>Select All, from each platform's chord, in both controls.</summary>
    [Theory]
    [InlineData(EditingKeymap.Windows, ModifierKeys.Control)]
    [InlineData(EditingKeymap.MacOs, ModifierKeys.Meta)]
    public void Select_all_answers_the_platform_chord_in_both_controls(EditingKeymap keymap, ModifierKeys verb) {
        using (var fixture = new AdvancedFixture()) {
            var field = Field(fixture, keymap);
            field.MoveCaret(0);

            fixture.Type(InputKey.A, verb);

            Assert.Equal(Line, field.SelectedText);
        }

        using (var fixture = new AdvancedFixture()) {
            var editor = Editor(fixture, keymap);

            fixture.Type(InputKey.A, verb);

            Assert.Equal(Line, editor.SelectedText);
        }
    }

    /// <summary>⌘← reaches the start of the line in the code editor, which it did not before.</summary>
    /// <remarks>
    ///     ⚠ <b>The concrete defect #648 named, and its description was slightly wrong.</b> The
    ///     editor's <c>word</c> was <c>Control</c> only, so ⌘← did not do <i>nothing</i> — it fell
    ///     through to the plain <c>Left</c> case and moved one character. A chord that quietly does
    ///     the wrong thing is the harder one to notice, and asserting on "did not move" would have
    ///     been green against the bug.
    /// </remarks>
    [Fact]
    public void Meta_left_is_the_line_start_in_the_code_editor_on_a_Mac() {
        using var fixture = new AdvancedFixture();

        var editor = Editor(fixture, EditingKeymap.MacOs);
        editor.Move(new TextPosition(0, Line.Length));

        fixture.Type(InputKey.Left, ModifierKeys.Meta);

        Assert.Equal(new TextPosition(0, 0), editor.Caret);
    }

    /// <summary>⌃A on a Mac is the start of the line, in both controls, and not Select All.</summary>
    [Fact]
    public void Control_A_on_a_Mac_moves_rather_than_selecting() {
        using (var fixture = new AdvancedFixture()) {
            var field = Field(fixture, EditingKeymap.MacOs);
            field.MoveCaret(Line.Length);

            fixture.Type(InputKey.A, ModifierKeys.Control);

            Assert.Equal(0, field.CaretIndex);
            Assert.False(field.HasSelection);
        }

        using (var fixture = new AdvancedFixture()) {
            var editor = Editor(fixture, EditingKeymap.MacOs);
            editor.Move(new TextPosition(0, Line.Length));

            fixture.Type(InputKey.A, ModifierKeys.Control);

            Assert.Equal(new TextPosition(0, 0), editor.Caret);
            Assert.False(editor.HasSelection);
        }
    }

    /// <summary>⌃K cuts the rest of the line, in both controls.</summary>
    [Fact]
    public void Control_K_on_a_Mac_deletes_to_the_end_of_the_line() {
        using (var fixture = new AdvancedFixture()) {
            var field = Field(fixture, EditingKeymap.MacOs);
            field.MoveCaret(4);

            fixture.Type(InputKey.K, ModifierKeys.Control);

            Assert.Equal("one ", field.Value);
        }

        using (var fixture = new AdvancedFixture()) {
            var editor = Editor(fixture, EditingKeymap.MacOs);
            editor.Move(new TextPosition(0, 4));

            fixture.Type(InputKey.K, ModifierKeys.Control);

            Assert.Equal("one ", editor.Source);
        }
    }

    /// <summary>Delete-by-word, from each platform's chord, in both controls.</summary>
    [Theory]
    [InlineData(EditingKeymap.Windows, ModifierKeys.Control)]
    [InlineData(EditingKeymap.MacOs, ModifierKeys.Alt)]
    public void Backspace_by_word_takes_the_word_in_both_controls(EditingKeymap keymap, ModifierKeys word) {
        using (var fixture = new AdvancedFixture()) {
            var field = Field(fixture, keymap);
            field.MoveCaret(Line.Length);

            fixture.Type(InputKey.Backspace, word);

            Assert.Equal("one two ", field.Value);
        }

        using (var fixture = new AdvancedFixture()) {
            var editor = Editor(fixture, keymap);
            editor.Move(new TextPosition(0, Line.Length));

            fixture.Type(InputKey.Backspace, word);

            Assert.Equal("one two ", editor.Source);
        }
    }

    /// <summary>A selection wins over the word beyond it.</summary>
    /// <remarks>
    ///     ⚠ Every desktop deletes the selection rather than reaching past it, and a control that
    ///     reached would delete text the user could see was not highlighted.
    /// </remarks>
    [Fact]
    public void Delete_by_word_over_a_selection_deletes_the_selection() {
        using var fixture = new AdvancedFixture();

        var field = Field(fixture, EditingKeymap.Windows);
        field.MoveCaret(0);
        field.MoveCaret(3, extend: true);

        fixture.Type(InputKey.Backspace, ModifierKeys.Control);

        Assert.Equal(" two three", field.Value);
    }

    /// <summary>A chord neither table knows is left for whatever else was listening.</summary>
    /// <remarks>
    ///     ⚠ <b>What each switch printed for a key it had no row for, which is the thing to check
    ///     before replacing one.</b> Both ended in <c>default: return;</c> — no <c>Handled</c>, so
    ///     the event kept climbing. Replacing them with a table that fell through to
    ///     <c>args.Handled = true</c> would have made a text control eat every unbound shortcut in
    ///     the application, silently.
    /// </remarks>
    [Fact]
    public void An_unmapped_chord_is_not_handled_by_either_control() {
        using var fixture = new AdvancedFixture();

        fixture.Document.EditingKeymap = EditingKeymap.Windows;

        var seen = 0;
        fixture.Document.Root.AddHandler<KeyEvent>((_, args) => {
            if (args is { Key: InputKey.G, Action: KeyAction.Pressed }) {
                seen++;
            }
        });

        var field = fixture.Add<TextArea>();
        field.Value = Line;
        fixture.Update();
        fixture.Document.Focus(field);

        fixture.Type(InputKey.G, ModifierKeys.Control);

        var editor = fixture.Add<CodeEditor>();
        editor.Source = Line;
        fixture.Update();
        editor.Refresh();
        fixture.Update();
        fixture.Document.Focus(editor);

        fixture.Type(InputKey.G, ModifierKeys.Control);

        Assert.Equal(2, seen);
    }
}
