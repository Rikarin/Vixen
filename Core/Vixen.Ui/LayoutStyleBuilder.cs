// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

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
    ///         because this engine has only flex and none, and an element with no <c>display</c>
    ///         should be laid out rather than skipped. And <c>box-sizing: border-box</c>, which most
    ///         UI work wants, belongs in a user-agent stylesheet where an author can see and
    ///         override it — not baked in here where they cannot.
    ///     </para>
    /// </remarks>
    public static readonly LayoutStyle CssInitial = CreateCssInitial();

    readonly StyleValueParser parser;
    readonly NameTable values;
    readonly Properties names;
    readonly Keywords keywords;

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
        names = new Properties(properties);
        keywords = new Keywords(keywordNames);
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

        return result;
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

        if (TryKeyword(style, names.Overflow, keywords.Overflows, out Overflow overflow)) {
            result.Overflow = overflow;
        }

        if (TryKeyword(style, names.Display, keywords.Displays, out Display display)) {
            result.Display = display;
        }

        if (TryKeyword(style, names.BoxSizing, keywords.BoxSizings, out BoxSizing boxSizing)) {
            result.BoxSizing = boxSizing;
        }
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

    void ApplyDimensions(ComputedStyle style, in LengthContext context, ref LayoutStyle result) {
        SetLength(style, names.Width, in context, ref result.Dimensions[(int) Dimension.Width]);
        SetLength(style, names.Height, in context, ref result.Dimensions[(int) Dimension.Height]);
        SetLength(style, names.MinWidth, in context, ref result.MinDimensions[(int) Dimension.Width]);
        SetLength(style, names.MinHeight, in context, ref result.MinDimensions[(int) Dimension.Height]);
        SetLength(style, names.MaxWidth, in context, ref result.MaxDimensions[(int) Dimension.Width]);
        SetLength(style, names.MaxHeight, in context, ref result.MaxDimensions[(int) Dimension.Height]);
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

    /// <summary>A length, or <c>auto</c>, which is a length as far as layout is concerned.</summary>
    StyleLength ToEdgeLength(StyleValue value, in LengthContext context) =>
        value.Kind == StyleValueKind.Keyword && value.Keyword == keywords.Auto
            ? StyleLength.Auto
            : context.ToLength(value);

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
            Display = table.Intern("display");
            BoxSizing = table.Intern("box-sizing");

            Flex = table.Intern("flex");
            FlexGrow = table.Intern("flex-grow");
            FlexShrink = table.Intern("flex-shrink");
            FlexBasis = table.Intern("flex-basis");
            AspectRatio = table.Intern("aspect-ratio");

            Width = table.Intern("width");
            Height = table.Intern("height");
            MinWidth = table.Intern("min-width");
            MinHeight = table.Intern("min-height");
            MaxWidth = table.Intern("max-width");
            MaxHeight = table.Intern("max-height");

            FontSize = table.Intern("font-size");
            Gap = table.Intern("gap");
            RowGap = table.Intern("row-gap");
            ColumnGap = table.Intern("column-gap");

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
        public int Display { get; }
        public int BoxSizing { get; }
        public int Flex { get; }
        public int FlexGrow { get; }
        public int FlexShrink { get; }
        public int FlexBasis { get; }
        public int AspectRatio { get; }
        public int Width { get; }
        public int Height { get; }
        public int MinWidth { get; }
        public int MinHeight { get; }
        public int MaxWidth { get; }
        public int MaxHeight { get; }
        public int FontSize { get; }
        public int Gap { get; }
        public int RowGap { get; }
        public int ColumnGap { get; }
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

            Overflows = new Dictionary<int, Overflow> {
                [table.Intern("visible")] = Overflow.Visible,
                [table.Intern("hidden")] = Overflow.Hidden,
                [table.Intern("scroll")] = Overflow.Scroll
            };

            Displays = new Dictionary<int, Display> {
                [table.Intern("flex")] = Display.Flex,
                [table.Intern("none")] = Display.None
            };

            BoxSizings = new Dictionary<int, BoxSizing> {
                [table.Intern("border-box")] = BoxSizing.BorderBox,
                [table.Intern("content-box")] = BoxSizing.ContentBox
            };
        }

        public int Auto { get; }
        public Dictionary<int, Direction> Directions { get; }
        public Dictionary<int, FlexDirection> FlexDirections { get; }
        public Dictionary<int, Justify> Justifications { get; }
        public Dictionary<int, Align> Alignments { get; }
        public Dictionary<int, PositionType> Positions { get; }
        public Dictionary<int, Wrap> Wraps { get; }
        public Dictionary<int, Overflow> Overflows { get; }
        public Dictionary<int, Display> Displays { get; }
        public Dictionary<int, BoxSizing> BoxSizings { get; }
    }
}
