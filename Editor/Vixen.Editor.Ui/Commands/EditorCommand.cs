// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Ui;

/// <summary>Something the editor can be asked to do, named by an id.</summary>
/// <remarks>
///     <para>
///         <b>Every action in the editor is one of these, and that is the whole architecture.</b>
///         Menus, toolbars, context menus and the command palette are <i>views over the registry</i>
///         rather than four places that each know how to save a file — so an action added once
///         appears everywhere it belongs, gets a keybinding, gets a place in the palette, and gets
///         its enablement from one predicate instead of four copies that drift.
///     </para>
///     <para>
///         ⚠ <b>Enablement is a predicate rather than a flag.</b> A flag has to be pushed at every
///         menu, every toolbar and the palette whenever the world changes, which means a menu that
///         is right only if somebody remembered to invalidate it. Asked on demand — a menu asks as
///         it opens, a toolbar as the shell ticks — it cannot be stale. The cost is that the
///         predicate runs often, so it has to be cheap: <c>stack.CanUndo</c>, not a directory scan.
///     </para>
///     <para>
///         <b>A command carries no keybinding.</b> That lives in <see cref="KeyMap" />, because a
///         binding is the user's and a command is the application's — putting the chord here would
///         mean a remapped shortcut edited the command table, and a plugin's command would arrive
///         with a chord it has no right to claim.
///     </para>
/// </remarks>
public sealed class EditorCommand {
    /// <summary>Creates a command.</summary>
    /// <param name="id">
    ///     What everything refers to it by: <c>file.save</c>, <c>view.panel.hierarchy</c>. Dotted,
    ///     lower-case and stable, because it is what a keymap file, a menu model and a plugin all
    ///     name it.
    /// </param>
    /// <param name="title">What it is called on screen.</param>
    /// <param name="run">What it does.</param>
    public EditorCommand(string id, StringId title, Action run) {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(run);

        Id = id;
        Title = title;
        Run = run;
    }

    /// <summary>What everything refers to it by.</summary>
    public string Id { get; }

    /// <summary>What it is called on screen.</summary>
    public StringId Title { get; }

    /// <summary>Where the palette files it, and what a menu would group it under.</summary>
    public StringId Category { get; init; }

    /// <summary>The icon a toolbar draws for it, if it has one.</summary>
    public PathBuilder? Icon { get; init; }

    /// <summary>Whether it can be run right now, or <c>null</c> for "always".</summary>
    /// <remarks>Asked every time a view needs to know. It must be cheap and must not throw.</remarks>
    public Func<bool>? Enablement { get; init; }

    /// <summary>Whether it is a toggle that is currently on, or <c>null</c> if it is not a toggle.</summary>
    /// <remarks>
    ///     What puts a tick beside "Show Grid" in a menu and makes its toolbar button look pressed.
    ///     Asked on demand for the same reason <see cref="Enablement" /> is.
    /// </remarks>
    public Func<bool>? Checked { get; init; }

    /// <summary>Whether it is hidden from the palette.</summary>
    /// <remarks>
    ///     For the handful that are noise there: the per-panel toggles a menu already lists one
    ///     level up, and anything whose title would be ambiguous without the menu around it.
    /// </remarks>
    public bool IsHiddenFromPalette { get; init; }

    /// <summary>What it does.</summary>
    internal Action Run { get; }

    /// <summary>Whether it can be run right now.</summary>
    public bool CanExecute => Enablement is null || Enablement();

    /// <summary>Whether it is a toggle that is on.</summary>
    public bool IsChecked => Checked is not null && Checked();

    /// <inheritdoc />
    public override string ToString() => Id;
}
