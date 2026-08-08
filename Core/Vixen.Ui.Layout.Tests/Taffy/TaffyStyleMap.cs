// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     Thrown when a fixture asks for something <see cref="LayoutTree" /> has no field for.
/// </summary>
/// <remarks>
///     ⚠ <b>This is not the same as a failing test and the two must never be counted together.</b> A
///     numeric mismatch says the algorithm is wrong; this says the algorithm was never asked. Track B
///     is judged on the first number, and conflating them would let an unimplemented mode look like a
///     conformance failure — or, far worse, let a real conformance failure hide inside a pile of
///     "not implemented".
/// </remarks>
sealed class TaffyUnsupportedException(string feature) : Exception($"unsupported: {feature}") {
    public string Feature { get; } = feature;
}

/// <summary>
///     Applies one fixture node's attributes to a <see cref="LayoutTree" /> node.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every attribute is either applied or refused; none is ignored.</b> Silently dropping an
///         attribute is the one failure this whole corpus is supposed to be immune to — it produces a
///         green test that proves nothing, and at 5 524 fixtures nobody would find it. So the switch
///         below is exhaustive over the 56 attributes the corpus uses and its default arm throws.
///     </para>
///     <para>
///         ⚠ <b>Three initial values differ between Yoga and CSS, and the corpus is CSS's.</b> Yoga
///         deviates deliberately on all three, <see cref="LayoutStyle.Default" /> follows Yoga, and
///         Chrome — which produced every number in the corpus — follows CSS. So
///         <see cref="ApplyCssInitialValues" /> resets them per node before the fixture's own
///         attributes are read: <c>flex-direction</c> is <c>row</c> and not <c>column</c>,
///         <c>flex-shrink</c> is <c>1</c> and not <c>0</c>, and <c>align-content</c> is <c>stretch</c>
///         and not <c>flex-start</c>. Skipping this does not produce a few wrong fixtures, it produces
///         thousands, and every one of them would look like a flexbox bug.
///     </para>
/// </remarks>
/// <summary>The facts about a box that decide what <c>start</c> and <c>end</c> point at.</summary>
/// <param name="IsColumn">Whether its main axis is the block axis, which puts the cross axis inline.</param>
/// <param name="IsReverse">Whether its main axis runs backwards.</param>
/// <param name="WrapReverse">Whether its cross axis runs backwards.</param>
/// <param name="Rtl">Whether its inline axis runs right to left.</param>
readonly record struct TaffyBox(bool IsColumn, bool IsReverse, bool WrapReverse, bool Rtl) {
    public static TaffyBox From(IReadOnlyDictionary<string, string> attributes) {
        var direction = attributes.GetValueOrDefault("flex-direction", "row");

        return new TaffyBox(
            direction.StartsWith("column", StringComparison.Ordinal),
            direction.EndsWith("-reverse", StringComparison.Ordinal),
            attributes.GetValueOrDefault("flex-wrap") == "wrap-reverse",
            attributes.GetValueOrDefault("direction") == "rtl"
        );
    }
}

static class TaffyStyleMap {
    /// <summary>Puts the node into CSS's initial state, which is not Yoga's.</summary>
    public static void ApplyCssInitialValues(LayoutTree tree, LayoutNodeId node, bool isRoot) {
        tree.SetFlexDirection(node, FlexDirection.Row);
        tree.SetAlignContent(node, Align.Stretch);

        // ⚠ The root is exempt from shrinking in the algorithm regardless (ResolveFlexShrink returns
        // 0 for it), so setting it there would be noise; Taffy's root is likewise never shrunk.
        if (!isRoot) {
            tree.SetFlexShrink(node, 1f);
        }
    }

