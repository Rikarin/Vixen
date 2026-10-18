// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Assets;

/// <summary>One named search of the project browser: the query, never its result.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The query and not what it matched, which is the whole difference between this and a
///         collection.</b> A filter re-runs, so it keeps answering as the project changes; a stored
///         list of matches is a snapshot that is wrong the moment somebody imports anything. Doc 20
///         § B1 asks for both and they are the same storage question with opposite answers.
///     </para>
///     <para>
///         ⚠ <b><see cref="Kind" /> is the importer tag and not an extension</b>, because that is
///         what the browser's own dropdown offers — the list is what the project actually holds
///         rather than everything the engine can import. Empty means every kind, which is the
///         dropdown's <c>All types</c> line rather than a filter that matches nothing.
///     </para>
/// </remarks>
[DataContract("SavedAssetFilter")]
public sealed class SavedAssetFilter {
    /// <summary>What the user called it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What was in the search box, which may be empty.</summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>The importer tag the kind dropdown was on, or empty for every kind.</summary>
    public string Kind { get; set; } = string.Empty;
}
