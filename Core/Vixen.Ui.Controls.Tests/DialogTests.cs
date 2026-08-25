// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Ui;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>An application's modal questions, and the frame they are answered on.</summary>
/// <remarks>
///     ⚠ <b>Every test here pumps, because the answer is completed from the pump and not from the
///     click.</b> That is the contract — see <see cref="DialogService.Pump" /> — and a test that
///     awaited without pumping would hang rather than fail, which is why they are written as
///     "press, pump, then assert the task is finished" instead of with <c>await</c>. Most call
///     <see cref="DialogService.Pump" /> directly because a unit test has no clock;
///     <c>The_document_s_tick_is_what_pumps_it</c> is the one that proves a host does not have to.
/// </remarks>
public class DialogTests : IDisposable {
    readonly UiDocument document = new(800f, 600f);
    readonly DialogService dialogs;

    public DialogTests() {
        ControlTheme.Install(document);
        dialogs = new DialogService(document);
    }

    public void Dispose() {
        dialogs.Dispose();
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

        Press(dialogs.Current!, "OK");

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

        Press(dialogs.Current!, "OK");
        dialogs.Pump();

        Assert.Equal("Shading", await answer);
    }

    [Fact]
    public void A_prompt_will_not_confirm_an_empty_name() {
        _ = dialogs.PromptAsync("Name the layout");
        dialogs.Pump();

        var accept = Button(dialogs.Current!, "OK");

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

        Press(dialogs.Current!, "OK");
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

    [Fact]
    public void An_ask_dirties_nothing_which_is_why_the_pump_is_the_tick_and_not_the_pass() {
        Settle();

        var answer = dialogs.ConfirmAsync("Delete it?");

        // ⚠ Measured rather than argued, and it is the whole reason `Ticked` is the subscription.
        // Asking a question mutates no element, so the document is not dirty and `Update` returns
        // false — a pump hung on the pass would not run on this frame, or on any frame in which the
        // interface was still, which is most of them.
        Assert.False(document.Update());
        Assert.False(dialogs.IsOpen);

        document.Tick(TimeSpan.FromSeconds(1));

        Assert.True(dialogs.IsOpen);
        Assert.Equal("Delete it?", dialogs.Current?.Title);
        Assert.False(answer.IsCompleted);
    }

    [Fact]
    public async Task The_document_s_tick_is_what_pumps_it_and_a_host_wires_nothing() {
        // Not one call to `Pump` in this test. An application that ticks its document — which every
        // host must, every frame — has working dialogs, which is what promoting this bought.
        var answer = dialogs.ConfirmAsync("Delete it?");

        document.Tick(TimeSpan.FromSeconds(1));
        Assert.True(dialogs.IsOpen);

        Press(dialogs.Current!, "OK");
        Assert.False(answer.IsCompleted);

        document.Tick(TimeSpan.FromSeconds(2));

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.True(await answer);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task A_dialog_opened_from_inside_another_s_continuation_is_presented_by_the_same_pump() {
        List<string> steps = [];
        Task<int>? whole = null;

        OnAFrameLoopThread(
            () => {
                whole = Ask();

                dialogs.Pump();
                Assert.Equal("First?", dialogs.Current?.Title);

                Press(dialogs.Current!, "OK");
                dialogs.Pump();

                // ⚠ The second ask was made from *inside* that pump — the await below resumed there,
                // inline — and the same call presented it. A service still holding the dialog it had
                // just closed would leave this null and the caller waiting for a frame that has no
                // reason to come; a `Pump` that re-entered itself would present it underneath the
                // one being torn down.
                Assert.Equal("Second?", dialogs.Current?.Title);
                Assert.False(whole.IsCompleted);

                Find<TextBox>(dialogs.Current!)!.Value = "Shading";
                Press(dialogs.Current!, "OK");
                dialogs.Pump();
            }
        );

        Assert.True(whole!.IsCompletedSuccessfully);
        Assert.Equal(1, await whole);
        Assert.Equal(["first answered", "Shading"], steps);

        return;

        async Task<int> Ask() {
            var first = await dialogs.ConfirmAsync("First?");
            steps.Add("first answered");

            var named = await dialogs.PromptAsync("Second?");
            steps.Add(named ?? "<none>");

            return first ? 1 : 0;
        }
    }

    [Fact]
    public async Task Disposing_the_service_answers_what_is_waiting_and_stops_pumping() {
        var first = dialogs.ConfirmAsync("First?");
        var second = dialogs.ConfirmAsync("Second?");

        dialogs.Pump();
        Assert.True(dialogs.IsOpen);

        dialogs.Dispose();

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.False(await first);
        Assert.False(await second);
        Assert.False(dialogs.IsOpen);

        // And the tick no longer reaches it: a late ask is answered where it stands rather than
        // queued behind a service that will not draw another frame.
        var late = dialogs.ConfirmAsync("Late?");
        document.Tick(TimeSpan.FromSeconds(1));

        Assert.False(dialogs.IsOpen);
        Assert.True(late.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_disposed_service_is_not_held_alive_by_the_document_it_pumped_from() {
        var kept = Make(dispose: false);
        var dropped = Make(dispose: true);

        Collect();

        // ⚠ The control half, and it is what makes the other half evidence rather than a tautology.
        // An undisposed service *is* reachable — the document's `Ticked` holds it, and through it
        // every awaiting caller's continuation — so a test that only asserted the second line would
        // pass just as well against a service nothing ever subscribed.
        Assert.True(kept.IsAlive);
        Assert.False(dropped.IsAlive);

        GC.KeepAlive(document);
        return;

        [MethodImpl(MethodImplOptions.NoInlining)]
        WeakReference Make(bool dispose) {
            var service = new DialogService(document);

            if (dispose) {
                service.Dispose();
            }

            return new WeakReference(service);
        }

        static void Collect() {
            for (var attempt = 0; attempt < 3; attempt++) {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
            }
        }
    }

    /// <summary>Runs the document's passes until nothing is dirty.</summary>
    void Settle() {
        for (var pass = 0; pass < 16 && document.Update(); pass++) { }
    }

    /// <summary>Runs a body the way a frame loop would: on a thread with no synchronisation context.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes the continuation resume inline from <c>Pump</c>.</b> A game or editor
    ///     frame loop has no <see cref="SynchronizationContext" />, so an <c>await</c> on one of
    ///     these tasks resumes on the completing thread — which is the contract this service states.
    ///     xunit installs a context around a test, and a test that left it in place would be
    ///     measuring xunit's scheduler instead.
    /// </remarks>
    static void OnAFrameLoopThread(Action body) {
        var restore = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try {
            body();
        } finally {
            SynchronizationContext.SetSynchronizationContext(restore);
        }
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
