// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>What a scan of the plugin folders found, and what it had to say about it.</summary>
/// <param name="Plugins">The plugins, in the order the roots were searched.</param>
/// <param name="Diagnostics">What was wrong with the ones that are not in the list.</param>
public sealed record PluginCatalog(
    IReadOnlyList<PluginDescriptor> Plugins,
    IReadOnlyList<PluginDiagnostic> Diagnostics
) {
    /// <summary>An empty catalog, which is what a project with no plugin folder has.</summary>
    public static PluginCatalog Empty { get; } = new([], []);

    /// <summary>The plugin with an id, or <c>null</c>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The descriptor, or <c>null</c>.</returns>
    public PluginDescriptor? Find(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, id, StringComparison.Ordinal));
    }
}
