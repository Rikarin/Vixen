// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>One language's worth of an application's strings, by id.</summary>
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
///     <para>
///         ⚠ <b>No file format, deliberately.</b> A catalog is built by <see cref="Set" /> and read
///         by <see cref="Find" />, and how it got there is the application's business: the editor
///         reads YAML through <c>StringCatalogYaml</c>, and an application publishing NativeAOT is
///         free to read JSON through a source-generated reader instead. Attaching a parser here
///         would put a serialiser in the package closure of every application that shows a word,
///         including the ones that never load a catalog at all.
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
}
