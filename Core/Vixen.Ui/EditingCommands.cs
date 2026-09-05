// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;

namespace Vixen.Ui;

/// <summary>Which set of editing chords a document answers.</summary>
/// <remarks>
///     ⚠ <b>Two tables rather than one lenient table, and the leniency is what had to go.</b> Taking
///     Control <i>or</i> Meta for every verb is fine until two platforms disagree about what a chord
///     <i>means</i> — and they do: ⌃A is Select All on Windows and "move to the start of the line" in
///     every AppKit text view, ⌘← is the start of the line on a Mac and nothing on Windows. A single
///     table cannot hold both readings of ⌃A, so a control that took either was not being generous;
///     it was picking Windows and calling it neutral.
/// </remarks>
public enum EditingKeymap : byte {
    /// <summary>Windows and Linux: Control is the verb modifier, Home and End are the line.</summary>
    Windows,

    /// <summary>
    ///     macOS: Command is the verb modifier, Option moves by word, and Control carries the emacs
    ///     bindings every AppKit text view has had since NeXT.
    /// </summary>
    MacOs
}

/// <summary>What a chord means to a text control, independently of which chord it was.</summary>
/// <remarks>
///     <para>
///         <b>The vocabulary, not the keys.</b> AppKit calls these selectors and routes them through
///         <c>doCommandBySelector:</c>; this is the same idea with no reflection in it (ADR-002).
///         Two controls answering the same verb is what stops a text box and a code editor from
///         drifting into two different keyboards, which is exactly what they had done.
///     </para>
///     <para>
///         ⚠ <b>Shift is not in here.</b> Extending the selection is orthogonal to every motion —
///         <see cref="MoveWordLeft" /> with Shift held is the same verb against a different anchor —
///         so doubling the enum would double every table and every switch to say one bit. A control
///         reads <see cref="ModifierKeys.Shift" /> off the event.
///     </para>
///     <para>
///         ⚠ <b>And nothing is in here that neither control answers.</b> <c>transpose</c> and
///         <c>yank</c> are real AppKit verbs and are deliberately absent: an id in a table that no
///         control implements is a menu entry that greys out forever, which is this codebase's
///         commonest defect wearing a keyboard.
///     </para>
/// </remarks>
public enum EditingCommand : byte {
    /// <summary>The chord means nothing to a text control; leave it for whatever else was listening.</summary>
    None,

    /// <summary>One grapheme left.</summary>
    MoveLeft,

    /// <summary>One grapheme right.</summary>
    MoveRight,

    /// <summary>One line up.</summary>
    MoveUp,

    /// <summary>One line down.</summary>
    MoveDown,

    /// <summary>To the previous word boundary.</summary>
    MoveWordLeft,

    /// <summary>To the next word boundary.</summary>
    MoveWordRight,

    /// <summary>To the start of the line.</summary>
    MoveLineStart,

    /// <summary>To the end of the line.</summary>
    MoveLineEnd,

    /// <summary>A screenful up.</summary>
    MovePageUp,

    /// <summary>A screenful down.</summary>
    MovePageDown,

    /// <summary>To the very beginning.</summary>
    MoveDocumentStart,

    /// <summary>To the very end.</summary>
    MoveDocumentEnd,

    /// <summary>Backspace.</summary>
    DeleteBackward,

    /// <summary>Delete.</summary>
    DeleteForward,

    /// <summary>Back to the previous word boundary.</summary>
    DeleteWordBackward,

    /// <summary>Forward to the next word boundary.</summary>
    DeleteWordForward,

    /// <summary>Back to the start of the line.</summary>
    DeleteToLineStart,

    /// <summary>Forward to the end of the line — AppKit's ⌃K, without the kill ring.</summary>
    DeleteToLineEnd,

    /// <summary>A line break.</summary>
    InsertNewline,

    /// <summary>A tab, or an indent of the selected lines — with Shift, an outdent.</summary>
    /// <remarks>
    ///     ⚠ <b>Not two verbs.</b> Shift is stripped before the lookup everywhere else, and an
    ///     <c>Outdent</c> that needed it kept would be the one exception in the table — for a
    ///     distinction that is exactly the same shape as Shift extending a selection. The control
    ///     reads the bit, as it does for every motion.
    /// </remarks>
    InsertTab,

    /// <summary>Select everything.</summary>
    SelectAll,

