// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>The stylesheet the editor's own chrome comes with.</summary>
/// <remarks>
///     <para>
///         A third user-agent sheet after <c>ControlTheme</c> and <c>AdvancedTheme</c>, on the same
///         terms and for the same reason: it draws what those two do not — the shell, the toolbar,
///         the status bar, the palette and the notification list — and it is written entirely
///         against their tokens, so recolouring the root recolours all three.
///     </para>
///     <para>
///         ⚠ <b><see cref="Install" /> loads this and neither of the others.</b> An editor needs all
///         three, in order, because this sheet reads <c>--surface</c>, <c>--border</c> and the rest,
///         and a custom property nothing declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>It also <i>overrides</i> the control set's tokens, which the control set is built to
///         allow.</b> An application shipping a button gets the neutral palette
///         <c>ControlTheme</c> declares; a tool window is a different room, and a set of values on
///         the root is exactly the mechanism its own remarks nominate for saying so. Nothing in
///         <c>ControlTheme</c> or <c>AdvancedTheme</c> is edited, so a game that never installs this
///         sheet is untouched — and every screenshot of a bare control still shows what a game gets.
///     </para>
///     <para>
///         <b>What it is trying to look like.</b> Two things, deliberately: the near-black neutral
///         density of a 3D tool, where a panel is a working surface and chrome gets out of the way;
///         and the layered, rounded, softly-lit materiality of a modern audio workstation, where a
///         field is a well you type into and a floating thing is visibly floating. The join is the
///         <i>desk</i> — the workspace is the darkest thing on screen and the panels are cards laid
///         on it, which is what gives depth without a single gradient.
///     </para>
///     <para>
///         ⚠ <b>Depth comes from luminance steps, not from borders.</b> Four surfaces —
///         <c>--workspace</c>, <c>--surface-sunken</c>, <c>--surface</c>, <c>--surface-raised</c> —
///         and a hairline that is barely there. A tool window that separates everything with visible
///         lines is the look this is getting away from, and it is why the borders below are one step
///         from invisible while the fills are not.
///     </para>
/// </remarks>
public static class EditorTheme {
    /// <summary>Loads the sheet into a document.</summary>
    /// <param name="document">The document, which should already have the other two sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The sheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        /* ── Tokens ─────────────────────────────────────────────────────────────
           The control set's nine, restated for a tool window, plus the ones only an
           editor has. Dark is the editor's default and is the one that was designed;
           light is the same relationships with the ramp inverted, so that toggling
           the theme changes the palette and not the layout.

           ⚠ The shadows and radii are tokens too. They are the difference between
           "a panel" and "a panel that is on top of something", and a sheet that
           wrote them inline would make changing the whole editor's depth a
           forty-place edit. */
        root {
            --workspace: #cfd3da;
            --surface: #f7f8fa;
            --surface-sunken: #e9ebf0;
            --surface-raised: #ffffff;
            --surface-hover: #eef0f4;
            --border: #ccd1d9;
            --border-active: #9fbaf3;
            --text: #14161a;
            --text-muted: #646b77;
            --accent: #2f6df0;
            --accent-text: #ffffff;
            --accent-soft: #d8e3fd;
            --accent-deep: #2b60d4;
            --danger: #d93a34;

            --track-color: #d7dae1;
            --fill-color: #2f6df0;
            --thumb-color: #9ba2ae;
            --selection-color: #bcd3fb;
            --caret-color: #2f6df0;

            --chrome: #e6e8ed;
            --chrome-sunken: #d5d8de;
            --chrome-text: #4a515c;
            --warning: #a86a00;

            --radius-panel: 10px;
            --radius-control: 7px;
            --radius-row: 6px;
            --elevation: 0px 12px 32px rgba(16, 20, 28, 0.18);
            --card: 0px 1px 3px rgba(16, 20, 28, 0.10);
            --status-height: 26px;
        }

