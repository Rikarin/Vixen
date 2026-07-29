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

    /// <summary>Which focus context it belongs to, or <c>null</c> for "anywhere".</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Delete means one thing in the outliner and another in the content browser, and
    ///         this is what tells them apart.</b> Both are Delete, both want the Delete key, and
    ///         guessing from an enablement predicate — "is anything selected in the scene?" — gets it
    ///         wrong the moment both have a selection, which after clicking an asset and then a row
    ///         is most of the time. A command declares the context it belongs to, the shell tracks
    ///         which context has the focus, and <see cref="CommandRegistry.IsInScope" /> is what
    ///         stops the wrong one running.
    ///     </para>
    ///     <para>
    ///         <b>Out of scope is not the same as disabled, and both grey the line out.</b> The
    ///         distinction matters to the dispatcher rather than to the reader: a chord aimed at a
    ///         command that is merely disabled is reported as refused, and one aimed at a command
    ///         belonging to another context is left alone so that the context which <i>does</i> have
    ///         the focus can have it.
    ///     </para>
    /// </remarks>
    public string? Context { get; init; }

    /// <summary>Which set of mutually-exclusive choices it is one of, or <c>null</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Translate, Rotate and Scale are a choice, and three ticks is not how a choice
    ///     reads.</b> A command in a radio group draws a dot rather than a tick, so a menu shows one
    ///     mark against the mode that is current instead of a column the reader has to interpret.
    ///     Setting this without <see cref="Checked" /> marks nothing: the group says how the state is
    ///     drawn and the predicate is still what says which member holds it.
    /// </remarks>
    public string? RadioGroup { get; init; }

    /// <summary>The icon a toolbar draws for it, if it has one.</summary>
    public PathBuilder? Icon { get; init; }

    /// <summary>A style class every view puts on the control it makes for this command.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What lets Play be green without the toolbar knowing what Play is.</b> A transport
    ///         strip has to say which button is which by colour — a row of identical grey glyphs is
    ///         one the eye has to read rather than recognise — and the alternative to a class is a
    ///         presenter with a list of command ids in it, which is the registry's whole point
    ///         undone.
    ///     </para>
    ///     <para>
    ///         The class is the command's and the rules are the theme's, so a plugin can colour its
    ///         own button by shipping a stylesheet rather than by asking the shell for a hook.
    ///     </para>
    /// </remarks>
    public string? ClassName { get; init; }

    /// <summary>Whether it can be run right now, or <c>null</c> for "always".</summary>
    /// <remarks>Asked every time a view needs to know. It must be cheap and must not throw.</remarks>
    public Func<bool>? Enablement { get; init; }

    /// <summary>Why it cannot be run at all yet, or the default for one that can.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20's first bar: a verb that is not implemented is <i>visibly</i> not
    ///         implemented rather than absent.</b> A menu line that is missing reads as an editor
    ///         that cannot do the thing; a line that is there and greyed reads as an editor that will
    ///         — and it is the difference between somebody filing "there is no Build and Run" and
    ///         somebody reading why. Setting this disables the command wherever it appears and gives
    ///         the palette and the refusal notice the sentence to show.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not a substitute for <see cref="Enablement" /> and does not compose with
    ///         it.</b> "Nothing is selected" is enablement and changes second by second; this is
    ///         "there is no sequencer yet" and changes when somebody writes one. A command carrying
    ///         both would be one whose predicate nobody ever reaches.
    ///     </para>
    /// </remarks>
    public StringId Unavailable { get; init; }

    /// <summary>Whether it is declared but not built yet.</summary>
    public bool IsUnavailable => Unavailable.Id is { Length: > 0 };

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
    public bool CanExecute => !IsUnavailable && (Enablement is null || Enablement());

    /// <summary>Whether it is a toggle that is on.</summary>
    public bool IsChecked => Checked is not null && Checked();

    /// <inheritdoc />
    public override string ToString() => Id;
}
