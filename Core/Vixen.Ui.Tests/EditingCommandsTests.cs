// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The chord tables: what each platform says, and what neither is allowed to say.</summary>
/// <remarks>
///     ⚠ <b>Driven against both tables by name, never against the machine.</b>
///     <see cref="EditingCommands.Current" /> is the platform's and a suite that read it would assert
///     one keyboard on a Mac and another in CI — the flake whose cause is in neither the diff nor the
///     test. Every case here names the keymap it is about, so both halves run everywhere.
/// </remarks>
public class EditingCommandsTests {
    static EditingCommand Windows(InputKey key, ModifierKeys modifiers = ModifierKeys.None) =>
        EditingCommands.Resolve(key, modifiers, EditingKeymap.Windows);

    static EditingCommand MacOs(InputKey key, ModifierKeys modifiers = ModifierKeys.None) =>
        EditingCommands.Resolve(key, modifiers, EditingKeymap.MacOs);

    /// <summary>Each platform's own word-motion chord means the same verb.</summary>
    /// <remarks>
    ///     The gate the whole type exists for: two chords, one id, so a control writes one handler.
    /// </remarks>
    [Fact]
    public void The_platform_chord_for_a_word_produces_the_same_command_on_both() {
        Assert.Equal(EditingCommand.MoveWordLeft, Windows(InputKey.Left, ModifierKeys.Control));
        Assert.Equal(EditingCommand.MoveWordLeft, MacOs(InputKey.Left, ModifierKeys.Alt));

        Assert.Equal(EditingCommand.MoveWordRight, Windows(InputKey.Right, ModifierKeys.Control));
        Assert.Equal(EditingCommand.MoveWordRight, MacOs(InputKey.Right, ModifierKeys.Alt));

        Assert.Equal(EditingCommand.SelectAll, Windows(InputKey.A, ModifierKeys.Control));
        Assert.Equal(EditingCommand.SelectAll, MacOs(InputKey.A, ModifierKeys.Meta));

        Assert.Equal(EditingCommand.Copy, Windows(InputKey.C, ModifierKeys.Control));
        Assert.Equal(EditingCommand.Copy, MacOs(InputKey.C, ModifierKeys.Meta));
    }

    /// <summary>⌃A cannot be Select All and the start of the line at once, which is why there are two tables.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim that killed the old <c>Control || Meta</c>.</b> A single lenient table has
    ///     to pick one reading of this chord, and picking Windows' while calling it platform-neutral
    ///     is what both controls were doing.
    /// </remarks>
    [Fact]
    public void Control_A_is_select_all_on_Windows_and_the_line_start_on_a_Mac() {
        Assert.Equal(EditingCommand.SelectAll, Windows(InputKey.A, ModifierKeys.Control));
        Assert.Equal(EditingCommand.MoveLineStart, MacOs(InputKey.A, ModifierKeys.Control));
    }

    /// <summary>The AppKit emacs bindings, which were absent from both controls entirely.</summary>
    [Fact]
    public void The_Mac_table_carries_the_emacs_bindings() {
        Assert.Equal(EditingCommand.MoveLineStart, MacOs(InputKey.A, ModifierKeys.Control));
        Assert.Equal(EditingCommand.MoveLineEnd, MacOs(InputKey.E, ModifierKeys.Control));
        Assert.Equal(EditingCommand.MoveLeft, MacOs(InputKey.B, ModifierKeys.Control));
        Assert.Equal(EditingCommand.MoveRight, MacOs(InputKey.F, ModifierKeys.Control));
        Assert.Equal(EditingCommand.DeleteForward, MacOs(InputKey.D, ModifierKeys.Control));
        Assert.Equal(EditingCommand.DeleteToLineEnd, MacOs(InputKey.K, ModifierKeys.Control));

        // And none of them means anything on Windows, where Ctrl-E and Ctrl-K belong to the
        // application.
        Assert.Equal(EditingCommand.None, Windows(InputKey.E, ModifierKeys.Control));
        Assert.Equal(EditingCommand.None, Windows(InputKey.K, ModifierKeys.Control));
        Assert.Equal(EditingCommand.None, Windows(InputKey.B, ModifierKeys.Control));
    }