    /// <summary>Cut.</summary>
    Cut,

    /// <summary>Copy.</summary>
    Copy,

    /// <summary>Paste.</summary>
    Paste,

    /// <summary>Accept the field's value — a form's default action, not a line break.</summary>
    Submit,

    /// <summary>Ask for completions at the caret.</summary>
    ShowCompletion,

    /// <summary>Dismiss whatever is open.</summary>
    Cancel
}

/// <summary>The chord tables, one per platform, and the lookup both text controls go through.</summary>
/// <remarks>
///     <para>
///         <b>One edited table instead of two hand-maintained switches.</b> <c>TextField</c> and
///         <c>CodeEditor</c> each carried a <c>switch (args.Key)</c> over the same vocabulary, and
///         they had already diverged on the question this type exists to answer: the field took
///         Control <i>or</i> Meta with a comment saying the assembly could not know which platform it
///         was on, and the editor took Control only — so ⌘← in the code editor moved by a single
///         character on macOS while the same chord in a text box moved by a word.
///     </para>
///     <para>
///         ⚠ <b>Which table is a document's is a setting with a platform-derived default, not a
///         compile-time question.</b> A test that depended on the machine it ran on would be a suite
///         that passes on Linux and fails on a Mac for a reason nobody could see in the diff; both
///         fixtures pin <see cref="EditingKeymap.Windows" /> and the tables are compared against each
///         other directly.
///     </para>
///     <para>
///         ⚠ <b>Shift is stripped before the lookup and every other modifier must match exactly.</b>
///         The switches this replaces used <c>HasFlag</c>, so ⌃⌥← was word motion — and ⌃⌥← is a
///         window-management chord on two of the three desktops. Exact matching is what lets the
///         macOS table give ⌥← and ⌘← two different meanings at all.
///     </para>
/// </remarks>
public static class EditingCommands {
    /// <summary>The table this machine's platform expects.</summary>
    /// <remarks>
    ///     ⚠ Only a <i>default</i>, and read once per document rather than once per keystroke. A
    ///     remote session, a recorded input trace and a test all legitimately want the other table
    ///     on the same machine, which is why <see cref="UiDocument.EditingKeymap" /> is settable.
    /// </remarks>
    public static EditingKeymap Current { get; } =
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? EditingKeymap.MacOs : EditingKeymap.Windows;

    /// <summary>What a chord means under a keymap.</summary>
    /// <param name="key">The physical key.</param>
    /// <param name="modifiers">What was held, Shift included — it is ignored.</param>
    /// <param name="keymap">Which table to read.</param>
    /// <returns>The verb, or <see cref="EditingCommand.None" /> if this keymap gives it none.</returns>
    public static EditingCommand Resolve(InputKey key, ModifierKeys modifiers, EditingKeymap keymap) {
        // Shift says how, not what. Everything else is matched exactly.
        var held = modifiers & ~ModifierKeys.Shift;

        return keymap == EditingKeymap.MacOs ? MacOs(key, held) : Windows(key, held);
    }

    /// <summary>The command's canonical id, as a keymap file and a menu spell it.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The id, or <c>null</c> for <see cref="EditingCommand.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The three clipboard verbs answer to <c>edit.*</c> and the rest to <c>text.*</c></b>,
    ///     because Cut, Copy and Paste are an application's verbs that a text control happens to
    ///     implement — a menu binds one id and the outliner, the node graph and the field all answer
    ///     it — while "move to the previous word boundary" is a text control's alone and no other
    ///     responder has a reading of it.
    /// </remarks>
    public static string? Id(EditingCommand command) =>
        command switch {
            EditingCommand.MoveLeft => "text.move-left",
            EditingCommand.MoveRight => "text.move-right",
            EditingCommand.MoveUp => "text.move-up",
            EditingCommand.MoveDown => "text.move-down",
            EditingCommand.MoveWordLeft => "text.move-word-left",
            EditingCommand.MoveWordRight => "text.move-word-right",
            EditingCommand.MoveLineStart => "text.move-line-start",
            EditingCommand.MoveLineEnd => "text.move-line-end",
            EditingCommand.MovePageUp => "text.move-page-up",
            EditingCommand.MovePageDown => "text.move-page-down",
            EditingCommand.MoveDocumentStart => "text.move-document-start",
            EditingCommand.MoveDocumentEnd => "text.move-document-end",
            EditingCommand.DeleteBackward => "text.delete-backward",
            EditingCommand.DeleteForward => "text.delete-forward",
            EditingCommand.DeleteWordBackward => "text.delete-word-backward",
            EditingCommand.DeleteWordForward => "text.delete-word-forward",
            EditingCommand.DeleteToLineStart => "text.delete-to-line-start",
            EditingCommand.DeleteToLineEnd => "text.delete-to-line-end",
            EditingCommand.InsertNewline => "text.insert-newline",
            EditingCommand.InsertTab => "text.insert-tab",
            EditingCommand.SelectAll => "edit.select-all",
            EditingCommand.Cut => "edit.cut",
            EditingCommand.Copy => "edit.copy",
            EditingCommand.Paste => "edit.paste",
            EditingCommand.Submit => "text.submit",
            EditingCommand.ShowCompletion => "text.show-completion",
            EditingCommand.Cancel => "text.cancel",
            _ => null
        };

