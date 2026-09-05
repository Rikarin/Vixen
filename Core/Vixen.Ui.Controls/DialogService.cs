// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

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

/// <summary>An application's modal questions: confirm, prompt, choose, and one it is handed.</summary>
/// <remarks>
///     <para>
///         <b>Drawn, not native, and doc 20 is explicit about why.</b> A modal that is an OS window
///         cannot be screenshotted by a golden-image suite and cannot be driven by a headless
///         harness — so every question an application asks about its own state is a
///         <see cref="Dialog" /> in its own document. A <i>file</i> picker is the opposite case and
///         belongs to the platform: that one is about the user's disk rather than the application's
///         state, and a drawn one has none of the places, tags or sandbox permissions a real picker
///         carries.
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
///         ⚠ <b>The pump is the document's tick, and there is nothing to wire.</b> The service
///         subscribes to <see cref="UiDocument.Ticked" /> for its lifetime, so an application that
///         calls <see cref="UiDocument.Tick" /> — which every host must, every frame, whether
///         anything happened or not — has working dialogs without knowing this method exists.
///         <see cref="UiDocument.Update" /> would have been the wrong half of the frame for the same
///         reason <c>CommandsInvalidated</c> is not raised from it: it returns early when nothing
///         dirtied the document, and a dialog being answered does not dirty one. <see cref="Pump" />
///         stays public because a test wants to step a frame's worth of dialog without a clock.
///     </para>
///     <para>
///         ⚠ <b>One at a time, and a second ask waits for the first.</b> Two backdrops over each
///         other is a picture with no answer in it: the lower dialog is visible, unreachable, and
///         still holds the focus scope. Queued rather than refused, because the callers are commands
///         and "your Save prompt was dropped because a rename was open" is the failure that loses
///         work.
///     </para>
///     <para>
///         ⚠ <b>One thread, and never a blocking wait.</b> Everything here happens on the thread
///         that ticks the document; the returned <see cref="Task{TResult}" /> is completed from
///         <see cref="Pump" /> and its continuations therefore run <i>inline</i>, on that thread,
///         from inside <see cref="Pump" />. So <c>await</c> is the only correct way to consume one:
///         a caller that blocks on <c>.Result</c> or <c>.Wait()</c> blocks the thread that would have
///         pumped the answer, and that is a deadlock rather than a slow frame. Re-entrancy is
///         allowed and is the ordinary case — a command that answers one dialog by opening another
///         is asking from inside <see cref="Pump" />, and the new ask is presented later in the same
///         call.
///     </para>
/// </remarks>
public sealed class DialogService : IDisposable {
    /// <summary>An ask that has not been shown yet: how to show it, and how to answer it unshown.</summary>
    readonly record struct Ask(Action Present, Action Dismiss);

    /// <summary>A dialog a panel owns, waiting its turn, and whether the panel still wants it.</summary>
    /// <remarks>
    ///     ⚠ <b>A flag rather than taking the entry back out of the queue, because a
    ///     <see cref="Queue{T}" /> has no way to.</b> A panel's state can flip twice before the ask
    ///     it made is reached — the flag is read when it is, and an ask nobody wants any more is
    ///     dropped there.
    /// </remarks>
    sealed class Presentation {
        internal required Dialog Dialog { get; init; }

        internal bool Wanted { get; set; }
    }

    readonly Queue<Ask> queued = [];
    readonly Dictionary<Dialog, Presentation> presentations = [];
    readonly UiDocument document;

    Action? finish;
    bool closed;
    bool disposed;

    /// <summary>Whether <see cref="Current" /> is a dialog this service made.</summary>
    /// <remarks>
    ///     ⚠ <b>What decides whether <see cref="Finish" /> removes the element.</b> A dialog this
    ///     service presented was created by it and is its to take away; a dialog a panel wrote in its
    ///     own markup belongs to that panel's region and would come back on the next rebuild having
    ///     been removed from underneath it — an element removed twice, and a <c>ref</c> pointing at a
    ///     corpse.
    /// </remarks>
    bool owned;

