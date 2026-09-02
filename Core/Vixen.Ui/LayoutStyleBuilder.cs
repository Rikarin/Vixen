// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Turns what the cascade decided into what the flexbox engine reads.</summary>
/// <remarks>
///     <para>
///         Two finished subsystems that do not reference each other:
///         <c>Vixen.Ui.Styling</c> resolves declarations without knowing what a length measures, and
///         <c>Vixen.Ui.Layout</c> measures without knowing where its numbers came from. This is the
///         wire between them, and it is the first thing <c>Vixen.Ui</c> owes — an element tree
///         cannot be built until a computed style can become a layout style.
///     </para>
///     <para>
///         <b>Font size is resolved first and separately, because it is the thing everything else is
///         relative to.</b> On <c>font-size</c> itself, <c>em</c> and <c>%</c> mean the
///         <i>parent's</i> size; on every other property they mean the element's own. Conflating the
///         two compounds down the tree — three nested <c>font-size: 1.2em</c> come out at 1.2× rather
///         than 1.728×, and the error grows with depth, so it looks like a rendering quirk rather
///         than an arithmetic one.
///     </para>
///     <para>
///         ⚠ <b>Percentages are not resolved here.</b> A percentage measures against the containing
///         block, which only the layout pass knows — so <c>50%</c> is carried through to
///         <see cref="LayoutUnit.Percent" /> untouched. This is the one place where doing less is
///         the correct behaviour rather than an omission.
///     </para>
/// </remarks>
public sealed class LayoutStyleBuilder {
    /// <summary>What an element with no declarations gets, per CSS rather than per Yoga.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="LayoutStyle.Default" /> is Yoga's initial state, and it differs from
    ///         CSS's in four places.</b> That is correct where it lives — <c>Vixen.Ui.Layout</c> is
    ///         judged by Yoga's own conformance suite and has to start where Yoga starts — and it is
    ///         wrong here, because a VCSS author writes CSS. The two specifications disagree about
    ///         <c>flex-direction</c> (<c>column</c> against <c>row</c>), <c>align-content</c>
    ///         (<c>flex-start</c> against <c>stretch</c>), <c>position</c> (<c>relative</c> against
    ///         <c>static</c>) and <c>box-sizing</c> (<c>border-box</c> against <c>content-box</c>).
    ///     </para>
    ///     <para>
    ///         Starting from the wrong one is the sort of mistake that produces a stylesheet full of
    ///         redundant declarations: the author writes <c>flex-direction: row</c> everywhere
    ///         because leaving it out gives a column, decides the engine is quirky, and never
    ///         reports it.
    ///     </para>
    ///     <para>
    ///         Two deliberate departures from CSS remain. <c>display</c> starts at <c>flex</c>
    ///         rather than at CSS's <c>inline</c>, and ⚠ <b>that is now a choice rather than a
    ///         limitation</b>: doc 43 § B3 gave the engine an inline formatting context, so
    ///         <c>inline</c> is a member it could start at. It does not, because an element with no
    ///         <c>display</c> at all is far more often a container somebody forgot to declare than a
    ///         run of text, and defaulting to <c>inline</c> would shrink-to-fit every undeclared box
    ///         in a document written against this engine's own conventions.
    ///         ⚠ It is now a real seven-way choice: <c>block</c> arrived with § B1, <c>grid</c> with
    ///         § B2, and <c>inline</c>, <c>inline-block</c> and <c>inline-flex</c> with § B3 — so a
    ///         stylesheet that says <c>display: block</c> gets stacking and margin collapsing rather
    ///         than a flex row, one that says <c>display: grid</c> gets track sizing, and one that
    ///         says <c>display: inline-block</c> gets a box that shares its line instead of taking
    ///         it. And <c>box-sizing: border-box</c>, which most
    ///         UI work wants, belongs in a user-agent stylesheet where an author can see and
    ///         override it — not baked in here where they cannot.
    ///     </para>
    /// </remarks>
    public static readonly LayoutStyle CssInitial = CreateCssInitial();

    readonly StyleValueParser parser;
    readonly NameTable values;
    readonly Properties names;
    readonly Keywords keywords;
    readonly VariableLengthProperty[] variableLength;
    readonly List<SelectorDiagnostic> diagnostics = [];

    /// <summary>The property table, kept so that a refusal can name the property a human wrote.</summary>
    readonly NameTable propertyNames;

