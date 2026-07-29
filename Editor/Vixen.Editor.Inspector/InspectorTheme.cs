// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Inspector;

/// <summary>The stylesheet the inspector's own elements come with.</summary>
/// <remarks>
///     <para>
///         A sheet after <c>ControlTheme</c> and <c>AdvancedTheme</c> and written against the same
///         tokens, on the same terms <c>NodeGraphTheme</c> is: the controls a drawer builds are
///         already styled by those two, and what is here is only the six elements this assembly
///         adds — the view, its body, a row, a row's label, a row's editor slot, and the component
///         group a vector drawer builds.
///     </para>
///     <para>
///         ⚠ <b>Both of those have to be loaded first.</b> Every colour below is a
///         <c>var(--…)</c> against a token one of them declares, and a custom property nothing
///         declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>Without this the inspector lays out as rows of rows.</b> CSS's initial
///         <c>flex-direction</c> is <c>row</c> and <c>LayoutStyleBuilder</c> starts from CSS's
///         initial values, so an element nothing styles is a row — which puts the search box beside
///         the fields, and every member beside the one before it. Each <c>flex-direction: column</c>
///         below that reads as redundant beside a browser stylesheet is not.
///     </para>
///     <para>
///         ⚠ <b>A field's background is <c>--surface-sunken</c> and not <c>--surface</c>.</b> The
///         control set gives a text box <c>--surface</c>, which is right on a page and wrong in a
///         tool window: <c>dock-group</c> is <c>--surface</c> too, so a box drawn in the panel's own
///         colour is a border around nothing. Sunk rather than raised because a field is a hole you
///         type into, which is the convention every editor with a docked inspector already follows.
///     </para>
/// </remarks>
public static class InspectorTheme {
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
        inspector { flex-direction: column; flex-grow: 1; gap: 6px; padding: 2px; overflow: hidden; }

        /* The strip keeps its height while the rows below it come and go — a search field that
           shrank when the selection emptied would move under the pointer mid-type. */
        inspector-header { flex-direction: row; align-items: center; gap: 6px; flex-shrink: 0; }
        inspector-header > search-box { flex-grow: 1; min-width: 0; }
        inspector-header > .inspector-lock { flex-shrink: 0; }

        /* ⚠ `min-height: 0` is what makes it scroll rather than grow. A flex item's automatic
           minimum is its content, so a scroll view full of rows refuses to be shorter than all of
           them and pushes the panel's own bottom off screen — the bar never appears and the last
           component is unreachable. The same rule `inspector-editor` follows for width. */
        inspector > scroll-view { flex-grow: 1; min-height: 0; }

        /* The rows are the content and the content does not shrink: a body that shrank to fit the
           viewport is a body with no overflow, which is a scroll view with nothing to scroll. */
        inspector-body { flex-direction: column; flex-shrink: 0; }

        /* ── A row ──────────────────────────────────────────────────────────────
           Label, editor, reset — and a minimum height, so a row holding a check box
           is as tall as the one holding a text field and the labels down the left
           are evenly spaced rather than following whatever their editor came to. */
        inspector-row {
            flex-direction: row;
            align-items: center;
            gap: 8px;
            padding: 2px 6px;
            min-height: 26px;
            border-radius: 6px;
        }

        inspector-row.filtered { display: none; }
        inspector-row:hover { background-color: var(--surface-hover, var(--surface-sunken)); }

        /* A share of the width rather than a fixed one, so a narrow panel gives the editor room
           instead of clipping it, with a floor a two-word name still fits inside. */
        inspector-label {
            width: 34%;
            min-width: 72px;
            flex-shrink: 0;
            color: var(--text-muted);
        }

        /* An override is the one thing a row says about itself, and it says it by not being muted —
           a mark that survives every palette, which a chosen colour would not. */
        inspector-row.overridden inspector-label { color: var(--text); }

        /* ⚠ `min-width: 0` is load-bearing. A flex item's automatic minimum is its content, and a
           number box's text is deliberately unshrinkable (`field-text` sets `flex-shrink: 0`), so
           without this the three boxes of a vector refuse to narrow and the row overflows the
           panel instead of the boxes clipping their own text. */
        inspector-editor {
            flex-grow: 1;
            flex-direction: row;
            align-items: center;
            min-width: 0;
        }

        inspector-editor textbox, inspector-editor textarea, inspector-editor numeric-input,
        inspector-editor select, inspector-editor multi-select, inspector-editor slider,
        inspector-editor combo-box, inspector-editor color-picker, inspector-editor curve-editor,
        inspector-editor asset-field {
            flex-grow: 1;
            min-width: 0;
        }

        /* The name grows and the two buttons do not, so a long asset name is what gets clipped
           rather than the picker being pushed out of the panel. */
        asset-field { flex-direction: row; align-items: center; gap: 4px; }
        .asset-name { flex-grow: 1; min-width: 0; }
        asset-field > icon-button { flex-shrink: 0; }

