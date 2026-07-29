// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;

namespace Vixen.Editor.Plugin;

/// <summary>Finding the plugins in a set of folders, and reading what they say about themselves.</summary>
/// <remarks>
///     <para>
///         <b>A root holds one plugin per subdirectory, and a subdirectory is a plugin when it has a
///         <c>plugin.yaml</c> in it.</b> Nothing recurses further: a plugin's own <c>lib/</c>,
///         <c>runtimes/</c> and content folders are its business, and a scan that walked into them
///         would find the manifest of a plugin the plugin itself vendored.
///     </para>
///     <para>
///         ⚠ <b>Roots are searched in order and the first id wins.</b> The editor passes the
///         project's folder before the user's, so a plugin checked into a project overrides the copy
///         the user has installed globally — which is what makes "everybody on this team gets the
///         same tools" true, and it is the same precedence a project-local tool manifest has. The
///         copy that lost is reported rather than dropped silently.
///     </para>
///     <para>
///         ⚠ <b>A root that does not exist is not an error.</b> Most projects have no
///         <c>Plugins/</c> folder, and an editor that warned about it on every launch would be
///         teaching people to ignore its warnings.
///     </para>
/// </remarks>
public static class PluginDiscovery {
    /// <summary>Where a NuGet-shaped plugin keeps its assembly.</summary>
    /// <remarks>
    ///     A plugin distributed as a package and unzipped in place has <c>lib/net10.0/Whatever.dll</c>
    ///     rather than <c>Whatever.dll</c>, and doc 11 says a plugin is "a NuGet package or a folder
    ///     with an assembly + a manifest" — so both layouts are the same thing to a scan, and the
    ///     manifest does not have to carry a path that says which one the author chose.
    /// </remarks>
    const string LibraryFolder = "lib";

    /// <summary>Reads every plugin under a set of roots.</summary>
    /// <param name="roots">The folders to look in, in precedence order.</param>
    /// <returns>What was found, and what was wrong with what was not.</returns>
    public static PluginCatalog Scan(params IEnumerable<string> roots) {
        ArgumentNullException.ThrowIfNull(roots);

        var plugins = new List<PluginDescriptor>();
        var diagnostics = new List<PluginDiagnostic>();
        var seen = new Dictionary<string, PluginDescriptor>(StringComparer.Ordinal);

        foreach (var root in roots) {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal)) {
                var manifestPath = Path.Combine(directory, PluginManifest.FileName);

                if (!File.Exists(manifestPath)) {
                    continue;
                }

                var descriptor = Read(manifestPath, diagnostics);

                if (descriptor is null) {
                    continue;
                }

                if (seen.TryGetValue(descriptor.Id, out var winner)) {
                    diagnostics.Add(
                        new PluginDiagnostic(
                            PluginSeverity.Warning,
                            descriptor.Id,
                            $"is installed twice. Using {winner.Directory} and ignoring {descriptor.Directory}."
                        )
                    );

                    continue;
                }

                seen[descriptor.Id] = descriptor;
                plugins.Add(descriptor);
            }
        }

        return new PluginCatalog(plugins, diagnostics);
    }

    /// <summary>Reads one plugin's manifest.</summary>
    /// <param name="manifestPath">The <c>plugin.yaml</c>.</param>
    /// <param name="diagnostics">Where a problem with it is recorded.</param>
    /// <returns>The descriptor, or <c>null</c> when the manifest could not be used.</returns>
    /// <remarks>
    ///     ⚠ <b>A manifest that does not parse is a diagnostic and not an exception.</b> The file is
    ///     hand-written by somebody who is not here, and one plugin with a stray tab in it must not
    ///     be able to stop the editor from opening — which is exactly what letting a
    ///     <see cref="YamlParseException" /> out of a start-up scan would do.
    /// </remarks>
    static PluginDescriptor? Read(string manifestPath, List<PluginDiagnostic> diagnostics) {
        var directory = Path.GetDirectoryName(manifestPath)!;
        var fallbackId = Path.GetFileName(directory);

        PluginManifest manifest;

        try {
            manifest = YamlSerializer.Parse<PluginManifest>(File.ReadAllText(manifestPath));
        } catch (Exception exception)
            when (exception is YamlParseException or YamlBindingException or IOException or UnauthorizedAccessException) {
            diagnostics.Add(
                new PluginDiagnostic(PluginSeverity.Error, fallbackId, $"{manifestPath} could not be read: {exception.Message}")
            );

            return null;
        }

        var problems = manifest.Problems();

        if (problems.Count > 0) {
            foreach (var problem in problems) {
                diagnostics.Add(
                    new PluginDiagnostic(
                        PluginSeverity.Error,
                        manifest.Id.Length > 0 ? manifest.Id : fallbackId,
                        $"{PluginManifest.FileName}: {problem}"
                    )
                );
            }

            return null;
        }

        return new PluginDescriptor(manifest, directory, manifestPath, FindAssembly(directory, manifest.AssemblyFileName));
    }

    /// <summary>Where the plugin's assembly is, in the two layouts a plugin arrives in.</summary>
    /// <param name="directory">The plugin's folder.</param>
    /// <param name="fileName">What the assembly is called.</param>
    /// <returns>The path, or empty if there is no such file.</returns>
    /// <remarks>
    ///     Empty rather than a throw, because "the manifest names an assembly that is not there" is
    ///     a fact about one plugin that the loader reports beside every other reason a plugin did
    ///     not start, and discovery's job is to describe what is on disk rather than to judge it.
    /// </remarks>
    static string FindAssembly(string directory, string fileName) {
        var beside = Path.Combine(directory, fileName);

        if (File.Exists(beside)) {
            return beside;
        }

        var library = Path.Combine(directory, LibraryFolder);

        if (!Directory.Exists(library)) {
            return string.Empty;
        }

        // Ordered descending so that a package carrying both net10.0 and netstandard2.0 gives up
        // the newer one — the same preference a NuGet restore would have applied if the plugin had
        // been installed rather than unzipped.
        foreach (var framework in Directory.EnumerateDirectories(library).OrderDescending(StringComparer.Ordinal)) {
            var candidate = Path.Combine(framework, fileName);

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return string.Empty;
    }
}
