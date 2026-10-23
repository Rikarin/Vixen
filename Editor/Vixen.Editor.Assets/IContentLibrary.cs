// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;

namespace Vixen.Editor.Assets;

/// <summary>Doc 50 § D2's Content Library service: what a person can do to the project's assets.</summary>
/// <remarks>
///     <para>
///         <b>A use case is a method here, and a menu line is only a name for one.</b> Import, package
///         export and the browser's collections and saved filters used to be private methods of
///         <c>EditorApplication</c> closed over its fields — so the Assets menu, the browser's OS drop
///         and the harness each reached them through the application, and a plugin could not reach
///         them at all. The editor publishes this through <c>PluginServices</c>, so a module asks for
///         it with <c>Require&lt;IContentLibrary&gt;()</c> exactly as it asks for <c>IMeshBaker</c>.
///     </para>
///     <para>
///         ⚠ <b>One API for collections and saved filters, and still two files — on purpose.</b> A
///         collection is a set of <see cref="AssetId" />s, which mean nothing in another project, so
///         it is a settings asset under <c>ProjectSettings/</c>; a saved filter is a query, which is
///         meaningful in every project, so it is a user preference. Doc 50 § F1 called that "same
///         feature, two stores"; the storage split is the documented decision of both types, and what
///         was actually doubled was the <i>API</i> — the application re-declared
///         <see cref="AssetCollections" />'s three verbs to add the save, and kept the filters' two in
///         another file. This is where both now are.
///     </para>
/// </remarks>
public interface IContentLibrary {
    /// <summary>The project whose assets these are.</summary>
    EditorProject Project { get; }

    /// <summary>What the content browser is showing, which outlives the panel that shows it.</summary>
    ShownContent Shown { get; }

    /// <summary>Copies files and folders from the user's disk into the project, then scans and imports.</summary>
    /// <param name="paths">Absolute paths on the user's disk — chosen in a dialog or dropped from the OS.</param>
    /// <param name="folder">The project-relative folder to land them in; empty means <c>Assets</c>.</param>
    /// <returns>How many of the paths were copied in. Each one refused has said why.</returns>
    int Import(IReadOnlyList<string> paths, string folder);

    /// <summary>Makes a new asset of a kind in the selected folder, scans it in and says so.</summary>
    /// <param name="kind">What to make — a Create ▸ line's kind, whoever contributed it.</param>
    /// <returns>
    ///     The new asset, or <see langword="null" /> when the file could not be written. Opening it is the
    ///     caller's: a document is the Documents context's, not this one's.
    /// </returns>
    AssetId? Create(NewAssetKind kind);

    /// <summary>Gives an asset a new name in the folder it is in, its sidecar with it, and says why not if it could not.</summary>
    /// <param name="asset">Which asset.</param>
    /// <param name="name">The new name, with or without the extension — a name without one keeps the asset's.</param>
    /// <returns>What happened. A refusal has already been reported.</returns>
    /// <remarks>
    ///     ⚠ <b>The identity survives because the <c>.meta</c> moves with the file</b> — see
    ///     <see cref="AssetOperations" />, which is that invariant and nothing else. Nothing that points
    ///     at the asset is rewritten, because nothing needs to be: a reference is a GUID.
    ///     ⚠ <b>Not undoable</b>, as the browser's rename never was: it is an operation on files, not an
    ///     edit to a document, and no command stack records it.
    /// </remarks>
    AssetOperation Rename(AssetId asset, string name);

    /// <summary>Makes an empty folder inside another one, and says why not if it could not.</summary>
    /// <param name="parent">Where, relative to the project root — <c>Assets</c> or a folder under it.</param>
    /// <param name="name">What it is called. A name already taken there is numbered rather than refused.</param>
    /// <returns>What happened. A refusal has already been reported.</returns>
    /// <remarks>
    ///     ⚠ <b>Over the project, not the browser (#1292).</b> Assets ▸ New Folder was the application's
    ///     and read nothing of the browser panel but whether it was open; the browser follows the
    ///     folder in through <see cref="Rearranged" />, as it follows a rename.
    /// </remarks>
    AssetOperation CreateFolder(string parent, string name);

    /// <summary>Moves assets into a folder of the project, each with its sidecar.</summary>
    /// <param name="assets">What to move.</param>
    /// <param name="folder">The destination, relative to the project root — <c>Assets/Props</c>.</param>
    /// <returns>How many moved. Each one refused has said why. Not undoable, as <see cref="Rename" /> is not.</returns>
    int Move(IReadOnlyList<AssetId> assets, string folder);