    /// <summary>⌘← is the start of the line, which is the divergence #648 was filed for.</summary>
    /// <remarks>
    ///     ⚠ <b>The issue said ⌘← "does nothing" in the code editor and that was not quite it.</b>
    ///     <c>CodeEditor</c>'s <c>word</c> was <c>Control</c> only, so ⌘← fell through to the plain
    ///     <c>Left</c> case and moved the caret <i>one character</i> — a chord that silently does the
    ///     wrong thing rather than nothing, which is the harder one to notice.
    /// </remarks>
    [Fact]
    public void Meta_left_is_the_line_start_on_a_Mac_and_nothing_on_Windows() {
        Assert.Equal(EditingCommand.MoveLineStart, MacOs(InputKey.Left, ModifierKeys.Meta));
        Assert.Equal(EditingCommand.None, Windows(InputKey.Left, ModifierKeys.Meta));
    }

    /// <summary>Shift says how, not what.</summary>
    [Fact]
    public void Shift_does_not_change_which_command_a_chord_is() {
        foreach (var keymap in (ReadOnlySpan<EditingKeymap>) [EditingKeymap.Windows, EditingKeymap.MacOs]) {
            foreach (var key in (ReadOnlySpan<InputKey>) [InputKey.Left, InputKey.Right, InputKey.Home, InputKey.Up]) {
                foreach (var modifiers in (ReadOnlySpan<ModifierKeys>) [
                    ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Meta
                ]) {
                    Assert.Equal(
                        EditingCommands.Resolve(key, modifiers, keymap),
                        EditingCommands.Resolve(key, modifiers | ModifierKeys.Shift, keymap)
                    );
                }
            }
        }
    }

    /// <summary>Shift is dropped only when the table does not name it.</summary>
    /// <remarks>
    ///     ⚠ <b>The one exception, and it is macOS's only spelling of Redo.</b> Shift says <i>how</i>
    ///     for every motion, so dropping it is right almost everywhere — but ⌘⇧Z has to be reachable,
    ///     and splitting the whole vocabulary into extending and non-extending halves to say one bit
    ///     would be a table twice the size for one chord. The exact chord is tried first and the
    ///     Shift-stripped one after, so a table names Shift where it means something and ignores it
    ///     elsewhere.
    /// </remarks>
    [Fact]
    public void Redo_is_the_chord_that_needs_Shift_kept() {
        Assert.Equal(EditingCommand.Undo, MacOs(InputKey.Z, ModifierKeys.Meta));
        Assert.Equal(EditingCommand.Redo, MacOs(InputKey.Z, ModifierKeys.Meta | ModifierKeys.Shift));

        Assert.Equal(EditingCommand.Undo, Windows(InputKey.Z, ModifierKeys.Control));
        Assert.Equal(EditingCommand.Redo, Windows(InputKey.Z, ModifierKeys.Control | ModifierKeys.Shift));

        // Windows' own Redo as well, because every editor on it answers both and leaving one out is
        // a chord that silently undoes instead.
        Assert.Equal(EditingCommand.Redo, Windows(InputKey.Y, ModifierKeys.Control));

        // And the rule still holds for everything the table does not name with Shift.
        Assert.Equal(
            EditingCommand.MoveWordLeft,
            Windows(InputKey.Left, ModifierKeys.Control | ModifierKeys.Shift)
        );
    }

