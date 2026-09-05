// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>The editor's own stack, seen through the interface a <c>Vixen.Ui</c> control finds.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A text field anywhere in the editor had no ⌘Z, and it was behaving correctly.</b>
///         <c>TextField</c> records each edit with whatever <c>UiElement.FindUndoManager</c> hands
///         back and leaves the keystroke alone when that is nothing — which is right for a throwaway
///         field in a dialog with no document behind it, and was the answer everywhere, because
///         <see cref="CommandStack" /> was the only undo stack in the repository and was not an
///         <see cref="IUndoManager" />.
///     </para>
///     <para>
///         ⚠ <b>Register is the opposite of <see cref="CommandStack.Execute" />, and that is the
///         thing to get wrong.</b> Execute runs the command and then records it; a control calling
///         Register has <i>already</i> made the edit, so running it here would apply it twice — a
///         keystroke that types two characters, and a stack whose first undo takes back an edit
///         nobody made.
///     </para>
/// </remarks>
public sealed class CommandStackUndoManagerTests {
    static IUndoManager Manager(out CommandStack stack) {
        stack = new TestDocument(ModelFixture.Project()).Stack;

        return stack;
    }

    [Fact]
    public void An_already_applied_edit_is_recorded_rather_than_run() {
        var manager = Manager(out var stack);

        var value = "typed";
        var applications = 0;

        manager.Register(
            "Typing",
            () => value = "",
            () => {
                applications++;
                value = "typed";
            }
        );

        // ⚠ Counted, not compared. The edit's redo puts the value back to what it already is — that
        // is what an already-applied edit means — so an implementation that ran it would leave the
        // same string behind and an assertion on the value alone could not fail.
        Assert.Equal(0, applications);
        Assert.Equal("typed", value);
        Assert.True(stack.CanUndo.Value);
        Assert.Equal("Typing", stack.UndoName.Value);

        Assert.True(manager.Undo());
        Assert.Equal("", value);

        Assert.True(manager.Redo());
        Assert.Equal("typed", value);
        Assert.Equal(1, applications);
    }

    [Fact]
    public void The_two_faces_are_one_stack_rather_than_two() {
        var manager = Manager(out var stack);
        var log = new List<string>();

        stack.Execute(new DelegateCommand("Move", _ => log.Add("do"), _ => log.Add("undo")));
        manager.Register("Typing", () => log.Add("untype"), () => log.Add("retype"));

        // ⚠ The point of the stack *being* the manager rather than an adapter beside it: an edit a
        // control registered and an edit a command made are one history, so ⌘Z steps back through
        // both in the order they happened. Two stacks would undo the typing and leave the move.
        Assert.Equal("Typing", stack.UndoName.Value);

        Assert.True(stack.Undo());
        Assert.Equal("Move", stack.UndoName.Value);
        Assert.Equal(["do", "untype"], log);

        Assert.True(stack.Undo());
        Assert.Equal(["do", "untype", "undo"], log);
        Assert.False(((IUndoManager) stack).CanUndo);
    }

    [Fact]
    public void Registering_while_the_stack_is_replaying_is_ignored() {
        var manager = Manager(out var stack);

        var value = "b";
        var reentered = 0;

        // What a control actually does: its value changes because the undo put the old one back, it
        // hears that as an edit and offers it to the manager. Undoing re-runs the code that made the
        // edit, so a stack that accepted this would record the undo of the undo and never reach the
        // state before it.
        manager.Register(
            "Typing",
            () => {
                value = "a";
                reentered++;
                manager.Register("Typing", () => { }, () => { });
            },
            () => value = "b"
        );

        Assert.True(manager.Undo());

        Assert.Equal("a", value);
        Assert.Equal(1, reentered);
        Assert.False(manager.CanUndo);
        Assert.True(manager.CanRedo);
    }

    [Fact]
    public void The_replaying_flag_is_false_outside_an_undo_and_true_inside_one() {
        var manager = Manager(out var stack);

        var seen = new List<bool>();

        Assert.False(manager.IsPerforming);

        // ⚠ Asserted from inside the command as well as outside, because a flag that is only ever
        // read between operations cannot distinguish "never set" from "set and cleared". The
        // executing path must read false — a command's own edits are not a replay — and the undo
        // path must read true.
        stack.Execute(
            new DelegateCommand("Move", _ => seen.Add(manager.IsPerforming), _ => seen.Add(manager.IsPerforming))
        );

        Assert.Equal([false], seen);

        stack.Undo();
        Assert.Equal([false, true], seen);

        stack.Redo();
        Assert.Equal([false, true, true], seen);

        Assert.False(manager.IsPerforming);
    }

    [Fact]
    public void An_edit_arriving_during_a_transaction_is_refused_rather_than_folded_into_it() {
        var manager = Manager(out var stack);

        var value = "after";

        using (stack.BeginTransaction("Drag")) {
            manager.Register("Typing", () => value = "before", () => value = "after");
        }

        // ⚠ A transaction is building one entry out of commands it ran itself. An already-applied
        // edit from a control has no place in it: rolling the transaction back would call this
        // edit's undo, taking back something the transaction never did.
        Assert.False(stack.CanUndo.Value);
        Assert.Equal("after", value);
    }

    [Fact]
    public void The_signals_and_the_interface_agree() {
        var manager = Manager(out var stack);

        Assert.Equal(stack.CanUndo.Value, manager.CanUndo);
        Assert.Equal(stack.CanRedo.Value, manager.CanRedo);

        manager.Register("Typing", () => { }, () => { });

        Assert.True(manager.CanUndo);
        Assert.Equal(stack.CanUndo.Value, manager.CanUndo);

        manager.Undo();

        Assert.False(manager.CanUndo);
        Assert.True(manager.CanRedo);
        Assert.Equal(stack.CanRedo.Value, manager.CanRedo);
    }
}
