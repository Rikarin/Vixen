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
        /* ⚠ `min-width: 0px` so a tab strip too wide for the window cannot widen the whole docking
           area — see `dock-tabs-viewport` for the other half of it. `dock-group` has always had the
           declaration and nothing above it did, so the group dutifully clipped a box that everything
           between it and the root had already agreed to make big enough. */
        docking-host { flex-direction: column; position: relative; flex-grow: 1; min-width: 0px; }

        /* Where panels wait. `display: none` rather than removal, because an element outside a
           document is a removed element and removal is final. */
        dock-detached { display: none; }

        dock-surface { flex-direction: column; flex-grow: 1; min-width: 0px; overflow: hidden; }

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
            align-items: stretch;
            flex-shrink: 0;
            background-color: var(--surface-sunken);
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
        }

        /*
         * The clipping box the tabs slide inside, and the list that slides.
         *
         * ⚠ `flex-basis: 0px` is what makes an overflow possible at all, and it is the same
         * declaration and the same reason `tree-view virtualizing-panel` gives: without it the
         * viewport takes its base size from its *content*, so it is always exactly as wide as the
         * tabs inside it and never overflows. Twelve tabs then produced a strip two thousand pixels
         * wide, which propagated up through the group, the split, the surface and the host — a
         * docking area wider than the window, with the arrows this exists to show never appearing
         * because nothing had overflowed anything. `min-width: 0px` here and on the two ancestors
         * below is the other half: a flex item's automatic minimum is its content, so the demand
         * survives the base size being zeroed.
         */
        dock-tabs-viewport {
            flex-direction: row;
            flex-grow: 1;
            flex-basis: 0px;
            min-width: 0px;
            overflow: hidden;
        }

        dock-tabs {
            flex-direction: row;
            flex-shrink: 0;
            align-items: stretch;
        }

        /* The arrows keep their width whatever the tabs do, or they are the first thing squeezed out. */
        dock-tabstrip > icon-button { flex-shrink: 0; align-self: center; }

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

        /*
         * The drop guides: five handles over the middle of whatever group the drag is over, and the
         * one the drop would use lit.
         *
         * ⚠ `pointer-events: none` on both, and the *sizes are the code's*. Which handle a drop
         * lands on is arithmetic — the pointer is captured by the tab being dragged for the whole
         * gesture, so none of this is ever hit-tested — and a sheet that resized a handle would move
         * the one that is drawn away from the one that answers. `DockingHost.GuideSize` and
         * `GuideSpan` are the numbers below, and the offsets are written inline as the cluster is
         * built.
         */
        dock-guides { position: absolute; left: 0px; top: 0px; pointer-events: none; }

        dock-guide {
            position: absolute;
            left: 0px;
            top: 0px;
            width: 28px;
            height: 28px;
            padding: 3px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 4px;
            background-color: var(--surface-raised);
            pointer-events: none;
        }

        dock-guide.left, dock-guide.right, dock-guide.center { flex-direction: row; }
        dock-guide.top, dock-guide.bottom { flex-direction: column; }
        dock-guide.right, dock-guide.bottom { justify-content: flex-end; }

        dock-guide.active { border-color: var(--accent); background-color: var(--accent); }

        /* What the handle says it would do, drawn rather than written: the half of the pane the
           panel would take, which is the preview rectangle in miniature.

           ⚠ 20px, not 22px, and the arithmetic is worth writing down: a `width` is the *border*
           box, so the room inside a handle is 28 less its 1px border and its 3px padding on each
           side. A hint sized as though the width were the content area overhangs the border along
           the bottom and the right — which does not clip, and reads as a cluster with every
           handle's marking nudged up and to the left. */
        dock-hint { width: 20px; height: 20px; border-radius: 2px; background-color: var(--text-muted); }

        dock-guide.left > dock-hint, dock-guide.right > dock-hint { width: 10px; }
        dock-guide.top > dock-hint, dock-guide.bottom > dock-hint { height: 10px; }
        dock-guide.active > dock-hint { background-color: var(--surface); }

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

            /* What `TreeRow` draws its indent guides in. A token rather than a constant so a
               theme can turn the lines up, down or off without the control knowing. */
            --tree-guide-color: rgba(128, 128, 140, 0.30);
        }

        /* ⚠ `flex-basis: 0px` as well as the grow, and it is load-bearing. Without it the viewport
           takes its base size from its content — a hundred thousand rows of it — so it never
           overflows, the scroll range is zero, and the virtualiser realises every row there is.
           That failure is silent: the tree looks right and the process runs out of memory. */
        tree-view virtualizing-panel { flex-grow: 1; flex-basis: 0px; min-height: 0px; }
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

        /* ⚠ The chevron keeps its box on a leaf and loses only its glyph — see `TreeRow.Chevron`,
           which says why. The rule that used to be here was `tree-row.leaf icon { display: none }`,
           which took the chevron out of the flow (so leaves sat a chevron's width to the left of
           their siblings) *and* matched every other icon in the row, including the ones a consumer
           had put in its own columns. Both were the same selector being too broad. */
        .tree-chevron { flex-shrink: 0; }

        /* The row's own glyph. Larger than the chevron because it is a picture rather than a
           direction, and it keeps its column when a node has none so that the text of a tree with
           mixed rows has one left edge. */
        .tree-glyph { width: 13px; height: 13px; flex-shrink: 0; color: var(--text-muted); }

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

        /* ── Node canvas ────────────────────────────────────────────────────── */
        node-canvas {
            position: relative;
            flex-direction: column;
            flex-grow: 1;
            flex-shrink: 1;
            min-width: 0px;
            min-height: 0px;
            overflow: hidden;
            background-color: var(--surface-sunken);

            --grid-color: #00000012;
            --wire-color: #8a919c;
            --wire-active-color: var(--accent);
            --marquee-color: #3b6cf033;

            /* ⚠ Graph units, not screen pixels. The canvas multiplies each of them by the zoom
               before it writes anything, and it computes a wire's endpoint from the first two —
               so these three numbers are a contract between the arithmetic and the picture. */
            --node-header: 22px;
            --port-pitch: 18px;
            --node-padding: 6px;
            --node-font-size: 12px;
        }

        node-surface { position: relative; flex-grow: 1; }

        /* Both layers fill the surface and neither takes a pointer: the groups are a backdrop and
           the wires are a drawing. What is clickable is the group's header and the nodes. */
        node-groups, node-wires {
            position: absolute;
            left: 0px;
            top: 0px;
            right: 0px;
            bottom: 0px;
            pointer-events: none;
        }

        node-group {
            position: absolute;
            flex-direction: column;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: #8a919c1f;
            pointer-events: none;
        }

        node-group.parked { display: none; }

        /* The one part of a group that answers the pointer. A group's body is a third of the canvas
           and a press inside it means "marquee", not "drag this group". */
        node-group-header {
            flex-shrink: 0;
            padding: 0px 0.5em;
            color: var(--text-muted);
            pointer-events: auto;
        }

        node-item {
            position: absolute;
            flex-direction: column;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 0.5em;
            background-color: var(--surface-raised);
            color: var(--text);
            overflow: hidden;
        }

        node-item:checked { border-color: var(--accent); }
        node-item.parked { display: none; }

        /* Everything inside a node is in `em`, which is what carries the zoom: the canvas writes one
           `font-size` per node and the padding, the gaps and the dots follow it. */
        node-header {
            flex-shrink: 0;
            flex-direction: row;
            align-items: center;
            padding: 0px 0.6em;
            background-color: var(--surface-sunken);
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
        }

        node-item:checked node-header { background-color: var(--accent); color: var(--accent-text); }

        node-body { flex-direction: row; flex-grow: 1; }
        node-inputs { flex-direction: column; flex-grow: 1; }
        node-outputs { flex-direction: column; flex-grow: 1; align-items: flex-end; }

        node-port { flex-direction: row; align-items: center; gap: 0.35em; padding: 0px 0.35em; }
        node-port.output { flex-direction: row-reverse; }
        node-port.parked { display: none; }

        node-dot {
            width: 0.6em;
            height: 0.6em;
            border-radius: 0.3em;
            background-color: var(--wire-color);
            flex-shrink: 0;
        }

        node-port:hover node-dot { background-color: var(--accent); }
        node-port-label { color: var(--text-muted); }

        node-marquee {
            position: absolute;
            left: 0px;
            top: 0px;
            background-color: var(--marquee-color);
            border-width: 1px;
            border-color: var(--accent);
            pointer-events: none;
        }

        node-minimap {
            position: absolute;
            right: 8px;
            bottom: 8px;
            width: 160px;
            height: 110px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 4px;
            background-color: var(--surface);
            color: var(--text-muted);
        }

        /* ── Code editor ────────────────────────────────────────────────────── */

        /* ⚠ The monospace family is not decoration. A column is turned into an x by multiplying, so
           a proportional face puts the caret, the selection and every click in the wrong place. */
        code-editor {
            position: relative;
            flex-direction: row;
            flex-grow: 1;
            flex-shrink: 1;
            min-width: 0px;
            min-height: 0px;
            overflow: hidden;
            font-family: monospace;
            background-color: var(--surface);
            color: var(--text);

            --current-line-color: #3b6cf00d;
            --gutter-color: var(--text-muted);
        }

        code-gutter {
            position: relative;
            flex-direction: column;
            flex-shrink: 0;
            min-width: 0px;
            width: 52px;
            overflow: hidden;
            background-color: var(--surface-sunken);
            border-width: 0px 1px 0px 0px;
            border-color: var(--border);
            color: var(--gutter-color);
        }

        code-gutter-row {
            position: absolute;
            left: 0px;
            right: 0px;
            flex-direction: row;
            align-items: center;
            gap: 2px;
            padding: 0px 4px;
        }

        code-gutter-row.parked { display: none; }
        code-gutter-row icon { width: 9px; height: 9px; flex-shrink: 0; }
        code-gutter-row.unfoldable icon { visibility: hidden; }
        code-gutter-number { flex-grow: 1; justify-content: flex-end; }

        code-gutter-row.has-warning { color: #b7791f; }
        code-gutter-row.has-error { color: var(--danger); }

        /* ⚠ `flex-basis: 0px` as well as the grow — see the tree's remarks. Without it the viewport
           takes its base size from a file's worth of content and never overflows.

           ⚠ And `min-width: 0px`, which is the same trap on the other axis. A flex item's automatic
           minimum is its content, so a viewport containing a two-thousand-character line is stretched
           to two thousand characters wide: it never overflows, the horizontal scroll range is zero,
           and every column virtualiser downstream realises everything there is. */
        code-editor scroll-view { flex-grow: 1; flex-shrink: 1; flex-basis: 0px; min-width: 0px; min-height: 0px; }

        /* The three layers, in painting order: selection, text, caret. */
        code-selection, code-caret, code-lines {
            position: absolute;
            left: 0px;
            top: 0px;
            right: 0px;
            bottom: 0px;
            pointer-events: none;
        }

        code-line {
            position: absolute;
            left: 0px;
            flex-direction: row;
            align-items: center;
        }

        code-line.parked { display: none; }
        code-line.has-error { background-color: #d6454514; }

        code-token { flex-shrink: 0; }
        code-token.parked { display: none; }

        .tok-keyword { color: #8250df; }
        .tok-type { color: #0550ae; }
        .tok-number { color: #0a7c5a; }
        .tok-string { color: #a3364b; }
        .tok-comment { color: var(--text-muted); }
        .tok-operator { color: var(--text-muted); }
        .tok-directive { color: #9a6700; }

        root.dark .tok-keyword { color: #c39bff; }
        root.dark .tok-type { color: #79b8ff; }
        root.dark .tok-number { color: #5fd6a4; }
        root.dark .tok-string { color: #ff9492; }
        root.dark .tok-directive { color: #e3b341; }

        /* Off the layout and out of the picture, and still styled and shaped — which is the whole
           point of it: it is a measurement of the font the cascade chose, not a number. */
        code-metrics { position: absolute; left: 0px; top: 0px; visibility: hidden; }

        code-completion {
            position: absolute;
            left: 0px;
            top: 0px;
            flex-direction: column;
            min-width: 180px;
            padding: 2px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 5px;
            background-color: var(--surface-raised);
        }

        code-completion.hidden { display: none; }

        code-completion-item { padding: 1px 6px; border-radius: 3px; color: var(--text); }
        code-completion-item.selected { background-color: var(--accent); color: var(--accent-text); }
        code-completion-item.parked { display: none; }

        /* ── Data grid ──────────────────────────────────────────────────────── */
        /* ⚠ `flex-shrink: 1` and `min-width: 0px`, on the control itself, and both are load-bearing.
           A flex item's base size is its content, and this one's content is as wide as the whole
           table — so without the shrink a grid of two hundred columns is twenty-four thousand pixels
           wide, grows straight out of the window it was put in, and never overflows anything. Its
           own viewport is then as wide as its content, the scroll range is zero, and the column
           virtualiser silently realises every column there is.

           ⚠ The shrink has to be said out loud because this layout engine takes Yoga's default of
           zero rather than CSS's of one — the one place the two disagree that a control cannot just
           inherit its way out of. Every control below that holds a scroller says it. */
        data-grid {
            flex-direction: column;
            flex-grow: 1;
            flex-shrink: 1;
            min-width: 0px;
            min-height: 0px;
            overflow: hidden;
            background-color: var(--surface);
            color: var(--text);

            --row-height: 24px;
        }

        /* Outside the scroller, which is why a header cell subtracts the scroll offset where a
           row's cell adds it. */
        data-header {
            position: relative;
            flex-shrink: 0;
            min-width: 0px;
            height: 26px;
            overflow: hidden;
            background-color: var(--surface-sunken);
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
        }

        data-header-cell {
            position: absolute;
            top: 0px;
            bottom: 0px;
            flex-direction: row;
            align-items: center;
            gap: 4px;
            padding: 0px 8px;
            color: var(--text-muted);
        }

        data-header-cell:hover { color: var(--text); }
        data-header-cell.parked { display: none; }

        /* Over the scrolling columns, which is the whole of freezing: one element, one offset. */
        data-header-cell.frozen {
            background-color: var(--surface-sunken);
            border-width: 0px 1px 0px 0px;
            border-color: var(--border);
        }

        data-header-cell icon { width: 9px; height: 9px; flex-shrink: 0; }
        data-header-cell.unsorted icon { visibility: hidden; }

        data-resizer { position: absolute; top: 0px; right: 0px; bottom: 0px; width: 6px; }
        data-resizer:hover { background-color: var(--accent); }

        /* The two zeroes are load-bearing — see the code editor's remarks. Without `min-width: 0px`
           a table of two hundred columns makes its own viewport twenty thousand pixels wide, which
           turns the column virtualiser off without saying so. */
        data-grid scroll-view { flex-grow: 1; flex-shrink: 1; flex-basis: 0px; min-width: 0px; min-height: 0px; }

        data-row {
            position: absolute;
            left: 0px;
            flex-direction: row;
            align-items: center;
        }

        data-row:hover { background-color: var(--surface-sunken); }
        data-row:checked { background-color: var(--accent); color: var(--accent-text); }
        data-row.parked { display: none; }

        data-row.data-group { background-color: var(--surface-sunken); color: var(--text-muted); }
        data-group-label { position: absolute; top: 0px; bottom: 0px; padding: 0px 8px; align-items: center; }
        data-group-label.hidden { display: none; }

        data-cell {
            position: absolute;
            top: 0px;
            bottom: 0px;
            flex-direction: row;
            align-items: center;
            padding: 0px 8px;
            overflow: hidden;
        }

        data-cell.parked { display: none; }
        data-cell.frozen { background-color: var(--surface); }
        data-row:checked data-cell.frozen { background-color: var(--accent); }

        data-text { flex-grow: 1; }
        data-text.hidden { display: none; }

        .data-editor { flex-grow: 1; padding: 0px 2px; border-radius: 3px; }

        /* ── Viewport ───────────────────────────────────────────────────────── */
        viewport {
            position: relative;
            flex-grow: 1;
            flex-shrink: 1;
            min-width: 0px;
            min-height: 0px;
            overflow: hidden;

            --viewport-color: #1b1d21;
            --axis-x-color: #de4a54;
            --axis-y-color: #6bbf4f;
            --axis-z-color: #4a82e6;
        }

        /* Over the scene, and transparent to the pointer: what is clickable is what is put in it. */
        viewport-overlay {
            position: absolute;
            left: 0px;
            top: 0px;
            right: 0px;
            bottom: 0px;
            flex-direction: column;
            pointer-events: none;
        }

        viewport-gizmo {
            position: absolute;
            right: 8px;
            top: 8px;
            width: 48px;
            height: 48px;
            pointer-events: none;
        }

        viewport-gizmo.hidden { display: none; }

        /* ── Colour picker ──────────────────────────────────────────────────── */
        color-picker {
            flex-direction: column;
            gap: 8px;
            padding: 8px;
            width: 240px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: var(--surface-raised);
            color: var(--text);
        }

        color-field { height: 150px; flex-shrink: 0; border-radius: 4px; }
        color-strip { height: 12px; flex-shrink: 0; border-radius: 6px; }
        color-strip.hidden { display: none; }

        color-row { flex-direction: row; align-items: center; gap: 6px; }
        color-row.hidden { display: none; }
        color-row text { color: var(--text-muted); }

        .color-hex { flex-grow: 1; }

        color-swatch {
            width: 20px;
            height: 20px;
            flex-shrink: 0;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 4px;
        }

        color-swatch.preview { width: 28px; height: 28px; }
        color-swatch.parked { display: none; }

        color-palette { flex-direction: row; gap: 4px; }

        /* ── Curve editor ───────────────────────────────────────────────────── */
        curve-editor {
            flex-grow: 1;
            flex-shrink: 1;
            min-width: 0px;
            min-height: 0px;
            overflow: hidden;
            background-color: var(--surface-sunken);
            color: var(--accent);

            --grid-color: #00000014;
            --curve-color: var(--accent);
            --key-color: var(--accent);
            --handle-color: #8a919c;
        }

        /* ── Gradient editor ────────────────────────────────────────────────── */
        /* ⚠ A declared width, and it is load-bearing rather than cosmetic. The picker below the bar
           appears and disappears with the selection, and an auto-width editor would change width
           with it — so the bar would move under the pointer between the two clicks of a double
           click, and deleting a marker would land somewhere else and add one. */
        gradient-editor {
            flex-direction: column;
            width: 260px;
            flex-grow: 0;
            flex-shrink: 0;
            align-self: flex-start;
            gap: 6px;
            padding: 8px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: var(--surface-raised);
            color: var(--accent);
        }

        gradient-rail { height: 16px; flex-shrink: 0; }
        gradient-bar { height: 28px; flex-shrink: 0; border-radius: 3px; }

        gradient-editor color-picker {
            width: auto;
            padding: 0px;
            border-width: 0px;
            background-color: transparent;
        }

        gradient-editor color-picker.hidden { display: none; }
        gradient-editor slider.hidden { display: none; }

        /* ── Timeline ───────────────────────────────────────────────────────── */
        timeline {
            flex-direction: column;
            flex-grow: 1;
            flex-shrink: 1;
            min-width: 0px;
            min-height: 0px;
            overflow: hidden;
            background-color: var(--surface);
            color: var(--text);

            --track-height: 24px;
            --grid-color: #0000001a;
            --stripe-color: #00000008;
            --key-color: #8a919c;
            --key-active-color: var(--accent);
            --playhead-color: #de4a54;
            --marquee-color: #3b6cf033;
            --curve-color: #8a919c99;
        }

        timeline-ruler {
            position: relative;
            flex-shrink: 0;
            height: 22px;
            overflow: hidden;
            background-color: var(--surface-sunken);
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
            color: var(--text-muted);
        }

        timeline-tick { position: absolute; top: 1px; font-size: 0.8em; }
        timeline-tick.parked { display: none; }

        timeline-body { flex-direction: row; flex-grow: 1; min-height: 0px; overflow: hidden; }

        timeline-headers {
            position: relative;
            flex-shrink: 0;
            width: 140px;
            overflow: hidden;
            background-color: var(--surface-sunken);
            border-width: 0px 1px 0px 0px;
            border-color: var(--border);
        }

        timeline-header {
            position: absolute;
            left: 0px;
            right: 0px;
            flex-direction: row;
            align-items: center;
            gap: 4px;
            padding: 0px 6px;
        }

        timeline-header.parked { display: none; }
        timeline-name { flex-grow: 1; }

        timeline-lanes { flex-grow: 1; flex-shrink: 1; min-width: 0px; }
        """;
}