    /// <summary>Windows and Linux.</summary>
    static EditingCommand Windows(InputKey key, ModifierKeys held) =>
        (key, held) switch {
            (InputKey.Left, ModifierKeys.None) => EditingCommand.MoveLeft,
            (InputKey.Left, ModifierKeys.Control) => EditingCommand.MoveWordLeft,
            (InputKey.Right, ModifierKeys.None) => EditingCommand.MoveRight,
            (InputKey.Right, ModifierKeys.Control) => EditingCommand.MoveWordRight,
            (InputKey.Up, ModifierKeys.None) => EditingCommand.MoveUp,
            (InputKey.Down, ModifierKeys.None) => EditingCommand.MoveDown,
            (InputKey.Home, ModifierKeys.None) => EditingCommand.MoveLineStart,
            (InputKey.Home, ModifierKeys.Control) => EditingCommand.MoveDocumentStart,
            (InputKey.End, ModifierKeys.None) => EditingCommand.MoveLineEnd,
            (InputKey.End, ModifierKeys.Control) => EditingCommand.MoveDocumentEnd,
            (InputKey.PageUp, ModifierKeys.None) => EditingCommand.MovePageUp,
            (InputKey.PageDown, ModifierKeys.None) => EditingCommand.MovePageDown,

            (InputKey.Backspace, ModifierKeys.None) => EditingCommand.DeleteBackward,
            (InputKey.Backspace, ModifierKeys.Control) => EditingCommand.DeleteWordBackward,
            (InputKey.Delete, ModifierKeys.None) => EditingCommand.DeleteForward,
            (InputKey.Delete, ModifierKeys.Control) => EditingCommand.DeleteWordForward,

            (InputKey.Enter or InputKey.KeypadEnter, ModifierKeys.None) => EditingCommand.InsertNewline,
            (InputKey.Enter or InputKey.KeypadEnter, ModifierKeys.Control) => EditingCommand.Submit,
            (InputKey.Tab, ModifierKeys.None) => EditingCommand.InsertTab,

            (InputKey.A, ModifierKeys.Control) => EditingCommand.SelectAll,
            (InputKey.X, ModifierKeys.Control) => EditingCommand.Cut,
            (InputKey.C, ModifierKeys.Control) => EditingCommand.Copy,
            (InputKey.V, ModifierKeys.Control) => EditingCommand.Paste,
            (InputKey.Space, ModifierKeys.Control) => EditingCommand.ShowCompletion,
            (InputKey.Escape, ModifierKeys.None) => EditingCommand.Cancel,

            _ => EditingCommand.None
        };

