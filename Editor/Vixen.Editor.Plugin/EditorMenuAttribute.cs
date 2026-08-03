// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>Puts a static method on a menu, by path.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P5, and the smallest thing a project's own editor script can be.</b> One
///         attribute, one static method, no class to derive from and no registration call:
///     </para>
///     <code language="csharp">
///         public static class Tools {
///             [EditorMenu("Tools/Rebuild Navigation")]
///             public static void Rebuild() { … }
///         }
///     </code>
///     <para>
///         ⚠ <b>Read only from a project's <c>Editor/</c> assembly, and nowhere else.</b> Finding
///         these means enumerating an assembly's types, which ADR-002 forbids as a way of building
///         the editor — a scan reads metadata a trimmed publish has deleted and makes start-up cost
///         grow with what is installed. Neither applies here: the assembly is one the editor compiled
///         seconds ago from a folder it is watching, it is a dozen types, and there is no publish. A
///         plugin shipped as a built assembly uses <see cref="IEditorPlugin" />, where the
///         registration is a call the author writes and the generator has a chance to help.
///     </para>
///     <para>
///         ⚠ <b>A path, not a menu object.</b> <c>"Tools/My Thing/Do It"</c> creates whatever of it
///         does not exist — no parent lookup, no ordering call, no menu the script has to find first.
///         Menus compose because nobody owns the tree, which is the one thing about Unity's menu API
///         worth copying wholesale.
///     </para>
/// </remarks>
/// <param name="path">Where it goes — <c>"Tools/Rebuild Navigation"</c>. Slashes separate levels.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EditorMenuAttribute(string path) : Attribute {
    /// <summary>Where the item goes. Slashes separate levels; the last part is the line itself.</summary>
    public string Path { get; } = path;

    /// <summary>Which of two lines in one menu comes first. Lower is higher up.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero means "in the order the compiler saw them", which is file order.</b>
    ///     <c>ScriptCompiler.Sources</c> sorts the files, so an unpriced menu is at least stable
    ///     between two runs — but it is stable in a way that depends on file names, which is why
    ///     anything an author cares about the position of says so.
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>The command id, or empty to derive one from the path.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth setting for anything a key should be bound to.</b> A derived id is the path
    ///     lowercased with its slashes turned into dots, so renaming a menu item silently drops the
    ///     user's keybinding for it. An id the author chose survives the rename, which is exactly the
    ///     bargain every built-in command already makes.
    /// </remarks>
    public string Id { get; init; } = string.Empty;
}
