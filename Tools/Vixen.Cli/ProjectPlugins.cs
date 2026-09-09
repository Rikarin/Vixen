// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.AssetCompiler;
using Vixen.Editor.Assets;
using Vixen.Editor.Plugin;

namespace Vixen.Cli;

/// <summary>Contributes a project's plugins' importers, so the CLI claims what the editor claims.</summary>
/// <remarks>
///     <para>
///         <b>The gap this closes, which is <c>GameAssemblies</c>' one door along.</b>
///         <c>BuiltInImporters.Create()</c> folds in <see cref="ImporterContributions.Default" />,
///         and in this process nothing had ever put anything in it: <c>PluginHost</c> has no caller
///         outside <c>EditorApplication</c>. So an asset a plugin's importer claims was imported by
///         the editor and fell through to <c>RawImporter</c> in a command-line content build —
///         <em>succeeding</em>, as a chunk called <c>Blob</c> that no typed reader resolves, with no
///         diagnostic. Silence is the failure.
///     </para>
///     <para>
///         ⚠ <b>Importers only, and no shell.</b> A plugin also registers commands, panels and menu
///         items, and a command-line tool has nowhere to put any of them — so this is
///         <c>PluginDiscovery</c> for the files and <c>PluginImporters</c> for the load, and not
///         <c>PluginHost</c>, which needs an <c>EditorShell</c> to activate anything against. It is
///         the same importer-only load a compiler worker performs, pointed at the same folder the
///         editor scans.
///     </para>
///     <para>
///         ⚠ <b>The project's <c>Plugins/</c> folder and deliberately not the user's.</b> The editor
///         scans both, project first, so that a plugin checked into a repository beats the copy
///         somebody installed globally. A content build has a stronger requirement than precedence:
///         two machines with the same checkout have to produce the same bytes, and a build that
///         imported an asset differently because of what one of them happened to have installed is
///         the very divergence this file exists to end. So a plugin a project's content depends on
///         is one the project carries — and the user root cannot be spelled from here anyway without
///         guessing the editor's own organisation and application qualifier.
///     </para>
///     <para>
///         ⚠ <b>A plugin that will not load is reported and not fatal</b>, on
///         <c>GameAssemblies</c>' terms — a project with one broken plugin still has a build worth
///         finishing. What it must not be is quiet, because the asset it claimed imports as bytes
///         and exits zero.
///     </para>
///     <para>
///         ⚠ <b>The scope has to be disposed before a second <see cref="Load" /> in one process.</b>
///         Nothing is cached — <c>PluginImporters.Load</c> makes a fresh load context per call and
///         says plainly why — so loading a plugin twice would give this process two types under one
///         <c>[DataContract]</c> alias, which <c>TypeRegistry</c> refuses. One command, one load.
///     </para>
/// </remarks>
public static class ProjectPlugins {
    /// <summary>The folder under a project root that holds its plugins, as the editor names it.</summary>
    public const string Folder = "Plugins";

    /// <summary>Loads every importer the project's plugins declare and contributes it.</summary>
    /// <param name="project">The project whose <c>Plugins/</c> folder to scan.</param>
    /// <param name="report">Told about a plugin that could not be read or loaded, and why.</param>
    /// <returns>A scope that withdraws every importer this call contributed.</returns>
    /// <remarks>
    ///     Additive, and a project with no <c>Plugins/</c> folder — which is most projects —
    ///     contributes nothing, reports nothing, and gets a scope that does nothing when disposed.
    /// </remarks>
    public static IDisposable Load(Project project, Action<string>? report = null) {
        ArgumentNullException.ThrowIfNull(project);

        var catalog = PluginDiscovery.Scan(Path.Combine(project.Paths.Root, Folder));

        foreach (var diagnostic in catalog.Diagnostics) {
            report?.Invoke($"{diagnostic.PluginId}: {diagnostic.Message}");
        }

        var scope = new Scope();

        foreach (var plugin in catalog.Plugins) {
            // ⚠ Discovery describes what is on disk and does not judge it, so the manifest's own
            // switch is read here — the same reading `PluginHost` gives it. A plugin somebody turned
            // off is off in both processes or the two disagree again, which is the whole defect.
            // (The editor's *user* list of disabled plugins lives in its own store and is not a
            // property of the project, so a build cannot and should not consult it.)
            if (!plugin.Manifest.Enabled) {
                continue;
            }

            if (plugin.AssemblyPath.Length == 0) {
                // A plugin whose manifest names an assembly discovery could not find. Not silently
                // skipped: the editor loads plugins from the same folder and would say so too, and a
                // build that quietly imported one file fewer is the failure this file is about.
                report?.Invoke(
                    $"{plugin.Id}: {plugin.Manifest.AssemblyFileName} is not in {plugin.Directory}, so any "
                    + "asset only its importers claim will be imported as raw bytes."
                );

                continue;
            }

            ImporterContributions loaded;

            try {
                loaded = PluginImporters.Load([plugin.AssemblyPath]);
            } catch (InvalidOperationException failure) {
                report?.Invoke(
                    $"{plugin.Id}: {failure.Message} Any asset only its importers claim will be imported as "
                    + "raw bytes."
                );

                continue;
            }

            foreach (var importer in loaded.All) {
                scope.Add(ImporterContributions.Default.Add(importer));
            }
        }

        return scope;
    }

    /// <summary>Withdraws what one <see cref="Load" /> contributed, in one call.</summary>
    /// <remarks>
    ///     ⚠ <b>The removals are what makes the process-wide static safe to write from a tool.</b>
    ///     An importer left in it names a type in an assembly nothing is going to use again, and the
    ///     next registry built in this process fails on it — with a message about a duplicate
    ///     extension rather than about the plugin.
    /// </remarks>
    sealed class Scope : IDisposable {
        readonly List<IDisposable> removals = [];

        public void Add(IDisposable removal) => removals.Add(removal);

        public void Dispose() {
            foreach (var removal in removals) {
                removal.Dispose();
            }

            removals.Clear();
        }
    }
}
