// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>The stylesheet the advanced controls come with.</summary>
/// <remarks>
///     <para>
///         A second sheet rather than more rules in <see cref="ControlTheme" />, because the two
///         assemblies ship separately: an application that wants a button and not a docking host
///         should not carry three hundred lines of CSS about splitters. Both load as
///         <see cref="StyleOrigin.UserAgent" /> and both are written against the same tokens, so
///         recolouring the root recolours everything either of them draws.
///     </para>
///     <para>
///         ⚠ <b><see cref="Install" /> loads this and not the base theme.</b> An application needs
///         both, in that order — this sheet reads <c>--surface</c>, <c>--border</c> and the rest, and
///         a custom property that nothing declared substitutes to nothing.
///     </para>
/// </remarks>
public static class AdvancedTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have <see cref="ControlTheme" /> in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        /* ── Docking ────────────────────────────────────────────────────────── */
        docking-host { flex-direction: column; position: relative; flex-grow: 1; }

        /* Where panels wait. `display: none` rather than removal, because an element outside a
           document is a removed element and removal is final. */
        dock-detached { display: none; }

        dock-surface { flex-direction: column; flex-grow: 1; overflow: hidden; }

        dock-split { flex-grow: 1; }
        dock-split.horizontal { flex-direction: row; }
        dock-split.vertical { flex-direction: column; }

        dock-splitter { flex-grow: 0; flex-shrink: 0; background-color: var(--border); }
        dock-split.horizontal > dock-splitter { width: 6px; }
        dock-split.vertical > dock-splitter { height: 6px; }
        dock-splitter:hover { background-color: var(--accent); }

        /* ⚠ The zero minimums are load-bearing. A flex item's base size is clamped by its
           minimum *before* the free space is shared out, so a group with `min-width: 48px`
           gets 48 pixels plus its share of what is left — and a splitter saved at 25% comes
           back at 28%. What stops a half being dragged to nothing is DockSplitNode's ratio
           clamp, which is the guard that does not distort the ratio it is guarding. */
        dock-group {
            flex-direction: column;
            min-width: 0px;
            min-height: 0px;
            overflow: hidden;
            background-color: var(--surface);
        }

        dock-tabstrip {
            flex-direction: row;
            flex-shrink: 0;
            background-color: var(--surface-sunken);
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
        }

        dock-tab {
            flex-direction: row;
            align-items: center;
            gap: 6px;
            padding: 4px 10px;
            border-width: 0px;
            background-color: transparent;
            color: var(--text-muted);
        }

        dock-tab:hover { color: var(--text); }
        dock-tab:checked { background-color: var(--surface); color: var(--text); }
        dock-tab icon { width: 10px; height: 10px; }

        dock-body { flex-direction: column; flex-grow: 1; overflow: hidden; }

        /* Every panel of a group is in its body; the selected one is the one with a size. */
        dock-panel { display: none; flex-direction: column; flex-grow: 1; }
        dock-panel.selected { display: flex; }

        dock-float {
            position: absolute;
            left: 0px;
            top: 0px;
            flex-direction: column;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: var(--surface-raised);
            overflow: hidden;
        }

        /* Over everything and clickable through: the preview says where a drop would land, and a
           preview that took the pointer would make the drop land on the preview. */
        dock-preview {
            position: absolute;
            left: 0px;
            top: 0px;
            background-color: #3b6cf055;
            border-width: 2px;
            border-color: var(--accent);
            pointer-events: none;
        }

        /* One name for "not showing", used by everything in this sheet. It is a class rather than a
           state because it is a mode something was put into rather than a condition it is in. */
        .hidden { display: none; }

        /* ── Tree ───────────────────────────────────────────────────────────── */
        tree-view {
            flex-direction: column;
            flex-grow: 1;
            position: relative;
            overflow: hidden;

            --row-height: 22px;
            --indent: 14px;
        }

        /* ⚠ `flex-basis: 0px` as well as the grow, and it is load-bearing. Without it the viewport
           takes its base size from its content — a hundred thousand rows of it — so it never
           overflows, the scroll range is zero, and the virtualiser realises every row there is.
           That failure is silent: the tree looks right and the process runs out of memory. */
        tree-view scroll-view { flex-grow: 1; flex-basis: 0px; }
        tree-view scroll-content { min-width: 100%; }

        /* ⚠ Absolutely positioned, which is what virtualisation needs: where row 40 000 goes has to
           be arithmetic rather than the sum of the 39 999 above it. */
        tree-row {
            position: absolute;
            left: 0px;
            right: 0px;
            flex-direction: row;
            align-items: center;
            gap: 4px;
            padding: 0px 6px;
            color: var(--text);
        }

        tree-row:hover { background-color: var(--surface-sunken); }
        tree-row:checked { background-color: var(--accent); color: var(--accent-text); }
        tree-row.parked { display: none; }

        tree-indent { flex-shrink: 0; }
        tree-row icon { width: 10px; height: 10px; flex-shrink: 0; color: var(--text-muted); }
        tree-row.leaf icon { display: none; }
        tree-label { flex-grow: 1; }

        .tree-editor { flex-grow: 1; padding: 0px 2px; border-radius: 3px; }

        tree-drop-indicator {
            position: absolute;
            left: 0px;
            top: 0px;
            background-color: #3b6cf055;
            border-width: 1px;
            border-color: var(--accent);
            pointer-events: none;
        }

        /* ── Property grid ──────────────────────────────────────────────────── */
        property-grid { flex-direction: column; gap: 6px; }
        property-grid search-box { flex-shrink: 0; }
        property-body { flex-direction: column; }

        property-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 2px 4px;
            min-height: 24px;
        }

        property-row.filtered { display: none; }
        property-row:hover { background-color: var(--surface-sunken); }

        property-label { width: 40%; flex-shrink: 0; color: var(--text-muted); }
        property-editor { flex-grow: 1; flex-direction: row; align-items: center; }
        property-editor textbox, property-editor numeric-input, property-editor select { flex-grow: 1; }
        property-editor slider { flex-grow: 1; }
        .property-readonly { color: var(--text-muted); }
        """;
}
