// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.App;

/// <summary>Doc 20 § B1's collections: named sets of assets, kept with the project.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Per project and not per user, which is the one thing a saved filter got the other way
///         round.</b> <c>EditorPreferences</c> is user-wide — one file across every project somebody
///         opens — and a collection holds <see cref="AssetId" />s, which mean nothing in another
///         project. So this is a settings asset under <c>ProjectSettings/</c>, read and written
///         through <c>ProjectSettingsStore</c> like every other one.
///     </para>
///     <para>
///         ⚠ <b><c>Library/</c> would have been the wrong per-project home even though it is the
///         per-project one that is not committed.</b> Everything under it is reproducible from a
///         source file plus its <c>.meta</c> — that is what makes deleting it safe — and a collection
///         is authored data that nothing can rebuild. Storing one there is storing it somewhere the
///         editor is allowed to throw away.
///     </para>
///     <para>
///         ⚠ <b>And the ids are the point.</b> A collection holding paths breaks the first time
///         somebody moves a file, which is the move a content browser exists to make cheap; an
///         <see cref="AssetId" /> is what the database's identity is for and survives it.
///         <c>BrowserCollectionTests</c> moves a file on disk and finds it in its collection
///         afterwards, which is the assertion a path-keyed store cannot pass.
///     </para>
/// </remarks>
[DataContract("AssetCollections")]
public sealed class AssetCollections {
    /// <summary>The collections, in the order they were made.</summary>
    public List<SavedAssetSet> Collections { get; set; } = [];

    /// <summary>The collection of a name, or <see langword="null" />.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The collection, or null when no collection has that name.</returns>
    public SavedAssetSet? Find(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : Collections.FirstOrDefault(collection => string.Equals(collection.Name, name, StringComparison.Ordinal));

    /// <summary>Puts assets into a collection, making it if it is not there yet.</summary>
    /// <param name="name">What the collection is called.</param>
    /// <param name="assets">What to put in it.</param>
    /// <returns>Whether anything changed, so a caller knows whether to write the file.</returns>
    /// <remarks>
    ///     ⚠ <b>A set rather than a list, so dropping the same asset twice is not two rows.</b> A
    ///     drag carries the whole selection and the commonest second drag is the same selection plus
    ///     one more; a collection that grew a duplicate every time would show the same tile twice and
    ///     make Remove ambiguous.
    /// </remarks>
    public bool Add(string name, IReadOnlyList<AssetId> assets) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(assets);

        var collection = Find(name);
        var created = collection is null;

        if (collection is null) {
            collection = new SavedAssetSet { Name = name };
            Collections.Add(collection);
        }

        var changed = false;

        foreach (var asset in assets) {
            if (asset.IsEmpty || collection.Assets.Contains(asset)) {
                continue;
            }

            collection.Assets.Add(asset);
            changed = true;
        }

        // Making one is a change even when nothing goes into it, which is what makes
        // "New Collection…" over an empty selection produce a row there is somewhere to drop onto.
        return changed || created;
    }

    /// <summary>Takes assets out of a collection, leaving the collection itself.</summary>
    /// <param name="name">What the collection is called.</param>
    /// <param name="assets">What to take out.</param>
    /// <returns>Whether anything changed.</returns>
    public bool Remove(string name, IReadOnlyList<AssetId> assets) {
        ArgumentNullException.ThrowIfNull(assets);

        if (Find(name) is not { } collection) {
            return false;
        }

        return collection.Assets.RemoveAll(assets.Contains) > 0;
    }

    /// <summary>Forgets a whole collection.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>Whether there was one.</returns>
    /// <remarks>
    ///     ⚠ <b>The assets are untouched, and the wording is deliberate.</b> A collection is a way of
    ///     looking at the project rather than a place in it, so forgetting one is not a delete —
    ///     which is why the menu line says Forget, the same word the saved filters use.
    /// </remarks>
    public bool Forget(string name) =>
        Collections.RemoveAll(collection => string.Equals(collection.Name, name, StringComparison.Ordinal)) > 0;
}

/// <summary>One named set of assets: what somebody put in it, never a query.</summary>
/// <remarks>
///     ⚠ <b>The result and not the query, which is the whole difference between this and
///     <see cref="SavedAssetFilter" />.</b> Doc 20 § B1 asks for both and they are the same storage
///     question with opposite answers: a filter re-runs and finds what has arrived since it was
///     named, a collection holds exactly what was put in it and goes on holding it after those files
///     have been moved, renamed and re-imported.
///     <para>
///         ⚠ <b>Named a set although everything a person sees calls it a collection</b>, because
///         CA1711 reserves the <c>Collection</c> suffix for types that are one — this holds a list, it
///         is not a list — and a suppression to keep the prettier name would be a rule turned off for
///         a naming preference.
///     </para>
/// </remarks>
[DataContract("SavedAssetSet")]
public sealed class SavedAssetSet {
    /// <summary>What the user called it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What is in it, by identity.</summary>
    /// <remarks>
    ///     ⚠ Ids rather than paths, and an id that no longer resolves is kept rather than pruned: a
    ///     file that is missing today is a file somebody may restore or a branch they may switch
    ///     back to, and a collection that quietly emptied itself over a checkout is worse than one
    ///     showing fewer tiles than it lists.
    /// </remarks>
    public List<AssetId> Assets { get; set; } = [];
}
