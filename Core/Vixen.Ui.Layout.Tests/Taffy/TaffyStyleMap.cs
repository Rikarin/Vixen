// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

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
/// <summary>The facts about a box that its own attributes decide, and that its neighbours' mapping needs.</summary>
/// <remarks>
///     ⚠ <b><see cref="IsLeaf" /> is not about <c>start</c> and <c>end</c> like the other four, and
///     it is here because it decides whether an attribute is <i>inert</i>.</b> A property whose value
///     cannot move a single box is not a gap in the store, and refusing it costs a fixture that would
///     otherwise assert everything else it sets. See <c>UnsupportedFixtures.txt</c> § HARNESS.
///
///     ⚠ <b>There was a <c>Scrolls</c> beside it and there is not any more, which is the shape a
///     harness gap closes in.</b> It existed to decide whether <c>scrollbar-width</c> could be
///     accepted as inert or had to be refused; <see cref="LayoutStyle.ScrollbarWidth" /> now takes
///     the value on every box, so the question the flag answered has stopped being asked.
/// </remarks>
/// <param name="IsColumn">Whether its main axis is the block axis, which puts the cross axis inline.</param>
/// <param name="IsReverse">Whether its main axis runs backwards.</param>
/// <param name="WrapReverse">Whether its cross axis runs backwards.</param>
/// <param name="Rtl">Whether its inline axis runs right to left.</param>
/// <param name="IsLeaf">Whether it has no children, so nothing inside it can see its axes.</param>
readonly record struct TaffyBox(
    bool IsColumn,
    bool IsReverse,
    bool WrapReverse,
    bool Rtl,
    bool IsLeaf
) {
    public static TaffyBox From(TaffyInput input) {
        var attributes = input.Attributes;
        var direction = attributes.GetValueOrDefault("flex-direction", "row");

        return new TaffyBox(
            direction.StartsWith("column", StringComparison.Ordinal),
            direction.EndsWith("-reverse", StringComparison.Ordinal),
            attributes.GetValueOrDefault("flex-wrap") == "wrap-reverse",
            attributes.GetValueOrDefault("direction") == "rtl",
            input.Children.Count == 0
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
                        "grid" => Display.Grid,

                        // ⚠ <b>Mapped since doc 43 § B3, and not one fixture reaches them.</b> The
                        // corpus's `display` attribute takes exactly five values across all eight
                        // files — `block` (2 276), `flex` (764), `flow-root` (12), `grid` (2 496) and
                        // `none` (68) — verified by enumeration rather than assumed. So unlike block
                        // and grid, whose keywords unlocked two thousand right answers apiece the day
                        // they were added, these three unlock nothing: inline formatting is the one
                        // layout mode Taffy's corpus has no opinion about, because Taffy delegates
                        // text and inline layout to its caller and never generates a fixture for it.
                        // They are mapped anyway so this bridge keeps telling the truth about what
                        // the store supports; the oracle for them is WPT, in InlineFormattingTests.
                        "inline" => Display.Inline,
                        "inline-block" => Display.InlineBlock,
                        "inline-flex" => Display.InlineFlex,

                        // ⚠ <b>`flow-root` is NOT an alias for `block`, and the fixture that says so
                        // is the one this keyword is written on.</b> A flow root establishes a block
                        // formatting context whatever its `overflow` says, so
                        // `block_flow_root_margin_non_collapse` puts one beside a plain `block` with
                        // byte-identical content and expects 60 points against 10. `Display` grew a
                        // member for it rather than being lied to.
                        "flow-root" => Display.FlowRoot,

                        // `inline-grid` still lands here.
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
                    var (align, overflow) = CrossAlign(name, value, self, self);
                    tree.SetAlignItems(node, align, overflow);
                }

                break;

            case "align-self": {
                var (align, overflow) = CrossAlign(name, value, parent, self);
                tree.SetAlignSelf(node, align, overflow);
                break;
            }

            case "align-content": {
                var (align, overflow) = CrossAlign(name, value, self, self);
                tree.SetAlignContent(node, align, overflow);
                break;
            }

            case "justify-content": {
                var (justify, overflow) = Justification(value, self);
                tree.SetJustifyContent(node, justify, overflow);
                break;
            }

            // The inline-axis pair. ⚠ `justify-items: self-*` is pushed down onto the children by
            // TaffyFixtureRunner.Build, exactly as `align-items: self-*` is, so it is not applied here.
            case "justify-items":
                if (!IsSelfRelative(value)) {
                    var (align, overflow) = GridAlign(name, value, self, self);
                    tree.SetJustifyItems(node, align, overflow);
                }

                break;

            case "justify-self": {
                var (align, overflow) = GridAlign(name, value, parent, self);
                tree.SetJustifySelf(node, align, overflow);
                break;
            }

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

            // ── Grid ────────────────────────────────────────────────────────────────────────────
            case "grid-template-columns":
            case "grid-template-rows": {
                var (list, repeat, index, count) = TaffyTrackListParser.Parse(name, value);
                var parsed = CollectionsMarshal.AsSpan(list);
                var rows = name == "grid-template-rows";

                if (repeat == GridAutoRepeat.None) {
                    if (rows) {
                        tree.SetGridTemplateRows(node, parsed);
                    } else {
                        tree.SetGridTemplateColumns(node, parsed);
                    }
                } else if (rows) {
                    tree.SetGridTemplateRows(node, parsed, repeat, index, count);
                } else {
                    tree.SetGridTemplateColumns(node, parsed, repeat, index, count);
                }

                break;
            }

            case "grid-auto-columns":
            case "grid-auto-rows": {
                var (list, repeat, _, _) = TaffyTrackListParser.Parse(name, value);

                // ⚠ An implicit track list is a *cycle*, not a template — §7.5 walks it modulo its
                // length as implicit tracks are created — so `auto-fill` has nothing to fill against
                // and no position to be stored at. CSS's grammar does not admit one; a corpus that
                // grows one is refused rather than silently flattened into a fixed list.
                if (repeat != GridAutoRepeat.None) {
                    throw new TaffyUnsupportedException($"{name}: {value}");
                }

                var parsed = CollectionsMarshal.AsSpan(list);

                if (name == "grid-auto-rows") {
                    tree.SetGridAutoRows(node, parsed);
                } else {
                    tree.SetGridAutoColumns(node, parsed);
                }

                break;
            }

            case "grid-auto-flow": tree.SetGridAutoFlow(node, AutoFlow(value)); break;

            // ⚠ Edge.Top/Bottom is the ROW pair and Edge.Left/Right the COLUMN pair — see the
            // remarks on SetGridPlacement. Reading `grid-row-start` as a left edge because "row" and
            // "left" both feel horizontal transposes the whole grid and is the mistake this comment
            // exists to stop.
            case "grid-row-start": tree.SetGridPlacement(node, Edge.Top, Placement(name, value)); break;
            case "grid-row-end": tree.SetGridPlacement(node, Edge.Bottom, Placement(name, value)); break;
            case "grid-column-start": tree.SetGridPlacement(node, Edge.Left, Placement(name, value)); break;
            case "grid-column-end": tree.SetGridPlacement(node, Edge.Right, Placement(name, value)); break;

            // ── Inert here, and refused only where it is not ─────────────────────────────────────
            // ⚠ These two arms are the difference between "the store cannot do this" and "this
            // fixture never asked it to", and conflating them cost 124 fixtures their assertions —
            // 30% of every refusal in the corpus. See UnsupportedFixtures.txt.

            // ⚠ A scrollbar gutter is reserved by a SCROLL CONTAINER, not by the property, and the
            // store now says so itself: `SetScrollbarWidth` takes the value on every box and
            // `Overflow.Scroll` on an axis is what turns it into space. `overflow: hidden` clips
            // without a scrollbar, so the 156 elements that declare it beside `hidden` still reserve
            // nothing — the difference has simply moved out of this switch and into the algorithm,
            // where it belongs.
            case "scrollbar-width": tree.SetScrollbarWidth(node, Number(value)); break;

            // ⚠ `writing-mode` turns the box's axes, and on a childless box very nearly the only
            // thing that can see them is its own intrinsic measurement — which TaffyAhemMeasure has
            // modelled since it was written, by swapping which physical axis it treats as inline.
            // Every one of the corpus's 24 `vertical-lr` elements is such a leaf, so the vertical
            // branch of that measure function was reachable in principle and dead in fact: the
            // attribute loop threw before `SetMeasureFunction` was ever called. A box with children
            // genuinely needs a writing mode in LayoutStyle, which there is not one of, so that
            // keeps its refusal.
            //
            // ⚠ "Very nearly" is doing real work in that sentence and the four `grid_relayout_`
            // `vertical_text` fixtures are what it costs. A leaf's CONTAINER also states its
            // constraint in flow-relative terms: Chrome gives a turned grid item the ROW height as
            // its inline constraint, and this store, which has no writing mode to consult, gives it
            // the COLUMN width. So accepting the attribute here buys 16 fixtures that were asserting
            // nothing and exposes 4 that disagree. Both halves are the point; see GridKnownGaps.txt,
            // which now names the first thing in this corpus that really needs a writing mode on
            // LayoutStyle.
            case "writing-mode":
                if (value != "vertical-lr" || !self.IsLeaf) {
                    throw new TaffyUnsupportedException($"{name}: {value}");
                }

                break;

            // ⚠ <b>The three values here are the LEGACY ones and they are not `text-align`.</b>
            // CSS Text §7.1: `-webkit-left`, `-webkit-center` and `-webkit-right` align a block
            // container's BLOCK-LEVEL children, which plain `left`/`center`/`right` do not — those
            // move only inline content, which is a different rule reaching a different algorithm.
            // So this maps onto `LegacyTextAlign` rather than onto a general `text-align` field, and
            // an unprefixed value is refused rather than being folded in with them: the corpus
            // writes none, and accepting one would put every block child of a `text-align: center`
            // container in the wrong place. `text-align` proper is owed in `InlineKnownGaps.txt`.
            case "text-align":
                tree.SetLegacyTextAlign(
                    node,
                    value switch {
                        "-webkit-left" => LegacyTextAlign.Left,
                        "-webkit-center" => LegacyTextAlign.Center,
                        "-webkit-right" => LegacyTextAlign.Right,
                        _ => throw new TaffyUnsupportedException($"{name}: {value}")
                    }
                );

                break;

            // ── Everything Vixen has no field for ───────────────────────────────────────────────
            // Refused by name so that a failure report says which property is missing rather than
            // which number is wrong. B1 and B2 deleted most of these lines as they landed.
            case "float":
            case "clear":

            // ⚠ Named areas are not a track list and are deliberately still refused even though B2
            // landed. `grid-template-areas` declares named *lines* that `grid-row-start: header`
            // then points at, and neither GridPlacement nor the track arena carries a name — so the
            // only faithful answer is a refusal. No fixture in the corpus writes one today; the arm
            // is kept so that a refreshed corpus that does is skipped rather than mis-parsed.
            case "grid-template-areas":
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
    ///         Both halves of the value come back from <see cref="Overflowing" />: the position,
    ///         which is what this method resolves, and CSS Box Alignment §4.4's prefix, which the
    ///         caller hands to <see cref="LayoutStyle.AlignSelfOverflow" /> and its siblings.
    ///         ⚠ <c>safe X</c> used to be refused outright, and rightly — mapping it onto plain
    ///         <c>X</c> is wrong on precisely the fixtures named <c>_overflow</c>. What changed is
    ///         the store, not the translation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <i>grid</i> container's <c>align-*</c> comes through here too, and the answer
    ///         is right for a stated reason rather than by luck.</b> On a grid, <c>align-items</c>
    ///         and friends name the BLOCK axis, where <c>start</c> is unambiguous in every writing
    ///         mode this store supports. This method would nonetheless flip it on
    ///         <see cref="TaffyBox.WrapReverse" /> and flip <c>self-start</c> on
    ///         <see cref="TaffyBox.IsColumn" /> — both of which are false for a grid container,
    ///         because <see cref="TaffyBox.From" /> reads <c>flex-direction</c> and <c>flex-wrap</c>
    ///         and no <c>display: grid</c> element in the corpus sets either to a reversing value.
    ///         So the flex resolution degenerates to the identity, which is exactly the block-axis
    ///         answer. The <i>inline</i> axis is a different matter and gets
    ///         <see cref="GridAlign" />; see its remarks for why the two cannot share a method.
    ///     </para>
    /// </remarks>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="value">Its value.</param>
    /// <param name="container">The box whose cross axis this aligns against.</param>
    /// <param name="item">The box being aligned — the same one for <c>align-items</c>.</param>
    static (Align Align, OverflowAlignment Overflow) CrossAlign(string property, string value, TaffyBox container, TaffyBox item) {
        (value, var overflow) = Overflowing(value);

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

        return (resolved, overflow);
    }

    // ── Grid ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The same keywords on a <i>grid</i> container's inline axis, which is not a flex axis.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists rather than reusing <see cref="CrossAlign" /> because every fact
    ///         <see cref="CrossAlign" /> resolves against is a flex fact.</b> It flips <c>start</c>
    ///         on <see cref="TaffyBox.WrapReverse" /> and <c>self-start</c> additionally on
    ///         <see cref="TaffyBox.IsColumn" />, and a grid container has neither a wrap nor a main
    ///         axis. Feeding a grid through it works today only because both fields are false for
    ///         every grid container in the corpus — verified: no <c>display: grid</c> element in the
    ///         2 120 fixtures carries a <c>flex-direction</c>, and the four that carry
    ///         <c>flex-wrap: wrap</c> are not <c>wrap-reverse</c>. Depending on that would be
    ///         depending on an accident, and the next corpus refresh gets to break it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>start</c> is <i>not</i> flipped for RTL here, and that is the deliberate
    ///         answer to the writing-mode question, not an oversight.</b> This store's
    ///         <see cref="Align" /> and <see cref="Justify" /> are inline-relative on a row axis, not
    ///         physical: <c>FlexAxis.Resolve(FlexDirection.Row, Direction.Rtl)</c> returns
    ///         <c>RowReverse</c> and <c>FlexAxis.FlexStartEdge(RowReverse)</c> returns
    ///         <see cref="Edge.Right" />, so <see cref="Align.FlexStart" /> on an inline axis already
    ///         <i>means</i> the right-hand edge under <c>direction: rtl</c> — the flex path resolves
    ///         it that way for every plain <c>flex-direction: row</c> container in the flex corpus.
    ///         <see cref="LayoutTree.SetJustifyItems" /> documents the same contract in as many
    ///         words. Resolving RTL a second time here would apply the flip twice and put every
    ///         <c>justify-self: start</c> item in an RTL grid against the wrong edge, which reads as
    ///         a placement bug rather than as a translation one. <c>justify-content</c> goes through
    ///         <see cref="Justification" /> for exactly the same reason and needs no grid twin: its
    ///         only flip is on <c>*-reverse</c>, which a grid never has.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>self-start</c> is the one keyword that <i>does</i> need work</b>, because it
    ///         resolves against the <i>item's</i> direction while the algorithm will resolve
    ///         <see cref="Align.FlexStart" /> against the <i>container's</i>. When the two disagree
    ///         the answer inverts, and ten nodes in the corpus disagree —
    ///         <c>grid_justify_items_self_end_child_rtl</c> is the clean case: an <c>rtl</c> child and
    ///         an <c>ltr</c> child of the same container land on opposite edges of their areas from
    ///         one declaration.
    ///     </para>
    ///     <para>
    ///         The overflow prefix is split off by <see cref="Overflowing" /> here exactly as it is
    ///         for flex, and travels to <see cref="LayoutStyle.JustifySelfOverflow" /> beside the
    ///         position rather than into it.
    ///     </para>
    /// </remarks>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="value">Its value.</param>
    /// <param name="container">The grid whose inline axis this aligns against.</param>
    /// <param name="item">The box being aligned — the same one for <c>justify-items</c>.</param>
    static (Align Align, OverflowAlignment Overflow) GridAlign(string property, string value, TaffyBox container, TaffyBox item) {
        (value, var overflow) = Overflowing(value);

        var flip = value is "self-start" or "self-end" && container.Rtl != item.Rtl;

        var resolved = value switch {
            "flex-start" or "start" => Align.FlexStart,
            "flex-end" or "end" => Align.FlexEnd,
            "self-start" => flip ? Align.FlexEnd : Align.FlexStart,
            "self-end" => flip ? Align.FlexStart : Align.FlexEnd,
            "center" => Align.Center,

            // CSS Box Alignment §6.2: `normal` behaves as `stretch` for a grid item.
            "stretch" or "normal" => Align.Stretch,
            "baseline" => Align.Baseline,
            "space-between" => Align.SpaceBetween,
            "space-around" => Align.SpaceAround,
            "space-evenly" => Align.SpaceEvenly,
            "auto" => Align.Auto,
            _ => throw new TaffyUnsupportedException($"{property}: {value}")
        };

        return (resolved, overflow);
    }

    /// <summary>Resolves a grid container's self-relative <c>justify-items</c> for one child.</summary>
    public static (Align Align, OverflowAlignment Overflow) JustifyItemsForChild(string value, TaffyBox container, TaffyBox item) =>
        GridAlign("justify-items", value, container, item);

    /// <summary><c>grid-auto-flow</c>, whose two words may be written in either order.</summary>
    /// <remarks>
    ///     The corpus writes only <c>column</c>, <c>row dense</c> and <c>column dense</c> — the
    ///     initial <c>row</c> is never spelled out and <c>dense</c> never comes first. §8.5's grammar
    ///     is <c>[ row | column ] || dense</c>, so both orderings and both bare words are legal and
    ///     all four are accepted here rather than left as a trap for a refreshed corpus.
    /// </remarks>
    static GridAutoFlow AutoFlow(string value) {
        var column = false;
        var dense = false;

        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            switch (word) {
                case "row": break;
                case "column": column = true; break;
                case "dense": dense = true; break;
                default: throw new TaffyUnsupportedException($"grid-auto-flow: {value}");
            }
        }

        return column
            ? dense ? GridAutoFlow.ColumnDense : GridAutoFlow.Column
            : dense ? GridAutoFlow.RowDense : GridAutoFlow.Row;
    }

    /// <summary>One of the four <c>grid-{row,column}-{start,end}</c> values.</summary>
    /// <remarks>
    ///     ⚠ The whole grammar the corpus uses is <c>-?&lt;integer&gt;</c> or
    ///     <c>span &lt;integer&gt;</c> — all 6 636 occurrences, with no <c>auto</c>, no bare
    ///     <c>span</c> and no named line among them. <c>auto</c> is accepted anyway because it is
    ///     the initial value and costs one arm; a name is refused, because
    ///     <see cref="GridPlacement" /> has nowhere to put one.
    /// </remarks>
    /// <param name="property">The attribute name, for the refusal message.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The placement.</returns>
    static GridPlacement Placement(string property, string value) {
        if (value == "auto") {
            return GridPlacement.Auto;
        }

        if (value.StartsWith("span ", StringComparison.Ordinal)) {
            return GridPlacement.Span(Integer(property, value["span ".Length..]));
        }

        return GridPlacement.Line(Integer(property, value));
    }

    static int Integer(string property, string value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new TaffyUnsupportedException($"{property}: {value}");

    /// <summary>The same resolution on the main axis, where <c>*-reverse</c> plays wrap-reverse's part.</summary>
    static (Justify Justify, OverflowAlignment Overflow) Justification(string value, TaffyBox container) {
        (value, var overflow) = Overflowing(value);

        var flip = value is "start" or "end" && container.IsReverse;

        var resolved = value switch {
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

        return (resolved, overflow);
    }

    /// <summary>
    ///     Splits CSS Box Alignment §4.4's overflow prefix off the front of an alignment value.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The prefix is not a value and the position is not a modifier</b> — the grammar is
    ///     <c>[ safe | unsafe ]? &lt;position&gt;</c>, two independent halves — so this returns the
    ///     half the four resolvers below know how to read and hands the other half back for
    ///     <see cref="LayoutStyle.AlignSelfOverflow" /> and its siblings. <c>unsafe X</c> is exactly
    ///     <c>X</c>, because unsafe is what a bare keyword already means; <c>safe X</c> is <c>X</c>
    ///     with <see cref="OverflowAlignment.Safe" /> beside it.
    /// </remarks>
    /// <param name="value">The attribute's value, prefix and all.</param>
    /// <returns>The position on its own, and what the prefix said about it.</returns>
    static (string Value, OverflowAlignment Overflow) Overflowing(string value) =>
        value.StartsWith("safe ", StringComparison.Ordinal) ? (value["safe ".Length..], OverflowAlignment.Safe)
        : value.StartsWith("unsafe ", StringComparison.Ordinal) ? (value["unsafe ".Length..], OverflowAlignment.Unsafe)
        : (value, OverflowAlignment.Unsafe);

    /// <summary>Whether a value names the item's own axis rather than its container's.</summary>
    public static bool IsSelfRelative(string value) =>
        Overflowing(value).Value is "self-start" or "self-end";

    /// <summary>Resolves a container's self-relative <c>align-items</c> for one particular child.</summary>
    public static (Align Align, OverflowAlignment Overflow) AlignItemsForChild(string value, TaffyBox container, TaffyBox item) =>
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