    /// <param name="tree">The tree being built.</param>
    /// <param name="node">The node the attribute belongs to.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">Its value, verbatim.</param>
    /// <param name="self">The node's own axis facts, which decide its <c>justify-content</c>.</param>
    /// <param name="parent">Its container's, which decide its <c>align-self</c>.</param>
    public static void Apply(LayoutTree tree, LayoutNodeId node, string name, string value, TaffyBox self, TaffyBox parent) {
        switch (name) {
            // ── Box and flow ────────────────────────────────────────────────────────────────────
            case "display":
                tree.SetDisplay(
                    node,
                    value switch {
                        "flex" => Display.Flex,
                        "none" => Display.None,
                        "block" => Display.Block,
                        _ => throw new TaffyUnsupportedException($"display: {value}")
                    }
                );

                break;

            case "direction":
                tree.SetDirection(node, value switch {
                    "ltr" => Direction.Ltr,
                    "rtl" => Direction.Rtl,
                    _ => throw new TaffyUnsupportedException($"direction: {value}")
                });

                break;

            case "box-sizing":
                tree.SetBoxSizing(node, value switch {
                    "border-box" => BoxSizing.BorderBox,
                    "content-box" => BoxSizing.ContentBox,
                    _ => throw new TaffyUnsupportedException($"box-sizing: {value}")
                });

                break;

            case "position":
                tree.SetPositionType(node, value switch {
                    "relative" => PositionType.Relative,
                    "absolute" => PositionType.Absolute,
                    "static" => PositionType.Static,
                    _ => throw new TaffyUnsupportedException($"position: {value}")
                });

                break;

            case "overflow-x":
                tree.SetOverflow(node, Overflow(value), tree.GetStyle(node).OverflowY);
                break;

            case "overflow-y":
                tree.SetOverflow(node, tree.GetStyle(node).OverflowX, Overflow(value));
                break;

            // ── Sizing ──────────────────────────────────────────────────────────────────────────
            case "width": tree.SetDimension(node, Dimension.Width, Length(value)); break;
            case "height": tree.SetDimension(node, Dimension.Height, Length(value)); break;
            case "min-width": tree.SetMinDimension(node, Dimension.Width, Length(value)); break;
            case "min-height": tree.SetMinDimension(node, Dimension.Height, Length(value)); break;
            case "max-width": tree.SetMaxDimension(node, Dimension.Width, Length(value)); break;
            case "max-height": tree.SetMaxDimension(node, Dimension.Height, Length(value)); break;
            case "aspect-ratio": tree.SetAspectRatio(node, Number(value)); break;

            // ── Edges ───────────────────────────────────────────────────────────────────────────
            case "top": tree.SetPosition(node, Edge.Top, Length(value)); break;
            case "left": tree.SetPosition(node, Edge.Left, Length(value)); break;
            case "bottom": tree.SetPosition(node, Edge.Bottom, Length(value)); break;
            case "right": tree.SetPosition(node, Edge.Right, Length(value)); break;

            case "margin-top": tree.SetMargin(node, Edge.Top, Length(value)); break;
            case "margin-left": tree.SetMargin(node, Edge.Left, Length(value)); break;
            case "margin-bottom": tree.SetMargin(node, Edge.Bottom, Length(value)); break;
            case "margin-right": tree.SetMargin(node, Edge.Right, Length(value)); break;

            case "padding-top": tree.SetPadding(node, Edge.Top, Length(value)); break;
            case "padding-left": tree.SetPadding(node, Edge.Left, Length(value)); break;
            case "padding-bottom": tree.SetPadding(node, Edge.Bottom, Length(value)); break;
            case "padding-right": tree.SetPadding(node, Edge.Right, Length(value)); break;

            case "border-top": tree.SetBorder(node, Edge.Top, Length(value)); break;
            case "border-left": tree.SetBorder(node, Edge.Left, Length(value)); break;
            case "border-bottom": tree.SetBorder(node, Edge.Bottom, Length(value)); break;
            case "border-right": tree.SetBorder(node, Edge.Right, Length(value)); break;

            case "row-gap": tree.SetGap(node, Gutter.Row, Length(value)); break;
            case "column-gap": tree.SetGap(node, Gutter.Column, Length(value)); break;

            // ── Alignment ───────────────────────────────────────────────────────────────────────
            // The cross-axis three read the container's own wrap; align-self reads its parent's.
            // ⚠ `align-items: self-*` is not applied here at all — see TaffyFixtureRunner.Build.
            case "align-items":
                // A self-relative align-items means something different for each child, so it cannot
                // be one container-level value; Build pushes it down onto the children instead.
                if (!IsSelfRelative(value)) {
                    tree.SetAlignItems(node, CrossAlign(name, value, self, self));
                }

                break;

            case "align-self": tree.SetAlignSelf(node, CrossAlign(name, value, parent, self)); break;
            case "align-content": tree.SetAlignContent(node, CrossAlign(name, value, self, self)); break;
            case "justify-content": tree.SetJustifyContent(node, Justification(value, self)); break;

            // ── Flex ────────────────────────────────────────────────────────────────────────────
            case "flex-direction":
                tree.SetFlexDirection(node, value switch {
                    "row" => FlexDirection.Row,
                    "row-reverse" => FlexDirection.RowReverse,
                    "column" => FlexDirection.Column,
                    "column-reverse" => FlexDirection.ColumnReverse,
                    _ => throw new TaffyUnsupportedException($"flex-direction: {value}")
                });

                break;

            case "flex-wrap":
                tree.SetFlexWrap(node, value switch {
                    "nowrap" => Wrap.NoWrap,
                    "wrap" => Wrap.Wrap,
                    "wrap-reverse" => Wrap.WrapReverse,
                    _ => throw new TaffyUnsupportedException($"flex-wrap: {value}")
                });

                break;

            case "flex-grow": tree.SetFlexGrow(node, Number(value)); break;
            case "flex-shrink": tree.SetFlexShrink(node, Number(value)); break;
            case "flex-basis": tree.SetFlexBasis(node, Length(value)); break;

            // ── Everything Vixen has no field for ───────────────────────────────────────────────
            // Refused by name so that a failure report says which property is missing rather than
            // which number is wrong. B1 and B2 delete these lines as they land.
            case "float":
            case "clear":
            case "text-align":
            case "scrollbar-width":
            case "writing-mode":
            case "justify-items":
            case "justify-self":
            case "grid-auto-flow":
            case "grid-template-rows":
            case "grid-template-columns":
            case "grid-template-areas":
            case "grid-auto-rows":
            case "grid-auto-columns":
            case "grid-row-start":
            case "grid-row-end":
            case "grid-column-start":
            case "grid-column-end":
                throw new TaffyUnsupportedException(name);

            default:
                // ⚠ Not "unsupported" — unknown. A new attribute in an updated corpus lands here, and
                // it has to be a hard failure rather than a skip, because the alternative is a
                // fixture that quietly stops testing what it was written to test.
                throw new InvalidOperationException(
                    $"'{name}' is not in the Taffy attribute map. Add it to TaffyStyleMap.Apply — do "
                    + "not let it fall through, or the fixtures that use it will pass without asserting it."
                );
        }
    }