        root.dark {
            --workspace: #08090b;
            --surface: #191c20;
            --surface-sunken: #0f1114;
            --surface-raised: #23272d;
            --surface-hover: #282c33;
            --border: #2c3138;
            --border-active: #35507f;
            --text: #e9ecf1;
            --text-muted: #868d99;
            --accent: #4d8dff;
            --accent-text: #06090f;
            --accent-soft: #1c2c4d;
            --accent-deep: #2f62c4;
            --danger: #ff5f57;

            --track-color: #23262b;
            --fill-color: #4d8dff;
            --thumb-color: #3d434c;
            --selection-color: #24406e;
            --caret-color: #4d8dff;

            --chrome: #08090b;
            --chrome-sunken: #08090b;
            --chrome-text: #99a1ad;
            --warning: #ffb454;

            --elevation: 0px 14px 36px rgba(0, 0, 0, 0.6);
            --card: 0px 1px 3px rgba(0, 0, 0, 0.45);
        }

        /* ── Shell ──────────────────────────────────────────────────────────────
           ⚠ The whole shell is the desk colour and the panels are what is on it. So
           the menu bar and the toolbar declare no background of their own and no
           dividing line: what separates them from the workspace is that the
           workspace has cards in it and they do not. A toolbar with its own fill and
           its own rule under it is two more edges to look at for no information. */
        editor-shell {
            flex-direction: column;
            flex-grow: 1;
            background-color: var(--workspace);
        }

        editor-shell > menu-bar {
            flex-shrink: 0;
            gap: 1px;
            padding: 5px 6px 2px 6px;
            border-width: 0px;
            background-color: transparent;
        }

        menu-bar-item {
            padding: 4px 10px;
            font-size: 0.95em;
            border-radius: var(--radius-row);
            background-color: transparent;
            color: var(--chrome-text);
        }

        menu-bar-item:hover:not(:disabled), menu-bar-item:checked {
            background-color: var(--surface-raised);
            color: var(--text);
        }

        toolbar {
            flex-direction: row;
            align-items: center;
            flex-shrink: 0;
            gap: 4px;
            padding: 2px 7px 5px 7px;
            border-width: 0px;
            background-color: transparent;
        }

        toolbar separator { height: 16px; margin: 0px 5px; background-color: var(--border); }

        toolbar button, toolbar icon-button, toolbar toggle-button {
            padding: 4px 10px;
            border-radius: var(--radius-control);
            border-color: var(--border);
            background-color: var(--surface);
            color: var(--text-muted);
        }

        /* ⚠ Including the subtle variant, which is what a toolbar is full of. Left
           transparent it is a word floating on the desk with no edge and no affordance
           — a toolbar's buttons have to look pressable before they are hovered. */
        toolbar button.variant-subtle, toolbar icon-button.variant-subtle,
        toolbar toggle-button.variant-subtle {
            border-color: var(--border);
            background-color: var(--surface);
        }

        toolbar button:hover:not(:disabled), toolbar icon-button:hover:not(:disabled),
        toolbar toggle-button:hover:not(:disabled) {
            background-color: var(--surface-hover);
            color: var(--text);
        }

        editor-workspace { flex-direction: column; flex-grow: 1; flex-basis: 0px; }

        /* ⚠ `flex-shrink: 0` and a fixed height, because it is the one strip whose
           size must not follow its contents: a task title three words longer must
           not make the viewport above it jump. */
        status-bar {
            flex-direction: row;
            align-items: center;
            flex-shrink: 0;
            gap: 8px;
            height: var(--status-height);
            padding: 0px 12px;
            border-width: 0px;
            background-color: transparent;
            color: var(--chrome-text);
            font-size: 0.85em;
            letter-spacing: 0.01em;
        }

        status-message { flex-grow: 1; overflow: hidden; color: var(--text); }
        status-bar progress-bar { width: 110px; }
        status-bar button { padding: 2px 8px; font-size: 1em; }

        /* ── The desk, and the cards on it ──────────────────────────────────────
           ⚠ The panels are separated by their own margins, not by the splitter. A
           splitter drawn as a grey bar is a piece of furniture between two documents;
           an empty gap with an invisible splitter *in* it reads as space, drags
           exactly the same, and lights up only while it is being used. That gap plus
           a corner radius is what makes a panel look like a surface rather than a
           region of the window. */
        docking-host, dock-surface { background-color: var(--workspace); }

