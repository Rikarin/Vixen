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
        import-settings, texture-editor, sprite-editor, model-editor, material-editor,
        code-editor-pane, group-editor, compositor-editor, shadergraph-editor, prefab-editor {
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
        texture-facts, model-facts, material-facts, compiled-scene-facts { flex-direction: column; }

        /* ── What a build makes of a scene ───────────────────────────────────
           A block is an archetype, and the archetype is the long part of the row — so it takes the
           space and the two numbers after it do not shrink. Monospaced because the rows are read
           down a column rather than across: somebody looking for the block with one entity in it is
           comparing digits in the same place. */
        /* ⚠ A tab set in a *document* fills it, where a tab set in a form is as tall as its content.
           `tabs` carries no `flex-grow` in ControlTheme and is right not to, so the ones that stand in
           for a whole panel say so — without this the tree inside collapses to nothing and a click in
           it lands on whatever is behind the panel. */
        tabs.document-tabs { flex-grow: 1; min-height: 0; }
        tabs.document-tabs > tab-panels { flex-grow: 1; min-height: 0; }
        tabs.document-tabs > tab-panels > tab-panel { flex-grow: 1; min-height: 0; padding: 6px 0px; }

        compiled-scene { flex-direction: column; flex-grow: 1; gap: 8px; padding: 6px; overflow: hidden; }
        compiled-scene-bar { flex-direction: row; align-items: center; gap: 8px; flex-shrink: 0; }
        compiled-scene-label { color: var(--text-muted); }

        compiled-scene-blocks, compiled-scene-diagnostics {
            flex-direction: column;
            gap: 2px;
            overflow-y: auto;
            min-height: 0;
        }

        compiled-scene-block {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            min-height: 20px;
            font-family: monospace;
        }

        compiled-scene-archetype { flex-grow: 1; min-width: 0; }
        compiled-scene-count, compiled-scene-bytes { flex-shrink: 0; color: var(--text-muted); }

        /* A diagnostic's severity is what is being scanned for, so it is the coloured part and the
           message is not — a whole row in red is a row nobody reads the end of. */
        compiled-scene-diagnostic { flex-direction: row; gap: 8px; min-height: 20px; }
        compiled-scene-severity { width: 88px; flex-shrink: 0; }
        compiled-scene-message { flex-grow: 1; min-width: 0; }

        compiled-scene-diagnostic.error > compiled-scene-severity { color: var(--danger, #f2696e); }
        compiled-scene-diagnostic.warning > compiled-scene-severity { color: var(--warning, #e2b341); }
        compiled-scene-diagnostic.information > compiled-scene-severity { color: var(--text-muted); }

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

        /* ── The texture editor ───────────────────────────────────────────────
           Reached by class rather than by `texture-editor > image`, because the preview is inside a
           tab panel now and the sprite editor's canvas holds an image of its own. A child combinator
           that stopped matching would leave the preview at its intrinsic size, which is nothing. */
        image.texture-preview {
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

        /* ── The sprite editor ───────────────────────────────────────────────
           A toolbar, then the picture and a side panel beside it. The canvas is sized inline in
           texels times the zoom — no rule can say "as big as that texture is" — and everything on
           the overlay is positioned against it, which is why it is the containing block. */
        sprite-toolbar {
            flex-direction: row;
            align-items: center;
            flex-wrap: wrap;
            gap: 6px;
            flex-shrink: 0;
        }

        sprite-toolbar > select { width: 160px; flex-shrink: 0; }
        sprite-toolbar > numeric-input { width: 64px; flex-shrink: 0; }

        sprite-body { flex-direction: row; gap: 8px; flex-grow: 1; min-height: 0; overflow: hidden; }

        /* Scrolls rather than shrinks: a sheet at 1:1 is larger than any panel, and a canvas that
           squeezed would put every rect on the overlay somewhere other than where the texel is. */
        sprite-canvas {
            position: relative;
            flex-shrink: 0;
            background-color: var(--surface-sunken);
            overflow: visible;
        }

        sprite-canvas > image { position: absolute; left: 0; top: 0; }

        sprite-rect {
            position: absolute;
            border-width: 1px;
            border-color: var(--border-strong, var(--border));
            flex-direction: column;
            justify-content: flex-end;
            overflow: hidden;
        }

        sprite-rect.selected {
            border-width: 2px;
            border-color: var(--accent);
            background-color: var(--accent-wash, transparent);
        }

        sprite-rect-label {
            font-size: 10px;
            color: var(--text-muted);
            padding: 0px 2px;
            display: none;
        }

        sprite-rect.selected > sprite-rect-label { display: flex; color: var(--accent); }

        /* The four nine-slice guides. Positioned as a percentage of the rect, so they follow the
           zoom without the overlay being rebuilt. */
        sprite-guide { position: absolute; background-color: var(--accent); opacity: 0.6; }
        sprite-guide.vertical { top: 0; bottom: 0; width: 1px; }
        sprite-guide.horizontal { left: 0; right: 0; height: 1px; }

        sprite-side { flex-direction: column; gap: 6px; width: 260px; flex-shrink: 0; min-height: 0; }
        sprite-list-bar { flex-direction: row; gap: 4px; flex-shrink: 0; }
        sprite-list { flex-direction: column; flex-grow: 1; min-height: 0; overflow-y: auto; }

        sprite-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            min-height: 22px;
            padding: 0px 4px;
            border-radius: 4px;
        }

        sprite-row.selected { background-color: var(--accent-deep, var(--surface-raised)); }
        sprite-row-name { flex-grow: 1; min-width: 0; }
        sprite-row-size { flex-shrink: 0; color: var(--text-muted); }

        sprite-fields { flex-direction: column; gap: 4px; flex-shrink: 0; }

        sprite-field-row { flex-direction: row; align-items: center; gap: 4px; min-height: 24px; }
        sprite-field-name { width: 52px; flex-shrink: 0; color: var(--text-muted); }
        sprite-field-label { color: var(--text-muted); font-size: 11px; }
        sprite-field-row > numeric-input { width: 52px; flex-shrink: 0; }
        sprite-field-row > textbox { flex-grow: 1; min-width: 0; }

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

        /* ── The shared node inspector ──────────────────────────────────────
           Beside every graph rather than inside one, because a node's numbers live on the graph and
           the port list that describes them is the registry's — so the same rows serve the VFX
           blocks and the animation graph's states. */
        node-inspector { flex-direction: column; gap: 2px; flex-shrink: 0; }
        node-inspector-title { font-weight: 600; }
        node-inspector-summary { color: var(--text-muted); font-size: 11px; }
        lane-name { color: var(--text-muted); font-size: 11px; width: 10px; flex-shrink: 0; }
        node-inspector fact-value { flex-direction: row; align-items: center; gap: 4px; }
        node-inspector fact-value > numeric-input { flex-grow: 1; flex-basis: 0px; min-width: 0; }

        /* ── The shader graph editor ────────────────────────────────────────
           Three columns, and the middle one is usually not there: a graph is what is being looked
           at, the generated Raven is what is being checked, and the fields are a fixed strip. The
           pane keeps a width rather than growing, so showing it narrows the canvas by a predictable
           amount instead of halving it. */
        shadergraph-editor { flex-direction: row; gap: 6px; }
        shadergraph-editor > node-graph { flex-grow: 1; min-width: 0; }

        shadergraph-source {
            flex-direction: column;
            width: 420px;
            flex-shrink: 0;
            overflow: hidden;
            background-color: var(--surface-sunken);
            border-radius: var(--radius-panel, 5px);
        }

        shadergraph-source > code-editor { flex-grow: 1; min-height: 0; }

        shadergraph-side { flex-direction: column; width: 300px; flex-shrink: 0; gap: 6px; overflow-y: auto; }
        shadergraph-transport { flex-direction: row; align-items: center; gap: 4px; flex-shrink: 0; }
        shadergraph-property { flex-direction: column; gap: 2px; flex-shrink: 0; }

        /* ── The VFX editor ─────────────────────────────────────────────────
           The canvas takes the width and the effect takes a fixed column, which is the way round a
           node graph wants: a block chain is wide and a preview of particles reads fine small. */
        vfx-editor { flex-direction: row; gap: 6px; }
        vfx-editor > node-graph { flex-grow: 1; min-width: 0; }
        vfx-side { flex-direction: column; width: 300px; flex-shrink: 0; gap: 6px; overflow-y: auto; }
        vfx-transport { flex-direction: row; align-items: center; gap: 4px; flex-shrink: 0; }

        vfx-preview {
            height: 240px;
            flex-shrink: 0;
            background-color: var(--surface-sunken);
            border-radius: var(--radius-panel, 5px);
        }

        vfx-readout { color: var(--text-muted); font-size: 11px; flex-shrink: 0; }

        /* ── E5's authoring surfaces ────────────────────────────────────────
           Six editors and one shape: a bar across the top, the thing being authored taking the
           width, and a fixed column of fields beside it. Written once here rather than six times,
           because six panels that drift apart is what a user reads as six different products. */
        animation-editor, animgraph-editor, input-editor, mixer-editor, font-editor, sequence-editor {
            flex-direction: column;
            flex-grow: 1;
            gap: 6px;
            padding: 6px;
            overflow: hidden;
        }

        /* ⚠ `flex-wrap`, for the same reason `sprite-toolbar` above has it: a row that does not wrap
           does not shrink either, it *overflows*, and the editor clips it — so on a narrowly docked
           panel the buttons past the right edge are drawn nowhere and cannot be pressed. The
           sequence bar is six controls wide and its panel can be three hundred pixels, which put
           "Key" and "Remove Track" outside the clip: reachable in a wide dock, gone in a narrow one,
           and nothing said so. `MilestoneE5Tests` is what caught it, by clicking through the pointer
           rather than raising the button's event — a covered control fails the first and is
           invisible to the second. */
        animation-bar, animgraph-bar, input-bar, mixer-bar, font-bar, sequence-bar {
            flex-direction: row;
            align-items: center;
            flex-wrap: wrap;
            gap: 6px;
            flex-shrink: 0;
        }

        /* ⚠ **Shrinkable, with a floor.** Three hundred pixels that refuse to shrink take the whole
           of a docked panel — an asset editor opens in a column about that wide — and the pane beside
           this one is left with nothing: the sequence timeline laid out zero pixels across, so its
           track headers were present, addressable and impossible to click, and the only gesture that
           reaches a track with no keys is a click on its header. A floor rather than plain
           `flex-shrink`, because a fields column narrower than this is not worth having either. */
        animation-side, animgraph-side, input-side, mixer-side, font-side, sequence-side {
            flex-direction: column;
            width: 300px;
            min-width: 120px;
            flex-shrink: 1;
            gap: 6px;
            overflow-y: auto;
        }

        animation-proposals { flex-direction: column; gap: 2px; margin-top: 8px; }

        animation-fields, animgraph-fields, animgraph-parameters, input-fields, mixer-fields,
        mixer-snapshots, font-facts, font-chain, font-blocks, sequence-fields {
            flex-direction: column;
            gap: 2px;
        }

        animation-title, animgraph-title, input-title, mixer-title, font-title, sequence-title {
            font-weight: 600;
            margin-top: 4px;
        }

        animation-error { color: var(--danger, #f2696e); }

        /* ── The variation matrix ─────────────────────────────────────────────
           A grid rather than a list sorted by severity, because the question it answers is "which
           *combination* fails" — and a sorted list hides that four of the five failures are all the
           same body. */
        harness-editor { flex-direction: column; flex-grow: 1; gap: 6px; padding: 6px; overflow: hidden; }
        harness-bar { flex-direction: row; align-items: center; flex-wrap: wrap; gap: 6px; flex-shrink: 0; }
        harness-body { flex-direction: row; flex-grow: 1; min-height: 0px; gap: 6px; }
        harness-matrix { flex-direction: column; flex-grow: 1; min-width: 0px; overflow: auto; }
        harness-side { flex-direction: column; width: 300px; min-width: 120px; flex-shrink: 1; overflow-y: auto; }
        harness-fields { flex-direction: column; gap: 2px; }
        harness-title { font-weight: 600; margin-top: 4px; }
        harness-error { color: var(--danger, #f2696e); margin-top: 6px; }
        harness-verdict { font-weight: 600; }
        harness-verdict.failed { color: var(--danger, #f2696e); }

        harness-row { flex-direction: row; align-items: stretch; gap: 1px; }
        harness-row.header { color: var(--text-muted); }
        harness-label { width: 180px; min-width: 100px; flex-shrink: 0; padding: 2px 4px; color: var(--text-muted); }
        harness-cell { width: 72px; flex-shrink: 0; padding: 2px 4px; background-color: var(--surface-raised); }
        harness-cell.failed { background-color: var(--danger, #f2696e); color: #ffffff; }

        /* ⚠ A different colour and not a redder one. "Never resolved" is a different kind of fact
           from "off by four centimetres", and grading it on the same scale is how an author ends up
           tuning a goal that is not running at all. */
        harness-cell.missing { background-color: var(--warning, #e2b341); color: #1a1a1a; }
        harness-cell.selected { border-width: 1px; border-color: var(--accent); }

        /* ── The move set table ───────────────────────────────────────────────
           A table, because a move set is a table. The filter box is in the bar and its answer is the
           Score column, so the query and its result are never on two screens. */
        moveset-editor { flex-direction: column; flex-grow: 1; gap: 6px; padding: 6px; overflow: hidden; }
        moveset-bar { flex-direction: row; align-items: center; flex-wrap: wrap; gap: 6px; flex-shrink: 0; }
        moveset-bar > text-box { flex-grow: 1; min-width: 160px; }
        moveset-body { flex-direction: row; flex-grow: 1; min-height: 0px; gap: 6px; }
        moveset-table { flex-direction: column; flex-grow: 1; min-width: 0px; overflow: auto; }
        moveset-side { flex-direction: column; width: 300px; min-width: 120px; flex-shrink: 1; overflow-y: auto; }
        moveset-fields { flex-direction: column; gap: 2px; }
        moveset-title { font-weight: 600; margin-top: 4px; }
        moveset-error { color: var(--danger, #f2696e); margin-top: 6px; }

        moveset-row { flex-direction: row; align-items: center; gap: 6px; padding: 1px 4px; min-height: 20px; }
        moveset-row.header { color: var(--text-muted); }
        moveset-row.selected { background-color: var(--accent-deep, var(--surface-raised)); border-radius: 3px; }
        moveset-row.chosen { color: var(--accent); font-weight: 600; }
        moveset-row.ineligible { color: var(--text-muted); }

        /* ⚠ Struck through and dimmed, not hidden. A base row this set replaces is still in
           somebody's file, and hiding it is how an author edits a row that has no effect. */
        moveset-row.inherited { color: var(--text-muted); }
        moveset-row.overridden { text-decoration: line-through; }
        moveset-row.missing { color: var(--danger, #f2696e); }

        moveset-name { width: 200px; min-width: 90px; flex-shrink: 0; }
        moveset-facets { flex-grow: 1; min-width: 0px; }
        moveset-number { width: 72px; flex-shrink: 0; }

        /* ── Proxy shapes ─────────────────────────────────────────────────────
           The list is the body; the column beside it is one shape and then the check's notes. The
           notes are below the fields rather than in a second panel, because reading a note and
           fixing the shape it is about is one action. */
        shape-editor { flex-direction: column; flex-grow: 1; gap: 6px; padding: 6px; overflow: hidden; }
        shape-bar { flex-direction: row; align-items: center; flex-wrap: wrap; gap: 6px; flex-shrink: 0; }
        shape-body { flex-direction: row; flex-grow: 1; min-height: 0px; gap: 6px; }
        /* ⚠ The viewport takes the height and the list takes what is left, rather than the two
           sharing it. A pane you cannot see the body in is not worth having, and a list of thirty
           shapes is scrolled anyway — so the list gets a floor and the stage gets the rest. */
        shape-stage { flex-direction: column; flex-grow: 1; min-width: 0px; gap: 6px; }
        shape-stage > viewport { flex-grow: 1; min-height: 120px; }
        shape-list { flex-direction: column; height: 180px; min-height: 60px; flex-shrink: 0; overflow: auto; }
        shape-side { flex-direction: column; width: 300px; min-width: 120px; flex-shrink: 1; gap: 6px; overflow-y: auto; }
        shape-fields, shape-report { flex-direction: column; gap: 2px; }
        shape-title { font-weight: 600; margin-top: 4px; }
        shape-error { color: var(--danger, #f2696e); margin-top: 6px; }

        /* ⚠ Not red. None of the audit's notes is an error — a shape that never moves is right on a
           prop, two shapes overlapping is right at a shoulder — and colouring them as failures is how
           a list an author should scan once becomes a list they learn to ignore. */
        shape-note { color: var(--text-muted); margin-top: 4px; }

        shape-row { flex-direction: row; align-items: center; gap: 6px; padding: 1px 4px; min-height: 20px; }
        shape-row.header { color: var(--text-muted); }
        shape-row.selected { background-color: var(--accent-deep, var(--surface-raised)); border-radius: 3px; }
        shape-row.missing { color: var(--danger, #f2696e); }

        shape-name { width: 160px; min-width: 80px; flex-shrink: 0; }
        shape-kind { width: 90px; flex-shrink: 0; }
        shape-joint { width: 140px; flex-shrink: 0; }
        shape-tags { flex-grow: 1; min-width: 0px; }
        shape-fields fact-value { flex-direction: row; gap: 4px; }
        shape-fields fact-value > numeric-input { flex-grow: 1; flex-basis: 0px; min-width: 0; }

        fact-row.error { color: var(--danger, #f2696e); }
        fact-row.warning { color: var(--warning, #e2b341); }
        fact-row.selected { background-color: var(--accent-deep, var(--surface-raised)); border-radius: 3px; }

        /* ⚠ **A column holding a bar and a row, never one row that wraps.** The bar has to span the
           panel and the two columns under it have to share what is left, and `flex-wrap` does not
           express that here: a wrapped line gets no height of its own, so the body was laid out over
           the bar and every button in all six editors was unclickable — the timeline's ruler answered
           the hit test at the centre of a button that was plainly visible. A row inside a column is
           what the docked panels already do, and it needs nothing from the layout engine that the
           rest of the editor does not already rely on. */
        animation-body, animgraph-body, input-body, mixer-body, font-body, sequence-body {
            flex-direction: row;
            flex-grow: 1;
            min-height: 0;
            gap: 6px;
        }

        animgraph-body > state-map, input-body > tree-view, mixer-body > mixer-strips,
        font-body > font-atlas, sequence-body > timeline, animation-body > animation-stage {
            flex-grow: 1;
            min-width: 0;
            min-height: 0;
        }

        /* ── The clip editor ────────────────────────────────────────────────
           The sheet and the curve editor share one slot: two modes over one document, never two
           panes, because a docked panel split in half gives each of them a height neither can use. */
        animation-stage { flex-direction: column; overflow: hidden; }
        animation-stage > timeline, animation-stage > curve-editor { flex-grow: 1; min-height: 0; }

        /* ── The animation graph ────────────────────────────────────────────
           The arrows are drawn by the map and the state boxes are elements on top of it, because
           text in this framework is an element — see StateMapView's own remarks. */
        state-map {
            position: relative;
            flex-grow: 1;
            min-width: 0;
            min-height: 0;
            overflow: hidden;
            background-color: var(--surface-sunken);
            border-radius: var(--radius-panel, 5px);
        }

        state-box {
            position: absolute;
            flex-direction: column;
            justify-content: center;
            padding: 4px 8px;
            gap: 2px;
        }

        state-box.parked { display: none; }
        state-box-name { color: var(--text); }
        state-box-motion { color: var(--text-muted); font-size: 11px; }

        /* ── The input editor ───────────────────────────────────────────── */


        /* ── The mixer ──────────────────────────────────────────────────────
           Strips side by side, which is the layout every mixer has had since the hardware ones: a
           fader is a thing you compare against its neighbours. */
        mixer-strips {
            flex-direction: row;
            align-items: stretch;
            gap: 4px;
            overflow-x: auto;
        }

        mixer-strip {
            flex-direction: column;
            align-items: center;
            gap: 4px;
            width: 76px;
            flex-shrink: 0;
            padding: 6px 4px;
            border-radius: var(--radius-panel, 5px);
            background-color: var(--surface-sunken);
        }

        mixer-strip.selected { background-color: var(--accent-deep, var(--surface-raised)); }

        /* ⚠ A width as well as a height, because `slider` is themed for the horizontal case —
           a fader given only a `min-height` was 80 pixels tall and as wide as the strip, which
           is a horizontal control drawn in a vertical hole. The height is what it is *for*:
           travel is what a fader trades everything else for. */
        mixer-strip > slider.vertical {
            flex-grow: 1;
            min-height: 120px;
            width: 22px;
            flex-shrink: 0;
        }
        mixer-strip-name { font-weight: 600; }
        mixer-strip-parent, mixer-strip-gain { color: var(--text-muted); font-size: 11px; }
        mixer-strip-buttons { flex-direction: row; gap: 4px; }
        mixer-snapshot { padding: 2px 4px; border-radius: 3px; }
        mixer-snapshot.selected { background-color: var(--accent-deep, var(--surface-raised)); }

        /* ── The font editor ────────────────────────────────────────────── */
        font-atlas {
            flex-grow: 1;
            min-width: 0;
            min-height: 0;
            background-color: var(--surface-sunken);
            border-radius: var(--radius-panel, 5px);
        }

        font-chain-row { color: var(--text-muted); font-size: 11px; }
        font-chain-row.error { color: var(--danger, #f2696e); }

        /* ── The sequencer ──────────────────────────────────────────────── */

        """;
}
