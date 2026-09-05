// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;

namespace Vixen.Ui;

/// <summary>Something a user has open, has changed, and expects to be asked about before losing.</summary>
/// <remarks>
///     <para>
///         <b>Three signals and two verbs, and the signals are what make it worth having.</b> Every
///         surface that shows a document's state — a window title's asterisk, a greyed Save item, a
///         tab's close button, a prompt on quit — reads one of these, and reads it through the
///         reactive graph, so there is no "raise the changed event" for a control to forget. That is
///         the whole difference from a <c>bool IsDirty</c> with a <c>Changed</c> event beside it,
///         which is what the editor's own document had and what every consumer of it had to
///         subscribe to by hand.
///     </para>
///     <para>
///         ⚠ <b>Deliberately smaller than <c>NSDocument</c> and than <c>EditorDocument</c>.</b> There
///         is no undo stack here (an element finds one with <see cref="UiElement.FindUndoManager" />,
///         and a document that wants to be the one that hosts it sets it there), no file watching, no
///         asset database and no project. <c>Editor/Vixen.Editor.Core/EditorDocument.cs</c> has all
///         of that and cannot be the framework's model for the reason its own README now records:
///         its only constructor demands an <c>EditorProject</c> and registers with it, so no document
///         exists without a project directory and an asset database behind it. A text editor with one
///         file open would have to invent a project to hold it.
///     </para>
///     <para>
///         ⚠ <b>An interface rather than only the base class</b>, because the thing an application
///         already has is usually a document — a scene, a buffer, a session — and asking it to
///         inherit from a UI type is asking it to reorganise around the window. <see cref="EditableDocument" />
///         is the implementation for everything that does not already have one.
///     </para>
/// </remarks>
public interface IEditableDocument {
    /// <summary>What to call it: a file name, or something like "Untitled 2".</summary>
    /// <remarks>Not the path. <see cref="Location" /> is the path, and a window shows both.</remarks>
    IReadOnlySignal<string> Name { get; }

    /// <summary>Where it lives, or <see langword="null" /> if it has never been saved.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is what makes Save mean Save As.</b> A new document with unsaved changes and no
    ///     location cannot be written anywhere, so a host that does not check this is a host whose
    ///     ⌘S silently does nothing the first time it is pressed.
    /// </remarks>
    IReadOnlySignal<string?> Location { get; }

    /// <summary>Whether it has changes that saving would write.</summary>
    IReadOnlySignal<bool> IsDirty { get; }

    /// <summary>Writes it out.</summary>
    /// <returns>Whether it was written. <see langword="false" /> leaves it dirty.</returns>
    bool Save();

    /// <summary>Throws the changes away and reloads what is on disk.</summary>
    /// <returns>Whether it could be. <see langword="false" /> leaves it dirty.</returns>
    bool Revert();
}

/// <summary>The ordinary implementation: two signals it owns and two methods to fill in.</summary>
/// <remarks>
///     ⚠ <b><see cref="Save" /> writes whether or not it is dirty, and it is
///     <see cref="IEditableDocument.IsDirty" /> that greys the menu item.</b> The two are not the
///     same question — Save As on an unchanged document must still write — and a base class that
///     short-circuited a clean save would make the difference unreachable from a subclass.
/// </remarks>
public abstract class EditableDocument : IEditableDocument {
    readonly Signal<bool> dirty = new(false);
    readonly Signal<string?> location;
    readonly Signal<string> name;

    /// <summary>Creates one.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="location">Where it lives, or <see langword="null" /> if it is new.</param>
    protected EditableDocument(string name, string? location = null) {
        ArgumentNullException.ThrowIfNull(name);

        this.name = new Signal<string>(name);
        this.location = new Signal<string?>(location);
    }

    /// <inheritdoc />
    public IReadOnlySignal<string> Name => name;

    /// <inheritdoc />
    public IReadOnlySignal<string?> Location => location;

