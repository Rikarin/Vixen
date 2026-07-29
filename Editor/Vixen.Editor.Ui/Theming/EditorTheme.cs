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

           ⚠ The greys are the middle of the range and not the bottom of it. A tool
           window that goes to near-black has nowhere left to put a recess — every
           field, every gutter and every gap is then the same colour, and the depth
           the ramp is *for* collapses into one flat sheet. Sitting the working
           surface at a mid grey leaves room below it for the wells and above it for
           the things you press, which is what the reference does and why it reads as
           material rather than as a dark theme.

           ⚠ The hairline is DARKER than the surface it edges, which is the other
           half of the same idea. A lighter border is a bevel and belongs on something
           raised; a darker one is a seam, and a tool window is made of seams. The
           gap between two panes is the same colour as the line around one, so a
           split and an edge are the same fact drawn at two widths.

           ⚠ The shadows and radii are tokens too. They are the difference between
           "a panel" and "a panel that is on top of something", and a sheet that
           wrote them inline would make changing the whole editor's depth a
           forty-place edit. */
        root {
            --workspace: #b4b7bc;
            --surface: #e4e6ea;
            --surface-sunken: #d3d6db;
            --surface-raised: #f2f3f6;
            --surface-hover: #ecedf1;
            --border: #a9adb4;
            --border-active: #5f8ddb;
            --text: #1b1d21;
            --text-muted: #5c616b;
            --accent: #2f6ecd;
            --accent-text: #ffffff;
            --accent-soft: #c6d8f5;
            --accent-deep: #35659f;
            --danger: #c8352f;

            --track-color: #c3c6cc;
            --fill-color: #2f6ecd;
            --thumb-color: #8d919a;
            --selection-color: #bcd3fb;
            --caret-color: #2f6ecd;

            --chrome: #d0d2d7;
            --chrome-sunken: #c4c6cc;
            --chrome-text: #43474f;
            --warning: #9a6200;
            --play: #2f8f46;
            --pause: #b8791b;
            --stop: #c8352f;

            /* ⚠ Two more shades each, because a hover has to stay the button's own
               colour. The generic toolbar hover is a neutral grey, which on a green
               button reads as the colour draining out of it at the moment the pointer
               arrives — the one instant it most needs to look live. `soft` is the wash
               under an idle button; `strong` is the filled one brightened. */
            --play-soft: #cfe6d6;
            --play-strong: #277a3b;
            --pause-soft: #f0e0c4;
            --pause-strong: #a06615;
            --stop-soft: #f2cfcd;
            --stop-strong: #ad2b26;

            --radius-panel: 5px;
            --radius-control: 4px;
            --radius-row: 4px;
            --elevation: 0px 10px 26px rgba(12, 14, 18, 0.22);
            --status-height: 24px;
        }

        root.dark {
            --workspace: #1b1b1d;
            --surface: #313134;
            --surface-sunken: #262628;
            --surface-raised: #3e3e42;
            --surface-hover: #48484d;
            --border: #1e1e20;
            --border-active: #4f7dbe;
            --text: #dcdcde;
            --text-muted: #97979c;
            --accent: #3f7fd8;
            --accent-text: #ffffff;
            --accent-soft: #33507a;
            --accent-deep: #35659f;
            --danger: #e5544c;

            --track-color: #232326;
            --fill-color: #3f7fd8;
            --thumb-color: #5a5a60;
            --selection-color: #2f5a94;
            --caret-color: #6fa4ee;

            --chrome: #2a2a2c;
            --chrome-sunken: #232325;
            --chrome-text: #a8a8ad;
            --warning: #d99a3c;
            --play: #3fae5c;
            --pause: #d99a3c;
            --stop: #e5544c;

            --play-soft: #24402c;
            --play-strong: #4cc46c;
            --pause-soft: #453a26;
            --pause-strong: #e8ab4e;
            --stop-soft: #4a2b29;
            --stop-strong: #f0655d;

            --elevation: 0px 10px 26px rgba(0, 0, 0, 0.5);
        }

        /* ── Shell ──────────────────────────────────────────────────────────────
           ⚠ Three greys down the window, not one. The transport strip at the top is
           its own shade, the panes below are the working surface, and what shows
           between the panes is the seam colour — which is the darkest of the three
           and is never a surface anything sits on. An editor that painted the whole
           frame one colour has to draw a line every time two things meet; this one
           only has to leave a gap. */
        editor-shell {
            flex-direction: column;
            flex-grow: 1;
            background-color: var(--chrome);
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

        /* ── Sections ───────────────────────────────────────────────────────────
           ⚠ A segmented control is one box with the seams *inside* it, which is the
           whole of what makes Translate/Rotate/Scale read as a choice rather than as
           three buttons that happen to be adjacent. Done by taking the gap away and
           squaring the inner corners: the group draws the border and the radius, and
           its members draw neither. */
        toolbar-group {
            flex-direction: row;
            align-items: center;
            gap: 0px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: var(--radius-control);
            background-color: var(--surface);
            overflow: hidden;
        }

        toolbar-group button, toolbar-group icon-button, toolbar-group toggle-button,
        toolbar-group button.variant-subtle, toolbar-group icon-button.variant-subtle {
            border-width: 0px;
            border-radius: 0px;
            background-color: transparent;
        }

        /* The seam between two members, drawn by the member rather than by a separator
           element: an element per seam would be a child the group has to keep in step
           with a rebuild that only knows about commands. */
        toolbar-group button + button, toolbar-group icon-button + icon-button,
        toolbar-group button + icon-button, toolbar-group icon-button + button {
            border-left-width: 1px;
            border-color: var(--border);
        }

        toolbar-group :checked { background-color: var(--accent-soft); color: var(--text); }

        /* ── The transport ──────────────────────────────────────────────────────
           ⚠ Colour first, fill when it is on. The play controls are the most
           clicked thing in either reference editor and are read at a glance rather
           than looked at — a row of identical grey glyphs is one the eye has to
           parse. And "am I in play mode" has to be answerable without reading
           anything at all, which is what the filled state is for: a green button
           with a white triangle while the game runs, a green triangle on the
           surface while it does not.

           The class is the command's (`EditorCommand.ClassName`) so the toolbar
           stays a view over ids and never learns which buttons are green. */
        toolbar .transport-play icon { color: var(--play); }
        toolbar .transport-pause icon { color: var(--pause); }
        toolbar .transport-stop icon { color: var(--stop); }
        toolbar .transport-step icon { color: var(--text-muted); }

        toolbar .transport-play:checked { background-color: var(--play); }
        toolbar .transport-pause:checked { background-color: var(--pause); }

        /* ⚠ White rather than `--accent-text`, and on both themes. The fill is a
           saturated colour rather than the accent, so the token that pairs with the
           accent is the wrong contrast — and a glyph that inherited `--text` would
           be near-black on green in light and near-white in dark, which is one of
           the two being unreadable. */
        toolbar .transport-play:checked icon, toolbar .transport-pause:checked icon { color: #ffffff; }

        /* ⚠ A hover keeps the button's own hue. The generic toolbar hover is a
           neutral grey, and applied to these it reads as the colour draining out of
           the button at the moment the pointer arrives. Idle buttons get a wash of
           their colour; a filled one gets a brighter fill of the same. */
        toolbar .transport-play:hover:not(:disabled):not(:checked) { background-color: var(--play-soft); }
        toolbar .transport-pause:hover:not(:disabled):not(:checked) { background-color: var(--pause-soft); }
        toolbar .transport-stop:hover:not(:disabled) { background-color: var(--stop-soft); }
        toolbar .transport-step:hover:not(:disabled) { background-color: var(--surface-hover); }

        toolbar .transport-play:checked:hover:not(:disabled) { background-color: var(--play-strong); }
        toolbar .transport-pause:checked:hover:not(:disabled) { background-color: var(--pause-strong); }

        /* Disabled is the ordinary muting and not a colour: a greyed Stop must not
           read as a Stop that is merely a different shade of red. */
        toolbar .transport-play:disabled icon, toolbar .transport-pause:disabled icon,
        toolbar .transport-stop:disabled icon, toolbar .transport-step:disabled icon {
            color: var(--text-muted);
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

        /* ⚠ The cells are muted and the message is not. Four things on one strip with
           equal weight is a strip nobody reads; the selection count and the frame time
           are there to be glanced at, and the message is there to be read. */
        status-cell { flex-shrink: 0; color: var(--text-muted); }

        /* Tabular figures would be better and there is no font feature to ask for
           them, so the cell is given a floor instead — a frame time going from 9.9 to
           10.1 must not shift the task button sideways. */
        status-cell.status-frame { min-width: 54px; }

        /* ── Panes and seams ────────────────────────────────────────────────────
           ⚠ The panes are separated by a seam, not spaced out on a desk. A one-pixel
           margin against the workspace colour is exactly the dark line a tool window
           divides itself with — and it is drawn by leaving a gap rather than by
           stroking an edge, so a split and a border are the same fact at two widths
           and can never disagree about their colour.

           The radius is small on purpose. A pane is a region of the window with work
           in it, not a card that arrived from somewhere; rounding it like one throws
           away four pixels of every corner of every panel and makes a dense tool
           read as a settings screen. */
        docking-host, dock-surface { background-color: var(--workspace); }

        dock-group {
            margin: 1px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: var(--radius-panel);
            background-color: var(--surface);
        }

        /* ⚠ The focused panel says so on its own edge. A dozen identical panes and a
           keyboard that goes to one of them is an editor where Delete is a guess —
           and a tinted hairline is the cheapest possible way to answer "which one",
           costing no layout and no second element.

           ⚠ Two selectors, because focus alone does not reach every panel. A tree row
           and a text field take focus, so the outliner and the scene lit up; a console
           row and an inspector's label do not, so those two never showed as focused
           however often they were clicked. `dock-group.active` is the docking host's
           own answer — the panel last worked in — and a border that is right for two
           panels out of four reads as broken rather than as absent. */
        dock-group:focus-within, dock-group.active { border-color: var(--border-active); }

        dock-splitter { background-color: transparent; }
        dock-split.horizontal > dock-splitter { width: 3px; }
        dock-split.vertical > dock-splitter { height: 3px; }
        dock-splitter:hover { background-color: var(--accent); }

        /* Tabs are pills on the panel's own surface: no strip fill, no rule beneath,
           and the selected one is the only thing with a background. A tab row that
           draws a container around itself spends eight pixels of a panel's height
           saying where the panel already is. */
        dock-tabstrip {
            flex-shrink: 0;
            gap: 1px;
            padding: 3px 3px 1px 3px;
            border-width: 0px;
            background-color: transparent;
        }

        dock-tab {
            padding: 3px 9px;
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
        dock-group:focus-within dock-tab:checked, dock-group.active dock-tab:checked { color: var(--accent); }

        dock-panel { padding: 3px; }

        dock-float {
            border-radius: var(--radius-control);
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
            padding: 4px;
            border-radius: 7px;
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

        dialog-surface { border-radius: 8px; box-shadow: var(--elevation); }
        dialog-body { gap: 8px; }

        /* ⚠ A fixed height and its own scroller, not a list that grows with the
           project. A picker sized to its contents is a dialog taller than the
           window on the first project with two hundred textures in it — and the
           search box, which is the way through a large project, would be the part
           pushed off the top. */
        asset-picker-list {
            flex-direction: column;
            gap: 1px;
            height: 260px;
            min-width: 320px;
            padding: 3px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: var(--radius-control);
            background-color: var(--surface-sunken);
            overflow: auto;
        }

        asset-picker-list > text { padding: 8px 6px; color: var(--text-muted); }

        /* Left-aligned rather than centred, because the list is read down its left
           edge and a centred name is one the eye has to find on every row. */
        button.asset-picker-row {
            flex-shrink: 0;
            justify-content: flex-start;
            padding: 5px 8px;
            border-width: 0px;
            border-radius: var(--radius-row);
            background-color: transparent;
            color: var(--text);
        }

        button.asset-picker-row:hover { background-color: var(--surface-hover); }
        dialog-header, dialog-footer { border-color: var(--border); }
        drawer-surface { box-shadow: var(--elevation); }

        toast {
            border-radius: 7px;
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
        checkbox box { border-radius: 3px; }
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
            gap: 1px;
            padding: 2px;
            border-width: 0px;
            border-radius: 6px;
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
        card { border-radius: var(--radius-control); border-color: var(--border); }
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

        /* ── The outliner's two columns ─────────────────────────────────────────
           The label takes what is left and the two toggles keep their width, so a long
           entity name is what gets clipped rather than the eye being pushed off the
           panel — which is the one thing that must stay reachable.

           ⚠ Faint until the row is hovered, and *not* faint when the mark is on. Two
           icons at full contrast beside every name is a wall of chrome down the side
           of a scene where nothing is hidden; two invisible ones are a feature nobody
           finds. What is on has to read from across the panel, because "why can I not
           click this" is the question the padlock exists to answer. */
        .outliner-hidden, .outliner-locked { flex-shrink: 0; opacity: 0.18; }

        tree-row:hover .outliner-hidden, tree-row:hover .outliner-locked { opacity: 0.7; }
        .outliner-hidden:checked, .outliner-locked:checked { opacity: 1; }

        /* ⚠ A row whose parent carries the mark shows it dimmed rather than on. It is not
           being drawn either way, and clicking here would do nothing — the mark it would
           clear is on an ancestor — so showing it on would be a button that lies about
           what pressing it does. */
        .outliner-hidden.inherited, .outliner-locked.inherited { opacity: 0.45; }

        outliner-filters { flex-direction: row; align-items: center; gap: 6px; flex-shrink: 0; }
        outliner-filters > search-box { flex-grow: 1; min-width: 0; }
        outliner-filters > select { flex-shrink: 0; width: 132px; }

        property-grid { gap: 4px; }
        property-row { border-radius: var(--radius-row); padding: 2px 6px; min-height: 26px; }
        property-row:hover { background-color: var(--surface-hover); }
        property-label { width: 38%; }

        data-row:hover { background-color: var(--surface-hover); }
        virtualizing-panel scrollbar { width: 8px; }

        empty-state { padding: 24px; }
        /* The shape only. What is inside is a render target the control paints itself,
           and a background under it would be a colour nobody ever sees. */
        viewport { border-radius: var(--radius-row); overflow: hidden; }

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
            border-radius: 8px;
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
            padding: 6px 9px;
            border-width: 0px;
            border-radius: var(--radius-row);
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

        /* ── Console ────────────────────────────────────────────────────────────
           A strip, a virtualised list and a detail pane, in a column. The list is
           the only part that grows: a console whose detail pane grew with the
           stack in it would push the lines off the top of the panel every time
           somebody clicked an exception. */
        console-view { flex-direction: column; flex-grow: 1; flex-basis: 0px; min-height: 0px; gap: 0px; }

        console-toolbar {
            flex-direction: row;
            align-items: center;
            flex-shrink: 0;
            gap: 4px;
            padding: 4px 6px;
            border-bottom-width: 1px;
            border-color: var(--border);
        }

        console-toolbar search-box { flex-grow: 1; min-width: 80px; }
        console-toolbar select { width: 150px; flex-shrink: 0; }

        /* ⚠ The four level buttons are pushed to the right, away from the verbs.
           Clear and Collapse *do* something; a level button changes what you are
           looking at, and mixing the two into one run of identical chips is how
           somebody clears the console meaning to hide the warnings. */
        console-toolbar toggle-button.console-level {
            flex-shrink: 0;
            min-width: 34px;
            padding: 2px 7px;
            border-radius: var(--radius-control);
            border-color: var(--border);
            background-color: var(--surface);
            color: var(--text-muted);
        }

        /* Off is muted and on is coloured, which is the right way round: a level
           that is being *shown* is the one carrying the signal. */
        console-toolbar toggle-button.console-level:checked.level-error { color: var(--danger); }
        console-toolbar toggle-button.console-level:checked.level-warning { color: var(--warning); }
        console-toolbar toggle-button.console-level:checked.level-info { color: var(--text); }
        console-toolbar toggle-button.console-level:checked.level-verbose { color: var(--text-muted); }

        console-view virtualizing-panel { flex-grow: 1; flex-basis: 0px; min-height: 0px; }

        console-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 0px 8px;
            border-width: 0px;
            background-color: transparent;
            color: var(--text);
            font-size: 0.9em;
        }

        console-row:hover { background-color: var(--surface-hover); }
        console-row:checked { background-color: var(--accent); color: var(--accent-text); }
        console-row.parked { display: none; }

        /* A three-pixel rule down the left of the row rather than a coloured
           message: a console where the text is the colour is one where a page of
           warnings is a page of orange, and the message stops being readable. */
        console-level-mark { width: 3px; height: 12px; flex-shrink: 0; border-radius: 2px; }
        console-level-mark.level-error { background-color: var(--danger); }
        console-level-mark.level-warning { background-color: var(--warning); }
        console-level-mark.level-info { background-color: var(--accent); }
        console-level-mark.level-verbose { background-color: var(--border); }

        console-time { width: 74px; flex-shrink: 0; color: var(--text-muted); font-size: 0.9em; }
        console-category { width: 112px; flex-shrink: 0; color: var(--text-muted); overflow: hidden; }
        console-message { flex-grow: 1; overflow: hidden; }

        console-repeats {
            flex-shrink: 0;
            min-width: 20px;
            padding: 0px 5px;
            border-radius: 8px;
            background-color: var(--surface-raised);
            color: var(--text-muted);
            font-size: 0.85em;
        }

        console-row:checked console-time, console-row:checked console-category,
        console-row:checked console-repeats { color: var(--accent-text); }

        /* ⚠ A fixed height and its own scroller. The stack of a deep exception is
           forty lines, and a pane that sized to its contents would leave the list
           two rows tall the moment one was selected. */
        console-detail {
            flex-direction: column;
            flex-shrink: 0;
            height: 132px;
            gap: 3px;
            padding: 7px 9px;
            border-top-width: 1px;
            border-color: var(--border);
            background-color: var(--surface-sunken);
            overflow: auto;
        }

        /* ⚠ One line tall with nothing selected. A console docked along the bottom is
           about six rows deep, and a detail pane holding a third of that open for a
           stack that is not there is a panel with no room to read the log in. */
        console-detail.empty { height: auto; padding: 5px 9px; color: var(--text-muted); }
        console-detail-heading { color: var(--text); }
        console-detail-meta { color: var(--text-muted); font-size: 0.85em; }
        console-detail-stack { color: var(--text-muted); font-size: 0.85em; white-space: pre; }
        """;
}
