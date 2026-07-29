// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>The stylesheet the controls come with.</summary>
/// <remarks>
///     <para>
///         <b>Loaded as <see cref="StyleOrigin.UserAgent" />, which is the whole design.</b> A game's
///         own stylesheet is <see cref="StyleOrigin.Author" /> and beats this at equal specificity,
///         so restyling a button is one rule rather than a fork of this file — and a rule that only
///         names <c>button</c> is enough, rather than one that has to out-specify whatever this
///         happened to write. That is the reason the cascade has three origins, and this is the
///         first thing in the engine to use the first of them.
///     </para>
///     <para>
///         ⚠ <b>Colours go through custom properties and geometry does not.</b> A theme wanting
///         different colours sets nine values on the root; one wanting different <i>shapes</i> writes
///         rules, because there is no useful token for "a switch's knob is a circle". The split is
///         where it is because recolouring is what nearly every application does and reshaping is
///         what a few do thoroughly.
///     </para>
///     <para>
///         ⚠ <b>The layout defaults are CSS's, so a container with no <c>flex-direction</c> is a
///         <i>row</i></b> — <c>LayoutStyleBuilder</c> starts from CSS's initial values rather than
///         from Yoga's, which differ in four places. Every rule below that wants a column therefore
///         says so, and a rule that reads as redundant beside a browser stylesheet is not.
///     </para>
///     <para>
///         <b><c>box-sizing: border-box</c> is set here on everything</b>, which is the one place it
///         can be: <c>LayoutStyleBuilder</c>'s own remarks say the property belongs in a user-agent
///         stylesheet where an author can see and override it rather than baked into the bridge
///         where they cannot. This is that stylesheet. Without it a control's declared height is the
///         height of its <i>content</i> and every padded control comes out taller than it says.
///     </para>
/// </remarks>
public static class ControlTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        /* Every control's declared size is its outside, which is what UI work wants and
           what LayoutStyleBuilder deliberately left to a user-agent sheet to say. */
        * { box-sizing: border-box; }

        /* ── Tokens ────────────────────────────────────────────────────────────
           Everything below is written against these. An application that wants a
           different palette sets them on the root and touches nothing else; one
           that wants a dark theme adds the `dark` class, which is the
           `DarkModeStrategy.Class` the utility generator already understands. */

        root {
            --surface: #ffffff;
            --surface-sunken: #f4f5f7;
            --surface-raised: #ffffff;
            --border: #d8dbe0;
            --text: #1c1f23;
            --text-muted: #6b7280;
            --accent: #3b6cf0;
            --accent-text: #ffffff;
            --danger: #d64545;

            --track-color: #e3e6eb;
            --fill-color: #3b6cf0;
            --thumb-color: #ffffff;
            --thumb-size: 14px;
            --selection-color: #b8ccf8;
            --caret-color: #1c1f23;

            color: var(--text);
        }

        root.dark {
            --surface: #1b1d21;
            --surface-sunken: #141619;
            --surface-raised: #24272c;
            --border: #383c43;
            --text: #e8eaed;
            --text-muted: #9aa1ab;
            --accent: #5b87ff;
            --accent-text: #0d0f12;
            --danger: #f07171;

            --track-color: #33373d;
            --fill-color: #5b87ff;
            --thumb-color: #e8eaed;
            --selection-color: #2c4a86;
            --caret-color: #e8eaed;
        }

        /* A second window's root. It is `root`'s counterpart for everything except the tokens,
           which it inherits — one document, one palette, however many windows. `position:
           relative` because a torn-off window is where a floating popover, a drop preview and a
           drag ghost are positioned from once the content is no longer in the main window. */
        ui-surface {
            flex-direction: column;
            position: relative;
            overflow: hidden;
            background-color: var(--surface);
        }

        /* ── Text ───────────────────────────────────────────────────────────── */
        text { color: var(--text); }
        text.variant-subtle { color: var(--text-muted); }
        text.variant-danger { color: var(--danger); }

        link {
            flex-direction: row;
            align-items: center;
            align-self: flex-start;
            color: var(--accent);
        }

        kbd {
            flex-direction: row;
            align-items: center;
            align-self: flex-start;
            padding: 1px 5px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 4px;
            background-color: var(--surface-sunken);
            color: var(--text-muted);
            font-size: 0.85em;
        }

        badge {
            flex-direction: row;
            align-items: center;
            align-self: flex-start;
            padding: 1px 7px;
            border-radius: 9px;
            background-color: var(--surface-sunken);
            color: var(--text-muted);
            font-size: 0.85em;
        }

        badge.variant-primary { background-color: var(--accent); color: var(--accent-text); }
        badge.variant-danger { background-color: var(--danger); color: var(--accent-text); }

        avatar {
            width: 32px;
            height: 32px;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            border-radius: 16px;
            background-color: var(--surface-sunken);
            color: var(--text-muted);
        }

        skeleton {
            height: 12px;
            border-radius: 6px;
            background-color: var(--track-color);
        }

        icon { width: 16px; height: 16px; }

        /* ── Buttons ────────────────────────────────────────────────────────── */
        button, icon-button, toggle-button {
            flex-direction: row;
            align-items: center;
            justify-content: center;
            align-self: flex-start;
            gap: 6px;
            padding: 5px 12px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: var(--surface);
            color: var(--text);
        }

        icon-button { padding: 5px; }
        icon-button label { display: none; }

        button.size-sm, icon-button.size-sm, toggle-button.size-sm { padding: 2px 8px; font-size: 0.9em; }
        button.size-lg, icon-button.size-lg, toggle-button.size-lg { padding: 8px 18px; font-size: 1.1em; }
        icon-button.size-sm { padding: 2px; }
        icon-button.size-lg { padding: 8px; }

        button:hover:not(:disabled), icon-button:hover:not(:disabled), toggle-button:hover:not(:disabled) { background-color: var(--surface-sunken); }
        button:active:not(:disabled), icon-button:active:not(:disabled), toggle-button:active:not(:disabled) { background-color: var(--track-color); }

        button.variant-primary {
            background-color: var(--accent);
            border-color: var(--accent);
            color: var(--accent-text);
        }

        button.variant-danger {
            background-color: var(--danger);
            border-color: var(--danger);
            color: var(--accent-text);
        }

        button.variant-subtle, icon-button.variant-subtle, toggle-button.variant-subtle {
            background-color: transparent;
            border-color: transparent;
            color: var(--text-muted);
        }

        toggle-button:checked {
            background-color: var(--accent);
            border-color: var(--accent);
            color: var(--accent-text);
        }

        /* The focus ring is one rule for the whole set, and it is `:focus-visible`
           rather than `:focus` — so a click does not light it and a Tab does. */
        button:focus-visible, icon-button:focus-visible, toggle-button:focus-visible,
        checkbox:focus-visible, switch:focus-visible, radio:focus-visible,
        textbox:focus-visible, textarea:focus-visible, search-box:focus-visible,
        numeric-input:focus-visible, select:focus-visible, multi-select:focus-visible,
        slider:focus-visible, range-slider:focus-visible, tab:focus-visible,
        menu-item:focus-visible, option:focus-visible, link:focus-visible,
        page-button:focus-visible, breadcrumb-item:focus-visible, expander-header:focus-visible {
            border-color: var(--accent);
        }

        /* ── Disabled ───────────────────────────────────────────────────────────
           Two halves, and the first one is the important half.

           ⚠ Every `:hover` and `:active` rule above carries `:not(:disabled)`,
           because otherwise a disabled control under the pointer lights up exactly
           as a live one does and then does nothing when it is clicked. That is not
           a styling nicety: hover feedback *is* the promise that something will
           happen, and a control that makes it and then refuses is worse than one
           that never looked interactive. The state was always right — the element
           carries `ElementState.Disabled` — so nothing but a picture could see it.

           ⚠ And the fade is `opacity` rather than a muted palette. A muted colour
           has to be chosen against every background a control can sit on and
           against every variant; `opacity` is one number that is correct on all of
           them, including a primary button whose text is white on blue and whose
           muted text colour is therefore almost exactly its unmuted one. The text
           colour stays as well, for the controls whose background is transparent
           and which the opacity alone barely touches. */
        :disabled {
            color: var(--text-muted);
            opacity: 0.55;
        }

        /* ── Toggles ────────────────────────────────────────────────────────── */
        checkbox, radio, switch {
            flex-direction: row;
            align-items: center;
            align-self: flex-start;
            gap: 8px;
            color: var(--text);
        }

        checkbox box {
            width: 16px;
            height: 16px;
            align-items: center;
            justify-content: center;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 4px;
            background-color: var(--surface);
        }

        checkbox box icon { width: 12px; height: 12px; display: none; }
        checkbox:checked box, checkbox.indeterminate box { background-color: var(--accent); border-color: var(--accent); }
        checkbox:checked box icon, checkbox.indeterminate box icon { display: flex; color: var(--accent-text); }

        radio box {
            width: 16px;
            height: 16px;
            align-items: center;
            justify-content: center;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 8px;
            background-color: var(--surface);
        }

        radio box dot { width: 8px; height: 8px; border-radius: 4px; display: none; }
        radio:checked box { border-color: var(--accent); }
        radio:checked box dot { display: flex; background-color: var(--accent); }

        radio-group { flex-direction: column; gap: 6px; }

        switch track {
            width: 34px;
            height: 18px;
            flex-direction: row;
            align-items: center;
            padding: 2px;
            border-radius: 9px;
            background-color: var(--track-color);
        }

        switch track knob {
            width: 14px;
            height: 14px;
            border-radius: 7px;
            background-color: var(--thumb-color);
            margin-left: 0px;
        }

        switch:checked track { background-color: var(--accent); }
        switch:checked track knob { margin-left: 16px; }

        /* ── Fields ─────────────────────────────────────────────────────────── */
        textbox, textarea, search-box, numeric-input {
            flex-direction: row;
            align-items: center;
            gap: 6px;
            padding: 5px 8px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: var(--surface);
            color: var(--text);
            overflow: hidden;

            /* ⚠ Load-bearing, and it is what the placeholder below is positioned against. An
               absolutely-positioned child is placed by the nearest ancestor that is not `static`,
               and `static` is the CSS initial — so without this the placeholder was laid out
               against whatever `position: relative` happened to be somewhere above the field, which
               in a docked panel is nothing at all. The symptom is a placeholder sitting at the left
               edge of the *window* rather than of the box, which in the project browser reads as
               "the placeholder overlaps the magnifying glass" because that is where it lands. */
            position: relative;
        }

        textarea { min-height: 72px; align-items: flex-start; }

        /*
         * A field's text does not wrap and a text area's does, which is the whole difference
         * between them now that the framework can break a line at all. `flex-shrink: 0` is what
         * lets the single-line one be wider than its box and scroll sideways; the text area gives
         * that up in exchange for its text staying inside.
         */
        field-text { flex-shrink: 0; white-space: nowrap; }
        textarea field-text { flex-shrink: 1; white-space: normal; }
        field-placeholder { position: absolute; left: 8px; color: var(--text-muted); display: none; }
        .empty field-placeholder { display: flex; }

        /* ⚠ Past the magnifying glass, because a search box has one and the other fields do not.
           The offset is the field's own padding plus the icon and the gap after it — the place
           `field-text` starts — so what the user typed appears exactly where the prompt for it was
           rather than a glyph's width to the right of it. Absolute rather than in flow so that the
           prompt does not decide how wide the box has to be: "Search assets" is wider than the
           field the project browser gives it. */
        search-box field-placeholder { left: 28px; }

        search-box icon { width: 14px; height: 14px; color: var(--text-muted); }
        search-box.empty icon-button { display: none; }

        /* ── Virtualisation ─────────────────────────────────────────────────── */
        virtualizing-panel { flex: 1; min-height: 0; }
        virtualizing-panel scroll-view { flex: 1; }

        /*
         * `position: relative` on the content and `absolute` on the rows is what makes a row's
         * `top` mean "this far down the list" rather than "this far down the document" — and it is
         * why a row 40 000 lines down can be placed without the 39 999 above it existing.
         */
        .virtual-content { position: relative; }
        .virtual-content > * { position: absolute; left: 0; right: 0; }

        /*
         * ⚠ Parked rather than removed. A pool that shrank would allocate on the next scroll, which
         * is the cost virtualisation exists to avoid — so a surplus row keeps its element and stops
         * drawing.
         */
        .virtual-content > .parked { display: none; }

        /* ── Range ──────────────────────────────────────────────────────────── */
        slider, range-slider { height: 20px; min-width: 80px; }

        progress-bar {
            height: 6px;
            min-width: 80px;
            border-radius: 3px;
        }

        spinner { width: 20px; height: 20px; color: var(--accent); }

        /* ── Containers ─────────────────────────────────────────────────────── */
        panel { flex-direction: column; }

        card {
            flex-direction: column;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 8px;
            background-color: var(--surface-raised);
            overflow: hidden;
        }

        card-header { padding: 10px 14px; border-width: 0px 0px 1px 0px; border-color: var(--border); }
        card-body { flex-direction: column; padding: 14px; }
        card-footer { padding: 10px 14px; flex-direction: row; justify-content: flex-end; gap: 8px; }

        separator.horizontal { height: 1px; background-color: var(--border); margin: 6px 0px; }
        separator.vertical { width: 1px; align-self: stretch; background-color: var(--border); margin: 0px 6px; }

        alert {
            flex-direction: row;
            gap: 10px;
            padding: 10px 12px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: var(--surface-sunken);
        }

        alert-body { flex-direction: column; gap: 2px; }
        alert-title { color: var(--text); }
        alert-message { color: var(--text-muted); }
        alert.variant-danger { border-color: var(--danger); }
        alert.variant-danger alert-title { color: var(--danger); }

        empty-state { flex-direction: column; align-items: center; justify-content: center; gap: 8px; padding: 32px; }
        empty-state icon { width: 40px; height: 40px; color: var(--text-muted); }
        empty-title { color: var(--text); }
        empty-description { color: var(--text-muted); }
        empty-actions { flex-direction: row; gap: 8px; }

        /* ── Tabs, disclosure ───────────────────────────────────────────────── */
        tabs { flex-direction: column; }
        tab-panels { flex-direction: column; }

        tab-strip {
            flex-direction: row;
            gap: 2px;
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
        }

        tab {
            flex-direction: row;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            border-width: 0px 0px 2px 0px;
            border-color: transparent;
            color: var(--text-muted);
        }

        tab:hover:not(:disabled) { color: var(--text); }
        tab:checked { color: var(--text); border-color: var(--accent); }

        tab-panel { display: none; flex-direction: column; padding: 12px 0px; }
        tab-panel.selected { display: flex; }

        expander { flex-direction: column; border-width: 0px 0px 1px 0px; border-color: var(--border); }

        expander-header {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 8px 4px;
            border-width: 0px;
            background-color: transparent;
            color: var(--text);
        }

        expander-header icon { width: 12px; height: 12px; color: var(--text-muted); }
        expander-content { display: none; flex-direction: column; padding: 4px 4px 12px 20px; }
        expander.open expander-content { display: flex; }

        /* ── Scrolling ──────────────────────────────────────────────────────── */
        /* ⚠ A scroll view that is meant to *fill* its parent rather than be sized by hand needs
           `flex-basis: 0px` on top of a `flex-grow` — see how the tree does it. A flex item's base
           size is its content, and this one's content is deliberately unshrinkable, so the viewport
           otherwise grows to the full height of what is inside it: nothing overflows, and the
           scrollbar never appears. It is not set here because a scroll view given an explicit height
           is just as common, and a zero basis would override that height. */
        scroll-view { flex-direction: column; overflow: hidden; position: relative; }
        scroll-content { flex-direction: column; flex-shrink: 0; align-self: flex-start; min-width: 100%; }

        scrollbar { position: absolute; }
        scrollbar.vertical { top: 0px; right: 0px; bottom: 0px; width: 10px; }
        scrollbar.horizontal { left: 0px; right: 0px; bottom: 0px; height: 10px; }

        /* ── Overlays ───────────────────────────────────────────────────────── */
        popover-content { flex-direction: column; }

        popover, menu, context-menu {
            position: absolute;
            left: 0px;
            top: 0px;
            flex-direction: column;
            padding: 4px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 8px;
            background-color: var(--surface-raised);
        }

        .closed { display: none; }

        tooltip {
            position: absolute;
            left: 0px;
            top: 0px;
            padding: 4px 8px;
            border-radius: 5px;
            background-color: #1c1f23;
            color: #ffffff;
            font-size: 0.9em;
            pointer-events: none;
        }

        menu-item, option {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 5px 10px;
            border-width: 0px;
            border-radius: 5px;
            background-color: transparent;
            color: var(--text);
        }

        menu-item:hover:not(:disabled), option:hover:not(:disabled), menu-item:focus, option:focus {
            background-color: var(--surface-sunken);
        }

        menu-item icon, option icon { width: 12px; height: 12px; }
        menu-item kbd { margin-left: auto; }

        /* The arrow that says a line opens a menu rather than running something. Pushed right the
           same way a shortcut is — the two never share a line, because a submenu is not a command
           and so has nothing to bind — and dimmed, because it is a property of the line rather than
           a second thing on it. */
        menu-item icon.submenu { margin-left: auto; color: var(--text-muted); }

        menu-bar { flex-direction: row; gap: 2px; }

        menu-bar-item {
            flex-direction: row;
            align-items: center;
            padding: 4px 10px;
            border-width: 0px;
            border-radius: 5px;
            background-color: transparent;
            color: var(--text);
        }

        menu-bar-item:hover:not(:disabled), menu-bar-item:checked { background-color: var(--surface-sunken); }

        /* ── Selects ────────────────────────────────────────────────────────── */
        select, multi-select, combo-box {
            flex-direction: row;
            align-items: center;
            gap: 6px;
            padding: 5px 8px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 6px;
            background-color: var(--surface);
            color: var(--text);
        }

        select-field { flex-grow: 1; }
        select.empty select-field, multi-select.empty select-field { color: var(--text-muted); }
        select icon, multi-select icon, combo-box icon { width: 12px; height: 12px; color: var(--text-muted); }

        popover.select-list { flex-direction: column; padding: 4px; min-width: 120px; }
        combo-box .combo-editor { border-width: 0px; padding: 0px; flex-grow: 1; background-color: transparent; }

        /* ── Dialogs ────────────────────────────────────────────────────────── */
        dialog, drawer {
            position: absolute;
            flex-direction: column;
            left: 0px;
            top: 0px;
            right: 0px;
            bottom: 0px;
            align-items: center;
            justify-content: center;
        }

        dialog-backdrop, drawer-backdrop {
            position: absolute;
            left: 0px;
            top: 0px;
            right: 0px;
            bottom: 0px;
            background-color: #00000066;
        }

        dialog-surface {
            flex-direction: column;
            min-width: 320px;
            max-width: 560px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 10px;
            background-color: var(--surface-raised);
            overflow: hidden;
        }

        dialog-header {
            flex-direction: row;
            align-items: center;
            justify-content: space-between;
            gap: 12px;
            padding: 12px 14px;
            border-width: 0px 0px 1px 0px;
            border-color: var(--border);
        }

        dialog-title { color: var(--text); }
        dialog-body { flex-direction: column; padding: 14px; }
        dialog-footer {
            flex-direction: row;
            justify-content: flex-end;
            gap: 8px;
            padding: 12px 14px;
            border-width: 1px 0px 0px 0px;
            border-color: var(--border);
        }

        drawer { align-items: flex-start; justify-content: flex-start; }

        drawer-surface {
            flex-direction: column;
            position: absolute;
            top: 0px;
            bottom: 0px;
            width: 320px;
            border-width: 0px 1px 0px 0px;
            border-color: var(--border);
            background-color: var(--surface-raised);
        }

        drawer.side-left drawer-surface { left: 0px; }
        drawer.side-right drawer-surface { right: 0px; border-width: 0px 0px 0px 1px; }
        drawer.side-top drawer-surface { left: 0px; right: 0px; top: 0px; bottom: auto; width: auto; height: 240px; }
        drawer.side-bottom drawer-surface { left: 0px; right: 0px; bottom: 0px; top: auto; width: auto; height: 240px; }
        drawer-body { flex-direction: column; padding: 14px; }

        /* ── Toasts ─────────────────────────────────────────────────────────── */
        toast-host {
            position: absolute;
            right: 16px;
            bottom: 16px;
            flex-direction: column;
            gap: 8px;
            align-items: flex-end;
        }

        toast {
            flex-direction: row;
            align-items: center;
            gap: 10px;
            min-width: 220px;
            padding: 10px 12px;
            border-width: 1px;
            border-color: var(--border);
            border-radius: 8px;
            background-color: var(--surface-raised);
            color: var(--text);
        }

        toast-message { flex-grow: 1; }
        toast.variant-danger { border-color: var(--danger); }

        /* ── Navigation ─────────────────────────────────────────────────────── */
        breadcrumb { flex-direction: row; align-items: center; gap: 4px; }
        breadcrumb-item {
            flex-direction: row;
            align-items: center;
            padding: 2px 4px;
            border-width: 0px;
            background-color: transparent;
            color: var(--text-muted);
        }

        breadcrumb-item:hover:not(:disabled) { color: var(--text); }
        breadcrumb-item:checked { color: var(--text); }
        .breadcrumb-separator { width: 10px; height: 10px; color: var(--text-muted); }

        pagination { flex-direction: row; align-items: center; gap: 2px; }
        page-button {
            flex-direction: row;
            align-items: center;
            justify-content: center;
            min-width: 28px;
            padding: 3px 6px;
            border-width: 1px;
            border-color: transparent;
            border-radius: 5px;
            background-color: transparent;
            color: var(--text);
        }

        page-button:hover:not(:disabled) { background-color: var(--surface-sunken); }
        page-button:checked { background-color: var(--accent); color: var(--accent-text); }
        page-button.page-arrow label { display: none; }
        page-button.ellipsis { color: var(--text-muted); }
        """;
}
