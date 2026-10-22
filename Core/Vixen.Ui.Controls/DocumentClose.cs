// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>What the user answered when asked about a dirty document on the way out.</summary>
public enum DocumentCloseAnswer : byte {
    /// <summary>Backed out. The application stays open and the document is untouched.</summary>
    Cancel,

    /// <summary>Wrote it, and the write succeeded.</summary>
    Saved,

    /// <summary>Chose to lose the changes.</summary>
    Discarded,

    /// <summary>Chose to save and the save refused. ⚠ Not a close: see <see cref="DocumentClosePrompt" />.</summary>
    SaveFailed
}

/// <summary>Save / Don't Save / Cancel, joined to the close request that asks it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every piece of this existed and nothing joined them.</b>
///         <see cref="CloseRequestEvent" /> routes from the focus outwards and can be refused,
///         <see cref="IEditableDocument.IsDirty" /> is the question, and <c>DialogService</c>'s own
///         <see cref="DialogService.ChooseAsync" /> remarks name "Save / Don't Save / Cancel" as the
///         shape it exists for. What no file in the repository did was put the three together, so
///         every application that wanted the one modal every desktop application has wrote it
///         itself — which is the defect this repository meets most often, one level up from a
///         missing feature.
///     </para>
///     <para>
///         ⚠ <b>The document is found by walking, not by being handed over.</b>
///         <see cref="UiElement.FindHostedDocument" /> answers with the nearest one above the
///         element the request reached, which is what makes this right in an application with two
///         panels holding two documents: the request is raised on the focused element, so the
///         document that is asked about is the one the user was working in.
///     </para>
///     <para>
///         ⚠ <b>The retry re-enters this handler, and "Don't Save" leaves the document dirty.</b>
///         A prompt is answered frames later, so the only way to close afterwards is to ask again —
///         and asking again runs this code again, against a document that is still dirty because
///         discarding does not clean it. Without the latch below, choosing Don't Save reopens the
///         prompt for ever and the application cannot be quit. Marking the document clean instead
///         would be a lie told to every other consumer of the same signal.
///     </para>
/// </remarks>
public static class DocumentClosePrompt {
    /// <summary>Makes an element ask before a dirty document it hosts is closed, or the application quits over it.</summary>
    /// <param name="element">Where to listen. The window's root, normally.</param>
    /// <param name="dialogs">Who asks the question.</param>
    /// <param name="close">What to call once the answer allows it — the host's own quit.</param>
    /// <param name="answered">Told what the user chose, after the fact. For a test, or a log.</param>
    /// <returns>A handle that stops asking when disposed.</returns>
    /// <exception cref="ArgumentNullException">Any of the first three is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b><paramref name="close" /> is the host's, because the framework has no quit.</b>
    ///         <c>UiApplication.Quit</c> lives one assembly up in <c>Vixen.Ui.Desktop</c> and this
    ///         is in <c>Core/</c>; handing the callback in is what lets the same prompt serve a
    ///         window close, an application quit and a test that counts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A quit asks about every dirty document under <paramref name="element" />, and
    ///         then quits once — or is cancelled as a whole (#1504).</b> The focused one is asked
    ///         first, the rest in tree order. A Cancel, or a save that refused, stops there and the
    ///         application stays; <paramref name="close" /> runs only after the last answer allowed
    ///         it. A request that is a document's own (<see cref="UiCloseReason.DocumentClosed" />)
    ///         asks about that document alone, as it always has.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A save that refused does not close.</b> <see cref="IEditableDocument.Save" />
    ///         returns a bool precisely so that a disk that is full is not a silently lost
    ///         document — so a failed save answers <see cref="DocumentCloseAnswer.SaveFailed" />
    ///         and leaves the application exactly where it was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not for a tab</b> — that is <see cref="InstallOnDocument" />. A quit raised with
    ///         the focus in a tab reaches the tab's handlers before the window's, and a prompt there
    ///         whose <paramref name="close" /> shuts the tab would answer the quit by shutting the tab.
    ///     </para>
    /// </remarks>
    public static IDisposable Install(
        UiElement element,
        DialogService dialogs,
        Action close,
        Action<DocumentCloseAnswer>? answered = null
    ) => Listen(element, dialogs, close, answered, quits: true);

    /// <summary>Makes one document's element — a tab, a panel — ask before that document is closed.</summary>
    /// <param name="element">The element that hosts the document and raises its close.</param>
    /// <param name="dialogs">Who asks the question.</param>
    /// <param name="close">What to call once the answer allows it — the tab's own removal.</param>
    /// <param name="answered">Told what the user chose, after the fact. For a test, or a log.</param>
    /// <returns>A handle that stops asking when disposed.</returns>
    /// <exception cref="ArgumentNullException">Any of the first three is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Answers <see cref="UiCloseReason.DocumentClosed" /> and nothing else (#1504).</b> A quit
    ///     is raised from the focus outwards, so with the focus inside a tab it reaches the tab first.
    ///     The per-tab form used to answer it: it refused the quit, asked, and on Save or Don't Save ran
    ///     its <paramref name="close" /> — which shut the tab — and nothing retried the quit, so the
    ///     application stayed open after the user had answered a question about quitting. Here a quit
    ///     passes through untouched, to the window's <see cref="Install" />, which asks about this
    ///     document along with every other one and then quits.
    /// </remarks>
    public static IDisposable InstallOnDocument(
        UiElement element,
        DialogService dialogs,
        Action close,
        Action<DocumentCloseAnswer>? answered = null
    ) => Listen(element, dialogs, close, answered, quits: false);