        dock-group {
            margin: 3px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: var(--radius-panel);
            background-color: var(--surface);
            box-shadow: var(--card);
        }

        /* ⚠ The focused panel says so on its own edge. A dozen identical cards and a
           keyboard that goes to one of them is an editor where Delete is a guess —
           and a tinted hairline is the cheapest possible way to answer "which one",
           costing no layout and no second element. */
        dock-group:focus-within { border-color: var(--border-active); }

        dock-splitter { background-color: transparent; border-radius: 2px; }
        dock-split.horizontal > dock-splitter { width: 4px; }
        dock-split.vertical > dock-splitter { height: 4px; }
        dock-splitter:hover { background-color: var(--accent); }

        /* Tabs are pills on the panel's own surface: no strip fill, no rule beneath,
           and the selected one is the only thing with a background. A tab row that
           draws a container around itself spends eight pixels of a panel's height
           saying where the panel already is. */
        dock-tabstrip {
            flex-shrink: 0;
            gap: 2px;
            padding: 4px 4px 2px 4px;
            border-width: 0px;
            background-color: transparent;
        }

        dock-tab {
            padding: 4px 10px;
            border-radius: var(--radius-row);
            background-color: transparent;
            color: var(--text-muted);
            font-size: 0.92em;
        }

        dock-tab:hover { color: var(--text); }
        dock-tab:checked { background-color: var(--surface-raised); color: var(--text); }
        dock-tab icon { width: 9px; height: 9px; }

        /* ⚠ The other half of "which panel has the keyboard", and the half that is
           readable at a glance: a tinted hairline says *a* panel is focused, and the
           accent on its tab's own label says *which*, from across the window and
           without counting borders. */
        dock-group:focus-within dock-tab:checked { color: var(--accent); }

        dock-panel { padding: 4px; }

        dock-float {
            border-radius: var(--radius-panel);
            border-color: var(--border);
            background-color: var(--surface);
            box-shadow: var(--elevation);
        }

        dock-preview { border-radius: var(--radius-panel); }

        /* ── Things that float ──────────────────────────────────────────────────
           ⚠ One shadow token on everything that leaves the plane. Elevation is the
           only cue an overlay gets — it has no title bar and no border worth seeing —
           and having each of them invent its own is how a menu ends up looking
           nearer than the dialog it was opened from. */
        popover, menu, context-menu {
            padding: 5px;
            border-radius: 12px;
            border-color: var(--border);
            background-color: var(--surface-raised);
            box-shadow: var(--elevation);
        }

        menu-item, option {
            padding: 5px 10px;
            border-radius: var(--radius-row);
            color: var(--text);
        }

        menu-item:hover:not(:disabled), option:hover:not(:disabled), menu-item:focus, option:focus {
            background-color: var(--accent);
            color: var(--accent-text);
        }

        menu-item:hover:not(:disabled) kbd, menu-item:focus kbd {
            background-color: transparent;
            border-color: transparent;
            color: var(--accent-text);
        }

        dialog-surface { border-radius: 14px; box-shadow: var(--elevation); }
        dialog-header, dialog-footer { border-color: var(--border); }
        drawer-surface { box-shadow: var(--elevation); }

        toast {
            border-radius: 12px;
            background-color: var(--surface-raised);
            box-shadow: var(--elevation);
        }

        tooltip {
            padding: 5px 9px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: var(--radius-control);
            background-color: var(--surface-raised);
            color: var(--text);
            box-shadow: var(--elevation);
        }

        /* ── Controls ───────────────────────────────────────────────────────────
           Raised where you press, sunken where you type. That is the whole rule, and
           it is the one an audio tool follows to the letter: a button is a thing on
           the surface and a field is a hole in it, so the two never have to be told
           apart by their border.

           ⚠ And the well is a fill, not an inner shadow, because `DrawListBuilder`
           refuses `inset` outright and says why: an inset shadow drawn as an outer
           one is not a near miss. So the recess is two luminance steps down from the
           panel and nothing else — which is the reason the surface ramp has four
           entries rather than three. */
        button, icon-button, toggle-button {
            padding: 5px 12px;
            border-radius: var(--radius-control);
            border-color: var(--border);
            background-color: var(--surface-raised);
            color: var(--text);
        }

