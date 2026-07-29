// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.AssetEditors;

/// <summary>The stylesheet the asset editors' own elements come with.</summary>
/// <remarks>
///     <para>
///         A fifth user-agent sheet, after <c>ControlTheme</c>, <c>AdvancedTheme</c>,
///         <c>EditorTheme</c> and <c>InspectorTheme</c>, and written against the tokens they declare.
///         What is here is only the elements this assembly adds — the matrix, the ladder, the fact
///         rows, the group list, the preview panes. Everything a drawer or a code editor builds is
///         already styled by the sheets above.
///     </para>
///     <para>
///         ⚠ <b>All four of those have to be loaded first.</b> Every colour below is a
///         <c>var(--…)</c> against a token one of them declares, and a custom property nothing
///         declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>Every <c>flex-direction: column</c> below that looks redundant is load-bearing.</b>
///         CSS's initial direction is <c>row</c> and <c>LayoutStyleBuilder</c> starts from CSS's
///         initial values, so an element nothing styles lays its children out side by side — which
///         for a settings panel means every section beside the one before it.
///     </para>
/// </remarks>
public static class AssetEditorTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other four sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        /* ── Shared ─────────────────────────────────────────────────────────── */
        import-settings, texture-editor, model-editor, material-editor, code-editor-pane,
        group-editor, compositor-editor, prefab-editor {
            flex-direction: column;
            flex-grow: 1;
            gap: 8px;
            padding: 6px;
            overflow: hidden;
        }

        /* `.hidden` is AdvancedTheme's and is not redeclared here: two rules for one class is two
           places to look when something will not disappear. */

        /* A fact is a name and a number, and the number is what the eye is looking for — so the
           name is muted and the value is not, rather than both being the same grey. */
        fact-row { flex-direction: row; align-items: center; gap: 8px; min-height: 20px; }
        fact-name { width: 40%; min-width: 72px; flex-shrink: 0; color: var(--text-muted); }
        fact-value { flex-grow: 1; min-width: 0; }
        texture-facts, model-facts, material-facts { flex-direction: column; }

        /* ── The override matrix ─────────────────────────────────────────────
           A row is a setting and a column is a target. The grid scrolls sideways rather than
           squeezing: four platforms in a docked inspector is narrower than any cell can be, and a
           column that shrank to nothing would be a tick nobody can hit. */
        override-matrix { flex-direction: column; gap: 6px; }
        override-body { flex-direction: column; overflow-x: auto; }
        override-bar { flex-direction: row; align-items: center; gap: 6px; }
        override-bar > textbox { flex-grow: 1; min-width: 0; }

        override-row {
            flex-direction: row;
            align-items: center;
            gap: 6px;
            min-height: 26px;
            padding: 1px 2px;
        }

        override-row.header { border-width: 0px 0px 1px 0px; border-color: var(--border); }
        override-row.header override-name, override-title { color: var(--text-muted); }

        override-name { width: 120px; min-width: 96px; flex-shrink: 0; color: var(--text-muted); }

        override-column { flex-direction: row; align-items: center; gap: 4px; width: 160px; flex-shrink: 0; }
        override-title { flex-grow: 1; min-width: 0; }

        /* ⚠ `min-width: 0` is load-bearing, for the reason InspectorTheme gives about vectors: a
           number box's text is deliberately unshrinkable, so without this a cell refuses to narrow
           and the row overflows rather than the box clipping. */
        override-cell {
            flex-direction: row;
            align-items: center;
            gap: 4px;
            width: 160px;
            min-width: 0;
            flex-shrink: 0;
        }

        override-cell > checkbox { flex-shrink: 0; }
        override-cell > select, override-cell > numeric-input, override-cell > textbox { flex-grow: 1; min-width: 0; }

        /* A cell that does not decide the value repeats the base's, and says so by being dimmer —
           the same mechanism the inspector's override mark uses, so the two read as one idea. */
        override-cell { opacity: 0.55; }
        override-cell.overridden { opacity: 1; }
        override-row > override-cell:first-child { opacity: 1; }

        /* ── The texture editor ─────────────────────────────────────────────── */
        texture-editor > image {
            height: 220px;
            flex-shrink: 0;
            background-color: var(--surface-sunken);
            border-radius: var(--radius-control, 4px);
        }

        texture-channels { flex-direction: row; gap: 4px; }
        texture-ladder { flex-direction: column; }

        ladder-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            min-height: 20px;
            padding: 0px 4px;
            border-radius: 4px;
        }

        ladder-row.selected { background-color: var(--accent-deep, var(--surface-raised)); }
        ladder-level { width: 28px; flex-shrink: 0; color: var(--text-muted); }
        ladder-extent { width: 96px; flex-shrink: 0; }
        ladder-bytes { flex-grow: 1; min-width: 0; color: var(--text-muted); }

        /* ── The model editor ───────────────────────────────────────────────── */
        model-editor > tree-view { max-height: 220px; flex-shrink: 0; }

        /* ── The material editor ────────────────────────────────────────────── */
        material-editor > image {
            height: 200px;
            flex-shrink: 0;
            background-color: var(--surface-sunken);
            border-radius: var(--radius-control, 4px);
        }

        material-parameters { flex-direction: column; }
        material-bar { flex-direction: row; align-items: center; gap: 6px; }
        material-bar > textbox { flex-grow: 1; min-width: 0; }

        /* ── The code editors ───────────────────────────────────────────────── */
        code-editor-pane { flex-direction: row; gap: 6px; padding: 0px; }
        code-editor-pane > code-editor { flex-grow: 1; flex-basis: 0px; min-width: 0; }

        preview-pane {
            flex-direction: column;
            flex-grow: 1;
            flex-basis: 0px;
            min-width: 0;
            overflow: hidden;
            background-color: var(--surface-sunken);
            border-radius: var(--radius-panel, 5px);
        }

        preview-surface { flex-direction: column; flex-grow: 1; padding: 8px; overflow: hidden; }
        preview-status { flex-direction: row; align-items: center; gap: 6px; padding: 4px 8px; }
        preview-status.errors { color: var(--danger, #f2696e); }

        /* ── The addressable group editor ───────────────────────────────────── */
        group-editor { flex-direction: column; }
        group-editor > tree-view { max-height: 240px; flex-shrink: 0; }
        analysis-list { flex-direction: column; gap: 2px; }

        analysis-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            min-height: 22px;
            padding: 0px 4px;
        }

        analysis-row.error { color: var(--danger, #f2696e); }
        analysis-row.warning { color: var(--warning, #e2b341); }
        analysis-stage { width: 72px; flex-shrink: 0; color: var(--text-muted); }
        analysis-message { flex-grow: 1; min-width: 0; }

        /* ── The compositor editor ──────────────────────────────────────────── */
        compositor-editor { flex-direction: row; gap: 6px; }
        compositor-editor > node-canvas { flex-grow: 1; min-width: 0; }
        compositor-side { flex-direction: column; width: 280px; flex-shrink: 0; gap: 6px; overflow: hidden; }

        /* ── The prefab editor ──────────────────────────────────────────────── */
        prefab-editor { flex-direction: column; }
        prefab-banner { flex-direction: row; align-items: center; gap: 8px; padding: 4px 8px; }
        """;
}