    static Removal Listen(
        UiElement element,
        DialogService dialogs,
        Action close,
        Action<DocumentCloseAnswer>? answered,
        bool quits
    ) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(close);

        var going = false;
        var asking = false;

        void Handle(UiElement reached, CloseRequestEvent args) {
            var quitting = args.Reason != UiCloseReason.DocumentClosed;

            // A document's prompt has no opinion about the application going away; the window's
            // prompt asks about this document along with the rest.
            if (quitting && !quits) {
                return;
            }

            // The latch. This is the second pass, made by `close` itself after an answer that allows
            // the close — let it through rather than asking about the same document twice.
            if (going) {
                return;
            }

            // A prompt is already on screen and the user pressed ⌘Q again. Refuse without stacking a
            // second copy of the same question behind the first.
            if (asking) {
                args.Cancel();

                return;
            }

            // ⚠ Something nearer the focus already refused and is asking — a tab's own prompt, for a
            // tab's own close. Asking here as well would put the same question up twice.
            if (args.IsCancelled) {
                return;
            }

            // ⚠ `Source`, not the element the handler is on. This is installed on the root — that is
            // where a close request always ends up — and a walk that started there would find the
            // *document's* fallback and answer for it in a window holding two panels and two
            // documents. `Source` is where the request was raised, which is the focus.
            var first = (args.Source ?? reached).FindHostedDocument();
            var pending = quitting ? Unsaved(element, first) : first is not null && DocumentCommands.HasUnsavedChanges(first) ? [first] : [];

            if (pending.Count == 0) {
                return;
            }

            args.Cancel();
            asking = true;

            _ = Ask(pending);
        }

        async Task Ask(List<IEditableDocument> pending) {
            try {
                foreach (var document in pending) {
                    // A document an earlier answer's save happened to clean, or one that went away.
                    if (!DocumentCommands.HasUnsavedChanges(document)) {
                        continue;
                    }

                    var answer = await AskAbout(document);

                    answered?.Invoke(answer);

                    // ⚠ The quit is cancelled as a whole: the documents already saved stay saved, and
                    // nothing further is asked about a quit the user has backed out of.
                    if (answer is not (DocumentCloseAnswer.Saved or DocumentCloseAnswer.Discarded)) {
                        return;
                    }
                }

                going = true;

                try {
                    close();
                } finally {
                    going = false;
                }
            } finally {
                asking = false;
            }
        }

        async Task<DocumentCloseAnswer> AskAbout(IEditableDocument document) {
            // ⚠ Don't Save, Cancel, Save — the platform order, and `ChooseAsync` makes the LAST
            // button the prominent one, which is the one this wants Save to be.
            var chosen = await dialogs.ChooseAsync(
                ControlStrings.DocumentSavePrompt.Text,
                document.Name.Value,
                ControlStrings.DocumentDiscard.Text,
                ControlStrings.DialogCancel.Text,
                ControlStrings.DocumentSave.Text
            );

            return chosen switch {
                0 => DocumentCloseAnswer.Discarded,
                2 => document.Save() ? DocumentCloseAnswer.Saved : DocumentCloseAnswer.SaveFailed,

                // -1 is the dismissal — Escape, or the header's close button. Backing out of a
                // question about losing work is "no", never "yes".
                _ => DocumentCloseAnswer.Cancel
            };
        }

        element.AddHandler<CloseRequestEvent>(Handle);

        return new Removal(element, Handle);
    }

    /// <summary>Every document with unsaved changes the application would close over, the focused one first.</summary>
    /// <param name="element">Where the prompt listens; every document hosted at or below it is asked about.</param>
    /// <param name="focused">The document nearest the focus, asked first.</param>
    /// <returns>Them, each once.</returns>
    /// <remarks>
    ///     The <see cref="UiDocument" />'s own <see cref="UiDocument.HostedDocument" /> counts, because it is
    ///     what <see cref="UiElement.FindHostedDocument" /> falls back to for anything in the window.
    /// </remarks>
    static List<IEditableDocument> Unsaved(UiElement element, IEditableDocument? focused) {
        List<IEditableDocument> found = [];

        Consider(focused);
        Walk(element);
        Consider(element.Document?.HostedDocument);

        return found;

        void Walk(UiElement at) {
            Consider(at.HostedDocument);

            foreach (var child in at.Children) {
                Walk(child);
            }
        }

        void Consider(IEditableDocument? document) {
            if (document is not null
                && !found.Any(known => ReferenceEquals(known, document))
                && DocumentCommands.HasUnsavedChanges(document)) {
                found.Add(document);
            }
        }
    }
    /// <remarks>
    ///     A named type rather than a lambda-shaped disposable, because <c>RemoveHandler</c> compares
    ///     the delegate — the handle has to hold the same instance that was added, and a fresh
    ///     method group would be a different one that removes nothing.
    /// </remarks>
    sealed class Removal(UiElement element, Action<UiElement, CloseRequestEvent> handler) : IDisposable {
        bool disposed;

        public void Dispose() {
            if (disposed) {
                return;
            }

            disposed = true;
            element.RemoveHandler(handler);
        }
    }
}
