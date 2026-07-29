// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The editor's modal questions, and the frame they are answered on.</summary>
/// <remarks>
///     ⚠ <b>Every test here pumps, because the answer is completed from the pump and not from the
///     click.</b> That is the contract — see <see cref="DialogService.Pump" /> — and a test that
///     awaited without pumping would hang rather than fail, which is why they are written as
///     "press, pump, then assert the task is finished" instead of with <c>await</c>.
/// </remarks>
public class DialogTests : IDisposable {
    readonly UiDocument document = new(800f, 600f);
    readonly DialogService dialogs;

    public DialogTests() {
        ControlTheme.Install(document);
        dialogs = new DialogService(document);
    }

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void An_ask_opens_nothing_until_the_shell_pumps() {
        var answer = dialogs.ConfirmAsync("Delete it?");

        Assert.False(dialogs.IsOpen);
        Assert.Equal(1, dialogs.Pending);
        Assert.False(answer.IsCompleted);

        dialogs.Pump();

        Assert.True(dialogs.IsOpen);
        Assert.Equal("Delete it?", dialogs.Current?.Title);
    }

    [Fact]
    public async Task Confirming_answers_true_on_the_next_pump() {
        var answer = dialogs.ConfirmAsync("Delete it?", "This cannot be undone.");
        dialogs.Pump();

        Press(dialogs.Current!, EditorStrings.DialogOk.Text);

        // ⚠ Not yet. The click ran inside the dialog's own event dispatch, and completing there
        // would resume the awaiting command with the subtree about to be torn down underneath it.
        Assert.False(answer.IsCompleted);

        dialogs.Pump();

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.True(await answer);
        Assert.False(dialogs.IsOpen);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Backing_out_answers_false_rather_than_leaving_the_caller_waiting() {
        var answer = dialogs.ConfirmAsync("Delete it?");
        dialogs.Pump();

        // Escape, the close button and the Cancel button are three ways to the same answer. This is
        // the one that does not go through a footer button at all.
        dialogs.Current!.Close(CloseReason.Cancelled);
        dialogs.Pump();

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.False(await answer);
    }

    [Fact]
    public async Task A_prompt_answers_with_what_was_typed() {
        var answer = dialogs.PromptAsync("Name the layout", initial: "Default");
        dialogs.Pump();

        var field = Find<TextBox>(dialogs.Current!)!;

        Assert.Equal("Default", field.Value);
        field.Value = "Shading";

        Press(dialogs.Current!, EditorStrings.DialogOk.Text);
        dialogs.Pump();

        Assert.Equal("Shading", await answer);
    }

    [Fact]
    public void A_prompt_will_not_confirm_an_empty_name() {
        _ = dialogs.PromptAsync("Name the layout");
        dialogs.Pump();

        var accept = Button(dialogs.Current!, EditorStrings.DialogOk.Text);

        // ⚠ Said before the click rather than after it. Every caller is naming something and none of
        // them has a use for the empty name.
        Assert.True(accept.Disabled);

        Find<TextBox>(dialogs.Current!)!.Value = "Shading";
        Assert.False(accept.Disabled);
    }

    [Fact]
    public async Task A_choice_answers_with_the_index_pressed_and_minus_one_when_dismissed() {
        var answer = dialogs.ChooseAsync("Save changes?", null, "Cancel", "Discard", "Save");
        dialogs.Pump();

        Press(dialogs.Current!, "Save");
        dialogs.Pump();

        Assert.Equal(2, await answer);

        var second = dialogs.ChooseAsync("Save changes?", null, "Cancel", "Discard", "Save");
        dialogs.Pump();

        dialogs.Current!.Close(CloseReason.Cancelled);
        dialogs.Pump();

        Assert.Equal(-1, await second);
    }

    [Fact]
    public void A_second_ask_waits_for_the_first_rather_than_stacking_over_it() {
        var first = dialogs.ConfirmAsync("First?");
        var second = dialogs.ConfirmAsync("Second?");

        dialogs.Pump();

        // Two backdrops over each other is a picture with no answer in it: the lower dialog is
        // visible, unreachable, and still holds the focus scope.
        Assert.Equal("First?", dialogs.Current?.Title);
        Assert.Equal(1, dialogs.Pending);

        Press(dialogs.Current!, EditorStrings.DialogOk.Text);
        dialogs.Pump();

        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);

        dialogs.Pump();
        Assert.Equal("Second?", dialogs.Current?.Title);
    }

    [Fact]
    public async Task Closing_the_editor_answers_everything_waiting_rather_than_dropping_it() {
        var first = dialogs.ConfirmAsync("First?");
        var second = dialogs.ConfirmAsync("Second?");
        var third = dialogs.ChooseAsync("Third?", null, "a", "b");

        dialogs.Pump();
        dialogs.CancelAll();

        // A task nobody completes is a shutdown that never finishes — which for the save-on-close
        // prompt is the one place it costs the user their work.
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.True(third.IsCompletedSuccessfully);

        Assert.False(await first);
        Assert.Equal(-1, await third);

        // And an ask made afterwards is answered rather than queued behind a shell that is not going
        // to draw another frame.
        var late = dialogs.ConfirmAsync("Late?");

        Assert.True(late.IsCompletedSuccessfully);
        Assert.False(await late);
    }

    [Fact]
    public async Task A_dialog_a_caller_fills_in_answers_with_whatever_its_buttons_say() {
        var answer = dialogs.ShowAsync<string>(
            "Pick one",
            session => {
                session.Body.Add<TextBlock>().Text = "Which target?";

                session.AddButton("Windows", () => "windows");
                session.AddButton("Linux", () => "linux", ControlVariant.Primary);
            }
        );

        dialogs.Pump();
        Press(dialogs.Current!, "Linux");
        dialogs.Pump();

        Assert.Equal("linux", await answer);
    }

    [Fact]
    public async Task The_first_answer_wins_and_the_rest_are_ignored() {
        var answer = dialogs.ChooseAsync("Save changes?", null, "Cancel", "Save");
        dialogs.Pump();

        var dialog = dialogs.Current!;

        Press(dialog, "Save");

        // A double-click landing on a button that has already answered, which is the ordinary way
        // this happens — and a second completion of one task is an exception from the frame loop.
        Press(dialog, "Cancel");
        dialogs.Pump();

        Assert.Equal(1, await answer);
    }

    static void Press(Dialog dialog, string label) => Button(dialog, label).Activate();

    static Button Button(Dialog dialog, string label) =>
        dialog.Footer.Children.OfType<Button>().FirstOrDefault(button => button.Label == label)
        ?? throw new InvalidOperationException($"the dialog has no '{label}' button");

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
