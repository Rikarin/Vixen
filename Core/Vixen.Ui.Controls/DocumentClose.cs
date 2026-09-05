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
    /// <summary>Makes an element ask before a dirty document it hosts is closed.</summary>
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
    ///         ⚠ <b>A save that refused does not close.</b> <see cref="IEditableDocument.Save" />
    ///         returns a bool precisely so that a disk that is full is not a silently lost
    ///         document — so a failed save answers <see cref="DocumentCloseAnswer.SaveFailed" />
    ///         and leaves the application exactly where it was.
    ///     </para>
    /// </remarks>
    public static IDisposable Install(
        UiElement element,
        DialogService dialogs,
        Action close,
        Action<DocumentCloseAnswer>? answered = null
    ) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(close);

        var going = false;
        var asking = false;

        void Handle(UiElement reached, CloseRequestEvent args) {
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

            // ⚠ `Source`, not the element the handler is on. This is installed on the root — that is
            // where a close request always ends up — and a walk that started there would find the
            // *document's* fallback and answer for it in a window holding two panels and two
            // documents. `Source` is where the request was raised, which is the focus.
            if ((args.Source ?? reached).FindHostedDocument() is not { } document || !document.IsDirty.Value) {
                return;
            }

            args.Cancel();
            asking = true;

            _ = Ask(document);
        }

        async Task Ask(IEditableDocument document) {
            try {
                // ⚠ Don't Save, Cancel, Save — the platform order, and `ChooseAsync` makes the LAST
                // button the prominent one, which is the one this wants Save to be.
                var chosen = await dialogs.ChooseAsync(
                    ControlStrings.DocumentSavePrompt.Text,
                    document.Name.Value,
                    ControlStrings.DocumentDiscard.Text,
                    ControlStrings.DialogCancel.Text,
                    ControlStrings.DocumentSave.Text
                );

                var answer = chosen switch {
                    0 => DocumentCloseAnswer.Discarded,
                    2 => document.Save() ? DocumentCloseAnswer.Saved : DocumentCloseAnswer.SaveFailed,

                    // -1 is the dismissal — Escape, or the header's close button. Backing out of a
                    // question about losing work is "no", never "yes".
                    _ => DocumentCloseAnswer.Cancel
                };

                if (answer is DocumentCloseAnswer.Saved or DocumentCloseAnswer.Discarded) {
                    going = true;

                    try {
                        close();
                    } finally {
                        going = false;
                    }
                }

                answered?.Invoke(answer);
            } finally {
                asking = false;
            }
        }

        element.AddHandler<CloseRequestEvent>(Handle);

        return new Removal(element, Handle);
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
