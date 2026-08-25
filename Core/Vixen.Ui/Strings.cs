// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>A string an interface shows, named by an id and carrying the source text it says.</summary>
/// <param name="Id">What a catalog calls it: <c>editor.command.file.save</c>.</param>
/// <param name="Source">What it says as it was written here, and what it says when nothing translates it.</param>
/// <remarks>
///     <para>
///         <b>The pair is the whole idea, and it is why localisation is not a retrofit.</b>
///         <c>item.Label = EditorStrings.Save.Text</c> is no more work at the call site than
///         <c>item.Label = "Save"</c>, so there is never a reason to write the literal — which is
///         the failure Stride's <c>Stride.Core.Translation</c> exists to repair, and repairing it
///         means finding every literal in an editor after the fact.
///     </para>
///     <para>
///         ⚠ <b>The source text lives at the declaration rather than in an <c>en</c> catalog.</b> An
///         application whose fallback is a file is one that shows <c>editor.command.file.save</c>
///         to anybody whose install is missing it, and the missing file is exactly what a
///         localisation bug looks like. Here the worst case is English.
///     </para>
///     <para>
///         What a <c>Strings.Resource</c> generator would add — doc 11 asks for one — is emitting
///         these declarations from a catalog rather than the other way round, so that an id used
///         nowhere and an id declared nowhere are both build errors. The shape it would emit is
///         a static class of these, member for member, plus an <c>All</c> list; nothing at a call
///         site changes when it lands.
///     </para>
/// </remarks>
public readonly record struct StringId(string Id, string Source) {
    /// <summary>What it says in the current language.</summary>
    public string Text => Strings.Get(this);

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>Which language an application is showing, and what each string says in it.</summary>
/// <remarks>
///     <para>
///         <b>Static, which is the one place in this assembly that is.</b> Every other service is an
///         instance a shell owns, because a document may have two of them; a language is a property
///         of the person using the application rather than of a window, and threading a localiser
///         through every control that shows a word is the design that makes people write the literal
///         instead.
///     </para>
///     <para>
///         ⚠ <b>Changing the language does not re-label what is already on screen.</b> A control was
///         handed a <c>string</c>, and nothing here knows which ones. <see cref="Changed" /> is what
///         a shell subscribes to in order to rebuild its menus and its palette; the editor asks for
///         a restart for the rest, which is what every editor with a language setting does.
///     </para>
/// </remarks>
public static class Strings {
    static readonly SortedSet<string> MissingIds = new(StringComparer.Ordinal);
    static StringCatalog current = StringCatalog.Source;

    /// <summary>The catalog in use.</summary>
    public static StringCatalog Catalog => current;

    /// <summary>Raised after <see cref="Use" /> changes it.</summary>
    public static event Action<StringCatalog>? Changed;

    /// <summary>
    ///     Ids asked for that the current catalog does not have, in id order.
    /// </summary>
    /// <remarks>
    ///     The list a translator works from, and the thing a test asserts is empty for a language
    ///     that claims to be complete. It is gathered rather than reported because an application
    ///     that logged a warning per missing string would log one per menu rebuild.
    /// </remarks>
    public static IReadOnlyCollection<string> Missing => MissingIds;

    /// <summary>Shows the interface in a language.</summary>
    /// <param name="catalog">The catalog, or <c>null</c> for the source strings.</param>
    public static void Use(StringCatalog? catalog) {
        current = catalog ?? StringCatalog.Source;
        MissingIds.Clear();

        Changed?.Invoke(current);
    }

    /// <summary>What a string says in the current language.</summary>
    /// <param name="id">The string.</param>
    /// <returns>The translation, or its source text if there is none.</returns>
    public static string Get(StringId id) {
        if (id.Id is null) {
            return id.Source ?? string.Empty;
        }

        var catalog = current;

        if (catalog.Find(id.Id) is { } translated) {
            return translated;
        }

        // ⚠ Only recorded against a catalog that is not the source one. Against the source catalog
        // every id is "missing" by construction, and a list of every string in the application is
        // not a list of anything.
        if (!ReferenceEquals(catalog, StringCatalog.Source)) {
            MissingIds.Add(id.Id);
        }

        return id.Source;
    }

    /// <summary>A catalog holding every string a set of declarations names, for a translator to start from.</summary>
    /// <param name="language">What to call the new catalog.</param>
    /// <param name="ids">The declarations to export — an <c>All</c> list, or several concatenated.</param>
    /// <returns>The catalog, filled with the source text.</returns>
    /// <remarks>
    ///     <para>
    ///         The template a <c>Strings.Resource</c> generator would emit at build time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It takes the declarations rather than finding them.</b> Nothing here can walk
    ///         "every <see cref="StringId" /> in the process": they are static properties on
    ///         whichever classes an application chose to declare, and a list gathered by reflecting
    ///         over those at run time is a list the application's trimming settings are entitled to
    ///         shorten. Passing the <c>All</c> lists in is what makes the answer a fact about the
    ///         source rather than about the trimmer.
    ///     </para>
    /// </remarks>
    public static StringCatalog Template(string language, IEnumerable<StringId> ids) {
        ArgumentNullException.ThrowIfNull(ids);

        var catalog = new StringCatalog(language);

        foreach (var id in ids) {
            catalog.Set(id.Id, id.Source);
        }

        return catalog;
    }
}
