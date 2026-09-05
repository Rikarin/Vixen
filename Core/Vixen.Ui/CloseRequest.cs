// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>Why an application is being asked to go away.</summary>
public enum UiCloseReason : byte {
    /// <summary>⌘Q, Alt-F4, the dock's Quit, or the session ending.</summary>
    Quit,

    /// <summary>The last window's close button, which nothing else claimed.</summary>
    WindowClosed
}

/// <summary>An application being asked to close, which anything on the route may refuse.</summary>
/// <remarks>
///     <para>
///         <b>Routed from the focus outwards</b>, like a key. That is what lets the document object
///         behind the focused view answer — it is the thing that knows whether there is unsaved work
///         — without the head knowing that any such object exists.
///     </para>
///     <para>
///         ⚠ <b>Refusing is <see cref="Cancel" />, which is not <see cref="UiEvent.Handled" />.</b>
///         They are two questions and a handler needs both: a document that saved silently has dealt
///         with the request and is content to go, and a handler that marked such a request handled
///         while allowing it would be indistinguishable from one that had refused.
///     </para>
///     <para>
///         ⚠ <b>Refusing does not mean "never" — it means "not now".</b> A Save / Don't Save /
///         Cancel prompt is a dialog, and a dialog is answered frames later; a synchronous veto could
///         not wait for one without blocking the loop that draws it. So a handler that needs to ask
///         cancels the request, opens the prompt, and calls the application's own <c>Quit</c> when it
///         has an answer. This is the shape the editor's host has used since save-on-close was built
///         — <c>EditorHost</c> calls <c>RequestClose()</c> and reads <c>IsClosing</c> on the next
///         frame — and the framework host is the copy that had no way to do it at all.
///     </para>
/// </remarks>
public sealed class CloseRequestEvent : UiEvent {
    /// <summary>What prompted it.</summary>
    public UiCloseReason Reason { get; init; }

    /// <summary>Whether something refused.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>Refuses the request, for now.</summary>
    /// <remarks>
    ///     ⚠ One-way. A later handler cannot un-refuse what an earlier one refused: the route asks
    ///     everybody, and a veto that a listener further along could quietly overturn would make
    ///     "did anything object" depend on registration order.
    /// </remarks>
    public void Cancel() => IsCancelled = true;
}

public sealed partial class UiDocument {
    /// <summary>Raised after the route, whether or not anything on it refused.</summary>
    /// <remarks>
    ///     For a head that wants to answer without being in the element tree — the application
    ///     object itself, most often. It sees what the tree decided and may refuse as well.
    /// </remarks>
    public event Action<CloseRequestEvent>? CloseRequested;

    /// <summary>Asks everything on the route whether the application may close.</summary>
    /// <param name="reason">What prompted the request.</param>
    /// <returns><see langword="true" /> if nothing refused.</returns>
    /// <remarks>
    ///     ⚠ <b>From the focus outwards, and then the document's own listeners</b> — the same order
    ///     a key takes, and the same order AppKit asks in. A head that listened first would decide
    ///     before the document object it is showing had been asked.
    /// </remarks>
    public bool RequestClose(UiCloseReason reason = UiCloseReason.Quit) {
        var args = new CloseRequestEvent { Reason = reason };

        (Focused ?? Root).Raise(args);

        CloseRequested?.Invoke(args);

        return !args.IsCancelled;
    }
}
