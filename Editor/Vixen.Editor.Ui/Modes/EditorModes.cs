// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Ui;

/// <summary>Which modes the editor has, and which one the viewport's input means right now.</summary>
/// <remarks>
///     <para>
///         <b>The registry behind the mode bar, and it is a registry for the same reason the command
///         table is one.</b> A mode adds a button to the strip, a radio entry to the palette, a
///         context to the keymap and a claim on viewport input, and all four come from one
///         <see cref="Add" /> — because the alternative, four places that each know what Blockout is,
///         is what doc 20's A1 calls "six mutually-exclusive booleans on the viewport".
///     </para>
///     <para>
///         ⚠ <b>Exactly one mode is active whenever there is a mode at all.</b> The first one added
///         becomes active, and removing the active one falls back rather than leaving none — a
///         viewport whose input means nothing is not a state any gesture knows how to be in. An
///         editor with no modes registered is the shell on its own, which is the arrangement every
///         test and every sample uses.
///     </para>
///     <para>
///         ⚠ <b>Activation is idempotent and a re-activation does nothing.</b> The mode bar's buttons
///         are ordinary commands and a command runs whenever it is clicked, so pressing the button of
///         the mode you are already in must not put a tool through
///         <see cref="IEditorMode.Deactivated" /> and back.
///     </para>
/// </remarks>
public sealed class EditorModes {
    readonly Dictionary<string, IEditorMode> byId = new(StringComparer.Ordinal);
    readonly List<IEditorMode> ordered = [];
    readonly EditorShell shell;

    /// <summary>Creates the registry over a shell.</summary>
    /// <param name="shell">Where the modes' commands, bindings and panels go.</param>
    /// <remarks>
    ///     ⚠ <b>Constructed by <see cref="EditorShell" /> itself, from inside its own constructor.</b>
    ///     Safe because nothing here touches the shell until a mode is added, and the shell's command
    ///     registry and workspace both exist by then. An application reaches this through
    ///     <see cref="EditorShell.Modes" /> rather than building one.
    /// </remarks>
    public EditorModes(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
    }

    /// <summary>The modes, in the order they were registered.</summary>
    /// <remarks>Which is the order they are drawn in, and the reason Select is added first.</remarks>
    public IReadOnlyList<IEditorMode> Modes => ordered;

    /// <summary>The one the viewport's input means, or <see langword="null" /> if there are none.</summary>
    public IEditorMode? Active { get; private set; }

    /// <summary>Which command context is in force because of the active mode, if any.</summary>
    /// <remarks>
    ///     What a host puts on <see cref="EditorShell.Context" /> in place of the viewport's own when
    ///     the viewport is what has the focus. Null both for no mode and for a mode that claims no
    ///     keys, so a caller can write <c>Modes.Context ?? SceneContext</c> and be right in every case.
    /// </remarks>
    public string? Context => Active?.Context;

    /// <summary>Raised when the set changes or a different mode becomes active.</summary>
    /// <remarks>What the shell rebuilds the mode bar from.</remarks>
    public event Action<EditorModes>? Changed;

    /// <summary>What the command that enters a mode is called.</summary>
    /// <param name="modeId">The mode.</param>
    /// <returns>The command id.</returns>
    public static string ModeCommand(string modeId) => "mode." + modeId;

    /// <summary>Registers a mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The mode, so a caller can keep hold of it.</returns>
    /// <exception cref="ArgumentException">Something is already registered under that id.</exception>
    /// <remarks>
    ///     <para>
    ///         The mode's own <see cref="IEditorMode.Register" /> runs first, so that its commands are
    ///         in the registry before the button that enters it is — which is what makes a mode's
    ///         verbs listed in the keybinding editor and rebindable without anybody having entered the
    ///         mode.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first mode added is activated, and it is activated <i>after</i> it is in the
    ///         list.</b> Its <see cref="IEditorMode.Activated" /> may look the registry up, and a mode
    ///         that could not find itself there would be one whose activation reads the state of the
    ///         editor before it existed.
    ///     </para>
    /// </remarks>
    public IEditorMode Add(IEditorMode mode) {
        ArgumentNullException.ThrowIfNull(mode);

        if (byId.ContainsKey(mode.Id)) {
            throw new ArgumentException($"A mode is already registered as '{mode.Id}'.", nameof(mode));
        }

        mode.Register(shell);

        byId.Add(mode.Id, mode);
        ordered.Add(mode);

        shell.Commands.Add(
            new EditorCommand(ModeCommand(mode.Id), mode.Title, () => Activate(mode.Id)) {
                Category = EditorStrings.CategoryMode,
                Icon = mode.Icon,
                Art = mode.Art,

                // ⚠ A radio group rather than a set of ticks, because entering a mode is a choice
                // between the modes and not four independent switches. `ToolbarGroup` draws the
                // strip as one segmented control for the same reason.
                RadioGroup = ModeGroup,
                Checked = () => IsActive(mode.Id)
            }
        );

        if (Active is null) {
            Enter(mode);
        }

        Changed?.Invoke(this);
        return mode;
    }

