// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>⌘Z in a text field, and the manager it has to find first.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What these asserted before: nothing, because <c>git grep "IUndo\|UndoManager"</c>
///         returned no hits anywhere in the repository.</b> <c>CodeBuffer</c>'s remarks argue
///         correctly that a text control must not own an undo stack, and the argument stops one step
///         short of saying where the control should <i>look</i> — so a dialog's text box had no ⌘Z in
///         any Vixen application, the editor included.
///     </para>
///     <para>
///         ⚠ <b>And "no manager" is a case with its own tests</b>, because the interesting failure is
///         not that undo does nothing — it is a field that <i>consumes</i> ⌘Z while doing nothing,
///         which would break the editor's own Undo for as long as any text box had the focus.
///     </para>
/// </remarks>
public class UndoTests {
    static (ControlFixture Fixture, TextBox Field, UndoManager Undo) Field(string value = "") {
        var fixture = new ControlFixture();
        var undo = new UndoManager();

        fixture.Document.UndoManager = undo;

        var field = fixture.Add<TextBox>();
        field.Value = value;

        fixture.Document.Focus(field);
        fixture.Update();

        return (fixture, field, undo);
    }

    [Fact]
    public void A_field_with_no_manager_undoes_nothing_and_keeps_the_chord_for_an_ancestor() {
        using var fixture = new ControlFixture();

        var seen = 0;
        fixture.Document.Root.AddHandler<KeyEvent>((_, args) => {
            if (args is { Key: InputKey.Z, Action: KeyAction.Pressed }) {
                seen++;
            }
        });

        var field = fixture.Add<TextBox>();
        fixture.Document.Focus(field);

        fixture.TypeText("abc");

        Assert.Null(field.FindUndoManager());
        Assert.False(field.Undo());

        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal("abc", field.Value);
        Assert.Equal(1, seen);
    }

    /// <summary>A run of typing is one undo, not one per character.</summary>
    /// <remarks>
    ///     ⚠ <b>Coalesced by shape, not by a clock.</b> A wall-clock typing window calibrated on an
    ///     idle machine is this repository's largest flake source; what makes two keystrokes one edit
    ///     here is that the second inserted where the first ended with nothing selected.
    /// </remarks>
    [Fact]
    public void A_run_of_typing_is_one_undo() {
        var (fixture, field, undo) = Field();
        using var _ = fixture;

        fixture.TypeText("a");
        fixture.TypeText("b");
        fixture.TypeText("c");

        Assert.Equal("abc", field.Value);

        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal(string.Empty, field.Value);
        Assert.False(undo.CanUndo);
    }

    /// <summary>Moving the caret ends the run, so the next typing is a second entry.</summary>
    /// <remarks>
    ///     ⚠ Without this, typing a word, clicking elsewhere and typing another is one ⌘Z that takes
    ///     back two edits in two places.
    /// </remarks>
    [Fact]
    public void A_caret_move_starts_a_new_undo_entry() {
        var (fixture, field, _) = Field();
        using var _f = fixture;

        fixture.TypeText("one");
        field.MoveCaret(0);
        fixture.TypeText("two");

        Assert.Equal("twoone", field.Value);

        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal("one", field.Value);

        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal(string.Empty, field.Value);
    }

    /// <summary>A delete is its own entry, never merged into the typing before it.</summary>
    [Fact]
    public void A_delete_is_not_merged_into_the_typing_before_it() {
        var (fixture, field, _) = Field();
        using var _f = fixture;

        fixture.TypeText("abc");
        fixture.Type(InputKey.Backspace);

        Assert.Equal("ab", field.Value);

        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal("abc", field.Value);
    }

    /// <summary>Undo restores the selection, not only the string.</summary>
    /// <remarks>
    ///     ⚠ An undo of a cut that leaves the user to re-select what came back is an undo that only
    ///     half happened.
    /// </remarks>
    [Fact]
    public void Undo_puts_the_selection_back() {
        var (fixture, field, _) = Field("Lovelace");
        using var _f = fixture;

        field.MoveCaret(0);
        field.MoveCaret(4, extend: true);

        field.Replace(string.Empty);

        Assert.Equal("lace", field.Value);

        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal("Lovelace", field.Value);
        Assert.Equal(0, field.SelectionStart);
        Assert.Equal(4, field.SelectionEnd);
    }