        icon-button { padding: 5px; }
        button.size-sm, icon-button.size-sm, toggle-button.size-sm { padding: 3px 9px; }
        icon-button.size-sm { padding: 3px; }

        button:hover:not(:disabled), icon-button:hover:not(:disabled), toggle-button:hover:not(:disabled) {
            background-color: var(--surface-hover);
        }

        button:active:not(:disabled), icon-button:active:not(:disabled), toggle-button:active:not(:disabled) {
            background-color: var(--surface-sunken);
        }

        button.variant-primary, toggle-button:checked {
            background-color: var(--accent);
            border-color: var(--accent);
            color: var(--accent-text);
        }

        button.variant-subtle, icon-button.variant-subtle, toggle-button.variant-subtle {
            background-color: transparent;
            border-color: transparent;
        }

        button.variant-subtle:hover:not(:disabled), icon-button.variant-subtle:hover:not(:disabled),
        toggle-button.variant-subtle:hover:not(:disabled) {
            background-color: var(--surface-hover);
        }

        textbox, textarea, search-box, numeric-input, select, multi-select, combo-box {
            padding: 4px 9px;
            border-radius: var(--radius-control);
            border-color: var(--border);
            background-color: var(--surface-sunken);
        }

        checkbox box, radio box { border-color: var(--border); background-color: var(--surface-sunken); }
        checkbox box { border-radius: 5px; }
        switch track { background-color: var(--track-color); }

        slider, range-slider { height: 18px; }
        progress-bar { height: 5px; border-radius: 3px; background-color: var(--track-color); }
        separator.horizontal, separator.vertical { background-color: var(--border); }

        /* ⚠ A ring rather than a recoloured border. The control set tints the border
           on focus, which on a field whose border is one step from the fill is a
           focus state you have to look for — and looking for the caret is faster,
           which means the ring is not doing its job. A spread shadow in the accent's
           own soft tone is visible at a glance and costs the layout nothing, because
           a shadow is not in the box model. */
        button:focus-visible, icon-button:focus-visible, toggle-button:focus-visible,
        checkbox:focus-visible, switch:focus-visible, radio:focus-visible,
        textbox:focus-visible, textarea:focus-visible, search-box:focus-visible,
        numeric-input:focus-visible, select:focus-visible, multi-select:focus-visible,
        slider:focus-visible, range-slider:focus-visible, tab:focus-visible,
        menu-item:focus-visible, option:focus-visible, link:focus-visible,
        page-button:focus-visible, breadcrumb-item:focus-visible, expander-header:focus-visible {
            border-color: var(--accent);
            box-shadow: 0px 0px 0px 3px var(--accent-soft);
        }

        /* A segmented control rather than a row of underlined words: the strip is the
           sunken track and the selected tab is the raised thing sitting in it. */
        tab-strip {
            align-self: flex-start;
            gap: 2px;
            padding: 3px;
            border-width: 0px;
            border-radius: 9px;
            background-color: var(--surface-sunken);
        }

        tab {
            padding: 4px 12px;
            border-width: 0px;
            border-radius: var(--radius-row);
            color: var(--text-muted);
        }

        tab:checked { background-color: var(--surface-raised); color: var(--text); }

        expander { border-color: var(--border); }
        expander-header { padding: 7px 4px; }
        card { border-radius: var(--radius-panel); border-color: var(--border); }
        badge, kbd { border-radius: var(--radius-row); }

        /* ── Lists ──────────────────────────────────────────────────────────────
           ⚠ Rows are inset from the panel's edge and rounded, which is what makes a
           selection read as an object rather than as a band across the window. The
           full-bleed square highlight is the single strongest tell of an interface
           built a decade ago, and it costs two properties to stop.

           `left`/`right` rather than a margin, because a virtualised row is
           absolutely positioned — see AdvancedTheme, which explains why. */
        tree-view { --row-height: 24px; }

