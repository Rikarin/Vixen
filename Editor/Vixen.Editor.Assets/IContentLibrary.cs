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

    /// <summary>Copies files and folders from the user's disk into the project, then scans and imports.</summary>
    /// <param name="paths">Absolute paths on the user's disk — chosen in a dialog or dropped from the OS.</param>
    /// <param name="folder">The project-relative folder to land them in; empty means <c>Assets</c>.</param>
    /// <returns>How many of the paths were copied in. Each one refused has said why.</returns>
    int Import(IReadOnlyList<string> paths, string folder);

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

    /// <summary>Raised after an import copied something in and the database has been rescanned.</summary>
    event Action? Arrived;
}
