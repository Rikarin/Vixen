// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;

namespace Vixen.Editor.Ui;

/// <summary>One language's worth of editor strings, by id.</summary>
/// <remarks>
///     <para>
///         <b>A flat id-to-text map, and the flatness is the point.</b> An id is
///         <c>editor.command.file.save</c> — a dotted path that says where the string is used rather
///         than what it says — so a translator's file is diffable, an untranslated entry is a
///         missing key rather than an English string masquerading as a translation, and two places
///         that happen to say "Open" can be translated differently where a language needs them to
///         be.
///     </para>
///     <para>
///         ⚠ <b>A catalog holds no fallbacks.</b> The English text lives beside the id at the
///         declaration — see <see cref="StringId" /> — so a catalog that is missing an entry falls
///         back to something that is in the source rather than to another file that may also be
///         missing it. This is what makes shipping with no catalog at all work, which is what the
///         editor does today.
///     </para>
/// </remarks>
public sealed class StringCatalog {
    readonly Dictionary<string, string> entries = new(StringComparer.Ordinal);

    /// <summary>Creates an empty catalog for a language.</summary>
    /// <param name="language">A BCP-47 tag: <c>en</c>, <c>en-GB</c>, <c>cs</c>.</param>
    public StringCatalog(string language) {
        ArgumentNullException.ThrowIfNull(language);
        Language = language;
    }

    /// <summary>Which language this is, as a BCP-47 tag.</summary>
    public string Language { get; }

    /// <summary>How many strings it has.</summary>
    public int Count => entries.Count;

    /// <summary>The ids it holds, in no particular order.</summary>
    public IReadOnlyCollection<string> Ids => entries.Keys;

    /// <summary>The catalog used when nothing has been loaded: empty, so every string falls back.</summary>
    /// <remarks>
    ///     Named <c>source</c> rather than <c>en</c> because that is what it is — the strings as
    ///     they were written in the source. An <c>en</c> catalog is a translation into English and
    ///     is entitled to differ from it, which is the distinction that lets an English proofreader
    ///     change a label without a code change.
    /// </remarks>
    public static StringCatalog Source { get; } = new("source");

    /// <summary>Adds or replaces a string.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="text">What it says in this language.</param>
    /// <returns>This catalog, so a hand-built one reads as a list.</returns>
    public StringCatalog Set(string id, string text) {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(text);

        entries[id] = text;
        return this;
    }

    /// <summary>What a string says here, or <c>null</c> if this language does not have it.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The text, or <c>null</c>.</returns>
    public string? Find(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return entries.GetValueOrDefault(id);
    }

    /// <summary>Writes the catalog as YAML.</summary>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     Sorted by id, because the file is checked in and a map written in hash order produces a
    ///     diff on every save that says nothing.
    /// </remarks>
    public string Save() {
        var document = new YamlMapping().Set("language", new YamlScalar(Language));
        var strings = new YamlMapping();

        foreach (var id in entries.Keys.Order(StringComparer.Ordinal)) {
            strings.Set(id, new YamlScalar(entries[id], YamlScalarStyle.DoubleQuoted));
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