        tree-row {
            left: 4px;
            right: 4px;
            border-radius: var(--radius-row);
            padding: 0px 6px;
        }

        tree-row:hover { background-color: var(--surface-hover); }

        /* ⚠ `--accent-deep` and not `--accent`. A list is mostly selection — one row in
           every panel, all the time — and the accent at full strength is the brightest
           thing in a dark editor. Reserving it for what the user just did, and giving
           the resting state a step down from it, is why the palette's highlight still
           reads as louder than the outliner's. */
        tree-row:checked, data-row:checked {
            background-color: var(--accent-deep);
            color: #ffffff;
        }

        tree-row:checked icon, data-row:checked icon { color: #ffffff; }
        .tree-editor { border-radius: 5px; }

        property-grid { gap: 4px; }
        property-row { border-radius: var(--radius-row); padding: 2px 6px; min-height: 26px; }
        property-row:hover { background-color: var(--surface-hover); }
        property-label { width: 38%; }

        data-row:hover { background-color: var(--surface-hover); }
        virtualizing-panel scrollbar { width: 8px; }

        empty-state { padding: 24px; }
        /* The shape only. What is inside is a render target the control paints itself,
           and a background under it would be a colour nobody ever sees. */
        viewport { border-radius: 7px; overflow: hidden; }

        /* ── Background tasks ───────────────────────────────────────────────────
           Never a modal dialog — doc 11 is explicit — so this is a panel that
           happens to be over the status bar and takes no input from the rest of
           the window. */
        task-center { flex-direction: column; gap: 6px; min-width: 280px; padding: 4px; }
        task-center > text { color: var(--text-muted); }

        task-row { flex-direction: column; gap: 4px; padding: 6px; border-radius: var(--radius-row); }
        task-row > task-line { flex-direction: row; align-items: center; gap: 6px; }
        task-title { flex-grow: 1; }
        task-status { color: var(--text-muted); font-size: 0.9em; }
        task-row progress-bar { flex-grow: 1; }

        /* ── Notifications ──────────────────────────────────────────────────────*/
        notification-list { flex-direction: column; gap: 2px; min-width: 300px; padding: 4px; }

        notification-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 6px 8px;
            border-radius: var(--radius-row);
        }

        notification-row > text { flex-grow: 1; }
        notification-row.severity-warning { color: var(--warning); }
        notification-row.severity-error { color: var(--danger); }

        /* ── Command palette ────────────────────────────────────────────────────
           Absolutely positioned by the overlay, so the width is declared and the
           height is whatever ten rows come to. Given the deepest elevation in the
           editor on purpose: it opens over everything and is the one surface that
           should look like it is in front of the window rather than in it. */
        command-palette {
            position: absolute;
            left: 0px;
            top: 0px;
            flex-direction: column;
            width: 600px;
            padding: 8px;
            gap: 6px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 14px;
            background-color: var(--surface-raised);
            box-shadow: var(--elevation);
        }

        /* `.closed` is the control set's own rule and already hides it; nothing is
           needed here beyond not fighting it. */
        command-palette search-box { flex-shrink: 0; padding: 8px 10px; font-size: 1.05em; }
        palette-list { flex-direction: column; gap: 1px; }
        palette-empty { padding: 10px 8px; color: var(--text-muted); }

        palette-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 7px 10px;
            border-width: 0px;
            border-radius: 8px;
            background-color: transparent;
            color: var(--text);
        }

        /* ⚠ `:checked` rather than `:hover` is what the highlight is, because the
           highlight is moved by the arrow keys while the pointer sits still. A
           hover rule as well would give two highlighted rows. */
        palette-row:checked { background-color: var(--accent); color: var(--accent-text); }
        palette-row.parked { display: none; }
        palette-row > text { flex-grow: 1; }
        palette-category { color: var(--text-muted); font-size: 0.85em; }
        palette-row:checked palette-category, palette-row:checked palette-detail { color: var(--accent-text); }
        palette-detail { color: var(--text-muted); font-size: 0.85em; }
        """;
}