        /* Right of the editor and out of the tab order — it appears and disappears as the value
           moves off and onto the type's default, and a row whose width jumped when it did would
           make every neighbouring editor twitch. */
        inspector-row > icon-button { flex-shrink: 0; }

        /* ── Fields ─────────────────────────────────────────────────────────────
           Sunken, denser than the control set's default, and the placeholder's offset follows the
           padding — it is positioned absolutely and would otherwise sit two pixels off the text it
           stands in for. */
        inspector textbox, inspector textarea, inspector numeric-input, inspector search-box,
        inspector select, inspector multi-select, inspector combo-box {
            padding: 3px 8px;
            border-radius: 6px;
            background-color: var(--surface-sunken);
        }

        /* ⚠ A placeholder is positioned absolutely, so its offset is a number rather than a
           consequence of the padding — narrowing a field without moving it leaves the grey text two
           pixels right of where the real text starts. The search box needs its own, because the
           magnifier is laid out before the text and an offset that ignores it draws "Search"
           through the icon. */
        inspector field-placeholder { left: 8px; }
        inspector search-box field-placeholder { left: 28px; }

        /* ── Sections ───────────────────────────────────────────────────────────
           ⚠ The indent goes. `expander-content` is indented by twenty pixels for prose, and a
           [Header] does not start a nested thing — it names a group of members that are siblings of
           the ungrouped ones above it. Left as it comes, "Name" and "Position" are labels in two
           different columns of the same panel. */
        inspector expander { border-width: 0px 0px 1px 0px; border-color: var(--border); }
        inspector expander-header { padding: 7px 6px; }
        inspector expander-content { flex-direction: column; padding: 0px 0px 8px 0px; }

        /* ── Vectors ────────────────────────────────────────────────────────────
           ⚠ `flex-basis: 0px` on every component, not just a grow. A flex item's base size is its
           content, so three boxes showing "0", "1.5" and "-12.25" would be three different widths —
           and X would move as the number in it changed. Zero basis makes the row a set of equal
           columns whatever is in them. */
        vector-editor { flex-direction: row; flex-grow: 1; gap: 4px; min-width: 0; }

        vector-component {
            flex-direction: row;
            align-items: center;
            flex-grow: 1;
            flex-basis: 0px;
            min-width: 0;
            gap: 4px;
        }

        vector-component > text { flex-shrink: 0; color: var(--text-muted); font-size: 0.85em; }
        vector-component numeric-input { flex-grow: 1; flex-basis: 0px; min-width: 0; }