    /// <inheritdoc />
    public IReadOnlySignal<bool> IsDirty => dirty;

    /// <summary>Says something changed.</summary>
    /// <remarks>
    ///     Called by whatever made the edit. It is not inferred, because a document knows what a
    ///     change to <i>it</i> is and the framework does not — a selection move is not an edit, and a
    ///     scroll position may or may not be one.
    /// </remarks>
    public void MarkDirty() => dirty.Value = true;

    /// <summary>Says there is nothing left to write.</summary>
    /// <remarks>
    ///     ⚠ <b>Public, and it is not only <see cref="Save" />'s to call.</b> A document whose undo
    ///     stack has been walked back to the state it was loaded in is clean again, which is the
    ///     behaviour every editor has and which nothing but the document itself can detect.
    /// </remarks>
    public void MarkClean() => dirty.Value = false;

    /// <summary>Renames it, which is what a Save As does after it has chosen a path.</summary>
    /// <param name="newName">What to call it now.</param>
    /// <param name="newLocation">Where it now lives, or <see langword="null" /> to leave that alone.</param>
    public void Rename(string newName, string? newLocation = null) {
        ArgumentNullException.ThrowIfNull(newName);

        name.Value = newName;

        if (newLocation is not null) {
            location.Value = newLocation;
        }
    }

    /// <inheritdoc />
    public bool Save() {
        if (!OnSave()) {
            return false;
        }

        MarkClean();
        return true;
    }

    /// <inheritdoc />
    public bool Revert() {
        if (!OnRevert()) {
            return false;
        }

        MarkClean();
        return true;
    }

    /// <summary>Writes it out. Returning <see langword="false" /> leaves it dirty.</summary>
    protected abstract bool OnSave();

    /// <summary>Reloads it, discarding the changes. Returning <see langword="false" /> leaves it dirty.</summary>
    protected abstract bool OnRevert();
}

/// <summary>The command ids a document answers, and the one call that makes an element answer them.</summary>
/// <remarks>
///     ⚠ <b>Answered through the command route rather than by a service, which is the whole point of
///     putting them here.</b> An application with two documents open in two panels has two answers to
///     ⌘S, and the right one is decided by where the focus is — the route already walks focus →
///     parents → root and picks the nearest handler, so a panel that hosts a document registers
///     these and nothing else has to know how many panels there are. A save routed to "the
///     application's document" is the bug this shape does not have.
/// </remarks>
public static class DocumentCommands {
    /// <summary>Write the nearest document out.</summary>
    public const string Save = "document.save";

    /// <summary>Throw the nearest document's changes away.</summary>
    public const string Revert = "document.revert";

    /// <summary>Makes an element answer <see cref="Save" /> and <see cref="Revert" /> for the document it hosts.</summary>
    /// <param name="element">The element hosting the document — the panel, the tab's content, the window's root.</param>
    /// <exception cref="InvalidOperationException"><paramref name="element" /> hosts no document.</exception>
    /// <remarks>
    ///     <para>
    ///         Both are greyed while there is nothing to write, read live out of
    ///         <see cref="IEditableDocument.IsDirty" /> — so a menu asks the handler and the handler
    ///         asks the signal, with no state in between to go stale.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A greyed item does not re-enable itself when a signal changes; the document has
    ///         to be told the route is out of date.</b> Command state is pulled by whatever is
    ///         showing it, once per raise, so an effect here reads <see cref="IEditableDocument.IsDirty" />
    ///         and calls <c>InvalidateCommands</c> when it moves. Without that, typing into a clean
    ///         document leaves Save greyed until something unrelated happens to invalidate — which
    ///         looks exactly like Save being broken.
    ///     </para>
    /// </remarks>
    public static void Install(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        if (element.HostedDocument is not { } document) {
            throw new InvalidOperationException(
                $"{nameof(Install)} needs {nameof(UiElement.HostedDocument)} set on the element first."
            );
        }

        element.AddCommandHandler(Save, () => document.Save(), () => document.IsDirty.Value);
        element.AddCommandHandler(Revert, () => document.Revert(), () => document.IsDirty.Value);

        var ui = element.Document;
        element.TrackDocumentCommands(new Effect(() => {
            _ = document.IsDirty.Value;
            ui.InvalidateCommands();
        }, ui.Effects));
    }
}

