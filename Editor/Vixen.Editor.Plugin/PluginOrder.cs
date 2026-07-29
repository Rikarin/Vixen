// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>Which order plugins activate in, given what each says it depends on.</summary>
/// <remarks>
///     <para>
///         <b>A depth-first topological sort with the discovery order as the tie-break.</b> Two
///         plugins that depend on nothing activate in the order they were found, which is
///         alphabetical within a root and project-before-user across them — so an editor that loads
///         the same set of plugins twice loads them in the same order twice, and a bug that depends
///         on the order is reproducible rather than a Tuesday thing.
///     </para>
///     <para>
///         ⚠ <b>A plugin whose dependency is missing does not load, and neither does anything that
///         depends on <i>it</i>.</b> The alternative — activate it anyway and let it find out —
///         means the failure surfaces inside the plugin's own code as a null service or an id
///         nothing registered, which is the same bug reported worse.
///     </para>
///     <para>
///         ⚠ <b>A cycle is reported once, naming the whole cycle.</b> Reporting it per plugin would
///         give three diagnostics for one mistake, and reporting it as "a cycle exists" without
///         naming the members would leave the author to find it.
///     </para>
/// </remarks>
static class PluginOrder {
    /// <summary>Orders plugins so that every plugin follows the ones it depends on.</summary>
    /// <param name="plugins">What discovery found, in discovery order.</param>
    /// <param name="diagnostics">Where a missing dependency or a cycle is recorded.</param>
    /// <returns>The ones that can be activated, in the order to activate them.</returns>
    public static List<PluginDescriptor> Sort(
        IReadOnlyList<PluginDescriptor> plugins,
        List<PluginDiagnostic> diagnostics
    ) {
        var byId = new Dictionary<string, PluginDescriptor>(StringComparer.Ordinal);

        foreach (var plugin in plugins) {
            byId[plugin.Id] = plugin;
        }

        var ordered = new List<PluginDescriptor>(plugins.Count);
        var resolved = new Dictionary<string, bool>(StringComparer.Ordinal);
        var path = new List<string>();
        var inCycle = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plugin in plugins) {
            Visit(plugin.Id);
        }

        return ordered;

        bool Visit(string id) {
            if (resolved.TryGetValue(id, out var already)) {
                return already;
            }

            var cycle = path.IndexOf(id);

            if (cycle >= 0) {
                var members = path.Skip(cycle).ToList();

                // Named from the plugin that closes the loop, so the message reads as the cycle
                // rather than as a list of plugins that happen to be involved in one.
                diagnostics.Add(
                    new PluginDiagnostic(
                        PluginSeverity.Error,
                        id,
                        "is in a dependency cycle: " + string.Join(" → ", members.Append(id)) + "."
                    )
                );

                inCycle.UnionWith(members);
                resolved[id] = false;

                return false;
            }

            if (!byId.TryGetValue(id, out var plugin)) {
                return false;
            }

            path.Add(id);
            var usable = true;

            foreach (var dependency in plugin.Manifest.Dependencies) {
                if (Visit(dependency)) {
                    continue;
                }

                usable = false;

                // ⚠ Silent for a plugin that is itself in the cycle just reported. The cycle line
                // already names every member, and "a needs b, b needs c, c needs a" underneath it
                // is three more lines saying the same thing to somebody who now has to read four.
                if (inCycle.Contains(id)) {
                    continue;
                }

                diagnostics.Add(
                    new PluginDiagnostic(
                        PluginSeverity.Error,
                        id,
                        byId.ContainsKey(dependency)
                            ? $"needs '{dependency}', which did not load."
                            : $"needs '{dependency}', which is not installed."
                    )
                );
            }

            path.RemoveAt(path.Count - 1);

            // ⚠ A cycle's members set themselves false on the way out of the recursion above, and
            // that answer stands: overwriting it here would let the plugin that started the walk
            // come back true because its own dependencies had by then been marked resolved.
            if (resolved.TryGetValue(id, out var decided)) {
                return decided;
            }

            resolved[id] = usable;

            if (usable) {
                ordered.Add(plugin);
            }

            return usable;
        }
    }
}
