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
///         ⚠ <b>And the level above it is now wired, which this remark used to say was not</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1229">#1229</a>). The editor's
///         <c>tools.export-strings</c> writes <see cref="EditorStrings.Template" /> through
///         <c>StringCatalogYaml.Save</c> into the project's <c>Localization/</c>, and
///         <c>EditorPreferences.Language</c> reads one back through <c>StringCatalogYaml.Load</c>
///         into <c>Strings.Use</c> — so a template taken after a module activates carries that
///         module's words, and the running editor re-labels when the language changes.
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

    /// <summary>Takes a declaration class's <c>All</c> list back out again.</summary>
    /// <param name="declarations">The same list that was passed to <see cref="Declare" />.</param>
    /// <returns>Whether it was there to remove.</returns>
    /// <remarks>
    ///     ⚠ <b>This class is static and a plugin's <c>All</c> list is an array of that plugin's
    ///     own <see cref="StringId" /> values, so a declaration nothing withdraws pins the plugin's
    ///     <c>AssemblyLoadContext</c> for the life of the process.</b> That is not a leak of memory
    ///     so much as a leak of a <em>context</em>: the module unregisters every panel, command and
    ///     editor it added and is still not collectible, which is what
    ///     <c>TexturingCollectionTests.The_module_leaves_no_load_context_behind</c> reports.
    ///     <para>
    ///         Matched by reference, exactly as <see cref="Declare" /> deduplicates, because a
    ///         plugin's <c>All</c> is one array and two different arrays holding equal ids are two
    ///         different contributions.
    ///     </para>
    ///     <para>
    ///         ⚠ Prefer <c>PluginContext.AddStrings</c> over calling this by hand — it registers and
    ///         records the undo in one line, which is the bargain every other <c>Add…</c> on that
    ///         class makes and the reason five modules did not have to remember this.
    ///     </para>
    /// </remarks>
    public static bool Withdraw(IReadOnlyList<StringId> declarations) {
        ArgumentNullException.ThrowIfNull(declarations);

        lock (Gate) {
            var at = Registered.FindIndex(registered => ReferenceEquals(registered, declarations));

            if (at < 0) {
                return false;
            }

            Registered.RemoveAt(at);

            return true;
        }
    }
}
