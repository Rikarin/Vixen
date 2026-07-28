// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.NodeGraph;

/// <summary>The stylesheet the graph view's own elements come with.</summary>
/// <remarks>
///     <para>
///         A third sheet, after <c>ControlTheme</c> and <c>AdvancedTheme</c> and written against the
///         same tokens: everything <c>NodeCanvas</c> draws is already styled by the second, and what is
///         here is only the four elements this assembly adds — the search popup, its rows, a sticky
///         note, and the preview layer.
///     </para>
///     <para>
///         ⚠ <b>Both of those have to be loaded first.</b> Every colour below is a
///         <c>var(--…)</c> against a token one of them declares, and a custom property nothing declared
///         substitutes to nothing — which is a popup with no background over a canvas.
///     </para>
/// </remarks>
public static class NodeGraphTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other two sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        /* ── The view ───────────────────────────────────────────────────────── */
        node-graph { flex-direction: column; flex-grow: 1; overflow: hidden; position: relative; }
        node-graph > node-canvas { flex-grow: 1; }

        /* The preview layer covers the canvas and takes no clicks: it is a picture of what the
           nodes under it are worth, and a full-canvas element that swallowed presses would make
           every node unreachable. Same bargain as node-wires. */
        node-previews {
            position: absolute;
            left: 0; top: 0; right: 0; bottom: 0;
            pointer-events: none;
        }

        /* ── Sticky notes ───────────────────────────────────────────────────── */
        graph-note {
            position: absolute;
            background-color: var(--surface-raised);
            border: 1px solid var(--border);
            border-radius: 4px;
            padding: 0.5em;
            overflow: hidden;
        }

        graph-note.parked { display: none; }
        graph-note-body { color: var(--foreground-muted); white-space: pre-wrap; }

        /* ── Search to create ───────────────────────────────────────────────── */
        node-search {
            flex-direction: column;
            width: 320px;
            max-height: 380px;
            background-color: var(--surface-overlay);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 6px;
            gap: 4px;
        }

        node-search-list { flex-direction: column; overflow: hidden; }
        node-search-empty { padding: 8px; color: var(--foreground-muted); }
        node-search-empty.hidden { display: none; }

        node-search-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 4px 8px;
            border-radius: 4px;
            background-color: transparent;
        }

        node-search-row.hidden { display: none; }
        node-search-row:hover { background-color: var(--surface-hover); }

        /* Checked is the highlight the popup owns, not a focus ring: the field keeps the focus so
           that the next letter typed goes into it. See NodeSearchPopup. */
        node-search-row:checked { background-color: var(--accent); color: var(--accent-foreground); }

        node-search-category { flex-grow: 1; text-align: right; color: var(--foreground-muted); }
        node-search-port { color: var(--accent); }
        node-search-port:empty { display: none; }
        """;
}
