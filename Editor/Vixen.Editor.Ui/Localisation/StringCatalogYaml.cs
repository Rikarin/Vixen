// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Ui;

namespace Vixen.Editor.Ui;

/// <summary>Reading and writing a <see cref="StringCatalog" /> as YAML.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here rather than on the catalog, and the reason is a package closure.</b>
///         <see cref="StringCatalog" /> is in <c>Vixen.Ui</c>, which every application that shows a
///         word references; <c>Vixen.Core.Yaml</c> is a serialiser these two methods are the only
///         use of. Leaving them attached would add a package to the pin of every consumer for a
///         code path most of them never call — and an application publishing NativeAOT with a
///         vendored closure pays that in files it has to vendor. So the catalog proper is
///         <c>Set</c>/<c>Find</c>/<c>Ids</c>/<c>Count</c>, and the format is the application's.
///     </para>
///     <para>
///         The editor's format is YAML for the reason <c>DockLayout</c> gives about layouts: a
///         translation is a file a person diffs, checks in and occasionally repairs by hand.
///         Another application is free to answer differently — JSON through a source-generated
///         reader, or resources — without either choice reaching the other.
///     </para>
/// </remarks>
public static class StringCatalogYaml {
    /// <summary>Writes a catalog as YAML.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     Sorted by id, because the file is checked in and a map written in hash order produces a
    ///     diff on every save that says nothing.
    /// </remarks>
    public static string Save(this StringCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var document = new YamlMapping().Set("language", new YamlScalar(catalog.Language));
        var strings = new YamlMapping();

        foreach (var id in catalog.Ids.Order(StringComparer.Ordinal)) {
            strings.Set(id, new YamlScalar(catalog.Find(id) ?? string.Empty, YamlScalarStyle.DoubleQuoted));
        }

        return YamlWriter.Write(document.Set("strings", strings));
    }

    /// <summary>Reads a catalog back.</summary>
    /// <param name="yaml">The text.</param>
    /// <param name="language">The language to use if the file does not name one.</param>
    /// <returns>The catalog.</returns>
    /// <remarks>
    ///     ⚠ <b>Never throws on a catalog that has gone stale</b>, for the reason
    ///     <c>DockLayout.Load</c> gives about layouts: a translation file outlives the ids in it, and
    ///     the answer to an id nothing uses any more is to ignore it rather than to refuse to start
    ///     the editor in front of somebody who wanted to open a project.
    /// </remarks>
    public static StringCatalog Load(string yaml, string language = "source") {
        ArgumentNullException.ThrowIfNull(yaml);

        if (YamlReader.Read(yaml) is not YamlMapping document) {
            return new StringCatalog(language);
        }

        var catalog = new StringCatalog((document["language"] as YamlScalar)?.Value is { Length: > 0 } named ? named : language);

        if (document["strings"] is YamlMapping strings) {
            foreach (var (id, node) in strings) {
                if (node is YamlScalar text) {
                    catalog.Set(id, text.Value);
                }
            }
        }

        return catalog;
    }
}
