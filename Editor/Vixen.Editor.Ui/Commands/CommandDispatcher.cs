// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>Turns keystrokes into commands.</summary>
/// <remarks>
///     <para>
///         <b>One handler on the root, on the bubble leg.</b> A key event is routed from the focus
///         outwards, so by the time it reaches the root every control that might have wanted it has
///         had its turn — which is what makes Ctrl+Z undo in the scene and undo the typing inside a
///         text box, with no list of exceptions anywhere.
///     </para>
///     <para>
///         ⚠ <b>A chord with no Control or Meta is not taken from a text field.</b> Otherwise a
///         single-key binding — <c>F</c> for frame-selection, which every 3D editor has — would fire
///         while somebody was naming an object, and the object would end up called
///         <c>Cubeaaa</c> with the camera somewhere else. Function keys are exempt, because they are
///         not text.
///     </para>
///     <para>
///         <b>Auto-repeat is ignored.</b> Holding Ctrl+S must save once; the platform reports a
///         stream of presses and <see cref="KeyEvent.IsRepeat" /> is what tells them apart.
///     </para>
/// </remarks>
public sealed class CommandDispatcher {
    readonly CommandRegistry commands;
    readonly KeyMap keys;

    /// <summary>Creates a dispatcher over a registry and a keymap.</summary>
    /// <param name="commands">What can be run.</param>
    /// <param name="keys">What runs it.</param>
    public CommandDispatcher(CommandRegistry commands, KeyMap keys) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        this.commands = commands;
        this.keys = keys;
    }

    /// <summary>Raised when a chord matched a command that could not run.</summary>
    /// <remarks>
    ///     Not silence, because "the shortcut does nothing" is indistinguishable from "the shortcut
    ///     is not bound" and the two have different fixes. A shell shows the command's name and why
    ///     it is unavailable.
    /// </remarks>
    public event Action<EditorCommand>? Refused;

    /// <summary>Listens for shortcuts in a document.</summary>
    /// <param name="document">The document.</param>
    public void Attach(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        document.Root.AddHandler<KeyEvent>((_, args) => Pressed(document, args));
    }

    /// <summary>Runs whatever a key event is bound to.</summary>
    /// <param name="document">The document the event was dispatched into.</param>
    /// <param name="args">The event.</param>
    /// <returns>Whether a command ran.</returns>
    /// <remarks>Public so a host with its own routing, and a test, can drive it directly.</remarks>
    public bool Pressed(UiDocument document, KeyEvent args) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Handled || args.Action != KeyAction.Pressed || args.IsRepeat) {
            return false;
        }

        var chord = KeyChord.Of(args);

        if (!chord.IsBound || !Available(document, chord)) {
            return false;
        }

        // ⚠ Resolved against the context that has the focus, which is what lets the outliner and the
        // content browser both answer Delete. A chord with no binding in that context falls back to
        // the global one — see `KeyMap.CommandFor` — so nothing has to re-declare Ctrl+S per panel.
        if (keys.CommandFor(chord, commands.FocusedContext?.Invoke()) is not { } id
            || !commands.TryGet(id, out var command)) {
            return false;
        }

        // ⚠ Out of scope is a fall-through and not a refusal. The chord resolved to a command
        // belonging somewhere the user is not, which means it is not this keystroke's command at all
        // — reporting it as unavailable would put "Delete — not available right now" on screen every
        // time somebody pressed Delete in a text field.
        if (!commands.IsInScope(command)) {
            return false;
        }

        if (!command.CanExecute) {
            // Handled all the same: the chord *is* this command's, and letting it fall through
            // would have a disabled Ctrl+S type an 's' somewhere.
            args.Handled = true;
            Refused?.Invoke(command);

            return false;
        }

        commands.Execute(id);
        args.Handled = true;

        return true;
    }

    /// <summary>Whether a chord may be taken given where the focus is.</summary>
    static bool Available(UiDocument document, KeyChord chord) {
        if ((chord.Modifiers & (ModifierKeys.Control | ModifierKeys.Meta)) != 0) {
            return true;
        }

        if (chord.Key is >= InputKey.F1 and <= InputKey.F12 or >= InputKey.F13 and <= InputKey.F24 or InputKey.Escape) {
            return true;
        }

        for (var element = document.Focused; element is not null; element = element.Parent) {
            if (element is TextField) {
                return false;
            }
        }

        return true;
    }
}