    /// <summary>macOS, including the emacs bindings every AppKit text view answers.</summary>
    /// <remarks>
    ///     ⚠ <b>⌃K deletes to the end of the line and does not fill a kill ring</b>, so ⌃Y is
    ///     deliberately not in the table. Yank without a ring is a paste wearing the wrong chord, and
    ///     a ring is a second clipboard with its own lifetime — worth having and worth being asked
    ///     for, rather than half-built here.
    /// </remarks>
    static EditingCommand MacOs(InputKey key, ModifierKeys held) =>
        (key, held) switch {
            (InputKey.Left, ModifierKeys.None) => EditingCommand.MoveLeft,
            (InputKey.Left, ModifierKeys.Alt) => EditingCommand.MoveWordLeft,
            (InputKey.Left, ModifierKeys.Meta) => EditingCommand.MoveLineStart,
            (InputKey.Right, ModifierKeys.None) => EditingCommand.MoveRight,
            (InputKey.Right, ModifierKeys.Alt) => EditingCommand.MoveWordRight,
            (InputKey.Right, ModifierKeys.Meta) => EditingCommand.MoveLineEnd,
            (InputKey.Up, ModifierKeys.None) => EditingCommand.MoveUp,
            (InputKey.Up, ModifierKeys.Meta) => EditingCommand.MoveDocumentStart,
            (InputKey.Down, ModifierKeys.None) => EditingCommand.MoveDown,
            (InputKey.Down, ModifierKeys.Meta) => EditingCommand.MoveDocumentEnd,

            // Full-size Apple keyboards have them, and they mean what they say on every other
            // desktop. A Mac table that answered nothing to Home would be worse than one that reads
            // the key the way the key is labelled.
            (InputKey.Home, ModifierKeys.None) => EditingCommand.MoveLineStart,
            (InputKey.End, ModifierKeys.None) => EditingCommand.MoveLineEnd,
            (InputKey.PageUp, ModifierKeys.None) => EditingCommand.MovePageUp,
            (InputKey.PageDown, ModifierKeys.None) => EditingCommand.MovePageDown,

            (InputKey.Backspace, ModifierKeys.None) => EditingCommand.DeleteBackward,
            (InputKey.Backspace, ModifierKeys.Alt) => EditingCommand.DeleteWordBackward,
            (InputKey.Backspace, ModifierKeys.Meta) => EditingCommand.DeleteToLineStart,
            (InputKey.Delete, ModifierKeys.None) => EditingCommand.DeleteForward,
            (InputKey.Delete, ModifierKeys.Alt) => EditingCommand.DeleteWordForward,

            (InputKey.Enter or InputKey.KeypadEnter, ModifierKeys.None) => EditingCommand.InsertNewline,
            (InputKey.Enter or InputKey.KeypadEnter, ModifierKeys.Meta) => EditingCommand.Submit,
            (InputKey.Tab, ModifierKeys.None) => EditingCommand.InsertTab,

            (InputKey.A, ModifierKeys.Meta) => EditingCommand.SelectAll,
            (InputKey.X, ModifierKeys.Meta) => EditingCommand.Cut,
            (InputKey.C, ModifierKeys.Meta) => EditingCommand.Copy,
            (InputKey.V, ModifierKeys.Meta) => EditingCommand.Paste,
            (InputKey.Space, ModifierKeys.Meta) => EditingCommand.ShowCompletion,
            (InputKey.Escape, ModifierKeys.None) => EditingCommand.Cancel,

            // The emacs half, which is not a nicety: ⌃A, ⌃E, ⌃B, ⌃F, ⌃N, ⌃P, ⌃D and ⌃K work in every
            // AppKit text view including Safari's, so a Mac user reaches for them without deciding
            // to, and a field that answered ⌃A with Select All would look like it had lost the text.
            (InputKey.A, ModifierKeys.Control) => EditingCommand.MoveLineStart,
            (InputKey.E, ModifierKeys.Control) => EditingCommand.MoveLineEnd,
            (InputKey.B, ModifierKeys.Control) => EditingCommand.MoveLeft,
            (InputKey.F, ModifierKeys.Control) => EditingCommand.MoveRight,
            (InputKey.P, ModifierKeys.Control) => EditingCommand.MoveUp,
            (InputKey.N, ModifierKeys.Control) => EditingCommand.MoveDown,
            (InputKey.D, ModifierKeys.Control) => EditingCommand.DeleteForward,
            (InputKey.H, ModifierKeys.Control) => EditingCommand.DeleteBackward,
            (InputKey.K, ModifierKeys.Control) => EditingCommand.DeleteToLineEnd,

            _ => EditingCommand.None
        };
}

public sealed partial class UiDocument {
    /// <summary>Which editing chords the text controls in this document answer.</summary>
    /// <remarks>
    ///     Defaults to <see cref="EditingCommands.Current" />, which is the platform's. An
    ///     application that knows better — a remote session showing a Mac's interface on Windows,
    ///     a test — assigns the other.
    /// </remarks>
    public EditingKeymap EditingKeymap { get; set; } = EditingCommands.Current;
}