    /// <summary>Every other modifier has to match exactly.</summary>
    /// <remarks>
    ///     ⚠ <b>The switches this replaces used <c>HasFlag</c></b>, so ⌃⌥← was word motion — and it
    ///     is a window-management chord on two of the three desktops. Exactness is also what lets the
    ///     Mac table give ⌥← and ⌘← two different readings at all.
    /// </remarks>
    [Fact]
    public void An_extra_modifier_is_a_different_chord() {
        Assert.Equal(EditingCommand.MoveWordLeft, Windows(InputKey.Left, ModifierKeys.Control));
        Assert.Equal(EditingCommand.None, Windows(InputKey.Left, ModifierKeys.Control | ModifierKeys.Alt));

        Assert.Equal(EditingCommand.MoveWordLeft, MacOs(InputKey.Left, ModifierKeys.Alt));
        Assert.Equal(EditingCommand.None, MacOs(InputKey.Left, ModifierKeys.Alt | ModifierKeys.Meta));
    }

    /// <summary>No verb in the vocabulary is unreachable, and none is unnamed.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument check.</b> A command nothing produces is a handler nobody can reach —
    ///     this repository's commonest defect, wearing a keyboard. <c>Outdent</c> was in the enum for
    ///     exactly as long as it took to write this: Shift is stripped before the lookup, so Shift-Tab
    ///     is <see cref="EditingCommand.InsertTab" /> and the control reads the bit.
    /// </remarks>
    [Fact]
    public void Every_command_is_produced_by_some_chord_and_has_an_id() {
        var reached = new HashSet<EditingCommand>();

        foreach (var keymap in (ReadOnlySpan<EditingKeymap>) [EditingKeymap.Windows, EditingKeymap.MacOs]) {
            foreach (var key in Enum.GetValues<InputKey>()) {
                foreach (var modifiers in (ReadOnlySpan<ModifierKeys>) [
                    ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Meta
                ]) {
                    reached.Add(EditingCommands.Resolve(key, modifiers, keymap));
                }
            }
        }

        foreach (var command in Enum.GetValues<EditingCommand>()) {
            if (command == EditingCommand.None) {
                Assert.Null(EditingCommands.Id(command));
                continue;
            }

            Assert.True(reached.Contains(command), $"No chord in either keymap produces {command}.");
            Assert.False(string.IsNullOrEmpty(EditingCommands.Id(command)), $"{command} has no id.");
        }
    }

    /// <summary>Two commands never share an id.</summary>
    [Fact]
    public void The_ids_are_distinct() {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in Enum.GetValues<EditingCommand>()) {
            if (EditingCommands.Id(command) is { } id) {
                Assert.True(ids.Add(id), $"{id} is claimed twice.");
            }
        }
    }

    /// <summary>The three clipboard verbs answer to the ids a menu already binds.</summary>
    /// <remarks>
    ///     ⚠ They are <c>edit.*</c> rather than <c>text.*</c> because an outliner and a node graph
    ///     answer them too; the motions are <c>text.*</c> because no other responder has a reading of
    ///     "move to the previous word boundary".
    /// </remarks>
    [Fact]
    public void The_clipboard_verbs_keep_the_application_wide_ids() {
        Assert.Equal("edit.cut", EditingCommands.Id(EditingCommand.Cut));
        Assert.Equal("edit.copy", EditingCommands.Id(EditingCommand.Copy));
        Assert.Equal("edit.paste", EditingCommands.Id(EditingCommand.Paste));
        Assert.Equal("edit.select-all", EditingCommands.Id(EditingCommand.SelectAll));
        Assert.Equal("text.move-word-left", EditingCommands.Id(EditingCommand.MoveWordLeft));
    }

    /// <summary>A fresh document takes the platform's table.</summary>
    [Fact]
    public void A_document_starts_on_the_platforms_keymap_and_can_be_told_otherwise() {
        using var document = new UiDocument(100f, 100f);

        Assert.Equal(EditingCommands.Current, document.EditingKeymap);

        document.EditingKeymap = EditingKeymap.MacOs;

        Assert.Equal(EditingKeymap.MacOs, document.EditingKeymap);
    }
}