public partial class UiElement {
    Effect? documentCommands;

    /// <summary>The document this element hosts, if it is the one that hosts it.</summary>
    /// <remarks>
    ///     Set on the view that owns it — a code panel, a scene view, a window's root — on
    ///     <see cref="UndoManager" />'s pattern and for its reasons: the *nearest* one wins, so two
    ///     panels showing two documents each answer for their own, and a control deep inside one
    ///     finds the right document without being told which.
    /// </remarks>
    public IEditableDocument? HostedDocument { get; set; }

    /// <summary>The nearest document on the way up, or the UI document's, or none.</summary>
    /// <returns>The document, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Walked on every ask rather than cached</b>, for <see cref="FindUndoManager" />'s
    ///     reason exactly: a panel is torn off into its own window and a cached answer is the one
    ///     that was nearest when the control was built.
    /// </remarks>
    public IEditableDocument? FindHostedDocument() {
        for (var element = this; element is not null; element = element.Parent) {
            if (element.HostedDocument is { } edited) {
                return edited;
            }
        }

        return document?.HostedDocument;
    }

    internal void TrackDocumentCommands(Effect effect) {
        documentCommands?.Dispose();
        documentCommands = effect;
    }

    internal void ForgetDocumentCommands() {
        documentCommands?.Dispose();
        documentCommands = null;
    }
}

public sealed partial class UiDocument {
    /// <summary>The document this whole tree is showing, if the application set one.</summary>
    /// <remarks>
    ///     The single-document application's answer, and the fallback for a tree where no element
    ///     claims one. Null is the ordinary case, as it is for <see cref="Clipboard" /> and
    ///     <see cref="Windows" />.
    /// </remarks>
    public IEditableDocument? HostedDocument { get; set; }
}

/// <summary>A window title that follows the document in it.</summary>
/// <remarks>
///     ⚠ <b>The asterisk is the point and it is not decoration.</b> On every platform an unsaved
///     window says so in its title bar — macOS puts a dot in the close button, Windows and most Linux
///     applications an asterisk after the name — and it is the only indication a user gets before the
///     prompt on close. <c>IUiWindow.Title</c> is a plain setter, so without this every application
///     would write the same effect.
/// </remarks>
public static class UiWindowTitle {
    /// <summary>Keeps a window's title equal to a document's name, marked while it is dirty.</summary>
    /// <param name="window">The window.</param>
    /// <param name="document">The document shown in it.</param>
    /// <param name="scheduler">Where the effect queues. The document's, normally.</param>
    /// <param name="dirtyMarker">What to put in front of the name while it is dirty.</param>
    /// <returns>The effect, which stops following when disposed.</returns>
    /// <remarks>
    ///     ⚠ <b>The first run is queued, not immediate.</b> <c>Effect</c> schedules rather than
    ///     running in its constructor, so the title is whatever the window was opened with until the
    ///     next flush — which is one frame in an application and an explicit <c>Update</c> in a test.
    ///     Anything that asserts on the title straight after this call is asserting on the request's
    ///     title.
    /// </remarks>
    public static IDisposable Bind(
        IUiWindow window,
        IEditableDocument document,
        EffectScheduler? scheduler = null,
        string dirtyMarker = "• "
    ) {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(dirtyMarker);

        return new Effect(
            () => window.Title = document.IsDirty.Value ? dirtyMarker + document.Name.Value : document.Name.Value,
            scheduler
        );
    }
}
