// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>Puts a verb on a menu, by path — or a line for a command something else registers.</summary>
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
///         <b>Three shapes, and the member decides which.</b> On a <c>static void ()</c> method it is
///         a verb: a command, and its line at the end of the path. On a public <c>static</c>
///         <c>string</c> field — a <c>const</c> command id, usually — it is a line for a command
///         something else registers, and every part of the path is a menu: this is how a mode, whose
///         commands are its own because their enablement and their keys are, still declares where
///         those commands appear. On a <c>static IEnumerable&lt;string&gt; ()</c> method it is a run of
///         such lines, read once, for a family of ids that are computed rather than written out.
///     </para>
///     <para>
///         ⚠ <b>Read two ways, and both must agree.</b> A plugin off a disk and a project's
///         <c>Editor/</c> scripts are scanned when the editor loads them — one discrete assembly it
///         has just loaded, which is why the scan is bounded and ADR-002 permits it. An in-tree module
///         is not scanned at all: <c>Vixen.Editor.Plugin.Generator</c> writes its
///         <c>Declared.Register(PluginContext)</c> at compile time and the module's <c>Activate</c>
///         calls it. Both hand what they found to <see cref="DeclaredMenus" />, so placement is decided
///         in one place.
///     </para>
///     <para>
///         ⚠ <b>A path, not a menu object.</b> <c>"Tools/My Thing/Do It"</c> creates whatever of it
///         does not exist and joins whatever does — no parent lookup, no ordering call, no menu the
///         author has to find first. Menus compose because nobody owns the tree, which is the one
///         thing about Unity's menu API worth copying wholesale. A submenu that needs a localised title
///         or a place among its siblings is declared with <see cref="EditorSubmenuAttribute" />.
///     </para>
/// </remarks>
/// <param name="path">Where it goes — <c>"Tools/Rebuild Navigation"</c>. Slashes separate levels.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Field)]
public sealed class EditorMenuAttribute(string path) : Attribute {
    /// <summary>Where the item goes. Slashes separate levels.</summary>
    /// <remarks>
    ///     On a verb the last part is the line itself. On a line for somebody else's command every
    ///     part is a menu, because the command already has a title and the line shows it.
    /// </remarks>
    public string Path { get; } = path;

    /// <summary>Which of two lines in one menu comes first. Lower is higher up.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero means "in the order the reader saw them", which is declaration order.</b> A
    ///     script's files are sorted by <c>ScriptCompiler.Sources</c>, so an unpriced menu is at least
    ///     stable between two runs — but it is stable in a way that depends on file names, which is
    ///     why anything an author cares about the position of says so.
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>The command id, or empty to derive one from the path. A verb's only.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth setting for anything a key should be bound to.</b> A derived id is the path
    ///     lowercased with its slashes turned into dots, so renaming a menu item silently drops the
    ///     user's keybinding for it. An id the author chose survives the rename, which is exactly the
    ///     bargain every built-in command already makes.
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>The verb's title, as the name of a <c>StringId</c> in a declaration class. A verb's only.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 50 § D5: what lets a localised built-in be declared at all.</b> Every word the
    ///         editor shows is a <c>StringId</c> in a <c>*Strings</c> class, and a path is a literal —
    ///         so <c>Title = nameof(SceneStrings.CreateCube)</c> names the declaration, the reader
    ///         resolves it, and a translator sees the line. Empty keeps the path's last part as the
    ///         title, which is right for a script with no catalogue.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Resolved the same way by both readers</b>: in <see cref="Strings" /> when that is
    ///         set, otherwise in whichever class of the declaring assembly whose name ends in
    ///         <c>Strings</c> has a public static <c>StringId</c> of that name — exactly one, or the
    ///         declaration is refused. A name that resolves to nothing is a compile error from the
    ///         generator and a load failure from the scan, never a silent literal.
    ///     </para>
    /// </remarks>
    public string Title { get; init; } = string.Empty;

    /// <summary>The declaration class <see cref="Title" /> is in, when it is not this assembly's.</summary>
    public Type? Strings { get; init; }

    /// <summary>Whether a separator goes above this line.</summary>
    /// <remarks>
    ///     Explicit rather than Unity's "a priority gap of more than ten", because a menu whose
    ///     grouping changes when somebody renumbers it is a menu nobody can read the grouping of.
    ///     Ignored for the first line in a menu, which has nothing to be separated from.
    /// </remarks>
    public bool Separator { get; init; }
}
