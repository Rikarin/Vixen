// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Controls;

namespace Vixen.Editor.SceneView;

/// <summary>Which of the viewport's two summoned menus an entry belongs to.</summary>
/// <remarks>
///     ⚠ <b>A flags enum, because the useful case is both.</b> A mode's four commonest verbs want to
///     be a flick away <i>and</i> to be in the list somebody reads when they cannot remember the
///     flick — and a contributor who had to register the same command twice would register it once
///     and pick wrong.
/// </remarks>
[Flags]
public enum SceneMenuSurface : byte {
    /// <summary>Nowhere. Not useful, and it is the default so that it has to be chosen.</summary>
    None = 0,

    /// <summary>The pie menu: a direction, and after a week a flick.</summary>
    Radial = 1 << 0,

    /// <summary>The list the context key opens: read rather than aimed.</summary>
    Context = 1 << 1,

    /// <summary>Both, which is what most entries want.</summary>
    Both = Radial | Context
}

/// <summary>A command the scene view's radial or context menu offers.</summary>
/// <remarks>
///     <para>
///         <b>The one registration behind both menus, and behind every mode's version of them.</b>
///         A mode is a set of verbs somebody uses constantly — extrude, bevel, sculpt, raise, paint —
///         and doc 24's whole argument for modes is that a viewport cannot show every tool at once.
///         A pie menu is the shape that answer takes: nothing on screen until it is asked for, and
///         then four directions.
///     </para>
///     <para>
///         ⚠ <b>A command id rather than a delegate, and that is the same decision the toolbar and
///         the menu bar already made.</b> An entry that carried an action would be a second place a
///         verb can live, with its own enablement and no keybinding — and the point of a command
///         registry is that "Extrude" is one thing whether it is reached from a menu, a pie, a
///         shortcut or the palette. What this adds is <i>where</i>, not <i>what</i>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Mode" /> is what makes it a per-mode menu rather than one long one.</b>
///         An entry naming a mode is offered only while that mode is active; one naming none is
///         always offered. Without it every module's tools would be in every mode's pie, which is the
///         wall of buttons modes exist to prevent — see <c>IEditorMode</c>.
///     </para>
///     <code language="csharp">
///         context.Owns(registry.Add(new SceneMenuItem("blockout.extrude", SceneMenuSurface.Both) {
///             Mode = "blockout",
///             Art = IconArt.Of(MyIcons.Extrude)
///         }));
///     </code>
/// </remarks>
/// <param name="CommandId">What it runs. Resolved when the menu opens, so enablement is current.</param>
/// <param name="Surface">Which menu, or both.</param>
public sealed record SceneMenuItem(string CommandId, SceneMenuSurface Surface = SceneMenuSurface.Both) {
    /// <summary>Which mode offers it, or <see langword="null" /> for every mode.</summary>
    public string? Mode { get; init; }

    /// <summary>Where it sits: lower comes first, and in a pie that means nearer the top.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth setting for anything in a pie, and not merely cosmetic there.</b> A radial
    ///     menu is fast because a given verb is always in the same direction; an order left to
    ///     chance is one that changes when a plugin loads, and every flick anybody had learnt goes
    ///     with it.
    /// </remarks>
    public int Order { get; init; }

    /// <summary>Its picture, or <see langword="null" /> for a label alone.</summary>
    public IconArt? Art { get; init; }

    /// <summary>What it is called, or empty to take the command's own title.</summary>
    /// <remarks>
    ///     Empty is nearly always right: a verb that reads differently in the pie from in the menu
    ///     bar is two names for one command, which is how somebody comes to believe there are two
    ///     commands.
    /// </remarks>
    public string Label { get; init; } = string.Empty;
}