    [Fact]
    public void Redo_puts_the_edit_back() {
        var (fixture, field, undo) = Field();
        using var _ = fixture;

        fixture.TypeText("abc");

        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal(string.Empty, field.Value);
        Assert.True(undo.CanRedo);

        fixture.Type(InputKey.Y, ModifierKeys.Control);

        Assert.Equal("abc", field.Value);
    }

    /// <summary>Two undos go further into the past rather than back and forth.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a test of <see cref="IUndoManager.IsPerforming" />, and it was labelled as one
    ///     until the sabotage came back green.</b> Removing that guard on both sides changed nothing
    ///     here, because <c>TextField.Restore</c> assigns <c>Value</c> directly instead of going
    ///     through <c>Replace</c>, so nothing re-enters the recorder during an undo at all. What is
    ///     asserted is the property a user has — the second ⌘Z is another step back — and the guard
    ///     has its own test in <see cref="A_registration_made_while_undoing_is_ignored" />, where it
    ///     can actually be broken.
    /// </remarks>
    [Fact]
    public void Undoing_twice_goes_further_back_rather_than_forwards() {
        var (fixture, field, _) = Field();
        using var _f = fixture;

        fixture.TypeText("one");
        field.MoveCaret(3);
        fixture.TypeText("two");

        Assert.Equal("onetwo", field.Value);

        fixture.Type(InputKey.Z, ModifierKeys.Control);
        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.Equal(string.Empty, field.Value);
    }

    /// <summary>An edit registered from inside an undo is dropped.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap every closure-based undo stack falls into once.</b> Undoing re-runs
    ///     the code that made the edit, so a registrant that did not check would record the undo as a
    ///     new edit and the second ⌘Z would put the text back rather than going further into the
    ///     past. The guard is in the manager rather than in every caller, which is why this is
    ///     asserted against <see cref="UndoManager" /> directly and not through a control — a control
    ///     cannot break it, because <c>TextField.Restore</c> never re-enters the recorder.
    /// </remarks>
    [Fact]
    public void A_registration_made_while_undoing_is_ignored() {
        var undo = new UndoManager();
        var log = new List<string>();

        undo.Register(
            "first",
            () => {
                log.Add("undo-first");

                // What a careless registrant does: the undo re-runs its edit path, which registers.
                undo.Register("echo", () => log.Add("undo-echo"), () => { });
            },
            () => { }
        );

        Assert.True(undo.Undo());
        Assert.False(undo.CanUndo);
        Assert.Equal(["undo-first"], log);
    }

    /// <summary>A nearer manager wins over the document's.</summary>
    /// <remarks>
    ///     AppKit's <c>NSResponder.undoManager</c>: the view that owns a document object supplies
    ///     that document's stack, and everything inside it registers there rather than with the
    ///     application's.
    /// </remarks>
    [Fact]
    public void The_nearest_manager_on_the_chain_is_the_one_that_gets_the_edit() {
        var (fixture, _, document) = Field();
        using var _f = fixture;

        var panel = fixture.Document.Root.Add("div");
        var nearer = new UndoManager();
        panel.UndoManager = nearer;

        var inner = panel.Add<TextBox>();
        fixture.Update();
        fixture.Document.Focus(inner);

        fixture.TypeText("abc");

        Assert.Same(nearer, inner.FindUndoManager());
        Assert.True(nearer.CanUndo);
        Assert.False(document.CanUndo);
    }

    /// <summary>A new edit ends the redo future.</summary>
    [Fact]
    public void An_edit_after_an_undo_drops_what_was_undone() {
        var (fixture, field, undo) = Field();
        using var _ = fixture;

        fixture.TypeText("abc");
        fixture.Type(InputKey.Z, ModifierKeys.Control);

        Assert.True(undo.CanRedo);

        fixture.TypeText("x");

        Assert.False(undo.CanRedo);
        Assert.Equal("x", field.Value);
    }

    /// <summary>The stack is bounded, and the bound drops the oldest.</summary>
    /// <remarks>
    ///     ⚠ Expressed as a count of registrations rather than as a memory figure, which is the only
    ///     half a test can hold. The reason for the bound is the other half: an edit is two closures
    ///     over the strings either side of it.
    /// </remarks>
    [Fact]
    public void The_stack_forgets_the_oldest_edit_past_its_limit() {
        var undo = new UndoManager { Limit = 2 };
        var log = new List<int>();

        for (var i = 0; i < 4; i++) {
            var index = i;
            undo.Register("edit", () => log.Add(index), () => { });
        }

        while (undo.Undo()) { }

        Assert.Equal([3, 2], log);
    }
}
