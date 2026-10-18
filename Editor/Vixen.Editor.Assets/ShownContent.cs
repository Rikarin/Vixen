// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;

namespace Vixen.Editor.Assets;

/// <summary>What the content browser is looking at: a folder or a collection, narrowed by a search and a kind.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The browser's durable state, held by the service rather than by the panel</b> — doc 50
///         § D2's rule for the parked fields. A panel's factory runs again on every reopen, so the
///         folder somebody walked into and the search they typed lived in fields of a view that was
///         thrown away with it: closing the Project tab and opening it again put them back at
///         <c>Assets</c> with an empty search box. The panel reads this on build and writes through it.
///     </para>
///     <para>
///         ⚠ <b>Session state, not a preference.</b> The grid toggle and the tile size are written to
///         the user's preferences because they are how somebody likes to browse; which folder they are
///         standing in is where they are, and it is not carried across a restart.
///     </para>
/// </remarks>
public sealed class ShownContent {
    /// <summary>The folder being shown, by project-relative path.</summary>
    /// <remarks>
    ///     By path rather than by node: a rescan rebuilds every <c>AssetTreeNode</c>, so a held
    ///     reference names a folder that no longer exists.
    /// </remarks>
    public string Folder { get; set; } = AssetTree.RootName;

    /// <summary>The collection being shown instead of the folder, or <see langword="null" />.</summary>
    public string? Collection { get; set; }

    /// <summary>What is in the search box.</summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>The importer tag the kind dropdown is on, or empty for every kind.</summary>
    public string Kind { get; set; } = string.Empty;
}
