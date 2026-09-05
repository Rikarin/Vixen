// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Composition;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A dialog that is a function of state, beside the one a command awaits.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a replacement for <c>ConfirmAsync</c>, and this file is not evidence that it
///         should be one.</b> An awaited dialog is the right shape for a command that has to have an
///         answer before it continues, and it is what every caller in the tree uses. What was
///         missing is the declarative half: nothing bound a presentation to state, so the open state
///         lived in the control and a panel that showed one held a <c>ref</c> and called it from a
///         handler.
///     </para>
///     <para>
///         ⚠ <b>And it goes through the same queue, which is what makes it more than markup sugar.</b>
///         A panel could always open its own <c>&lt;Dialog&gt;</c> from an effect; what it could not
///         do is take its turn behind a command's ask. Two backdrops over each other is a picture
///         with no answer in it.
///     </para>
/// </remarks>
public class StateDialogTests {
    [Fact]
    public void A_dialog_appears_and_disappears_with_the_signal_its_panel_owns() {
        using var ui = Sheet(out var sheet, out var dialogs);

        Assert.False(sheet.Question.IsOpen);

        sheet.Asking.Value = true;
        Settle(ui);

        Assert.True(sheet.Question.IsOpen);
        Assert.Same(sheet.Question, dialogs.Current);

        sheet.Asking.Value = false;
        Settle(ui);

        Assert.False(sheet.Question.IsOpen);
        Assert.Null(dialogs.Current);
    }

    /// <summary>The answer lands in the model, which is where a declarative dialog has to put it.</summary>
    [Fact]
    public void The_answer_reaches_the_model_and_takes_the_dialog_down_with_it() {
        using var ui = Sheet(out var sheet, out var dialogs);

        sheet.Asking.Value = true;
        Settle(ui);

        Button(sheet.Question, "Delete").Activate();
        Settle(ui);

        Assert.Equal("deleted", sheet.Answer);
        Assert.False(sheet.Asking.Value);
        Assert.False(sheet.Question.IsOpen);
        Assert.Null(dialogs.Current);
    }

    /// <summary>
    ///     ⚠ The dialog is the panel's, so the service does not take it away — a rebuild would
    ///     otherwise find its <c>ref</c> pointing at a removed element and add a second one beside it.
    /// </summary>
    [Fact]
    public void A_panel_s_own_dialog_is_not_removed_when_it_is_answered() {
        using var ui = Sheet(out var sheet, out _);

        var dialog = sheet.Question;
        var parent = dialog.Parent;

        sheet.Asking.Value = true;
        Settle(ui);

        Button(dialog, "Keep").Activate();
        Settle(ui);

        Assert.False(dialog.IsRemoved);
        Assert.Same(parent, dialog.Parent);

        // And it can be asked again, which is the consequence: a removed dialog would open nothing.
        sheet.Asking.Value = true;
        Settle(ui);

        Assert.True(dialog.IsOpen);
    }

    /// <summary>Escape leaves the model agreeing with the screen, or the next flush asks again.</summary>
    [Fact]
    public void Dismissing_it_leaves_the_model_agreeing_with_the_screen() {
        using var ui = Sheet(out var sheet, out var dialogs);

        sheet.Asking.Value = true;
        Settle(ui);

        ui.PressKey(InputKey.Escape);
        Settle(ui);

        Assert.False(sheet.Question.IsOpen);
        Assert.False(sheet.Asking.Value);
        Assert.Null(dialogs.Current);
    }

    /// <summary>
    ///     ⚠ A panel's ask waits behind a command's rather than appearing over it, which is the whole
    ///     reason this goes through <c>DialogService</c> instead of calling <c>Open</c> itself.
    /// </summary>
    [Fact]
    public void A_declarative_ask_takes_its_turn_behind_an_awaited_one() {
        using var ui = Sheet(out var sheet, out var dialogs);

        var awaited = dialogs.ConfirmAsync("Save first?");

        Settle(ui);
        Assert.NotSame(sheet.Question, dialogs.Current);

        sheet.Asking.Value = true;
        Settle(ui);

        // Still the command's, and the panel's is behind it rather than over it.
        Assert.NotSame(sheet.Question, dialogs.Current);
        Assert.False(sheet.Question.IsOpen);

        dialogs.Current!.Close();
        Settle(ui);

        Assert.True(awaited.IsCompleted);
        Assert.Same(sheet.Question, dialogs.Current);
        Assert.True(sheet.Question.IsOpen);
    }

    /// <summary>A panel that changes its mind while it waits is dropped rather than flashed.</summary>
    [Fact]
    public void An_ask_withdrawn_before_its_turn_never_appears() {
        using var ui = Sheet(out var sheet, out var dialogs);

        var awaited = dialogs.ConfirmAsync("Save first?");
        Settle(ui);

        sheet.Asking.Value = true;
        Settle(ui);

        sheet.Asking.Value = false;
        Settle(ui);

        dialogs.Current!.Close();
        Settle(ui);

        Assert.True(awaited.IsCompleted);
        Assert.False(sheet.Question.IsOpen);
        Assert.Null(dialogs.Current);
    }

    /// <summary>Two frames: one to run the effect that asks, one for the pump that presents.</summary>
    /// <remarks>
    ///     ⚠ <b>An ask is not a presentation, and that is <c>DialogService</c>'s own contract rather
    ///     than an artefact of this binding</b> — see <c>An_ask_opens_nothing_until_the_shell_pumps</c>.
    ///     What is new here is only that the ask is made by an effect, so it takes a flush to reach
    ///     the queue at all. Frames, not elapsed time.
    /// </remarks>
    static void Settle(UiTest ui) {
        ui.Frame();
        ui.Frame();
    }

    static Button Button(Dialog dialog, string label) =>
        dialog.Body.Children.OfType<Button>().First(button => button.Label == label);

    static UiTest Sheet(out StateDialogSheet sheet, out DialogService dialogs) {
        var ui = ControlHarness.Open(500f, 400f);
        var service = new DialogService(ui.Document);

        sheet = new() { Dialogs = service };
        dialogs = service;

        BuildContext.BuildInto(sheet, ui.Document, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