    /// <summary>Enters the next mode along the strip, wrapping at the end.</summary>
    /// <returns>Whether there was one to enter.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What <c>Tab</c> does.</b> A mode is a statement about what a click means for the
    ///         whole session, there are a handful of them, and cycling is how somebody moves between
    ///         them without taking a hand off the mouse to find a number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cycles rather than toggling between two.</b> "Back to Select" sounds tidier and
    ///         is wrong the moment there are four: it would make Terrain reachable only from Select,
    ///         so somebody in Blockout who wants Terrain presses Tab twice and lands back where they
    ///         started.
    ///     </para>
    /// </remarks>
    public bool Next() {
        if (ordered.Count == 0) {
            return false;
        }

        var index = Active is null ? -1 : ordered.IndexOf(Active);

        return Activate(ordered[(index + 1) % ordered.Count].Id);
    }

    /// <summary>Takes a mode back out.</summary>
    /// <param name="modeId">Its id.</param>
    /// <returns>Whether it was registered.</returns>
    /// <remarks>
    ///     ⚠ <b>The active mode is left first and something else is entered.</b> Unloading the plugin
    ///     whose mode you are in must not leave the viewport's input meaning a mode that is no longer
    ///     loaded — and the fall-back is the first remaining mode, which is Select in the shipped
    ///     editor because Select is added first.
    /// </remarks>
    public bool Remove(string modeId) {
        ArgumentNullException.ThrowIfNull(modeId);

        if (!byId.Remove(modeId, out var mode)) {
            return false;
        }

        ordered.Remove(mode);
        shell.Commands.Remove(ModeCommand(modeId));

        if (ReferenceEquals(Active, mode)) {
            Leave(mode);
            Active = null;

            if (ordered.Count > 0) {
                Enter(ordered[0]);
            }
        }

        mode.Unregister(shell);

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>Looks a mode up.</summary>
    /// <param name="modeId">Its id.</param>
    /// <param name="mode">The mode, if there is one.</param>
    /// <returns>Whether there is.</returns>
    public bool TryGet(string modeId, [NotNullWhen(true)] out IEditorMode? mode) {
        ArgumentNullException.ThrowIfNull(modeId);
        return byId.TryGetValue(modeId, out mode);
    }

    /// <summary>Whether a mode is the active one.</summary>
    /// <param name="modeId">Its id.</param>
    /// <returns>Whether it is.</returns>
    public bool IsActive(string modeId) =>
        Active is { } active && string.Equals(active.Id, modeId, StringComparison.Ordinal);

    /// <summary>Makes a mode the active one.</summary>
    /// <param name="modeId">Its id.</param>
    /// <returns>Whether it is now active — false for an id nothing registered.</returns>
    public bool Activate(string modeId) {
        ArgumentNullException.ThrowIfNull(modeId);

        if (IsActive(modeId)) {
            return true;
        }

        if (!byId.TryGetValue(modeId, out var mode)) {
            return false;
        }

        if (Active is { } previous) {
            Leave(previous);
        }

        Enter(mode);
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>What the mode bar shows: the mode buttons, then the active mode's own strip.</summary>
    /// <returns>The entries, which is empty when nothing has registered a mode.</returns>
    /// <remarks>
    ///     ⚠ <b>One strip rather than two, and the separator is only there when there is something on
    ///     the other side of it.</b> This is Unreal's arrangement — the mode buttons on the left and
    ///     the mode's own tools immediately beside them — and it is the arrangement that makes the
    ///     tools read as belonging to the mode rather than to the window.
    /// </remarks>
    public IReadOnlyList<ToolbarEntry> Bar() {
        if (ordered.Count == 0) {
            return [];
        }

        List<ToolbarEntry> entries = [new ToolbarGroup([.. ordered.Select(mode => ModeCommand(mode.Id))])];

        if (Active?.Toolbar is { Count: > 0 } tools) {
            entries.Add(new ToolbarSeparator());
            entries.AddRange(tools);
        }

        return entries;
    }

    /// <summary>The radio group the mode buttons are all in.</summary>
    const string ModeGroup = "mode";

    void Enter(IEditorMode mode) {
        Active = mode;
        mode.Activated();

        if (mode.Panel is { Length: > 0 } panel) {
            shell.Workspace.Open(panel);
        }
    }

    void Leave(IEditorMode mode) {
        // ⚠ The panel goes before the mode is told, so that a mode which writes to its own panel from
        // `Deactivated` is writing to one that is already gone rather than to one that is about to
        // be — the second is a panel that flashes the tool's final state on the way out.
        if (mode.Panel is { Length: > 0 } panel) {
            shell.Workspace.Close(panel);
        }

        mode.Deactivated();
    }
}
