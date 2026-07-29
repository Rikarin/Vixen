// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>A plugin as found on disk: its manifest, and where the three files are.</summary>
/// <param name="Manifest">What it says about itself.</param>
/// <param name="Directory">The folder holding it, which is also the folder its own data goes in.</param>
/// <param name="ManifestPath">The <c>plugin.yaml</c> itself.</param>
/// <param name="AssemblyPath">
///     The assembly, or empty when discovery could not find the file the manifest names.
/// </param>
/// <remarks>
///     ⚠ <b>A descriptor is the result of reading, not of loading.</b> Nothing here has run any of
///     the plugin's code, and a descriptor for a plugin that turns out to be incompatible, broken or
///     disabled is a perfectly ordinary thing to be holding — which is what lets a plugin-management
///     panel list what is installed without activating it.
/// </remarks>
public sealed record PluginDescriptor(
    PluginManifest Manifest,
    string Directory,
    string ManifestPath,
    string AssemblyPath
) {
    /// <summary>What everything refers to it by.</summary>
    public string Id => Manifest.Id;

    /// <inheritdoc />
    public override string ToString() => Manifest.ToString();
}
