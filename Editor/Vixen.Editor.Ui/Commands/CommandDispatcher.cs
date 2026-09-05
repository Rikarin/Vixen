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
///     <para>
///         ⚠ <b>The chord goes through <see cref="CommandRoute" /> before the table, and it used not
///         to.</b> A chord resolved to an id and then went straight to <see cref="CommandRegistry" />
///         — a flat lookup — so an element that had registered a handler for that id answered the
///         menu item and not the shortcut printed beside it. The same verb reached two different
///         handlers depending on how it was invoked, and the difference was invisible because both
///         did *something*. Now the element walk runs first: a caret in a text box means ⌘C copies
///         the text, exactly as clicking Edit ▸ Copy already did.
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

        // ⚠ Swapped into the vocabulary the table is written in before anything is looked up. On a
        // Mac the user pressed ⌘S and the keymap holds Ctrl+S — one portable spelling in the file
        // and in the model, adapted at the two ends. See `KeyChord.ForPlatform`.
        var chord = KeyChord.Of(args).ForPlatform();

        if (!chord.IsBound || !Available(document, chord)) {
            return false;
        }

        // ⚠ Resolved against the context that has the focus, which is what lets the outliner and the
        // content browser both answer Delete. A chord with no binding in that context falls back to
        // the global one — see `KeyMap.CommandFor` — so nothing has to re-declare Ctrl+S per panel.
        if (keys.CommandFor(chord, commands.FocusedContext?.Invoke()) is not { } id) {
            return false;
        }

        if (Focused(document, id) is { } focused) {
            return Run(focused, id, args);
        }

        if (!commands.TryGet(id, out var command)) {
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

    /// <summary>The element leg of <see cref="CommandRoute" />, or <c>null</c> when nothing in the
    ///     tree answers.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole route and not an element-only walk, filtered on
    ///         <see cref="CommandHandler.Element" />.</b> The tail of the chain is
    ///         <see cref="CommandRegistry" /> itself — <see cref="EditorShell" /> installs it as the
    ///         document's <see cref="UiDocument.ApplicationCommandResponder" /> — so a resolve that
    ///         reached the end would hand back the very command this method exists to fall through
    ///         to, and running it here would skip the scope gate below. A non-element answer means
    ///         "the tree was silent", which is the same thing <c>null</c> means.
    ///     </para>
    /// </remarks>
    static CommandHandler? Focused(UiDocument document, string id) =>
        CommandRoute.Resolve(document, id) is { Element: not null } handler ? handler : null;

    /// <summary>Runs what the focused element answered with, or reports that it refused.</summary>
    /// <remarks>
    ///     ⚠ <b>A refusal here does not fall through to the editor's command of the same id.</b>
    ///     <c>Commands.cs</c>'s defining rule is that the nearest responder that answers wins and its
    ///     <see cref="CommandHandler.CanExecute" /> is the only one asked; an empty text box saying
    ///     no to <c>edit.select-all</c> must not then select every entity in the scene.
    /// </remarks>
    bool Run(CommandHandler handler, string id, KeyEvent args) {
        args.Handled = true;

        if (handler.CanExecute) {
            handler.Run();
            return true;
        }

        // The editor's command is what names the verb on screen, so a refusal reports it when there
        // is one. A control answering an id the registry never heard of refuses in silence.
        if (commands.TryGet(id, out var named)) {
            Refused?.Invoke(named);
        }

        return false;
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
