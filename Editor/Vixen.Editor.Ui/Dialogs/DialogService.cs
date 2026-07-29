// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>A dialog being built, and the only way it answers.</summary>
/// <typeparam name="TResult">What the dialog is asked for.</typeparam>
/// <remarks>
///     <para>
///         <b>A button is a label and an answer, and there is no other kind.</b> A dialog that closed
///         itself from somewhere other than its own footer is one whose caller has to guess what it
///         returned — so <see cref="Answer" /> is the single exit, and the footer's buttons are the
///         ordinary way to reach it.
///     </para>
///     <para>
///         ⚠ <b>Answering does not remove anything.</b> The click that answers is dispatched into the
///         dialog's own subtree, and tearing that subtree down inside its own event would leave the
///         router walking removed elements. What <see cref="Answer" /> does is record the value and
///         close the overlay; <see cref="DialogService.Pump" /> takes the element away on the next
///         tick, which is also where the caller's continuation runs.
///     </para>
/// </remarks>
public sealed class DialogSession<TResult> {
    readonly List<(ButtonBase Button, Func<TResult> Result)> buttons = [];

    internal DialogSession(Dialog dialog) {
        Dialog = dialog;

        dialog.AddHandler<ClickEvent>(
            (_, args) => {
                foreach (var (button, result) in buttons) {
                    if (ReferenceEquals(args.Source, button)) {
                        Answer(result());
                        return;
                    }
                }
            }
        );
    }

    /// <summary>The dialog itself, for a caller that wants to restyle it.</summary>
    public Dialog Dialog { get; }

    /// <summary>Where the content goes.</summary>
    public UiElement Body => Dialog.Body;

    /// <summary>Where the buttons go.</summary>
    public UiElement Footer => Dialog.Footer;

    /// <summary>Whether the dialog has been answered.</summary>
    public bool IsAnswered { get; private set; }

    /// <summary>What it was answered with.</summary>
    internal TResult? Result { get; private set; }

    /// <summary>Adds a button to the footer, and says what pressing it means.</summary>
    /// <param name="label">What it says.</param>
    /// <param name="result">What it answers, asked when it is pressed.</param>
    /// <param name="variant">How prominent it is.</param>
    /// <returns>The button, so a caller can enable or disable it.</returns>
    /// <remarks>
    ///     The result is a delegate rather than a value because the ordinary confirming button
    ///     answers with whatever is in the dialog's field, which does not exist yet when the button
    ///     is declared.
    /// </remarks>
    public Button AddButton(string label, Func<TResult> result, ControlVariant variant = ControlVariant.Default) {
        ArgumentNullException.ThrowIfNull(result);

        var button = Footer.Add<Button>();
        button.Label = label;
        button.Variant = variant;

        buttons.Add((button, result));
        return button;
    }

    /// <summary>Answers the dialog and closes it.</summary>
    /// <param name="result">The answer.</param>
    /// <remarks>
    ///     ⚠ <b>The first answer wins and the rest are ignored.</b> The Escape key, the header's
    ///     close button and a footer button can all arrive within one frame — a double-click landing
    ///     on a button that was replaced by a confirmation is the ordinary way — and a second
    ///     completion of one task is an exception thrown from inside the frame loop.
    /// </remarks>
    public void Answer(TResult result) {
        if (IsAnswered) {
            return;
        }

        IsAnswered = true;
        Result = result;

        if (Dialog.IsOpen) {
            Dialog.Close();
        }
    }
}

/// <summary>The editor's modal questions: confirm, prompt, choose, and one it is handed.</summary>
/// <remarks>
///     <para>
///         <b>Drawn, not native, and doc 20 is explicit about why.</b> A modal that is an OS window
///         cannot be screenshotted by the golden-image suite and cannot be driven by the automation
///         harness — so every question the editor asks about its own state is a
///         <c>Vixen.Ui.Controls</c> <see cref="Dialog" /> in the shell's document. The <i>file</i>
///         pickers are the opposite case and go through <c>INativeDialogs</c>: those are about the
///         user's disk rather than the editor's state, and a drawn one has none of the places, tags
///         or sandbox permissions a real picker carries.
///     </para>
///     <para>
///         ⚠ <b>Asynchronous, and the continuation runs on the frame loop.</b> The answer is
///         completed from <see cref="Pump" /> rather than from the click handler, so an
///         <c>await ConfirmAsync(…)</c> resumes on the thread that owns the document, between two
///         frames, with nothing half-dispatched underneath it. Nothing here posts to the thread pool
///         and nothing here blocks — a dialog that blocked the loop would be a dialog that never
///         drew.
///     </para>
///     <para>
///         ⚠ <b>One at a time, and a second ask waits for the first.</b> Two backdrops over each
///         other is a picture with no answer in it: the lower dialog is visible, unreachable, and
///         still holds the focus scope. Queued rather than refused, because the callers are commands
///         and "your Save prompt was dropped because a rename was open" is the failure that loses
///         work.
///     </para>
/// </remarks>
public sealed class DialogService {
    /// <summary>An ask that has not been shown yet: how to show it, and how to answer it unshown.</summary>
    readonly record struct Ask(Action Present, Action Dismiss);

