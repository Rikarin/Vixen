// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;

namespace Vixen.Editor.Assets;

/// <summary>What the content browser is looking at and how: a folder or a collection, a search, a kind, tiles or a tree.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The browser's durable state, held by the service rather than by the panel</b> — doc 50
///         § D2's rule for the parked fields. A panel's factory runs again on every reopen, so the
///         folder somebody walked into and the search they typed lived in fields of a view that was
///         thrown away with it: closing the Project tab and opening it again put them back at
///         <c>Assets</c> with an empty search box. The panel reads this on build and writes through it.
///     </para>
///     <para>
///         ⚠ <b>Two halves with two lifetimes.</b> The folder, the collection, the search and the kind
///         are where somebody is standing, and are not carried across a restart. The grid toggle and
///         the tile size are how somebody likes to browse, so the editor builds this over the user's
///         preferences and they are read from and written to there — the panel only ever reads and
///         writes them here, which is what used to be two events out of the panel and a restore into
///         it on every reopen.
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

    /// <summary>Whether the browser shows tiles rather than the tree.</summary>
    public bool Grid {
        get => grid();

        set {
            if (grid() != value) {
                keep(value, TileSize);
            }
        }
    }

    /// <summary>How big the tiles are, by the name the size dropdown shows; empty is the grid's own default.</summary>
    /// <remarks>
    ///     A name rather than a number: a tile is a width, a height, a glyph and a caption that have to
    ///     agree, and a name is what a preferences file can hold across a version that changes the steps.
    /// </remarks>
    public string TileSize {
        get => tileSize();

        set {
            ArgumentNullException.ThrowIfNull(value);

            if (!string.Equals(tileSize(), value, StringComparison.Ordinal)) {
                keep(Grid, value);
            }
        }
    }

    /// <summary>A model whose view lives only as long as it does: tiles, at the grid's default size.</summary>
    public ShownContent() {
        var tiles = true;
        var size = string.Empty;

        grid = () => tiles;
        tileSize = () => size;
        keep = (on, named) => (tiles, size) = (on, named);
    }

    /// <summary>A model whose view is kept somewhere that outlives it — the editor's preferences.</summary>
    /// <param name="grid">Whether tiles are wanted, asked for on every read.</param>
    /// <param name="tileSize">The tile size's name, asked for on every read.</param>
    /// <param name="keep">Keeps a changed view: both halves, so a store writes one consistent record.</param>
    /// <remarks>
    ///     ⚠ <b>Asked for on every read rather than copied in, for the saved filters' reason.</b> The
    ///     settings window's Revert re-reads the preferences into a <i>new</i> object, so a copy taken at
    ///     start-up would go on showing the view from before the Revert — and seeding a copy one setter
    ///     at a time writes a half-restored view back, which is what the first version of this did.
    /// </remarks>
    public ShownContent(Func<bool> grid, Func<string> tileSize, Action<bool, string> keep) {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(tileSize);
        ArgumentNullException.ThrowIfNull(keep);

        this.grid = grid;
        this.tileSize = tileSize;
        this.keep = keep;
    }

    readonly Func<bool> grid;
    readonly Func<string> tileSize;
    readonly Action<bool, string> keep;
}
