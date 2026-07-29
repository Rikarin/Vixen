// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Editor.Plugin;

/// <summary>What a plugin says about itself, before any of its code has run.</summary>
/// <remarks>
///     <para>
///         <b>Everything the loader needs to refuse a plugin is here, in a file it can read without
///         loading an assembly.</b> That is the whole reason a manifest exists rather than an
///         attribute on the entry type: an incompatible plugin, a plugin whose dependency is
///         missing and a plugin the user switched off all have to be dealt with <i>before</i> the
///         editor maps somebody else's IL into its own process.
///     </para>
///     <para>
///         It is a <c>[DataContract]</c> read by the same YAML binder as a <c>.meta</c> file and a
///         settings asset, so an unknown key is ignored rather than fatal — a manifest written for
///         a later editor still loads on this one, which is the behaviour that lets a plugin ship a
///         single file for two versions.
///     </para>
///     <para>
///         ⚠ <b>The file is <c>plugin.yaml</c> and its directory is the plugin.</b> Not a search of
///         the whole tree for assemblies: a folder either declares itself or is not a plugin, and
///         an editor that loaded whatever DLLs it found under a directory the user can write to is
///         an editor with an interesting security model.
///     </para>
/// </remarks>
/// <example>
///     <code language="yaml">
///     id: com.example.terrain
///     name: Terrain Tools
///     version: 1.2.0
///     api: 0.1
///     assembly: Example.Terrain.dll
///     description: Sculpting brushes and a heightmap importer.
///     author: Example Ltd
///     dependencies:
///       - com.example.brushes
///     </code>
/// </example>
[DataContract("VixenPlugin")]
public sealed record PluginManifest {
    /// <summary>What the file is called, in every directory that has one.</summary>
    public const string FileName = "plugin.yaml";

    /// <summary>
    ///     What everything refers to the plugin by: <c>com.example.terrain</c>.
    /// </summary>
    /// <remarks>
    ///     Dotted, lower-case and stable — the same shape as a command id, and for the same reason.
    ///     It is what another plugin's <see cref="Dependencies" /> names, what the user's list of
    ///     disabled plugins names, and what a diagnostic about this plugin is filed under, so a
    ///     rename is a new plugin rather than an edit.
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>What it is called on screen.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The plugin's own version, which is the author's to number.</summary>
    public Version Version { get; init; } = new(0, 0);

    /// <summary>Which version of the editor's contract it was built against.</summary>
    /// <remarks>See <see cref="EditorApi" /> for what the loader does with it.</remarks>
    public Version Api { get; init; } = new(0, 0);

    /// <summary>
    ///     The file holding its code, relative to the plugin's directory.
    /// </summary>
    /// <remarks>
    ///     Defaults to <c><see cref="Id" />.dll</c> when it is not given, which is right for a
    ///     plugin whose assembly is named after itself and wrong often enough to be worth stating.
    ///     A NuGet-shaped layout puts it under <c>lib/net10.0/</c>; see
    ///     <see cref="PluginDiscovery" /> for where the file is looked for.
    /// </remarks>
    public string Assembly { get; init; } = string.Empty;

    /// <summary>
    ///     The full name of the type implementing <see cref="IEditorPlugin" />, or empty to find it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Only needed when the assembly has more than one.</b> The loader scans for the entry
    ///     type and refuses an assembly with two rather than picking one, because which of two
    ///     plugins in a file ran is not something anybody should have to discover by experiment.
    /// </remarks>
    public string EntryPoint { get; init; } = string.Empty;

    /// <summary>A sentence about what it does, shown in the plugin list.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Who wrote it.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>The ids of plugins that have to be activated before this one.</summary>
    /// <remarks>
    ///     ⚠ <b>An ordering constraint, not an assembly reference.</b> A plugin that calls another
    ///     plugin's code references its assembly like any other library; what this list buys is
    ///     that the other one's commands, panels and services are already registered when this
    ///     one's <see cref="IEditorPlugin.Activate" /> runs. A missing dependency, or a cycle, means
    ///     neither plugin is loaded and the report says which.
    /// </remarks>
    public List<string> Dependencies { get; init; } = [];

    /// <summary>Whether to load it at all.</summary>
    /// <remarks>
    ///     What a plugin that broke the editor is switched to, by hand, in a file the editor is not
    ///     running while it is being edited. A plugin the user disabled is not an error and does not
    ///     appear in the report as one.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>The assembly file to look for, which is <see cref="Assembly" /> or a name from the id.</summary>
    public string AssemblyFileName =>
        Assembly.Length > 0 ? Assembly : Id + ".dll";

    /// <summary>What is wrong with this manifest, if anything.</summary>
    /// <returns>One line per problem, in reading order, or empty when there is nothing wrong.</returns>
    /// <remarks>
    ///     Every problem at once rather than the first: a plugin author fixing a manifest one
    ///     rejection at a time is being made to run the editor four times to learn four things the
    ///     first run already knew.
    /// </remarks>
    public IReadOnlyList<string> Problems() {
        var problems = new List<string>();

        if (Id.Length == 0) {
            problems.Add("'id' is missing, and it is what everything else names this plugin by.");
        } else if (!IsWellFormedId(Id)) {
            problems.Add(
                $"'id' is '{Id}'. It must be lower-case letters, digits, dots and dashes — the same shape as a command id."
            );
        }

        if (Name.Length == 0) {
            problems.Add("'name' is missing, and it is what the plugin list shows.");
        }

        if (Api == new Version(0, 0)) {
            problems.Add(
                $"'api' is missing. Declare the editor API the plugin was built against; this editor implements {EditorApi.Version.ToString(2)}."
            );
        }

        foreach (var dependency in Dependencies) {
            if (dependency.Length == 0 || !IsWellFormedId(dependency)) {
                problems.Add($"'dependencies' contains '{dependency}', which is not a plugin id.");
            }

            if (string.Equals(dependency, Id, StringComparison.Ordinal)) {
                problems.Add("'dependencies' contains this plugin's own id.");
            }
        }

        return problems;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Id} {Version.ToString(3)}");

    static bool IsWellFormedId(string id) {
        // Dots and dashes separate, so neither may start, end or double — 'com..example' and
        // 'com.example.' are the two typos a permissive check lets through and a comparison against
        // a dependency list then fails to match.
        if (id.Length == 0 || !char.IsAsciiLetterLower(id[0]) || !char.IsAsciiLetterOrDigit(id[^1])) {
            return false;
        }

        for (var index = 0; index < id.Length; index++) {
            var character = id[index];

            if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character)) {
                continue;
            }

            if (character is not ('.' or '-') || (index > 0 && id[index - 1] is '.' or '-')) {
                return false;
            }
        }

        return true;
    }
}