    static Overflow Overflow(string value) =>
        value switch {
            "visible" => Layout.Overflow.Visible,
            "hidden" => Layout.Overflow.Hidden,
            "scroll" => Layout.Overflow.Scroll,
            // CSS `auto` and `scroll` lay out identically here; see the Overflow enum's remarks.
            "auto" => Layout.Overflow.Scroll,
            _ => throw new TaffyUnsupportedException($"overflow: {value}")
        };

    /// <summary>
    ///     CSS Box Alignment keywords onto Yoga's flex-relative <see cref="Align" />, on the cross axis.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>start</c> and <c>flex-start</c> are not synonyms, and treating them as a pair of
    ///         spellings is the single easiest way to make this corpus look like it found a flexbox
    ///         bug.</b> <c>flex-start</c> is <i>flex</i>-relative: under <c>flex-wrap: wrap-reverse</c>
    ///         the cross axis is reversed and it points at what was the cross-end.
    ///         <c>start</c> is <i>writing-mode</i>-relative and does not move. Vixen's
    ///         <see cref="Align" /> carries only the flex-relative pair — as Yoga's does — so the
    ///         non-flex-relative keywords have to be resolved here, at translation time, where the
    ///         container's wrap is known. Getting this wrong cost thirteen fixtures on the first run
    ///         and every one of them read as an alignment bug.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>self-start</c> resolves against the <i>item's</i> direction, not the
    ///         container's</b>, and on a column container the cross axis <i>is</i> the inline axis, so
    ///         the two can disagree. <c>flex_column_align_self_self_start_child_rtl</c> is the fixture
    ///         that says so: an <c>rtl</c> child of an <c>ltr</c> column lands at x=90 while its
    ///         <c>ltr</c> sibling lands at x=0, from the same declaration.
    ///     </para>
    ///     <para>
    ///         <c>unsafe X</c> is exactly <c>X</c> — unsafe is the default overflow behaviour — so it
    ///         is mapped. <c>safe X</c> falls back to start alignment when the item overflows, which
    ///         has no expression here, so it is refused rather than approximated.
    ///     </para>
    /// </remarks>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="value">Its value.</param>
    /// <param name="container">The box whose cross axis this aligns against.</param>
    /// <param name="item">The box being aligned — the same one for <c>align-items</c>.</param>
    static Align CrossAlign(string property, string value, TaffyBox container, TaffyBox item) {
        value = Unsafe(property, value);

        var flip = value switch {
            // Flex-relative already: Vixen's own enum reverses these under wrap-reverse.
            "flex-start" or "flex-end" => false,

            // Writing-mode-relative against the container. Only wrap-reverse separates them.
            "start" or "end" => container.WrapReverse,

            // Writing-mode-relative against the item. Additionally disagrees with the container
            // whenever the cross axis is inline and the two directions differ.
            "self-start" or "self-end" => container.WrapReverse ^ (container.IsColumn && container.Rtl != item.Rtl),

            _ => false
        };

        var resolved = value switch {
            "flex-start" or "start" or "self-start" => flip ? Align.FlexEnd : Align.FlexStart,
            "flex-end" or "end" or "self-end" => flip ? Align.FlexStart : Align.FlexEnd,
            "center" => Align.Center,
            "stretch" => Align.Stretch,
            "baseline" => Align.Baseline,
            "space-between" => Align.SpaceBetween,
            "space-around" => Align.SpaceAround,
            "space-evenly" => Align.SpaceEvenly,
            "normal" => Align.Stretch,
            "auto" => Align.Auto,
            _ => throw new TaffyUnsupportedException($"{property}: {value}")
        };

        return resolved;
    }