    /// <summary>Deletes assets and the sidecars that carry their identities, without asking.</summary>
    /// <param name="assets">What to delete.</param>
    /// <returns>How many went. Each one refused has said why.</returns>
    /// <remarks>
    ///     ⚠ <b>This does not ask, and cannot be undone.</b> Asking — with the referrers that would be
    ///     left pointing at nothing in the question — is the command's, which is a name for this; a
    ///     script or a plugin deleting what it generated has nobody to ask.
    /// </remarks>
    int Delete(IReadOnlyList<AssetId> assets);

    /// <summary>Imports every asset in the project again, on the content tasks' worker.</summary>
    void ReimportAll();

    /// <summary>Whether an import or a build is running, so another should wait.</summary>
    bool IsImporting { get; }

    /// <summary>Everything that points at some assets, one step back along the reference graph.</summary>
    /// <param name="assets">What is pointed at — usually the selection.</param>
    /// <returns>The referrers, in the order found and each once; empty when nothing points at them.</returns>
    IReadOnlyList<AssetId> Referrers(IEnumerable<AssetId> assets);

    /// <summary>Everything some assets point at, one step forward along the reference graph.</summary>
    /// <param name="assets">Where to start — usually the selection.</param>
    /// <returns>The dependencies, in the order found and each once; empty when there are none.</returns>
    IReadOnlyList<AssetId> Dependencies(IEnumerable<AssetId> assets);

    /// <summary>Everything some assets reach through the reference graph, themselves included.</summary>
    /// <param name="roots">Where to start.</param>
    /// <returns>The closure, roots first and each asset once.</returns>
    IReadOnlyList<AssetId> Closure(IEnumerable<AssetId> roots);

    /// <summary>Writes assets into a package archive, each under its project-relative path with its sidecar.</summary>
    /// <param name="path">The archive to write, replaced if it exists.</param>
    /// <param name="assets">What goes in — usually a <see cref="Closure" />.</param>
    /// <returns>How many assets were written, or <c>-1</c> when the archive could not be written.</returns>
    int ExportPackage(string path, IReadOnlyList<AssetId> assets);

    /// <summary>The project's collections, in the order they were made.</summary>
    IReadOnlyList<SavedAssetSet> Collections { get; }

    /// <summary>Puts assets into a collection, making it if it is not there yet, and writes the file.</summary>
    /// <param name="name">What the collection is called.</param>
    /// <param name="assets">What goes into it, which may be nothing.</param>
    void AddToCollection(string name, IReadOnlyList<AssetId> assets);

    /// <summary>Takes assets out of a collection, leaving the collection.</summary>
    /// <param name="name">What the collection is called.</param>
    /// <param name="assets">What comes out of it.</param>
    void RemoveFromCollection(string name, IReadOnlyList<AssetId> assets);

    /// <summary>Forgets a whole collection, which touches no file it named.</summary>
    /// <param name="name">What it is called.</param>
    void ForgetCollection(string name);

    /// <summary>The user's saved filters, in the order they were saved.</summary>
    IReadOnlyList<SavedAssetFilter> Filters { get; }

    /// <summary>Keeps one named filter, replacing a saved one of the same name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="query">What was in the search box.</param>
    /// <param name="kind">The importer tag, or empty for every kind.</param>
    void SaveFilter(string name, string query, string kind);

    /// <summary>Forgets a saved filter.</summary>
    /// <param name="name">What it is called.</param>
    void ForgetFilter(string name);

    /// <summary>Raised after the collections changed and were written.</summary>
    event Action? CollectionsChanged;

    /// <summary>Raised after an import or a <see cref="Create" /> put something in and the database has been rescanned.</summary>
    event Action? Arrived;

    /// <summary>
    ///     Raised after a <see cref="Rename" />, <see cref="CreateFolder" />, <see cref="Move" /> or
    ///     <see cref="Delete" /> — refused or not — once the database agrees with the disk.
    /// </summary>
    /// <remarks>
    ///     Raised on a refusal too: the browser's tree has already shown the name that was typed or the
    ///     row where it was dropped, and a view left showing what the disk does not have is worse than
    ///     the refusal it was told about.
    /// </remarks>
    event Action? Rearranged;
}
