// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>Declares a submenu: its localised title and where among its siblings it goes.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D5's other half.</b> <see cref="EditorMenuAttribute" />'s path makes any
///         submenu it names, titled with the path's own text and appended to its parent. That is the
///         right default for a script and the wrong one for a built-in, whose submenus have
///         translated titles and a place in a menu the application owns — Blockout's six go after
///         Scene ▸ Measure, and a feature that could only append would reorder somebody's menu the day
///         it stopped being compiled in.
///     </para>
///     <code language="csharp">
///         [EditorSubmenu("Scene/Geometry", Title = nameof(BlockoutStrings.MenuGeometry), After = "editor.menu.elements")]
///         public sealed class BlockoutModule : IEditorPlugin { … }
///     </code>
///     <para>
///         ⚠ <b>On a class, and any class.</b> It describes the menu rather than a member, so it goes
///         on whatever type owns the feature — the module, by convention.
///     </para>
/// </remarks>
/// <param name="path">The submenu — <c>"Scene/Geometry"</c>. The first part is a menu on the bar.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class EditorSubmenuAttribute(string path) : Attribute {
    /// <summary>The submenu. The first part is a menu on the bar, the last is this one.</summary>
    public string Path { get; } = path;

    /// <summary>Its title, as the name of a <c>StringId</c> in a declaration class.</summary>
    /// <remarks>Resolved as <see cref="EditorMenuAttribute.Title" /> is. Empty titles it with the path's last part.</remarks>
    public string Title { get; init; } = string.Empty;

    /// <summary>The declaration class <see cref="Title" /> is in, when it is not this assembly's.</summary>
    public Type? Strings { get; init; }

    /// <summary>The title id of the sibling it goes after, or empty to append it.</summary>
    /// <remarks>
    ///     ⚠ <b>An id, not a position.</b> A number moves the next time a line is added above it; a
    ///     sibling's id names the same place for as long as the sibling exists. A sibling that does
    ///     not exist appends, rather than failing a module over where a line goes.
    /// </remarks>
    public string After { get; init; } = string.Empty;

    /// <summary>Which of two submenus declared for one menu is placed first. Lower is first.</summary>
    /// <remarks>Matters when one is declared <see cref="After" /> another declared in the same pass.</remarks>
    public int Priority { get; init; }
}