        /* The axis colours every 3D application uses, and they are worth having: the boxes are
           otherwise three identical fields and the letter beside them is one glyph wide. Literals
           rather than tokens — an axis is red, green and blue in a dark theme and in a light one,
           because it is naming X, Y and Z rather than following a palette. */
        vector-component.axis-x > text { color: #f2696e; }
        vector-component.axis-y > text { color: #7ece6b; }
        vector-component.axis-z > text { color: #58a6ff; }
        vector-component.axis-w > text { color: var(--text-muted); }

        /* ── Nested objects and lists ───────────────────────────────────────────
           ⚠ The row's own label goes and the row stacks. A nested object is a block of rows rather
           than a value, so it cannot sit in the editor column beside a name — the foldout carries
           the name instead, and a row that kept both would say it twice with the second copy
           squeezed into a third of the panel. */
        inspector-row.nested { flex-direction: column; align-items: stretch; padding: 0px; }
        inspector-row.nested > inspector-label { display: none; }
        inspector-row.nested > inspector-editor { width: 100%; }
        inspector-row.nested:hover { background-color: transparent; }

        composite-editor { flex-direction: column; flex-grow: 1; min-width: 0; }

        /* Indented after all, unlike a [Header]'s group — these members *are* a nested thing, and the
           step is what says the four rows under "Bounds" belong to it rather than to the type. */
        composite-editor expander-content { padding: 0px 0px 4px 10px; }
        composite-editor expander { border-width: 0px; }

        /* The element's own buttons, right of its editor and out of the tab order. Muted until the
           row is hovered, because three buttons per element at full contrast is a wall of chrome
           down the side of a list nobody is editing. */
        .list-up, .list-down, .list-remove { flex-shrink: 0; opacity: 0.35; }
        inspector-row:hover .list-up, inspector-row:hover .list-down, inspector-row:hover .list-remove {
            opacity: 1;
        }

        .list-add { align-self: flex-start; margin: 4px 0px 0px 0px; }

        /* The count, and whatever the last resort drew. Muted and small: it is a statement about the
           value rather than a way of changing it. */
        .property-readonly { color: var(--text-muted); font-size: 0.9em; }
        """;
}

/// <summary>The content browser's own two rules, which have nowhere better to live.</summary>
/// <remarks>
///     ⚠ <b>Here rather than in the shell's theme because the browser is the application's panel and
///     the shell knows nothing about it</b> — the same reason this assembly's sheet is loaded by the
///     application rather than by <c>EditorShell</c>. Two rules is not worth a fifth stylesheet.
/// </remarks>
public static class BrowserTheme {
    /// <summary>Adds the sheet to a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text.</summary>
    public static string Css => Sheet;

    const string Sheet = """
        /* The search box takes what is left and the type filter keeps its width, so a long importer
           name is what gets clipped rather than the search field disappearing. */
        browser-filters { flex-direction: row; align-items: center; gap: 6px; flex-shrink: 0; padding: 2px; }
        browser-filters > search-box { flex-grow: 1; min-width: 0; }
        browser-filters > select { flex-shrink: 0; width: 140px; }
        browser-filters > .browser-view { flex-shrink: 0; }

        /* ── The grid ───────────────────────────────────────────────────────────
           ⚠ `flex-wrap` is the whole of the layout, and the tiles have a fixed basis so that
           a row holds as many as fit rather than as many as the widest name allows. A grid
           whose columns moved as you typed in the search box is one you cannot aim at. */
        asset-grid { flex-direction: column; flex-grow: 1; min-height: 0; gap: 4px; }
        asset-grid.hidden { display: none; }

        asset-path { flex-direction: row; flex-wrap: wrap; align-items: center; flex-shrink: 0; gap: 2px; }
        .asset-crumb { flex-shrink: 0; }

        /* ⚠ The tile size is a custom property rather than a rule on the tile, because the grid
           does the placing: it needs the number to work out how many fit across and where item
           40 000 is, and a size the control could only discover by measuring an element would
           defeat the whole arrangement. */
        asset-tiles { --tile-width: 82px; --tile-height: 84px; flex-grow: 1; min-height: 0; }

        /* Absolutely positioned by the grid, so the tile styles what is *inside* it and nothing
           about where it is. */
        asset-tile {
            position: absolute;
            flex-direction: column;
            align-items: center;
            padding: 8px 4px;
            gap: 6px;
            border-radius: var(--radius-row, 6px);
        }

        asset-tile.parked { display: none; }

        asset-tile:hover { background-color: var(--surface-hover, var(--surface-sunken)); }

        /* The same `--accent-deep` the tree rows use, so a selection reads the same in both views. */
        asset-tile:checked { background-color: var(--accent-deep, var(--accent)); color: #ffffff; }
        asset-tile:checked icon { color: #ffffff; }

        /* ⚠ Bigger than a row's icon by a lot. A grid whose glyphs are row-sized is a list with
           gaps in it — the size *is* the affordance, and it is what makes the colour readable
           from across the panel. */
        asset-tile > icon { width: 40px; height: 40px; }

        /* The picture takes the glyph's place and its size, so a tile is the same shape whether its
           asset has one or not — a grid whose rows changed height as thumbnails arrived would
           reflow under the pointer. */
        asset-tile > image { width: 40px; height: 40px; }
        asset-tile > .hidden { display: none; }

        /* Two lines and then clipped: a tile whose height followed its name would make every row of
           the grid a different height and the whole thing impossible to scan. */
        asset-caption { text-align: center; font-size: 0.85em; max-height: 30px; overflow: hidden; }


        /* ── The component foldouts ─────────────────────────────────────────────
           One block per component, under the inspector's own rows and separated from them
           by a rule, because "what this entity is" and "what is on it" are two lists and a
           panel that ran them together reads as one long one. */
        components { flex-direction: column; flex-shrink: 0; }

        /* ⚠ Said out loud, because the initial value of `flex-direction` is `row` and nothing else
           here sets it. `LayoutStyle.Default` is column — which is what a document with no
           stylesheet gets — but every styled element is built from the CSS initial instead, so a
           part with no rule of its own lays its children out across rather than down. The symptom
           was three component foldouts side by side, each squeezed to a third of the panel. */
        component-list { flex-direction: column; flex-shrink: 0; }

        expander.component { border-width: 1px 0px 0px 0px; border-color: var(--border); }
        expander.component > expander-header { flex-direction: row; align-items: center; }

        /* ⚠ The remove button is faint until the header is hovered, and it is inside the header
           rather than beside it — a component's Remove has to be unmistakably *that* component's,
           and a column of identical crosses down the right of the panel is not. */
        .remove-component { flex-shrink: 0; margin-left: auto; opacity: 0.2; }
        expander-header:hover .remove-component { opacity: 1; }

        .add-component { align-self: stretch; margin: 8px 4px 4px 4px; }
        .add-component.hidden { display: none; }
        """;
}
