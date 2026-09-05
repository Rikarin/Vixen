// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>Somewhere for a control to put an edit so that it can be taken back.</summary>
/// <remarks>
///     <para>
///         <b>A control <i>finds</i> a manager rather than owning one.</b> <c>CodeBuffer</c> argues
///         the point and is right: undo belongs to the application, because it has to interleave with
///         everything else — a rename that touched three files, a refactor, a move — and a stack
///         inside a text control can only ever undo typing. What the argument does not settle is
///         where the control looks, and until this existed the answer was nowhere, so a dialog's text
///         box had no ⌘Z in any Vixen application including the editor.
///     </para>
///     <para>
///         ⚠ <b>Nearest wins, and nothing is a real answer.</b> The lookup walks the element's
///         ancestors and then the document, which is AppKit's <c>NSResponder.undoManager</c> with the
///         ceremony removed. A field in a dialog with no document behind it finds nothing, registers
///         nothing, and leaves ⌘Z to whatever else was listening — which is the right behaviour and
///         is what keeps a text field from shadowing an application's own Undo.
///     </para>
///     <para>
///         ⚠ <b>Closures, not commands.</b> An edit is two delegates. There is no interface for a
///         caller to implement, no reflection and nothing to register (ADR-002); an implementation
///         that already has a command stack — the editor's — adapts by wrapping its own command in
///         the pair.
///     </para>
/// </remarks>
public interface IUndoManager {
    /// <summary>Whether there is anything to take back.</summary>
    bool CanUndo { get; }

    /// <summary>Whether there is anything to put back.</summary>
    bool CanRedo { get; }

    /// <summary>Whether an undo or a redo is running right now.</summary>
    /// <remarks>
    ///     ⚠ <b>The one piece of state every caller needs and nobody expects to.</b> Undoing an edit
    ///     re-runs the code that made it, so a control that registered unconditionally would push the
    ///     undo of an undo onto the stack and never reach the state before it. Every implementation
    ///     answers this and every registrant checks it.
    /// </remarks>
    bool IsPerforming { get; }

    /// <summary>Records something that has already happened.</summary>
    /// <param name="name">What to call it — "Typing", "Paste" — for a menu that shows "Undo Typing".</param>
    /// <param name="undo">Puts the world back.</param>
    /// <param name="redo">Does it again.</param>
    /// <remarks>The edit has been applied; this is the record of it, not the doing of it.</remarks>
    void Register(string name, Action undo, Action redo);

    /// <summary>Takes back the most recent edit.</summary>
    /// <returns>Whether there was one.</returns>
    bool Undo();

    /// <summary>Puts back the most recently undone edit.</summary>
    /// <returns>Whether there was one.</returns>
    bool Redo();
}

/// <summary>The ordinary in-memory undo stack.</summary>
/// <remarks>
///     <para>
///         What a document with no command stack of its own wants, and what <c>UiApplication</c>
///         installs so that a dialog's text box has ⌘Z without an application deciding to.
///     </para>
///     <para>
///         ⚠ <b>Bounded, and the bound is a decision.</b> An edit is two closures over the strings
///         either side of it, so an unbounded stack in a long-lived application is a copy of every
///         version of every field it ever showed. <see cref="Limit" /> drops the oldest, which is what
///         every editor does and is why "undo all the way to the beginning" is not a promise anything
///         makes.
///     </para>
/// </remarks>
public sealed class UndoManager : IUndoManager {
    readonly List<(string Name, Action Undo, Action Redo)> undone = [];
    readonly List<(string Name, Action Undo, Action Redo)> done = [];

    /// <summary>How many edits it keeps.</summary>
    public int Limit { get; init; } = 128;

    /// <inheritdoc />
    public bool CanUndo => done.Count > 0;

    /// <inheritdoc />
    public bool CanRedo => undone.Count > 0;

    /// <inheritdoc />
    public bool IsPerforming { get; private set; }

    /// <summary>What the next undo would take back, for a menu item's label.</summary>
    public string? UndoName => done.Count > 0 ? done[^1].Name : null;

    /// <summary>What the next redo would put back.</summary>
    public string? RedoName => undone.Count > 0 ? undone[^1].Name : null;

    /// <inheritdoc />
    public void Register(string name, Action undo, Action redo) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);

        // ⚠ Silently, and it is not a guard against a caller's mistake. Undoing re-runs the code that
        // made the edit, so an implementation that threw here would make every correct control
        // responsible for a check the manager is already doing.
        if (IsPerforming) {
            return;
        }

        // A new edit ends the redo future, exactly as everywhere else: the states beyond this point
        // were reached from a past that no longer happened.
        undone.Clear();

        done.Add((name, undo, redo));

        if (done.Count > Limit) {
            done.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public bool Undo() {
        if (done.Count == 0) {
            return false;
        }

        var edit = done[^1];
        done.RemoveAt(done.Count - 1);

        Perform(edit.Undo);
        undone.Add(edit);

        return true;
    }

    /// <inheritdoc />
    public bool Redo() {
        if (undone.Count == 0) {
            return false;
        }

        var edit = undone[^1];
        undone.RemoveAt(undone.Count - 1);

        Perform(edit.Redo);
        done.Add(edit);

        return true;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() {
        done.Clear();
        undone.Clear();
    }

    void Perform(Action action) {
        IsPerforming = true;

        try {
            action();
        } finally {
            IsPerforming = false;
        }
    }
}

public partial class UiElement {
    /// <summary>The undo manager this element hosts, if it is one.</summary>
    /// <remarks>
    ///     Set on the view that owns a document object — a code editor's panel, an inspector — so
    ///     that everything inside it registers with that document's stack rather than the
    ///     application's.
    /// </remarks>
    public IUndoManager? UndoManager { get; set; }

    /// <summary>The nearest undo manager on the way up, or the document's, or none.</summary>
    /// <returns>The manager, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Walked on every edit rather than cached.</b> An element is reparented, a panel is
    ///     torn off into its own window, and a cached manager would be the one that was nearest when
    ///     the control was built. The walk is a handful of pointer hops and an edit is a keystroke.
    /// </remarks>
    public IUndoManager? FindUndoManager() {
        for (var element = this; element is not null; element = element.Parent) {
            if (element.UndoManager is { } manager) {
                return manager;
            }

            // ⚠ The responders appended at this element too, and in the same order the command walk
            // asks them: this is `NSResponder.undoManager`, and the object that owns a document's
            // stack is usually a controller rather than a view. An element leg that skipped them
            // would make a view controller able to answer `edit.undo` and unable to supply the
            // manager the edit has to be recorded on.
            var responders = element.Responders;

            for (var i = 0; i < responders.Count; i++) {
                if (responders[i].UndoManager is { } appended) {
                    return appended;
                }
            }
        }

        return document?.UndoManager;
    }
}

public sealed partial class UiDocument {
    /// <summary>The document-wide undo stack, if anything installed one.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is a real answer</b>, the way <see cref="Windows" /> and <see cref="Clipboard" />
    ///     are: a control that finds nothing registers nothing and leaves ⌘Z alone, which is what
    ///     keeps a text field in the editor from shadowing the editor's own Undo with a stack that
    ///     knows about typing and nothing else.
    /// </remarks>
    public IUndoManager? UndoManager { get; set; }
}
