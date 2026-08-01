// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Ui;

/// <summary>A statement about what the viewport's input means right now.</summary>
/// <remarks>
///     <para>
///         <b>A mode is not a toolbar of commands.</b> Unreal's Select / Landscape / Foliage / Mesh
///         Paint strip is a claim about what a click in the viewport <i>is</i>, and doc 20's A1 asks
///         for the interface before the second mode exists because retrofitting one is how editors
///         end up with six mutually-exclusive booleans on the viewport.
///     </para>
///     <para>
///         ⚠ <b>What makes the seam necessary rather than nice is <see cref="Context" />.</b> Doc 24's
///         B2 is the case: <c>1</c>/<c>2</c>/<c>3</c> are vertex, edge and face in every modelling
///         tool ever written, and doc 20's B2 gives <c>1..9</c> to view-bookmark recall. Both are
///         right. A mode that owns those keys while it is active and releases them when it is not is
///         the only resolution that does not make one of them worse — and the machinery for it
///         already exists, because <see cref="EditorCommand.Context" /> and
///         <see cref="KeyMap.CommandFor" /> are how the outliner and the content browser already
///         share Delete.
///     </para>
///     <para>
///         ⚠ <b><see cref="Register" /> and <see cref="Activated" /> are different moments and
///         registering from the second is the mistake this pair exists to prevent.</b> A mode's
///         commands have to be in the registry from the moment the mode is, or they are absent from
///         the keybinding editor, absent from the palette, and their bindings are unsaveable until
///         somebody has entered the mode once. Activation is state, not registration.
///     </para>
///     <para>
///         ⚠ <b>Everything optional is genuinely optional.</b> A mode with no icon draws its title, a
///         mode with no toolbar adds nothing to the mode bar, a mode with no panel opens none, and a
///         mode that refuses no input is a mode that only owns its keys — which is exactly what doc
///         24's P0 ships Blockout as.
///     </para>
/// </remarks>
public interface IEditorMode {
    /// <summary>What everything refers to it by: <c>select</c>, <c>blockout</c>.</summary>
    /// <remarks>
    ///     Lower-case and stable, because it is what <see cref="EditorModes.ModeCommand" /> builds a
    ///     command id out of and therefore what a saved keymap holds.
    /// </remarks>
    string Id { get; }

    /// <summary>What the mode bar's button says.</summary>
    StringId Title { get; }

    /// <summary>The glyph on that button, or <see langword="null" /> to draw the title instead.</summary>
    /// <remarks>
    ///     Null is not a placeholder. A mode bar of two glyphs is one where "what does the cube mean"
    ///     is a question, and <c>ToolbarPresenter</c> draws a command with no icon as its words for
    ///     exactly this reason.
    /// </remarks>
    PathBuilder? Icon { get; }

    /// <summary>Which command context the mode claims while it is active, or <see langword="null" />.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the mode's claim on keys that already mean something.</b> A command declaring
    ///         this context is reachable while the mode is active and out of scope while it is not,
    ///         and a chord it does not claim falls through to the global binding — so a mode takes the
    ///         four keys it needs and leaves Ctrl+S alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The context is the mode's, and where it applies is the application's.</b> This
    ///         interface says nothing about the viewport, because <c>Vixen.Editor.Ui</c> has never
    ///         heard of one; the host is what decides that pressing in the scene pane means this
    ///         context rather than the outliner's. See <c>EditorApplication.Contextual</c>.
    ///     </para>
    /// </remarks>
    string? Context { get; }

    /// <summary>A panel opened while the mode is active, or <see langword="null" /> for none.</summary>
    /// <remarks>
    ///     ⚠ <b>Opened on activation and closed on deactivation, and the mode does not own the
    ///     panel.</b> It is an ordinary registered panel — the mode names it, so that leaving the mode
    ///     does not leave a settings panel behind for a tool nobody is holding.
    /// </remarks>
    string? Panel { get; }

    /// <summary>What the mode puts on the mode bar beside the mode buttons.</summary>
    /// <remarks>
    ///     Empty for a mode with no toolbar of its own, which is the common case and is what the
    ///     shell draws as nothing at all rather than as an empty section.
    /// </remarks>
    IReadOnlyList<ToolbarEntry> Toolbar { get; }

    /// <summary>Puts the mode's commands, bindings and panels into the shell.</summary>
    /// <param name="shell">The shell.</param>
    /// <remarks>Called once, by <see cref="EditorModes.Add" />, whether or not the mode is ever
    ///     activated.</remarks>
    void Register(EditorShell shell);

    /// <summary>Takes them back out.</summary>
    /// <param name="shell">The shell.</param>
    /// <remarks>
    ///     ⚠ <b>Everything, and it is a plugin's mode that makes this load-bearing.</b> A command left
    ///     behind is a lambda over the plugin's own state held by the editor's registry, which keeps
    ///     the plugin's assembly loaded for the rest of the session with no error anywhere. See
    ///     <c>PluginRegistrations</c>.
    /// </remarks>
    void Unregister(EditorShell shell);

    /// <summary>The mode has become the active one.</summary>
    void Activated();

    /// <summary>It has stopped being it.</summary>
    /// <remarks>
    ///     ⚠ <b>Where a mode drops whatever it was in the middle of.</b> A sub-object selection, a
    ///     half-finished gesture and a held key all belong to the mode rather than to the document,
    ///     and one that survived a mode switch would be applied by the next gesture in a mode that
    ///     does not know what it is.
    /// </remarks>
    void Deactivated();

    /// <summary>First refusal on a pointer event in the viewport.</summary>
    /// <param name="args">The event.</param>
    /// <returns>Whether the mode took it, in which case the viewport does nothing else with it.</returns>
    bool Pointer(PointerEvent args);

    /// <summary>First refusal on a key event in the viewport.</summary>
    /// <param name="args">The event.</param>
    /// <returns>Whether the mode took it.</returns>
    /// <remarks>
    ///     ⚠ <b>This is for keys that are <i>not</i> commands, and there are fewer of them than a
    ///     mode author expects.</b> Anything with a name and a place in a menu is a command, scoped by
    ///     <see cref="Context" />, and gets its binding from the keymap like everything else. What is
    ///     left is what has no meaning outside a gesture already under way — doc 24's numeric entry
    ///     mid-drag, where <c>X</c> means "along X" only because a drag is in flight.
    /// </remarks>
    bool Key(KeyEvent args);
}
