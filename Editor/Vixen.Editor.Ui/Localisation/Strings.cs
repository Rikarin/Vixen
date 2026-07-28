// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>A string the editor shows, named by an id and carrying the source text it says.</summary>
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
///         editor whose fallback is a file is an editor that shows <c>editor.command.file.save</c>
///         to anybody whose install is missing it, and the missing file is exactly what a
///         localisation bug looks like. Here the worst case is English.
///     </para>
///     <para>
///         What a <c>Strings.Resource</c> generator would add — doc 11 asks for one — is emitting
///         these declarations from a catalog rather than the other way round, so that an id used
///         nowhere and an id declared nowhere are both build errors. The shape it would emit is
///         <see cref="EditorStrings" />, member for member; nothing at a call site changes when it
///         lands.
///     </para>
/// </remarks>
public readonly record struct StringId(string Id, string Source) {
    /// <summary>What it says in the current language.</summary>
    public string Text => Strings.Get(this);

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>Which language the editor is showing, and what each string says in it.</summary>
/// <remarks>
///     <para>
///         <b>Static, which is the one place in this assembly that is.</b> Every other service here
///         is an instance a shell owns, because a document may have two of them; a language is a
///         property of the person using the editor rather than of a window, and threading a
///         localiser through every control that shows a word is the design that makes people write
///         the literal instead.
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
    ///     that claims to be complete. It is gathered rather than reported because an editor that
    ///     logged a warning per missing string would log one per menu rebuild.
    /// </remarks>
    public static IReadOnlyCollection<string> Missing => MissingIds;

    /// <summary>Shows the editor in a language.</summary>
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

        if (current.Find(id.Id) is { } translated) {
            return translated;
        }

        // ⚠ Only recorded against a catalog that is not the source one. Against the source catalog
        // every id is "missing" by construction, and a list of every string in the editor is not a
        // list of anything.
        if (!ReferenceEquals(current, StringCatalog.Source)) {
            MissingIds.Add(id.Id);
        }

        return id.Source;
    }

    /// <summary>A catalog holding every string the editor declares, for a translator to start from.</summary>
    /// <param name="language">What to call the new catalog.</param>
    /// <returns>The catalog, filled with the source text.</returns>
    /// <remarks>
    ///     The template a <c>Strings.Resource</c> generator would emit at build time. Written from
    ///     <see cref="EditorStrings.All" /> so that a string added to the editor appears in the next
    ///     template without anybody remembering to add it.
    /// </remarks>
    public static StringCatalog Template(string language) {
        var catalog = new StringCatalog(language);

        foreach (var id in EditorStrings.All) {
            catalog.Set(id.Id, id.Source);
        }

        return catalog;
    }
}