    /// <summary>The same resolution on the main axis, where <c>*-reverse</c> plays wrap-reverse's part.</summary>
    static Justify Justification(string value, TaffyBox container) {
        value = Unsafe("justify-content", value);

        var flip = value is "start" or "end" && container.IsReverse;

        return value switch {
            "flex-start" or "normal" => Justify.FlexStart,
            "flex-end" => Justify.FlexEnd,
            "start" => flip ? Justify.FlexEnd : Justify.FlexStart,
            "end" => flip ? Justify.FlexStart : Justify.FlexEnd,
            "center" => Justify.Center,
            "space-between" => Justify.SpaceBetween,
            "space-around" => Justify.SpaceAround,
            "space-evenly" => Justify.SpaceEvenly,
            _ => throw new TaffyUnsupportedException($"justify-content: {value}")
        };
    }

    /// <summary>Strips an <c>unsafe</c> prefix, and refuses a <c>safe</c> one.</summary>
    static string Unsafe(string property, string value) {
        if (value.StartsWith("safe ", StringComparison.Ordinal)) {
            throw new TaffyUnsupportedException($"{property}: {value}");
        }

        return value.StartsWith("unsafe ", StringComparison.Ordinal) ? value["unsafe ".Length..] : value;
    }

    /// <summary>Whether a value names the item's own axis rather than its container's.</summary>
    public static bool IsSelfRelative(string value) =>
        value is "self-start" or "self-end" or "unsafe self-start" or "unsafe self-end";

    /// <summary>Resolves a container's self-relative <c>align-items</c> for one particular child.</summary>
    public static Align AlignItemsForChild(string value, TaffyBox container, TaffyBox item) =>
        CrossAlign("align-items", value, container, item);

    static StyleLength Length(string value) =>
        value switch {
            "auto" => StyleLength.Auto,
            "min-content" => throw new TaffyUnsupportedException("min-content sizing"),
            "max-content" => StyleLength.Keyword(LayoutUnit.MaxContent),
            _ when value.EndsWith("px", StringComparison.Ordinal) => StyleLength.Points(Number(value[..^2])),
            _ when value.EndsWith('%') => StyleLength.Percent(Number(value[..^1])),
            _ when value.StartsWith("fit-content(", StringComparison.Ordinal) => throw new TaffyUnsupportedException("fit-content()"),
            _ when value.EndsWith("fr", StringComparison.Ordinal) => throw new TaffyUnsupportedException("fr units"),
            _ => StyleLength.Points(Number(value))
        };

    static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);
}