    readonly Queue<Ask> queued = [];
    readonly UiDocument document;

    Action? finish;
    bool closed;

    /// <summary>Creates the service over a document.</summary>
    /// <param name="document">Where the dialogs go.</param>
    public DialogService(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
    }

    /// <summary>The dialog on screen, or <see langword="null" />.</summary>
    /// <remarks>What the automation harness reaches for, and what a test asserts against.</remarks>
    public Dialog? Current { get; private set; }

    /// <summary>Whether something is being asked.</summary>
    public bool IsOpen => Current is not null;

    /// <summary>How many asks are waiting behind it.</summary>
    public int Pending => queued.Count;

    /// <summary>Asks a yes-or-no question.</summary>
    /// <param name="title">The bold first line.</param>
    /// <param name="message">The body, if there is one.</param>
    /// <param name="confirm">What the confirming button says.</param>
    /// <param name="cancel">What the other one says.</param>
    /// <param name="danger">Whether the confirming button destroys something.</param>
    /// <returns>Whether the user confirmed. Escape and the close button are both "no".</returns>
    public Task<bool> ConfirmAsync(
        string title,
        string? message = null,
        string? confirm = null,
        string? cancel = null,
        bool danger = false
    ) =>
        ShowAsync<bool>(
            title,
            session => {
                Message(session, message);

                session.AddButton(cancel ?? EditorStrings.DialogCancel.Text, () => false);

                session.AddButton(
                    confirm ?? EditorStrings.DialogOk.Text,
                    () => true,
                    danger ? ControlVariant.Danger : ControlVariant.Primary
                );
            }
        );

    /// <summary>Asks for a line of text.</summary>
    /// <param name="title">The bold first line.</param>
    /// <param name="message">The body, if there is one.</param>
    /// <param name="initial">What the field starts with, selected.</param>
    /// <param name="confirm">What the confirming button says.</param>
    /// <returns>What was typed, or <see langword="null" /> if the user backed out.</returns>
    /// <remarks>
    ///     ⚠ <b>An empty field is a cancel rather than an empty answer.</b> Every caller here is
    ///     naming something — a layout, a scene, an asset — and none of them has a use for the empty
    ///     name; a confirming button that stays disabled until there is something to confirm says so
    ///     before the click rather than after it.
    /// </remarks>
    public Task<string?> PromptAsync(
        string title,
        string? message = null,
        string? initial = null,
        string? confirm = null
    ) =>
        ShowAsync<string?>(
            title,
            session => {
                Message(session, message);

                var field = session.Body.Add<TextBox>();
                field.Value = initial;

                session.AddButton(EditorStrings.DialogCancel.Text, () => null);

                var accept = session.AddButton(
                    confirm ?? EditorStrings.DialogOk.Text,
                    () => field.Value,
                    ControlVariant.Primary
                );

                accept.Disabled = string.IsNullOrWhiteSpace(field.Value);
                field.ValueChanged += (_, value) => accept.Disabled = string.IsNullOrWhiteSpace(value);

                // Return commits, which is what every text field in a dialog does and what makes the
                // ordinary rename two keystrokes rather than a reach for the mouse.
                field.Submitted += committed => {
                    if (!string.IsNullOrWhiteSpace(committed.Value)) {
                        session.Answer(committed.Value);
                    }
                };
            }
        );