    /// <summary>Creates the service over a document, and hangs its pump on that document's tick.</summary>
    /// <param name="document">Where the dialogs go, and whose frame answers them.</param>
    /// <remarks>
    ///     ⚠ <b>The document holds this service alive, so something has to let go.</b> The
    ///     subscription is a strong reference from the document to the service and the service holds
    ///     a queue of callers' continuations — which is a leak the moment the two lifetimes differ.
    ///     <see cref="Dispose" /> is the whole of the answer: it unsubscribes and then
    ///     <see cref="CancelAll" />s, so every awaiting caller resumes rather than being collected
    ///     mid-await.
    /// </remarks>
    public DialogService(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        this.document = document;
        document.Ticked += OnTicked;
    }

    /// <summary>Answers everything outstanding and stops pumping.</summary>
    /// <remarks>
    ///     Idempotent, and the ordering is load bearing: the tick subscription is dropped first, so
    ///     a continuation resumed by <see cref="CancelAll" /> below cannot reach a later frame of a
    ///     service that is going away — and a host disposing this before its document does not leave
    ///     something subscribed that would present a dialog into a disposed tree.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        document.Ticked -= OnTicked;

        CancelAll();
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

                session.AddButton(cancel ?? DefaultCancel, () => false);

                session.AddButton(
                    confirm ?? DefaultConfirm,
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
    /// <param name="cancel">What the other one says.</param>
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
        string? confirm = null,
        string? cancel = null
    ) =>
        ShowAsync<string?>(
            title,
            session => {
                Message(session, message);

                var field = session.Body.Add<TextBox>();
                field.Value = initial;

                session.AddButton(cancel ?? DefaultCancel, () => null);

                var accept = session.AddButton(
                    confirm ?? DefaultConfirm,
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

    /// <summary>Shows a dialog a panel owns for as long as the panel's own state says to.</summary>
    /// <param name="dialog">The dialog, written in the panel's markup and owned by it.</param>
    /// <param name="asking">Whether the panel's state says the question is being asked.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The declarative half, beside the awaited one, and it is not a replacement for
    ///         it.</b> <see cref="ConfirmAsync" /> and its neighbours are imperative on purpose: a
    ///         command that must have an answer before it continues is exactly a call that returns
    ///         one, and every caller in the tree is that shape. What had no spelling at all was
    ///         SwiftUI's other arrangement — a dialog that is a <i>function of state</i>, so the
    ///         panel that shows one is the panel that owns the flag, and the presentation survives a
    ///         rebuild because the flag does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It goes through the same queue, and that is the whole reason it is here rather
    ///         than in markup on its own.</b> A panel could already open its own <c>&lt;Dialog&gt;</c>
    ///         from an effect; what it could not do is take its turn. Two backdrops over each other
    ///         is a picture with no answer in it, and a state-driven dialog that appeared over an
    ///         awaited one would be exactly that — with the awaited one still holding the focus
    ///         scope. So this enqueues, and a panel's ask waits behind a command's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Idempotent, because what calls it is an effect.</b>
    ///         <c>use="@(d =&gt; Dialogs.Present(d, Asking.Value))"</c> re-runs on every change of
    ///         every signal the expression read; saying the same thing twice must not enqueue twice.
    ///     </para>
    ///     <para>
    ///         <b>The answer is the panel's own business</b>, which is the other half of what makes
    ///         this a different shape from an awaited ask: there is no <see cref="Task{TResult}" />
    ///         to complete, so what the buttons do is write the model — an <c>on:click</c> that sets
    ///         a signal — and the dialog goes away because that signal is what this reads. A dialog
    ///         the user dismisses instead needs <c>on:openchanged</c> to tell the model, or the model
    ///         asks for it again on the next flush.
    ///     </para>
    /// </remarks>
    public void Present(Dialog dialog, bool asking) {
        ArgumentNullException.ThrowIfNull(dialog);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!asking) {
            if (presentations.TryGetValue(dialog, out var waiting)) {
                waiting.Wanted = false;
            }

            if (ReferenceEquals(Current, dialog) && dialog.IsOpen) {
                dialog.Close();
            }

            return;
        }

        if (ReferenceEquals(Current, dialog) || closed) {
            return;
        }

        if (presentations.TryGetValue(dialog, out var already)) {
            already.Wanted = true;
            return;
        }

        var presentation = new Presentation { Dialog = dialog, Wanted = true };

        presentations[dialog] = presentation;
        queued.Enqueue(new Ask(() => Show(presentation), () => presentations.Remove(dialog)));
    }

    /// <summary>Opens whatever is waiting, and completes whatever has been answered.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called once a frame from <see cref="UiDocument.Ticked" />, and it is where a
    ///         caller's continuation runs.</b> See the class's remarks: completing from the click
    ///         handler would resume the awaiting command in the middle of the event that answered
    ///         it, with the dialog's subtree about to be removed underneath the router. A host does
    ///         not have to call this — ticking the document is what drives it — and calling it as
    ///         well is harmless, which is what makes it usable from a test that has no clock.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Re-entrant, and deliberately without a guard.</b> The continuation
    ///         <see cref="Finish" /> invokes runs inline, so a command resumed by it can call this
    ///         again, or <see cref="CancelAll" />. Clearing the fields before the invoke is what
    ///         makes that safe, and it is the whole of the mechanism: whichever call presents the
    ///         next ask, the other one finds <see cref="Current" /> already set and does nothing. A
    ///         <c>pumping</c> flag was written here and taken out again — it would have turned
    ///         <see cref="CancelAll" />'s own finish into a no-op when a continuation called it,
    ///         which is the awaiting-caller-never-completes defect this class exists to prevent, and
    ///         no test could be made to fail without it.
    ///     </para>
    /// </remarks>
    public void Pump() {
        Finish();

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

        // ⚠ `Finish` rather than `Pump`, and the difference is not tidiness: `Pump` would go on to
        // present the next ask out of a queue this method is about to answer and empty.
        Current?.Close(CloseReason.Cancelled);
        Finish();

        while (queued.Count > 0) {
            queued.Dequeue().Dismiss();
        }

        presentations.Clear();
    }

    /// <summary>Takes the answered dialog away and runs the caller's continuation.</summary>
    /// <remarks>
    ///     ⚠ <b>The removal is here and not in <c>Answer</c>, which is the point of the whole
    ///     arrangement.</b> The click that answered was dispatched into this subtree; removing it
    ///     from inside that event leaves the router walking elements that are no longer in the
    ///     document. So <c>Answer</c> records and closes, and the element goes away here — a frame
    ///     later, with nothing half-dispatched over it.
    /// </remarks>
    void Finish() {
        if (Current is not { IsOpen: false } answered) {
            return;
        }

        var completed = finish;

        // ⚠ Only what this service made. A dialog a panel wrote in its own markup is that panel's
        // region's to remove, and taking it out here would leave a `ref` pointing at a removed
        // element and a rebuild re-adding one beside it.
        if (owned) {
            answered.Remove();
        }

        Current = null;
        finish = null;
        owned = false;

        // ⚠ After the fields are cleared, because the continuation runs inline from here and a
        // command that answers one dialog by opening another would otherwise find the service still
        // holding the one it just closed.
        completed?.Invoke();
    }

    // ⚠ Read from `ControlStrings` rather than written here, which is the row doc 46 § A3 left open
    // and the promotion closed: the catalogue used to be in `Vixen.Editor.Ui` and unreachable from a
    // control assembly, so these two were literals. They are read per call rather than cached in a
    // static, because a static initialiser runs once and would freeze the words in whatever language
    // was in use the first time any dialog was opened.
    static string DefaultConfirm => ControlStrings.DialogConfirm.Text;

    static string DefaultCancel => ControlStrings.DialogCancel.Text;

    void OnTicked(UiDocument _, TimeSpan __) => Pump();

    /// <summary>Puts a panel's own dialog up, if the panel still wants it by the time its turn comes.</summary>
    void Show(Presentation presentation) {
        presentations.Remove(presentation.Dialog);

        if (!presentation.Wanted) {
            // The panel changed its mind while this waited. Dropped rather than shown-and-closed,
            // which is a dialog that flashes.
            return;
        }

        Current = presentation.Dialog;
        owned = false;
        finish = null;

        presentation.Dialog.Open();
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
        owned = true;

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
