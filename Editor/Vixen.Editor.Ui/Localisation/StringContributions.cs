// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Ui;

/// <summary>What a toolset adds to the editor's translator template when it activates.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Five declaration classes had an <c>All</c> list and nothing walked it</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1202">#1202</a>).
///         <see cref="StringFamily" />'s whole argument is that an id built at a call site "is in no
///         <c>All</c> list, so <see cref="Strings.Template" /> does not export it, so no translator's
///         template contains it" — and the migration that answered it put the ids in an <c>All</c>
///         list that no production code read. <c>WaterStrings.All</c>, <c>TexturingStrings.All</c>,
///         <c>TerrainStrings.All</c>, <c>BlockoutStrings.All</c> and <c>DiagnosticsStrings.All</c>
///         were each read by nothing at all, so every word those five toolsets say was in exactly
///         the state the census exists to make impossible, one level up.
///     </para>
///     <para>
///         <b>A registration rather than a list here</b>, because a toolset is a plugin: the editor
///         cannot name <c>WaterStrings</c> without depending on <c>Vixen.Editor.Water</c>, which is
///         the dependency doc 36 § P3 spent real effort removing. A module hands its <c>All</c> over
///         as it activates, the same way it hands over its panels and its commands, and an
///         out-of-tree plugin does the identical thing with no change here.
///     </para>
///     <para>
///         ⚠ <b>Registering the same list twice is one registration.</b> An <c>All</c> list is a
///         static property, so the reference identifies the class; a module that is deactivated and
///         activated again — which is what the plugins panel's Disable/Enable pair does — would
///         otherwise export every one of its strings a second time. The catalog would survive that
///         (it is a map) and the count a translator is quoted would not.
///     </para>
///     <para>
///         ⚠ <b>What this still does not do is call <see cref="EditorStrings.Template" />.</b>
///         Nothing in the editor exports a template, nothing reads one back —
///         <c>StringCatalogYaml.Save</c> has no caller and <c>Strings.Use</c> has no production
///         caller anywhere in the repository — so the editor cannot yet be shown in another
///         language at all. That is one level further up again and is tracked separately; this is
///         the half that makes the words <em>reachable</em>.
///     </para>
/// </remarks>
public static class StringContributions {
    static readonly Lock Gate = new();
    static readonly List<IReadOnlyList<StringId>> Registered = [];

    /// <summary>Every string a toolset has contributed, in registration order.</summary>
    /// <remarks>
    ///     Deliberately not called <c>All</c>: an <c>All</c> beside <see cref="StringId" />
    ///     declarations is what marks a declaration class, for <c>VXS0310</c> and for
    ///     <c>CheckStrings</c> alike, and this class declares nothing.
    /// </remarks>
    public static IReadOnlyList<StringId> Declared {
        get {
            lock (Gate) {
                return [.. Registered.SelectMany(strings => strings)];
            }
        }
    }

    /// <summary>Adds a declaration class's <c>All</c> list to the editor's template.</summary>
    /// <param name="declarations">The class's <c>All</c>, which is the whole of what it declares.</param>
    /// <remarks>
    ///     Called from a module's <c>Activate</c>, beside the panels and the commands it registers,
    ///     because the words are part of what the toolset contributes and not a separate concern.
    /// </remarks>
    public static void Declare(IReadOnlyList<StringId> declarations) {
        ArgumentNullException.ThrowIfNull(declarations);

        lock (Gate) {
            if (!Registered.Any(registered => ReferenceEquals(registered, declarations))) {
                Registered.Add(declarations);
            }
        }
    }
}