    /// <summary>Asks the user to pick one of several.</summary>
    /// <param name="title">The bold first line.</param>
    /// <param name="message">The body, if there is one.</param>
    /// <param name="choices">What the buttons say, the last one being the most prominent.</param>
    /// <returns>Which was chosen, or <c>-1</c> if the user backed out.</returns>
    /// <remarks>
    ///     ⚠ <b>Three buttons, not a list.</b> This is Save / Don't Save / Cancel and the handful
    ///     like it; a choice with eight options is a list control in a
    ///     <see cref="ShowAsync{TResult}" /> dialog, and putting eight buttons in a footer is how it
    ///     ends up being one.
    /// </remarks>
    public Task<int> ChooseAsync(string title, string? message, params ReadOnlySpan<string> choices) {
        var labels = choices.ToArray();

        return ShowAsync(
            title,
            (DialogSession<int> session) => {
                Message(session, message);

                for (var index = 0; index < labels.Length; index++) {
                    var chosen = index;

                    session.AddButton(
                        labels[index],
                        () => chosen,
                        index == labels.Length - 1 ? ControlVariant.Primary : ControlVariant.Default
                    );
                }
            },
            () => -1
        );
    }

    /// <summary>Shows a dialog a caller fills in.</summary>
    /// <typeparam name="TResult">What it answers with.</typeparam>
    /// <param name="title">The bold first line.</param>
    /// <param name="build">Fills the body and adds the buttons.</param>
    /// <param name="dismissed">What backing out answers, or <see langword="null" /> for the default.</param>
    /// <returns>The answer.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="build" /> runs when the dialog opens, not when it is asked for.</b>
    ///     A dialog queued behind another builds its rows against the state they are in when it
    ///     finally appears — which for a save prompt behind a rename is a different set of dirty
    ///     documents than the one that existed when the command ran.
    /// </remarks>
    public Task<TResult?> ShowAsync<TResult>(
        string title,
        Action<DialogSession<TResult>> build,
        Func<TResult?>? dismissed = null
    ) {
        ArgumentNullException.ThrowIfNull(build);

        var completion = new TaskCompletionSource<TResult?>();

        void Give() => completion.TrySetResult(dismissed is null ? default : dismissed());

        if (closed) {
            // The shell has already gone. Answering with the dismissal is what unblocks a caller
            // that awaits this; queueing it would be a task nothing will ever complete.
            Give();
            return completion.Task;
        }

        queued.Enqueue(new Ask(() => Present(title, build, dismissed, completion), Give));
        return completion.Task;
    }

    /// <summary>Opens whatever is waiting, and completes whatever has been answered.</summary>
    /// <remarks>
    ///     ⚠ <b>Called once a frame by the shell, and it is where a caller's continuation runs.</b>
    ///     See the class's remarks: completing from the click handler would resume the awaiting
    ///     command in the middle of the event that answered it, with the dialog's subtree about to
    ///     be removed underneath the router.
    /// </remarks>
    public void Pump() {
        if (Current is { IsOpen: false }) {
            var completed = finish;

            Current.Remove();
            Current = null;
            finish = null;

            // ⚠ After the fields are cleared, because the continuation runs inline from here and a
            // command that answers one dialog by opening another would otherwise find the service
            // still holding the one it just closed.
            completed?.Invoke();
        }

        if (Current is null && !closed && queued.Count > 0) {
            queued.Dequeue().Present();
        }
    }

    /// <summary>Answers the dialog on screen and everything queued behind it, and refuses more.</summary>
    /// <remarks>
    ///     What closing the editor does. Every waiting ask is answered with its dismissal rather
    ///     than dropped, so a command awaiting one resumes and unwinds instead of never finishing —
    ///     which for the save-on-close prompt is the difference between a clean shutdown and a
    ///     process that will not go. Asks made afterwards are answered the same way rather than
    ///     queued behind a shell that is not going to draw another frame.
    /// </remarks>
    public void CancelAll() {
        closed = true;

        if (Current is { } open) {
            open.Close(CloseReason.Cancelled);
            Pump();
        }

        while (queued.Count > 0) {
            queued.Dequeue().Dismiss();
        }
    }

    static void Message<TResult>(DialogSession<TResult> session, string? message) {
        if (!string.IsNullOrEmpty(message)) {
            session.Body.Add<TextBlock>().Text = message;
        }
    }

    void Present<TResult>(
        string title,
        Action<DialogSession<TResult>> build,
        Func<TResult?>? dismissed,
        TaskCompletionSource<TResult?> completion
    ) {
        var dialog = document.Root.Add<Dialog>();
        dialog.Title = title;

        var session = new DialogSession<TResult>(dialog);
        build(session);

        Current = dialog;

        finish = () => completion.TrySetResult(
            session.IsAnswered
                ? session.Result
                : dismissed is null
                    ? default
                    : dismissed()
        );

        dialog.Open();
    }
}