    /// <summary>Creates a builder over a style engine's name tables.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywordNames">The table identifiers are interned in.</param>
    /// <remarks>
    ///     Every name is interned once here rather than looked up per element per frame, which is
    ///     the same trade <see cref="Animator" /> makes and for the same reason: the cascade's whole
    ///     performance story is that a property is an <see cref="int" />.
    /// </remarks>
    public LayoutStyleBuilder(NameTable properties, NameTable values, NameTable keywordNames) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywordNames);

        parser = new StyleValueParser(values, keywordNames);
        this.values = values;
        propertyNames = properties;
        names = new Properties(properties);
        keywords = new Keywords(keywordNames);

        variableLength = [
            new TrackListProperty(names.GridTemplateColumns, GridTrackSlot.Columns),
            new TrackListProperty(names.GridTemplateRows, GridTrackSlot.Rows),
            new TrackListProperty(names.GridAutoColumns, GridTrackSlot.AutoColumns),
            new TrackListProperty(names.GridAutoRows, GridTrackSlot.AutoRows),
            new AreaTemplateProperty(names.GridTemplateAreas),
            new NamedPlacementProperty(names.GridColumnStart, Edge.Left),
            new NamedPlacementProperty(names.GridColumnEnd, Edge.Right),
            new NamedPlacementProperty(names.GridRowStart, Edge.Top),
            new NamedPlacementProperty(names.GridRowEnd, Edge.Bottom)
        ];
    }

    /// <summary>What this bridge could not read, and why.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same shape as <c>StyleSheetLoader.Diagnostics</c>, and deliberately so — but
    ///         it answers a question that list cannot.</b> The loader reports what it could not
    ///         <i>parse</i>; this reports what parsed as CSS and then meant nothing here. Those are
    ///         different failures: <c>grid-template-columns: 4furlongs</c> is a perfectly well-formed
    ///         declaration that ExCSS hands through untouched, because ExCSS 4.3.2 has never heard of
    ///         the property and so validates nothing about its value.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reported rather than thrown, because this runs inside a frame.</b> An exception
    ///         out of the style pass takes down the surface over a typo in a stylesheet, and the
    ///         cascade's whole premise is that a declaration it cannot use is survivable. Reported
    ///         rather than ignored, because the alternative is what this list exists to end: a track
    ///         list that half-parses is a one-column grid, which reads as a layout bug in a panel
    ///         rather than as a stylesheet the engine refused.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<SelectorDiagnostic> Diagnostics => diagnostics;

    /// <summary>Forgets every refusal recorded so far.</summary>
    /// <remarks>
    ///     ⚠ The list is per-builder and a builder outlives a frame, so a caller that watches it has
    ///     to be able to say "from here". Without this a hot reload could only ever see the union of
    ///     every refusal since the document was created and could never tell a new one from an old.
    /// </remarks>
    public void ClearDiagnostics() => diagnostics.Clear();

    /// <summary>Records a refused declaration, once per distinct declaration.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>These carry no <c>Rule</c>, and it is not an oversight — the information does not
    ///         reach here.</b> <see cref="SelectorDiagnostic" /> was widened so that a refusal naming
    ///         a fragment can also name the rule the fragment was written in, and the loader and the
    ///         compiler both pass one. This producer cannot: it is handed a
    ///         <see cref="ComputedStyle" />, which is two <c>int[]</c>s of interned property and
    ///         value ids with every trace of where they came from already cascaded away. By the time
    ///         a declaration is refused here, the rule that declared it, its origin, its layer and
    ///         its specificity have all been resolved and discarded.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And carrying provenance to get it would cost more than it is worth.</b> It would
    ///         mean a third parallel array of rule ids on every <see cref="ComputedStyle" />, which is
    ///         the object the cascade <i>interns</i> — ten thousand identical cells are one entry
    ///         today precisely because the style is nothing but its properties and values, and a rule
    ///         id per declaration would make two cells styled by different rules into two entries
    ///         however identical they look.
    ///     </para>
    ///     <para>
    ///         What it can do instead, and does, is make <c>Text</c> a locator in its own right:
    ///         <c>grid-template-rows: 4furlongs</c> is the declaration as the author wrote it, which
    ///         is greppable across a project's sheets in a way a bare <c>::before</c> is not. That is
    ///         the reason the bridge's half of this was already answerable and the compiler's was not.
    ///     </para>
    /// </remarks>
    void Refuse(int property, string value, string reason) {
        var text = $"{propertyNames.NameOf(property)}: {value}";

        // ⚠ Deduplicated, because this is reached from the per-element style pass. One bad
        // declaration in a user-agent stylesheet applies to every element that matches it, and a
        // list that grew a line per element per restyle would be a leak rather than a diagnostic.
        foreach (var existing in diagnostics) {
            if (existing.Text == text) {
                return;
            }
        }

        diagnostics.Add(new SelectorDiagnostic(text, reason));
    }

    /// <summary>Resolves an element's font size, which everything else is measured against.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <param name="parentFontSize">The parent's already-resolved font size, in pixels.</param>
    /// <param name="context">The surface's context. Its font size is ignored.</param>
    /// <returns>The element's font size in pixels.</returns>
    /// <remarks>
    ///     ⚠ The context handed to the parser is the one with the <i>parent's</i> font size in it,
    ///     which is what makes <c>font-size: 1.2em</c> mean "a fifth larger than my parent" rather
    ///     than the circular "a fifth larger than myself".
    /// </remarks>
    public float ResolveFontSize(ComputedStyle style, float parentFontSize, in LengthContext context) {
        ArgumentNullException.ThrowIfNull(style);

        if (!TryValue(style, names.FontSize, out var value)) {
            return parentFontSize;
        }

        var parent = context.WithFontSize(parentFontSize);
        var length = parent.ToLength(value);

        return length.Unit switch {
            LayoutUnit.Point => length.Value,
            LayoutUnit.Percent => parentFontSize * length.Value / 100f,
            _ => parentFontSize
        };
    }

    /// <summary>The id of <c>letter-spacing</c>, for the computed-value stage.</summary>
    public int LetterSpacingId => names.LetterSpacing;

    /// <summary>The id of <c>word-spacing</c>.</summary>
    public int WordSpacingId => names.WordSpacing;

    /// <summary>The id of <c>text-indent</c>.</summary>
    public int TextIndentId => names.TextIndent;

    /// <summary>Resolves one absolute text length against this element's own font size.</summary>
    /// <param name="style">The computed style.</param>
    /// <param name="property">Which of the three.</param>
    /// <param name="context">The context, with this element's resolved font size already in it.</param>
    /// <param name="points">Receives the length.</param>
    /// <returns>Whether the element declared it at all.</returns>
    /// <remarks>
    ///     ⚠ A percentage is refused rather than resolved. <c>letter-spacing</c> takes no percentage
    ///     in CSS, and <c>text-indent</c>'s resolves against the containing block's width — which is a
    ///     layout result and is not known here. Answering with a wrong number would be worse than
    ///     answering with the initial value, and it is recorded rather than silently approximated.
    /// </remarks>
    public bool TryTextLength(ComputedStyle style, int property, in LengthContext context, out float points) {
        points = 0f;

        if (!TryValue(style, property, out var value)) {
            return false;
        }

        var length = context.ToLength(value);

        if (length.Unit != LayoutUnit.Point) {
            return false;
        }

        points = length.Value;
        return true;
    }

    /// <summary>Resolves <c>line-height</c>, keeping a bare number as a number.</summary>
    /// <param name="style">The computed style.</param>
    /// <param name="context">The context, with this element's resolved font size in it.</param>
    /// <param name="factor">The multiplier, when the declaration was a bare number.</param>
    /// <param name="points">The length, when it was a length.</param>
    /// <returns>Whether the element declared it.</returns>
    /// <remarks>
    ///     ⚠ <b>The bare number is not a shorthand for <c>1.5em</c> and the difference only shows up
    ///     under inheritance.</b> On the element that declares it the two are identical; on a
    ///     descendant with a different font size the number re-resolves and the length does not,
    ///     which is exactly what CSS intends and why <c>ComputedText</c> carries both.
    /// </remarks>
    public bool TryLineHeight(ComputedStyle style, in LengthContext context, out float? factor, out float points) {
        factor = null;
        points = 0f;

        if (!TryValue(style, names.LineHeight, out var value)) {
            return false;
        }

        // A bare number, which is the form that stays a number. Zero is excluded because
        // `LengthContext.ToLength` already reads a zero number as the length zero, and
        // `line-height: 0` means a zero-height line box rather than a zero multiplier — the same
        // answer either way, and taking the length path keeps it that way.
        if (value is { Kind: StyleValueKind.Number, Number: not 0f }) {
            factor = value.Number;
            return true;
        }

        var length = context.ToLength(value);

        if (length.Unit != LayoutUnit.Point) {
            return false;
        }

        points = length.Value;
        return true;
    }

    /// <summary>Builds the layout style for one element.</summary>
    /// <param name="style">Its computed style.</param>
    /// <param name="context">
    ///     The resolution context, with <see cref="LengthContext.FontSize" /> already set to this
    ///     element's own resolved size.
    /// </param>
    /// <returns>The layout style, starting from CSS's initial values.</returns>
    public LayoutStyle Build(ComputedStyle style, in LengthContext context) {
        ArgumentNullException.ThrowIfNull(style);

        var result = CssInitial;

        ApplyKeywords(style, ref result);
        ApplyNumbers(style, ref result);
        ApplyDimensions(style, in context, ref result);
        ApplyEdges(style, in context, ref result);
        ApplyGaps(style, in context, ref result);
        ApplyScrollbar(style, in context, ref result);
        ApplyPlacements(style, ref result);

        return result;
    }

    /// <summary>Applies the style properties whose values are lists rather than fixed-size fields.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <param name="tree">The tree the node belongs to.</param>
    /// <param name="node">The node, which is what these properties need and <see cref="Build" /> lacks.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is a second call rather than a wider <see cref="Build" />, and the reason is
    ///         ownership rather than convenience.</b> A track list is stored in the tree's
    ///         <c>TrackArena</c> behind an <c>(offset, count)</c> handle that belongs to the
    ///         <i>node</i>, not to the value — which is exactly why
    ///         <see cref="LayoutTree.SetStyle" /> deliberately carries those four handles across a
    ///         whole-style write instead of overwriting them. A <see cref="Build" /> that returned
    ///         them would be returning a lease on another object's memory, and two elements that
    ///         happened to resolve alike would name one block and free it twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Call it after <see cref="LayoutTree.SetStyle" />, not before.</b> `SetStyle`
    ///         preserves the handles it finds, so applying tracks first works — but it also compares
    ///         the whole struct to decide whether anything changed, and a node whose only change was
    ///         its template would be compared against a style that already had it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An absent declaration resets rather than being skipped.</b> This is the half that
    ///         is easy to leave out and impossible to see: an element that had
    ///         <c>grid-template-columns</c> and no longer does keeps its old tracks forever, because
    ///         nothing else in the store will ever clear them. The cascade says absent means initial,
    ///         so absent has to mean a write.
    ///     </para>
    ///     <para>
    ///         The mechanism generalises to any property whose value is a list: a new one is a class
    ///         with a grammar and a store call, and the driver below neither knows nor asks what it
    ///         parses. ⚠ <b><c>grid-template-areas</c> was named here as the thing this was shaped
    ///         for and is now one of its entries</b>, at the promised cost of one subclass and one
    ///         line. It also showed what the mechanism is really for, which is not length: a named
    ///         placement is <i>one word</i> and still cannot ride in a <see cref="LayoutStyle" />,
    ///         because a name resolves against the container's template and so belongs to the node.
    ///         Named grid <i>lines</i> in a track list are still out of scope.
    ///     </para>
    /// </remarks>
    public void ApplyVariableLength(ComputedStyle style, LayoutTree tree, LayoutNodeId node) {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(tree);

        foreach (var property in variableLength) {
            if (!style.TryGet(property.Property, out var id)) {
                property.Reset(tree, node);
                continue;
            }

            var text = values.NameOf(id);

            if (property.TryApply(text, tree, node, out var refusal)) {
                continue;
            }

            Refuse(property.Property, text, refusal);

            // CSS drops an invalid declaration whole rather than keeping the part that parsed, and
            // the node has to end up where it would have been had the line never been written.
            property.Reset(tree, node);
        }
    }

    static LayoutStyle CreateCssInitial() {
        var style = LayoutStyle.Default;

        style.FlexDirection = FlexDirection.Row;
        style.AlignContent = Align.Stretch;
        style.PositionType = PositionType.Static;
        style.BoxSizing = BoxSizing.ContentBox;

        return style;
    }

    void ApplyKeywords(ComputedStyle style, ref LayoutStyle result) {
        if (TryKeyword(style, names.Direction, keywords.Directions, out Direction direction)) {
            result.Direction = direction;
        }

        if (TryKeyword(style, names.FlexDirection, keywords.FlexDirections, out FlexDirection flexDirection)) {
            result.FlexDirection = flexDirection;
        }

        if (TryKeyword(style, names.JustifyContent, keywords.Justifications, out Justify justify)) {
            result.JustifyContent = justify;
        }

        if (TryKeyword(style, names.AlignContent, keywords.Alignments, out Align alignContent)) {
            result.AlignContent = alignContent;
        }

        if (TryKeyword(style, names.AlignItems, keywords.Alignments, out Align alignItems)) {
            result.AlignItems = alignItems;
        }

        if (TryKeyword(style, names.AlignSelf, keywords.Alignments, out Align alignSelf)) {
            result.AlignSelf = alignSelf;
        }

        if (TryKeyword(style, names.Position, keywords.Positions, out PositionType position)) {
            result.PositionType = position;
        }

        if (TryKeyword(style, names.FlexWrap, keywords.Wraps, out Wrap wrap)) {
            result.FlexWrap = wrap;
        }

        // ⚠ <b>The shorthand first and each longhand over it, rather than in source order.</b> Nothing
        // expands `overflow` into its two longhands on the way in — ExCSS treats all three as plain
        // properties and `ShorthandExpansion` is not wired to the cascade — so by the time a computed
        // style is built, "which was written last" is a question that no longer has an answer. The
        // rule here is the one every stylesheet in this repository is already written against, and the
        // one CSS agrees with whenever the longhand really did come last: a named axis wins.
        if (TryKeyword(style, names.Overflow, keywords.Overflows, out Overflow overflow)) {
            result.OverflowX = overflow;
            result.OverflowY = overflow;
        }

        if (TryKeyword(style, names.OverflowX, keywords.Overflows, out Overflow overflowX)) {
            result.OverflowX = overflowX;
        }

        if (TryKeyword(style, names.OverflowY, keywords.Overflows, out Overflow overflowY)) {
            result.OverflowY = overflowY;
        }

        if (TryKeyword(style, names.Display, keywords.Displays, out Display display)) {
            result.Display = display;
        }

        if (TryKeyword(style, names.Float, keywords.Floats, out FloatSide floatSide)) {
            result.Float = floatSide;
        }

        if (TryKeyword(style, names.Clear, keywords.Clears, out Clear clear)) {
            result.Clear = clear;
        }

        if (TryKeyword(style, names.VerticalAlign, keywords.VerticalAligns, out VerticalAlign verticalAlign)) {
            result.VerticalAlign = verticalAlign;
        }

        if (TryKeyword(style, names.BoxSizing, keywords.BoxSizings, out BoxSizing boxSizing)) {
            result.BoxSizing = boxSizing;
        }

        if (TryKeyword(style, names.GridAutoFlow, keywords.GridAutoFlows, out GridAutoFlow autoFlow)) {
            result.GridAutoFlow = autoFlow;
        }

        // ⚠ <b>Grid's inline-axis alignment reuses the flexbox <see cref="Align" /> table, and the
        // member names lie about the axis rather than about the value.</b> `Align.FlexStart` is the
        // inline start here, which is what `LayoutStyle.JustifyItems` documents; sharing the table is
        // what makes `justify-items: center` and `align-items: center` mean the same word.
        if (TryKeyword(style, names.JustifyItems, keywords.Alignments, out Align justifyItems)) {
            result.JustifyItems = justifyItems;
        }

        if (TryKeyword(style, names.JustifySelf, keywords.Alignments, out Align justifySelf)) {
            result.JustifySelf = justifySelf;
        }
    }

    /// <summary>Applies the six placement properties, the cascade having already ordered them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This no longer decides the precedence, and the fact that it once did was a
    ///         defect.</b> <see cref="Vixen.Ui.Styling.ShorthandExpansion" /> now splits
    ///         <c>grid-column</c> and <c>grid-row</c> into their two longhands at load, so both halves
    ///         of the question reach the cascade as comparable declarations and the one written last
    ///         wins — which is the answer CSS gives and the one this method had no way to compute.
    ///         Applying the shorthand first and each longhand over it made a longhand beat a shorthand
    ///         <i>whatever order they were declared in</i>: <c>row-span-full</c> emits
    ///         <c>grid-row: 1 / -1</c>, and on any element whose theme sheet also set
    ///         <c>grid-row-start</c> it was discarded in silence and the item auto-placed into a real
    ///         cell — the exact failure the paragraph below says this bridge exists to stop.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two shorthand branches stay, and they are now a fallback rather than a
    ///         rule.</b> A shorthand only reaches here when the expander refused it — <c>grid-column:
    ///         var(--place)</c>, whose <c>var()</c> may itself hold the slash, and the two-slash
    ///         <c>grid-area</c> form written under the wrong name. The loader has already reported
    ///         that refusal, so reading what can be read beats dropping it. Their order against the
    ///         longhands is unchanged and is still <c>overflow</c>'s above: the cascade could not
    ///         order a declaration it never took apart, so somebody has to choose, and choosing the
    ///         same way twice in one file is worth more than choosing differently for a case that
    ///         needs a <c>var()</c> holding a slash to arise at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A refused placement is reported and then left at <c>auto</c>.</b> Leaving it
    ///         silently is the failure this bridge exists to stop being possible: an item whose
    ///         <c>grid-column</c> did not parse is auto-placed into a real cell, so the grid looks
    ///         built rather than broken and nothing anywhere names the declaration that was dropped.
    ///     </para>
    /// </remarks>
    void ApplyPlacements(ComputedStyle style, ref LayoutStyle result) {
        if (TryPlacementShorthand(style, names.GridColumn, out var columnStart, out var columnEnd)) {
            result.GridColumnStart = columnStart;
            result.GridColumnEnd = columnEnd;
        }

        if (TryPlacementShorthand(style, names.GridRow, out var rowStart, out var rowEnd)) {
            result.GridRowStart = rowStart;
            result.GridRowEnd = rowEnd;
        }

        if (TryPlacement(style, names.GridColumnStart, out var placement)) {
            result.GridColumnStart = placement;
        }

        if (TryPlacement(style, names.GridColumnEnd, out placement)) {
            result.GridColumnEnd = placement;
        }

        if (TryPlacement(style, names.GridRowStart, out placement)) {
            result.GridRowStart = placement;
        }

        if (TryPlacement(style, names.GridRowEnd, out placement)) {
            result.GridRowEnd = placement;
        }
    }

    bool TryPlacement(ComputedStyle style, int property, out GridPlacement placement) {
        placement = GridPlacement.Auto;

        if (!style.TryGet(property, out var id)) {
            return false;
        }

        var text = values.NameOf(id);

        if (GridPlacement.TryParse(text, out placement)) {
            return true;
        }

        // ⚠ <b>Silent here, and loud one pass later.</b> Since named areas landed, these four
        // longhands are read twice — as a line by this and as an area's name by
        // <see cref="NamedPlacementProperty" /> — and exactly one reading can be right. Reporting
        // both refusals would put two diagnostics on one declaration and, worse, would report
        // `grid-row-start: header` as broken on every document that uses a named area. The other
        // reader knows both grammars and is the one that speaks.
        return false;
    }

    bool TryPlacementShorthand(ComputedStyle style, int property, out GridPlacement start, out GridPlacement end) {
        start = GridPlacement.Auto;
        end = GridPlacement.Auto;

        if (!style.TryGet(property, out var id)) {
            return false;
        }

        var text = values.NameOf(id);

        if (GridPlacement.TryParseShorthand(text, out start, out end)) {
            return true;
        }

        Refuse(property, text, "not a `<start> / <end>` placement");
        return false;
    }

    void ApplyNumbers(ComputedStyle style, ref LayoutStyle result) {
        if (TryNumber(style, names.Flex, out var flex)) {
            result.Flex = flex;
        }

        if (TryNumber(style, names.FlexGrow, out var grow)) {
            result.FlexGrow = grow;
        }

        if (TryNumber(style, names.FlexShrink, out var shrink)) {
            result.FlexShrink = shrink;
        }

        // ⚠ <b>A fractional value is dropped rather than rounded, and that is the specification
        // rather than fastidiousness.</b> `order` takes `<integer>`, so `order: 1.5` is an invalid
        // declaration and an invalid declaration leaves the initial value — the same rule `Set`
        // applies to lengths, and the reason it matters is that rounding would put the item in
        // ordinal group 2 where every browser puts it in group 0. The cascade has no integer kind
        // (`order: 2` arrives as the float 2), so integrality is checked here or nowhere.
        if (TryNumber(style, names.Order, out var order) && float.IsInteger(order)) {
            result.Order = (int) order;
        }

        if (TryNumber(style, names.AspectRatio, out var bare)) {
            result.AspectRatio = bare;
        } else if (TryRatio(style, names.AspectRatio, out var ratio)) {
            result.AspectRatio = ratio;
        }
    }

    /// <summary>Reads <c>a / b</c>, which no shared parser produces.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ ExCSS normalises <c>aspect-ratio: 16 / 9</c> to the text <c>16/9</c>, with the
    ///         spaces gone — so the value parser, which splits on whitespace, sees one token and
    ///         cannot make sense of it. The declaration comes back <c>Unknown</c> and every ratio in
    ///         the document is silently dropped.
    ///     </para>
    ///     <para>
    ///         Read here rather than by teaching <see cref="StyleValueParser" /> that <c>/</c>
    ///         separates values. It does in CSS — <c>font: 12px/1.5</c>, <c>grid-area: 1/2/3/4</c> —
    ///         but making it a general separator changes how every shorthand parses, which is a
    ///         wider change than one property is worth and would need its own tests.
    ///     </para>
    /// </remarks>
    bool TryRatio(ComputedStyle style, int property, out float ratio) {
        ratio = 0f;

        if (!style.TryGet(property, out var id)) {
            return false;
        }

        var text = values.NameOf(id).AsSpan();
        var slash = text.IndexOf('/');

        if (slash < 0
            || !float.TryParse(text[..slash].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !float.TryParse(text[(slash + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || height == 0f) {
            return false;
        }

        ratio = width / height;
        return true;
    }

    /// <summary>Writes the six size slots, which are the only ones a content keyword may land in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="SetSizeLength" /> and not <see cref="SetLength" />, and the split is the
    ///         grammar rather than a convenience.</b> CSS Sizing § 5's <c>min-content</c>,
    ///         <c>max-content</c> and <c>fit-content</c> are values of <c>width</c>, <c>height</c> and
    ///         their four bounds and of nothing else — a <c>margin: max-content</c> or a
    ///         <c>gap: fit-content</c> is not a narrower thing than CSS allows, it is invalid. Sharing
    ///         one converter with the edges would have accepted both and handed
    ///         <c>Vixen.Ui.Layout</c> a keyword in a slot whose reader has no measurement to answer it
    ///         with.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>flex-basis</c> keeps the edge converter, deliberately.</b> It takes the same
    ///         three keywords in CSS, and the store has a <see cref="LayoutUnit" /> for them — but a
    ///         basis is a size on the container's MAIN axis, which is not known until layout runs and
    ///         is not a fact about the declaration. <c>LayoutTree.Intrinsic.cs</c> resolves per
    ///         dimension, so it has nothing to hand back here; mapping the keyword would put a value
    ///         in the slot that resolves to NaN, which is the dead-declaration shape this change
    ///         exists to remove. <c>basis-min</c> and its siblings are recorded against that in
    ///         <c>docs/plan/43</c> rather than quietly emitted.
    ///     </para>
    /// </remarks>
    void ApplyDimensions(ComputedStyle style, in LengthContext context, ref LayoutStyle result) {
        SetSizeLength(style, names.Width, in context, ref result.Dimensions[(int) Dimension.Width]);
        SetSizeLength(style, names.Height, in context, ref result.Dimensions[(int) Dimension.Height]);
        SetSizeLength(style, names.MinWidth, in context, ref result.MinDimensions[(int) Dimension.Width]);
        SetSizeLength(style, names.MinHeight, in context, ref result.MinDimensions[(int) Dimension.Height]);
        SetSizeLength(style, names.MaxWidth, in context, ref result.MaxDimensions[(int) Dimension.Width]);
        SetSizeLength(style, names.MaxHeight, in context, ref result.MaxDimensions[(int) Dimension.Height]);
        SetLength(style, names.FlexBasis, in context, ref result.FlexBasis);
    }

    /// <summary>Writes the edge families.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only <c>inset</c> arrives as a shorthand.</b> ExCSS expands <c>margin</c>,
    ///         <c>padding</c>, <c>border-width</c>, <c>gap</c> and <c>flex</c> into longhands while
    ///         parsing, exactly as a browser does, so the cascade never sees those words and the
    ///         document-order problem they would otherwise have does not exist —
    ///         <c>margin-left: 0; margin: 8px</c> gives eight, because by the time the cascade runs
    ///         it is two <c>margin-left</c> declarations and the later one wins.
    ///     </para>
    ///     <para>
    ///         This was checked rather than assumed, and the assumption was wrong: the bridge was
    ///         written to expand the box shorthands itself, and its tests said every one of those
    ///         paths was dead. <c>inset</c> is the exception because ExCSS does not know the
    ///         property, so it passes the text through whole.
    ///     </para>
    /// </remarks>
    void ApplyEdges(ComputedStyle style, in LengthContext context, ref LayoutStyle result) {
        ApplyEdgeLonghands(style, names.Margin, in context, ref result.Margin);
        ApplyEdgeLonghands(style, names.Padding, in context, ref result.Padding);
        ApplyEdgeLonghands(style, names.Border, in context, ref result.Border);

        if (TryValue(style, names.Inset.Shorthand, out var inset)) {
            ApplyEdgeShorthand(inset, in context, ref result.Position);
        }

        ApplyEdgeLonghands(style, names.Inset, in context, ref result.Position);
    }

    /// <summary>Writes the gutters.</summary>
    /// <remarks>
    ///     Only the longhands, because ExCSS expands <c>gap</c> into them — and it gets the order
    ///     right, which is worth knowing since <c>gap: 4px 12px</c> is row then column and the
    ///     enum lists column first. That is exactly the sort of thing that reads correct and
    ///     renders transposed.
    /// </remarks>
    void ApplyGaps(ComputedStyle style, in LengthContext context, ref LayoutStyle result) {
        SetLength(style, names.RowGap, in context, ref result.Gap[(int) Gutter.Row]);
        SetLength(style, names.ColumnGap, in context, ref result.Gap[(int) Gutter.Column]);
    }

    /// <summary>Writes the scrollbar gutter.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A length here, where CSS has a three-valued keyword, and the difference is
    ///         deliberate.</b> The web's <c>scrollbar-width</c> is <c>auto | thin | none</c> because
    ///         the browser owns the widget and only the page's preference is negotiable. Nothing here
    ///         owns one — <c>ScrollView</c> builds its own bar and knows how thick it is — so the
    ///         useful value is the thickness itself. <c>none</c> is spelled <c>0</c> and the keyword
    ///         is accepted for it, because a stylesheet turning a gutter off should not have to know
    ///         that.
    ///     </para>
    ///     <para>
    ///         Inert unless an axis is a scroll container, which is what makes this safe to put in a
    ///         utility layer: <c>scrollbar-15</c> on a box that clips or spills moves nothing. See
    ///         <see cref="LayoutStyle.ScrollbarWidth" />.
    ///     </para>
    /// </remarks>
    void ApplyScrollbar(ComputedStyle style, in LengthContext context, ref LayoutStyle result) {
        if (!TryValue(style, names.ScrollbarWidth, out var value)) {
            return;
        }

        if (value.Kind == StyleValueKind.Keyword && value.Keyword == keywords.None) {
            result.ScrollbarWidth = 0f;
            return;
        }

        var length = context.ToLength(value);
        if (length.IsDefined && length.Unit == LayoutUnit.Point) {
            result.ScrollbarWidth = MathF.Max(0f, length.Value);
        }
    }

    /// <summary>Applies one family's physical and logical longhands.</summary>
    /// <remarks>
    ///     The logical pair is written to the <c>Start</c> and <c>End</c> slots rather than being
    ///     turned into left and right here, because which is which depends on the writing direction
    ///     and the layout store already resolves that when it reads them.
    /// </remarks>
    void ApplyEdgeLonghands(ComputedStyle style, in EdgeNames group, in LengthContext context, ref EdgeLengths edges) {
        SetLength(style, group.Left, in context, ref edges[(int) Edge.Left]);
        SetLength(style, group.Top, in context, ref edges[(int) Edge.Top]);
        SetLength(style, group.Right, in context, ref edges[(int) Edge.Right]);
        SetLength(style, group.Bottom, in context, ref edges[(int) Edge.Bottom]);
        SetLength(style, group.Start, in context, ref edges[(int) Edge.Start]);
        SetLength(style, group.End, in context, ref edges[(int) Edge.End]);
    }

    /// <summary>CSS's one-to-four-value edge shorthand.</summary>
    /// <remarks>
    ///     One value is all four edges; two are vertical then horizontal; three are top, horizontal,
    ///     bottom; four are top, right, bottom, left — clockwise from the top. The two- and
    ///     three-value forms land in the <c>Vertical</c> and <c>Horizontal</c> slots rather than
    ///     being expanded, which is what those slots are for.
    /// </remarks>
    void ApplyEdgeShorthand(StyleValue value, in LengthContext context, ref EdgeLengths edges) {
        if (value.Kind != StyleValueKind.List) {
            Set(ToEdgeLength(value, in context), ref edges[(int) Edge.All]);
            return;
        }

        var parts = value.Items;

        switch (parts.Length) {
            case 2:
                Set(ToEdgeLength(parts[0], in context), ref edges[(int) Edge.Vertical]);
                Set(ToEdgeLength(parts[1], in context), ref edges[(int) Edge.Horizontal]);
                break;

            case 3:
                Set(ToEdgeLength(parts[0], in context), ref edges[(int) Edge.Top]);
                Set(ToEdgeLength(parts[1], in context), ref edges[(int) Edge.Horizontal]);
                Set(ToEdgeLength(parts[2], in context), ref edges[(int) Edge.Bottom]);
                break;

            case 4:
                Set(ToEdgeLength(parts[0], in context), ref edges[(int) Edge.Top]);
                Set(ToEdgeLength(parts[1], in context), ref edges[(int) Edge.Right]);
                Set(ToEdgeLength(parts[2], in context), ref edges[(int) Edge.Bottom]);
                Set(ToEdgeLength(parts[3], in context), ref edges[(int) Edge.Left]);
                break;

            default:
                break;
        }
    }

    void SetLength(ComputedStyle style, int property, in LengthContext context, ref StyleLength target) {
        if (property != NameTable.None && TryValue(style, property, out var value)) {
            Set(ToEdgeLength(value, in context), ref target);
        }
    }

    void SetSizeLength(ComputedStyle style, int property, in LengthContext context, ref StyleLength target) {
        if (property != NameTable.None && TryValue(style, property, out var value)) {
            Set(ToSizeLength(value, in context), ref target);
        }
    }

    /// <summary>A length, or <c>auto</c>, which is a length as far as layout is concerned.</summary>
    StyleLength ToEdgeLength(StyleValue value, in LengthContext context) =>
        value.Kind == StyleValueKind.Keyword && value.Keyword == keywords.Auto
            ? StyleLength.Auto
            : context.ToLength(value);

    /// <summary>The same, plus CSS Sizing § 5's three content keywords.</summary>
    /// <remarks>
    ///     ⚠ <b>These do not go through <see cref="LengthContext.ToLength" /> and could not.</b> That
    ///     resolves a written unit against a font size and a viewport, and neither of those settles
    ///     <c>max-content</c> — the answer is a measurement of the element's own subtree, which only
    ///     layout can take. So the keyword is carried across as a <see cref="LayoutUnit" /> and
    ///     <c>LayoutTree.Intrinsic.cs</c> turns it into a number in a pre-pass. Before that existed,
    ///     every one of these came back <see cref="StyleLength.Undefined" /> here and
    ///     <see cref="Set" /> left the dimension alone — thirteen Tailwind sizing roots' worth of
    ///     classes that resolved, cascaded and moved nothing.
    /// </remarks>
    StyleLength ToSizeLength(StyleValue value, in LengthContext context) {
        if (value.Kind == StyleValueKind.Keyword && keywords.ContentSizes.TryGetValue(value.Keyword, out var unit)) {
            return StyleLength.Keyword(unit);
        }

        return ToEdgeLength(value, in context);
    }

    /// <summary>Writes a length only if it was understood.</summary>
    /// <remarks>
    ///     ⚠ An unparseable declaration leaves the initial value alone rather than writing zero or
    ///     undefined over it. Zero is a perfectly valid answer that happens to be invisible, so
    ///     using it for "I did not understand this" turns one typo into a missing element with
    ///     nothing said about it.
    /// </remarks>
    static void Set(StyleLength length, ref StyleLength target) {
        if (length.IsDefined) {
            target = length;
        }
    }

    bool TryValue(ComputedStyle style, int property, out StyleValue value) {
        if (property != NameTable.None && style.TryGet(property, out var id)) {
            value = parser.Parse(id);
            return value.Kind != StyleValueKind.Unknown;
        }

        value = StyleValue.Unknown;
        return false;
    }

    bool TryNumber(ComputedStyle style, int property, out float number) {
        if (TryValue(style, property, out var value) && value.Kind == StyleValueKind.Number) {
            number = value.Number;
            return true;
        }

        number = 0f;
        return false;
    }

    bool TryKeyword<T>(ComputedStyle style, int property, Dictionary<int, T> table, out T result)
        where T : struct {
        if (TryValue(style, property, out var value)
            && value.Kind == StyleValueKind.Keyword
            && table.TryGetValue(value.Keyword, out result)) {
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The four properties of one edge family, interned.</summary>
    readonly record struct EdgeNames(int Shorthand, int Left, int Top, int Right, int Bottom, int Start, int End) {
        public static EdgeNames For(NameTable properties, string shorthand, string prefix, string suffix = "") =>
            new(
                properties.Intern(shorthand),
                properties.Intern($"{prefix}-left{suffix}"),
                properties.Intern($"{prefix}-top{suffix}"),
                properties.Intern($"{prefix}-right{suffix}"),
                properties.Intern($"{prefix}-bottom{suffix}"),
                properties.Intern($"{prefix}-inline-start{suffix}"),
                properties.Intern($"{prefix}-inline-end{suffix}")
            );

        /// <summary>The physical-edge family for <c>inset</c>, whose longhands have no prefix.</summary>
        public static EdgeNames ForInset(NameTable properties) =>
            new(
                properties.Intern("inset"),
                properties.Intern("left"),
                properties.Intern("top"),
                properties.Intern("right"),
                properties.Intern("bottom"),
                properties.Intern("inset-inline-start"),
                properties.Intern("inset-inline-end")
            );
    }

    /// <summary>Which of the four track lists a <see cref="TrackListProperty" /> writes.</summary>
    enum GridTrackSlot { Columns, Rows, AutoColumns, AutoRows }

    /// <summary>A style property whose value is a list, and so cannot live in a fixed-size struct.</summary>
    /// <remarks>
    ///     ⚠ <b>The driver knows only "present, absent, refused" — everything about the grammar and
    ///     about where the value is stored belongs to the subclass.</b> That is the whole of what
    ///     makes this a mechanism rather than four special cases: adding
    ///     <c>grid-template-areas</c> was a new subclass and one line in the array, and it brought
    ///     its own scratch buffer with it rather than widening a signature everything else has to
    ///     carry. ⚠ The five entries beside the four track lists are what that promise cost when it
    ///     was called in.
    /// </remarks>
    /// <param name="property">The interned property name.</param>
    abstract class VariableLengthProperty(int property) {
        /// <summary>The interned property name this reads.</summary>
        public int Property { get; } = property;

        /// <summary>Reads a value and writes it to the node.</summary>
        /// <param name="value">The declaration's value, verbatim.</param>
        /// <param name="tree">The tree.</param>
        /// <param name="node">The node.</param>
        /// <param name="refusal">Why it could not be read, when this returns false.</param>
        /// <returns>Whether the value was understood and written.</returns>
        public abstract bool TryApply(
            string value,
            LayoutTree tree,
            LayoutNodeId node,
            [NotNullWhen(false)] out string? refusal
        );

        /// <summary>Puts the node back to the property's initial value.</summary>
        public abstract void Reset(LayoutTree tree, LayoutNodeId node);
    }

    /// <summary>One of the four <c>&lt;track-list&gt;</c> properties.</summary>
    /// <remarks>
    ///     ⚠ <b>One scratch list per property per builder, reused for the life of the document.</b>
    ///     The bridge parses once per restyled element, and a grid-heavy panel restyles a great many
    ///     of them at once; allocating a list per parse would put the whole track list of every grid
    ///     in the document through gen 0 on every theme change. The list is never handed out — the
    ///     store copies into its arena before this returns.
    /// </remarks>
    sealed class TrackListProperty(int property, GridTrackSlot slot) : VariableLengthProperty(property) {
        readonly List<GridTrackSize> scratch = [];

        public override bool TryApply(
            string value,
            LayoutTree tree,
            LayoutNodeId node,
            [NotNullWhen(false)] out string? refusal
        ) {
            // ⚠ <b><c>none</c> is read here rather than in the grammar, for the reason the empty case
            // below is: the two callers disagree.</b> §7.2 puts <c>none</c> in a <c>&lt;track-list&gt;</c>
            // and *not* in the <c>&lt;auto-track-list&gt;</c> the two implicit properties take, so
            // <c>grid-auto-rows: none</c> is genuinely invalid while <c>grid-template-rows: none</c> is
            // the property's initial value written out. The grammar refuses both and names the token —
            // which is right for it and wrong here, because `grid-rows-none` is a Tailwind class an
            // author will write and a refusal would log a diagnostic on a declaration that is correct.
            if (value.AsSpan().Trim().Equals("none", StringComparison.Ordinal)
                && slot is GridTrackSlot.Columns or GridTrackSlot.Rows) {
                Reset(tree, node);
                refusal = null;
                return true;
            }

            if (!GridTrackList.TryParse(value, scratch, out var repeat, out refusal)) {
                return false;
            }

            // ⚠ Empty is a refusal here and not in the grammar, because the two callers disagree
            // about what it means. `grid-auto-columns:` with nothing after it is a typo in a
            // stylesheet; the layout corpus feeds whitespace through expecting the documented
            // "empty means auto". The grammar stays neutral and each caller decides.
            if (scratch.Count == 0) {
                refusal = "no tracks";
                return false;
            }

            switch (slot) {
                case GridTrackSlot.Columns:
                    tree.SetGridTemplateColumns(node, CollectionsMarshal.AsSpan(scratch), repeat.Kind, repeat.Index, repeat.Count);
                    break;

                case GridTrackSlot.Rows:
                    tree.SetGridTemplateRows(node, CollectionsMarshal.AsSpan(scratch), repeat.Kind, repeat.Index, repeat.Count);
                    break;

                default:
                    // ⚠ §7.2.3.2 admits `repeat()` in a `<track-list>` only. `grid-auto-rows` is an
                    // `<auto-track-list>`, whose tracks cycle rather than being laid out once, so an
                    // automatic repetition there has nothing to count against and is refused rather
                    // than dropped to a fixed list — which would silently change how many sizes the
                    // cycle has.
                    if (repeat.Kind != GridAutoRepeat.None) {
                        refusal = "an automatic repeat() is not allowed in an implicit track list";
                        return false;
                    }

                    if (slot == GridTrackSlot.AutoColumns) {
                        tree.SetGridAutoColumns(node, CollectionsMarshal.AsSpan(scratch));
                    } else {
                        tree.SetGridAutoRows(node, CollectionsMarshal.AsSpan(scratch));
                    }

                    break;
            }

            return true;
        }

        public override void Reset(LayoutTree tree, LayoutNodeId node) {
            switch (slot) {
                case GridTrackSlot.Columns: tree.SetGridTemplateColumns(node, default); break;
                case GridTrackSlot.Rows: tree.SetGridTemplateRows(node, default); break;
                case GridTrackSlot.AutoColumns: tree.SetGridAutoColumns(node, default); break;
                default: tree.SetGridAutoRows(node, default); break;
            }
        }
    }

    /// <summary><c>grid-template-areas</c>, the second shape this registry was built for.</summary>
    /// <remarks>
    ///     ⚠ <b>It is here rather than in <see cref="Build" /> for the reason the track lists are,
    ///     and the reason is not that the value is long.</b> A <see cref="LayoutStyle" /> never sees
    ///     a node id, and an area template belongs to the <i>node</i> — the store keeps one object
    ///     per grid container beside the style array, exactly as it keeps a measure function. A
    ///     value returned from <c>Build</c> would be a reference two elements that resolved alike
    ///     would share.
    /// </remarks>
    sealed class AreaTemplateProperty(int property) : VariableLengthProperty(property) {
        public override bool TryApply(
            string value,
            LayoutTree tree,
            LayoutNodeId node,
            [NotNullWhen(false)] out string? refusal
        ) {
            if (!GridAreaTemplate.TryParse(value, out var template, out refusal)) {
                return false;
            }

            tree.SetGridTemplateAreas(node, template);
            return true;
        }

        public override void Reset(LayoutTree tree, LayoutNodeId node) => tree.SetGridTemplateAreas(node, null);
    }

    /// <summary>One placement longhand, read as the name of a grid area.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same declaration is read twice, by two different halves of this bridge, and
    ///         that is deliberate rather than an oversight.</b> <c>grid-row-start</c> is a line
    ///         number to <see cref="ApplyPlacements" /> and an area name to this, and exactly one of
    ///         the two can be true of any one value — so each reads it, one of them succeeds, and the
    ///         one that does not <i>writes the absence</i>. Leaving the name alone when the number
    ///         wins is the failure this shape exists to stop: an element restyled from
    ///         <c>grid-area: header</c> to <c>grid-row-start: 2</c> would keep the name, and the name
    ///         beats the number in the store.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A number is read first, because a number is also a legal area name.</b> The
    ///         conformance oracle accepts <c>"10"</c> as an area, so <c>grid-row-start: 10</c> is
    ///         ambiguous to a character test and is not ambiguous in CSS, where the two are different
    ///         token types. Call order is what stands in for the token type here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Anything that is neither is refused rather than ignored</b>, which restores what
    ///         <see cref="TryPlacement" /> gave up when this class took the identifier case off it:
    ///         <c>grid-row-start: 4px</c> still reaches the diagnostics list, and an item whose
    ///         placement did not parse still says so rather than auto-placing into a plausible cell.
    ///     </para>
    /// </remarks>
    sealed class NamedPlacementProperty(int property, Edge edge) : VariableLengthProperty(property) {
        public override bool TryApply(
            string value,
            LayoutTree tree,
            LayoutNodeId node,
            [NotNullWhen(false)] out string? refusal
        ) {
            refusal = null;

            if (GridPlacement.TryParse(value, out _)) {
                tree.SetGridPlacement(node, edge, name: null);
                return true;
            }

            var name = value.Trim();

            if (!GridAreaTemplate.IsAreaName(name)) {
                refusal = "not a line, a span, an area's name or auto";
                return false;
            }

            tree.SetGridPlacement(node, edge, name);
            return true;
        }

        public override void Reset(LayoutTree tree, LayoutNodeId node) => tree.SetGridPlacement(node, edge, name: null);
    }

    sealed class Properties {
        public Properties(NameTable table) {
            Direction = table.Intern("direction");
            FlexDirection = table.Intern("flex-direction");
            JustifyContent = table.Intern("justify-content");
            AlignContent = table.Intern("align-content");
            AlignItems = table.Intern("align-items");
            AlignSelf = table.Intern("align-self");
            Position = table.Intern("position");
            FlexWrap = table.Intern("flex-wrap");
            Overflow = table.Intern("overflow");
            OverflowX = table.Intern("overflow-x");
            OverflowY = table.Intern("overflow-y");
            Display = table.Intern("display");
            Float = table.Intern("float");
            Clear = table.Intern("clear");
            VerticalAlign = table.Intern("vertical-align");
            BoxSizing = table.Intern("box-sizing");

            Flex = table.Intern("flex");
            FlexGrow = table.Intern("flex-grow");
            FlexShrink = table.Intern("flex-shrink");
            FlexBasis = table.Intern("flex-basis");
            AspectRatio = table.Intern("aspect-ratio");
            Order = table.Intern("order");

            Width = table.Intern("width");
            Height = table.Intern("height");
            MinWidth = table.Intern("min-width");
            MinHeight = table.Intern("min-height");
            MaxWidth = table.Intern("max-width");
            MaxHeight = table.Intern("max-height");

            FontSize = table.Intern("font-size");

            // The four the computed-value stage resolves. See ComputedText.
            LineHeight = table.Intern("line-height");
            LetterSpacing = table.Intern("letter-spacing");
            WordSpacing = table.Intern("word-spacing");
            TextIndent = table.Intern("text-indent");
            Gap = table.Intern("gap");
            ScrollbarWidth = table.Intern("scrollbar-width");
            RowGap = table.Intern("row-gap");
            ColumnGap = table.Intern("column-gap");

            // ── Grid ────────────────────────────────────────────────────────────────────────────
            //
            // ⚠ The four templates are interned here but are NOT read by `Build`. They are the
            // variable-length half of the surface and are applied against a node id by
            // `ApplyVariableLength`; see the remarks on `GridTemplates`.
            GridTemplateColumns = table.Intern("grid-template-columns");
            GridTemplateRows = table.Intern("grid-template-rows");
            GridAutoColumns = table.Intern("grid-auto-columns");
            GridAutoRows = table.Intern("grid-auto-rows");
            GridTemplateAreas = table.Intern("grid-template-areas");

            GridAutoFlow = table.Intern("grid-auto-flow");
            JustifyItems = table.Intern("justify-items");
            JustifySelf = table.Intern("justify-self");

            GridColumn = table.Intern("grid-column");
            GridRow = table.Intern("grid-row");
            GridColumnStart = table.Intern("grid-column-start");
            GridColumnEnd = table.Intern("grid-column-end");
            GridRowStart = table.Intern("grid-row-start");
            GridRowEnd = table.Intern("grid-row-end");

            Margin = EdgeNames.For(table, "margin", "margin");
            Padding = EdgeNames.For(table, "padding", "padding");
            Border = EdgeNames.For(table, "border-width", "border", "-width");
            Inset = EdgeNames.ForInset(table);
        }

        public int Direction { get; }
        public int FlexDirection { get; }
        public int JustifyContent { get; }
        public int AlignContent { get; }
        public int AlignItems { get; }
        public int AlignSelf { get; }
        public int Position { get; }
        public int FlexWrap { get; }
        public int Overflow { get; }
        public int OverflowX { get; }
        public int OverflowY { get; }
        public int Display { get; }
        public int Float { get; }
        public int Clear { get; }
        public int VerticalAlign { get; }
        public int BoxSizing { get; }
        public int Flex { get; }
        public int FlexGrow { get; }
        public int FlexShrink { get; }
        public int FlexBasis { get; }
        public int AspectRatio { get; }
        public int Order { get; }
        public int Width { get; }
        public int Height { get; }
        public int MinWidth { get; }
        public int MinHeight { get; }
        public int MaxWidth { get; }
        public int MaxHeight { get; }
        public int FontSize { get; }
        public int LineHeight { get; }
        public int LetterSpacing { get; }
        public int WordSpacing { get; }
        public int TextIndent { get; }
        public int Gap { get; }
        public int ScrollbarWidth { get; }
        public int RowGap { get; }
        public int ColumnGap { get; }
        public int GridTemplateColumns { get; }
        public int GridTemplateRows { get; }

        public int GridTemplateAreas { get; }
        public int GridAutoColumns { get; }
        public int GridAutoRows { get; }
        public int GridAutoFlow { get; }
        public int JustifyItems { get; }
        public int JustifySelf { get; }
        public int GridColumn { get; }
        public int GridRow { get; }
        public int GridColumnStart { get; }
        public int GridColumnEnd { get; }
        public int GridRowStart { get; }
        public int GridRowEnd { get; }
        public EdgeNames Margin { get; }
        public EdgeNames Padding { get; }
        public EdgeNames Border { get; }
        public EdgeNames Inset { get; }
    }

    /// <summary>Every CSS identifier this bridge understands, interned and mapped to its enum.</summary>
    /// <remarks>
    ///     A keyword the tables do not list leaves the property at its initial value, which is what
    ///     CSS says an invalid declaration does. That is why the tables are consulted rather than a
    ///     switch with a default arm: an unrecognised keyword and a recognised one that maps to the
    ///     first enum member must not look the same.
    /// </remarks>
    sealed class Keywords {
        public Keywords(NameTable table) {
            Auto = table.Intern("auto");
            None = table.Intern("none");

            ContentSizes = new Dictionary<int, LayoutUnit> {
                [table.Intern("min-content")] = LayoutUnit.MinContent,
                [table.Intern("max-content")] = LayoutUnit.MaxContent,
                [table.Intern("fit-content")] = LayoutUnit.FitContent
            };

            Directions = new Dictionary<int, Direction> {
                [table.Intern("inherit")] = Direction.Inherit,
                [table.Intern("ltr")] = Direction.Ltr,
                [table.Intern("rtl")] = Direction.Rtl
            };

            FlexDirections = new Dictionary<int, FlexDirection> {
                [table.Intern("row")] = FlexDirection.Row,
                [table.Intern("row-reverse")] = FlexDirection.RowReverse,
                [table.Intern("column")] = FlexDirection.Column,
                [table.Intern("column-reverse")] = FlexDirection.ColumnReverse
            };

            Justifications = new Dictionary<int, Justify> {
                [table.Intern("flex-start")] = Justify.FlexStart,
                [table.Intern("center")] = Justify.Center,
                [table.Intern("flex-end")] = Justify.FlexEnd,
                [table.Intern("space-between")] = Justify.SpaceBetween,
                [table.Intern("space-around")] = Justify.SpaceAround,
                [table.Intern("space-evenly")] = Justify.SpaceEvenly,
                [table.Intern("start")] = Justify.FlexStart,
                [table.Intern("end")] = Justify.FlexEnd
            };

            Alignments = new Dictionary<int, Align> {
                [Auto] = Align.Auto,
                [table.Intern("flex-start")] = Align.FlexStart,
                [table.Intern("center")] = Align.Center,
                [table.Intern("flex-end")] = Align.FlexEnd,
                [table.Intern("stretch")] = Align.Stretch,
                [table.Intern("baseline")] = Align.Baseline,
                [table.Intern("space-between")] = Align.SpaceBetween,
                [table.Intern("space-around")] = Align.SpaceAround,
                [table.Intern("space-evenly")] = Align.SpaceEvenly,
                [table.Intern("start")] = Align.FlexStart,
                [table.Intern("end")] = Align.FlexEnd
            };

            Positions = new Dictionary<int, PositionType> {
                [table.Intern("static")] = PositionType.Static,
                [table.Intern("relative")] = PositionType.Relative,
                [table.Intern("absolute")] = PositionType.Absolute
            };

            Wraps = new Dictionary<int, Wrap> {
                [table.Intern("nowrap")] = Wrap.NoWrap,
                [table.Intern("wrap")] = Wrap.Wrap,
                [table.Intern("wrap-reverse")] = Wrap.WrapReverse
            };

            // ⚠ `auto` maps onto `Scroll` rather than adding a fourth mode. The two are the same
            // layout in CSS — both establish a scroll container — and differ only in whether the
            // scrollbar gutter is always reserved, which nothing here draws. Its absence was not
            // neutral: `overflow: auto` fell out of this table entirely, so a box that declared it
            // clipped in the draw list, which tests anything that is not `visible`, and stayed
            // `Visible` to flexbox, which reads this — half a property, in four editor panels and
            // however many stylesheets follow. See `Overflow`.
            Overflows = new Dictionary<int, Overflow> {
                [table.Intern("visible")] = Overflow.Visible,
                [table.Intern("hidden")] = Overflow.Hidden,
                [table.Intern("scroll")] = Overflow.Scroll,
                [Auto] = Overflow.Scroll
            };

            // ⚠ <b>`inline`, `inline-block` and `inline-flex` arrived with doc 43 § B3, and they are
            // the three keywords this comment used to explain the absence of.</b> They were unmapped
            // rather than aliased because mapping them onto `Block` and `Flex` would have made
            // `inline-block` take the whole line, which is the one thing an author writes it to
            // prevent. There is now an inline formatting context behind them — line boxes, atomic
            // inlines and §10.3.9 shrink-to-fit — so the alias is no longer the only option and the
            // keywords cross for real. ⚠ `inline` is *atomic* here: a `span` with text in it behaves
            // exactly as CSS says, and one with box children in it does not fragment. See
            // `LayoutTree.Inline.cs` and `InlineKnownGaps.txt`.
            //
            // `inline-grid` is still absent, and for the original reason: it would be an alias.
            //
            // ⚠ <b><c>grid</c> arrived with doc 43 § B2 and maps to a real algorithm.</b> The
            // keyword used to be all that crossed this bridge, because a track list is not a fixed
            // number of bytes and a <see cref="LayoutStyle" /> is — the tracks live in the tree's
            // <c>TrackArena</c> behind a node id that <c>Build</c> does not have. That is now solved
            // rather than worked around: the placement longhands are ordinary fixed-size fields and
            // cross here, and the four track lists cross through <c>ApplyVariableLength</c>, which
            // does take a node. <c>grid-cols-3</c> reaches §12.
            Displays = new Dictionary<int, Display> {
                [table.Intern("flex")] = Display.Flex,
                [table.Intern("none")] = Display.None,
                [table.Intern("block")] = Display.Block,
                [table.Intern("grid")] = Display.Grid,
                [table.Intern("inline")] = Display.Inline,
                [table.Intern("inline-block")] = Display.InlineBlock,
                [table.Intern("inline-flex")] = Display.InlineFlex,
                [table.Intern("flow-root")] = Display.FlowRoot
            };

            // ⚠ <b>The two LOGICAL keywords are absent, and it is the same refusal <c>inline-grid</c>
            // gets one table up.</b> Tailwind v4 emits <c>float: inline-start</c> and
            // <c>inline-end</c>, and CSS Logical Properties defines both against the writing mode.
            // <see cref="FloatSide" /> and <see cref="Clear" /> are physical by construction — CSS
            // 2.1 §9.5's keywords, which do not flip with <see cref="Direction" />, and which the
            // whole `float_bfc_*` corpus asserts do not flip by shipping RTL variants with identical
            // expectations. Mapping `inline-start` onto `Left` would be right in LTR and wrong in
            // RTL within the same declaration; accepting it and doing nothing is worse in a
            // different way. So the utility families do not emit them either, and
            // `docs/plan/43-web-styling-parity.tsv` records both roots as `partial` with the gap
            // named, rather than as `works` with a class that quietly does nothing.
            Floats = new Dictionary<int, FloatSide> {
                [table.Intern("none")] = FloatSide.None,
                [table.Intern("left")] = FloatSide.Left,
                [table.Intern("right")] = FloatSide.Right
            };

            Clears = new Dictionary<int, Clear> {
                [table.Intern("none")] = Clear.None,
                [table.Intern("left")] = Clear.Left,
                [table.Intern("right")] = Clear.Right,
                [table.Intern("both")] = Clear.Both
            };

            // ⚠ <b>Three of the eight, and the five that are missing are missing on purpose.</b>
            // `middle`, `text-top`, `text-bottom`, `sub` and `super` are each defined against the
            // parent's strut — its font's x-height, ascent or descent — and `Vixen.Ui.Layout` has no
            // font: it is geometry, and `FontRegistry` is on this side of the boundary rather than
            // that one. The layout store falls them back to `baseline`, which is what an engine must
            // do with a value it cannot honour; what it must not do is let this bridge report them as
            // supported, so they are dropped here and the utilities that emit them stay in the
            // editor's inert inventory with a task number. See `VerticalAlign`.
            VerticalAligns = new Dictionary<int, VerticalAlign> {
                [table.Intern("baseline")] = VerticalAlign.Baseline,
                [table.Intern("top")] = VerticalAlign.Top,
                [table.Intern("bottom")] = VerticalAlign.Bottom
            };

            BoxSizings = new Dictionary<int, BoxSizing> {
                [table.Intern("border-box")] = BoxSizing.BorderBox,
                [table.Intern("content-box")] = BoxSizing.ContentBox
            };

            // ⚠ <b>All four written-out spellings, because `dense` is a second word rather than a
            // second declaration.</b> CSS Grid §8.5's grammar is `[ row | column ] || dense`, so
            // `row dense` and `dense row` are the same value and both occur; the cascade hands this
            // bridge one interned string, not a token list, so the pairs are enumerated rather than
            // parsed. `dense` alone means `row dense`, per the grammar's omitted-first-term rule.
            GridAutoFlows = new Dictionary<int, GridAutoFlow> {
                [table.Intern("row")] = GridAutoFlow.Row,
                [table.Intern("column")] = GridAutoFlow.Column,
                [table.Intern("dense")] = GridAutoFlow.RowDense,
                [table.Intern("row dense")] = GridAutoFlow.RowDense,
                [table.Intern("dense row")] = GridAutoFlow.RowDense,
                [table.Intern("column dense")] = GridAutoFlow.ColumnDense,
                [table.Intern("dense column")] = GridAutoFlow.ColumnDense
            };
        }

        public int Auto { get; }
        public int None { get; }
        public Dictionary<int, LayoutUnit> ContentSizes { get; }
        public Dictionary<int, VerticalAlign> VerticalAligns { get; }
        public Dictionary<int, Direction> Directions { get; }
        public Dictionary<int, FlexDirection> FlexDirections { get; }
        public Dictionary<int, Justify> Justifications { get; }
        public Dictionary<int, Align> Alignments { get; }
        public Dictionary<int, PositionType> Positions { get; }
        public Dictionary<int, Wrap> Wraps { get; }
        public Dictionary<int, Overflow> Overflows { get; }
        public Dictionary<int, Display> Displays { get; }
        public Dictionary<int, FloatSide> Floats { get; }
        public Dictionary<int, Clear> Clears { get; }
        public Dictionary<int, BoxSizing> BoxSizings { get; }
        public Dictionary<int, GridAutoFlow> GridAutoFlows { get; }
    }
}
