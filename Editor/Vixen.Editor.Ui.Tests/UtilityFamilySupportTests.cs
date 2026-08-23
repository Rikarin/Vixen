// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;
using Vixen.Ui.Styling.Utilities;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Which utility families the engine actually reads, resolved rather than believed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>"It resolves" and "something reads it" are two different questions, and only the
///         first one a stylesheet can answer.</b> Every family below emits syntactically valid CSS
///         and the cascade computes a value for every one of them — so a test that stops at
///         <c>StyleOf(element, property) is not null</c> passes for the inert families too. The
///         second question is answered by the property tables in <c>LayoutStyleBuilder</c>,
///         <c>DrawListBuilder</c>, <c>UiDocument</c>, <c>Cursor</c>, <c>Animator</c> and
///         <c>ComputedText</c>: a property no consumer interns is a rule that computes and then
///         nothing happens.
///     </para>
///     <para>
///         <see cref="Supported" /> is the first question for the families that survive the second.
///         <see cref="Inert" /> is the first question for the families that do not — kept in the
///         suite rather than deleted, because a family becoming real is a thing this file should
///         notice, and because "it resolves, and that is all it does" is exactly the fact somebody
///         reaching for <c>select-none</c> needs told. The per-axis <c>overflow</c> pair is the
///         standing proof that the notice is worth having: it sat in <see cref="Inert" /> until the
///         engine learned it, and moving it was a row in each table and a test that reversed.
///     </para>
///     <para>
///         The two <c>Fact</c>s at the bottom look at what the layout and the draw list did rather
///         than at what the cascade stored, which is the only way either question gets a real answer.
///         One of them still proves inertness and the other now proves the opposite. They are the
///         shape the rest of this table would take if every property had as cheap an observable.
///     </para>
///     <para>
///         ⚠ <b>The division of labour with
///         <c>Core/Vixen.Ui.Styling.Utilities.Tests/UtilityConsumptionGateTests</c>, because the two
///         look alike and neither can do the other's job.</b> That one is a <i>gate</i>: it enumerates
///         the family registry, measures what every family emits after ExCSS expansion, and fails the
///         build if any property moves nothing in the engine — so nothing escapes classification and
///         no new family can be added inert without a task number. It is coarse on purpose, and its
///         verdict is "something acted on this". This file is the other half: it names the utility a
///         person actually writes, against the editor's own tokens, and asserts <i>what</i> happened —
///         a bottom border paints a band along the bottom, a per-corner radius leaves the other three
///         square, a per-axis clip is unbounded on the axis it did not name. The gate would pass every
///         one of those with the wrong edge painted. Neither subsumes the other, and the
///         <see cref="Supported" /> and <see cref="Inert" /> tables are hand-maintained precisely so
///         that a person has to look at them.
///     </para>
///     <para>
///         ⚠ <b>Three rows moved from <see cref="Supported" /> to <see cref="Inert" /> when the gate
///         first ran, and that is the argument for the gate in one sentence.</b> The
///         <c>transition-*</c> trio had been sitting in the supported table because the cascade
///         computed a value for each — the exact reading this file's own first paragraph says is not
///         enough.
///     </para>
/// </remarks>
public class UtilityFamilySupportTests {
    /// <summary>Utility, property, and the value the cascade must compute for it.</summary>
    /// <remarks>
    ///     One row per family the engine reads, against the editor's own tokens — so
    ///     <c>bg-surface</c> here is the same <c>var(--surface)</c> the hand-written sheet uses, and
    ///     the spacing rows are in steps of four, which is Tailwind's base and now the editor's too.
    /// </remarks>
    public static TheoryData<string, string, string> Supported => new() {
        // Layout. ⚠ `block` moved up from `Inert` when doc 43 § B1 landed and it is the first row in
        // this file to change tables because an *algorithm* arrived rather than because a property
        // found a reader. ⚠ `grid` is the second, with § B2 — and it moved while its own family did
        // not, which is the state this file recorded for exactly as long as that was true.
        // `grid-cols-*` and `col-span-*` have now followed it, and with doc 43 § B3 so have the three
        // `inline*` utilities this comment used to say were staying behind.
        //
        // ⚠ <b>`align-top` moves and `align-middle` does not, and they are the same family.</b> That
        // is the sharpest row-level distinction in this file. `vertical-align` is read now — there
        // are line boxes to align to — but only three of its eight values are, and the five that are
        // not are refused at the bridge rather than approximated: `middle`, `text-top`,
        // `text-bottom`, `sub` and `super` are each defined against the parent's font strut, and
        // `Vixen.Ui.Layout` has no font. A family is not a property, and half a family being real is
        // a state this file has to be able to express.
        //
        // ⚠ These two are the first rows here whose *emitted value* changed as they moved rather
        // than only their reader. `grid-cols-3` used to compute `grid-template-columns: 3` and the
        // old expectation would have been that string — a table row asserting the family produced
        // something no engine could ever read. That is why the expectations below are the CSS
        // Tailwind writes, and why the two facts further down measure a box instead.
        { "flex", "display", "flex" },
        { "hidden", "display", "none" },
        { "block", "display", "block" },
        { "grid", "display", "grid" },
        { "inline", "display", "inline" },
        { "inline-block", "display", "inline-block" },
        { "inline-flex", "display", "inline-flex" },
        { "align-top", "vertical-align", "top" },
        { "align-bottom", "vertical-align", "bottom" },
        { "align-baseline", "vertical-align", "baseline" },
        { "grid-cols-3", "grid-template-columns", "repeat(3, minmax(0, 1fr))" },

        // ⚠ <b>`col-span-2` emits `grid-column` and the cascade never holds it</b>, which is the
        // `flex-1` note below arriving for a second family and for a nearly opposite reason. ExCSS
        // has never heard of `grid-column`, so it hands the shorthand back whole — and a shorthand
        // that reaches a computed style intact is what let a `grid-column-start` from any other rule
        // beat a later `grid-column` outright, silently, whatever order they were written in.
        // `ShorthandExpansion` now splits it at load, so the row names the longhand for the same
        // reason `flex-1` does: it is what the cascade ends up holding and what the bridge reads.
        { "col-span-2", "grid-column-start", "span 2" },
        { "col-span-2", "grid-column-end", "span 2" },
        { "flex-col", "flex-direction", "column" },
        { "flex-wrap", "flex-wrap", "wrap" },
        { "items-center", "align-items", "center" },
        { "self-start", "align-self", "flex-start" },
        { "justify-between", "justify-content", "space-between" },
        { "content-center", "align-content", "center" },

        // Flex. `flex-1` is a shorthand ExCSS expands while parsing, so the cascade only ever sees
        // the three longhands — which is why the assertion names one of them.
        { "flex-1", "flex-grow", "1" },
        { "grow", "flex-grow", "1" },
        { "shrink-0", "flex-shrink", "0" },
        { "basis-0", "flex-basis", "0" },

        // ⚠ `order` was in `Inert` — filed under *grid*, which it is not — until `LayoutStyle` grew
        // a field for it. See `An_ordered_item_is_laid_out_and_painted_in_its_ordinal_group`.
        { "order-2", "order", "2" },

        // Spacing, including the logical edges the layout resolves against `direction`.
        // ⚠ **Every number here doubled when the editor's spacing base went from 2 to 4.** The base
        // of 2 was justified by the chrome being drawn on a two-pixel rhythm — 6px was its commonest
        // gutter — and the chrome is being redone. What the exception cost is what these rows now
        // show: `p-4` in the editor meant 8px where `p-4` everywhere else in the Tailwind world means
        // 16px, so every measurement a designer or a Tailwind author brought with them was half size.
        { "gap-3", "row-gap", "12px" },
        { "gap-x-2", "column-gap", "8px" },
        { "gap-y-2", "row-gap", "8px" },
        { "p-3", "padding-top", "12px" },
        { "px-2", "padding-left", "8px" },
        { "pt-1", "padding-top", "4px" },
        { "ps-2", "padding-inline-start", "8px" },
        { "m-2", "margin-top", "8px" },
        { "me-1", "margin-inline-end", "4px" },

        // ⚠ <b>Scroll insets — doc 43 A18, and note what the shorthand rows are <i>not</i>.</b>
        // `scroll-m-2` emits four longhands where `m-2` emits the one shorthand `margin`, because
        // ExCSS expands `margin` on the way in and has never heard of `scroll-margin`. So a row here
        // asserting `scroll-m-2` → `scroll-margin` would be asserting the emission that does not
        // work; the per-edge value is the one that reaches `ScrollView`. The logical pair keeps CSS's
        // spelling for the reason `ms-*` does — `ScrollView.InsetOf` folds it against `direction` —
        // and there are no `scroll-mbs-*`/`scroll-pbs-*` rows because the block pair is registered by
        // nobody, exactly as `inset-bs-*` records below.
        { "scroll-m-2", "scroll-margin-top", "8px" },
        { "scroll-mt-1", "scroll-margin-top", "4px" },
        { "scroll-mx-2", "scroll-margin-left", "8px" },
        { "scroll-ms-2", "scroll-margin-inline-start", "8px" },
        { "scroll-p-2", "scroll-padding-top", "8px" },
        { "scroll-pb-1", "scroll-padding-bottom", "4px" },
        { "scroll-pe-2", "scroll-padding-inline-end", "8px" },

        // The three keyword families that go with them. `scroll-smooth` is the one worth a row of its
        // own: it is the only member of any of these that changes *when* something happens rather
        // than where, and `ScrollView` animates it off `UiDocument.Ticked`.
        { "scroll-smooth", "scroll-behavior", "smooth" },
        { "overscroll-contain", "overscroll-behavior", "contain" },
        { "overscroll-y-none", "overscroll-behavior-y", "none" },

        // Sizing.
        { "w-full", "width", "100%" },
        { "h-4", "height", "16px" },
        { "min-w-0", "min-width", "0" },
        { "max-w-40", "max-width", "160px" },

        // Position.
        { "absolute", "position", "absolute" },
        { "relative", "position", "relative" },
        { "top-0", "top", "0" },
        { "inset-x-1", "left", "4px" },
        { "start-2", "inset-inline-start", "8px" },

        // ⚠ <b>v4's four logical insets, and the pair of them is here because the two halves emit
        // different <i>kinds</i> of longhand on purpose.</b> `inset-s-*` keeps CSS's logical spelling
        // because the layout reads it and mirrors it under `direction: rtl`; `inset-bs-*` emits the
        // physical `top`, because `inset-block-start` is interned by nobody and `Vixen.Ui.Layout` has
        // no writing mode for the block axis to be anything but top-to-bottom. Asserting the
        // *property* rather than only the value is what makes these two rows worth having: a later
        // hand "correcting" the second to Tailwind's spelling would leave a class that cascades
        // perfectly and moves no box.
        { "inset-s-2", "inset-inline-start", "8px" },
        { "inset-bs-2", "top", "8px" },
        { "z-10", "z-index", "10" },
        { "box-border", "box-sizing", "border-box" },

        // Typography. `text-` is alignment, then size, then colour, resolved in that order.
        { "text-center", "text-align", "center" },
        { "text-sm", "font-size", "11px" },
        { "text-text-muted", "color", "#5c616b" },
        { "font-semibold", "font-weight", "600" },
        { "leading-8", "line-height", "32px" },
        { "leading-tight", "line-height", "1.25" },
        { "tracking-px", "letter-spacing", "1px" },
        { "whitespace-nowrap", "white-space", "nowrap" },

        // Text decoration. ⚠ `decoration-` is the second three-way prefix in this table — a keyword
        // is a thickness or a style, and anything else is a colour — so all three are here rather
        // than one standing for the family. And `underline-offset-2` is here because it is the pair
        // where a *shorter* family name is a prefix of a longer one and both are registered: if the
        // name split ever stopped sorting longest-first, this row would resolve as `underline` with
        // the offset silently dropped, which the row above it would not notice.
        { "underline", "text-decoration-line", "underline" },
        { "overline", "text-decoration-line", "overline" },
        { "line-through", "text-decoration-line", "line-through" },
        { "no-underline", "text-decoration-line", "none" },
        { "underline-offset-2", "text-underline-offset", "2px" },
        { "decoration-2", "text-decoration-thickness", "2px" },
        { "decoration-double", "text-decoration-style", "double" },
        { "decoration-text-muted", "text-decoration-color", "#5c616b" },

        // Paint.
        { "bg-surface-raised", "background-color", "#f2f3f6" },
        { "opacity-50", "opacity", "0.5" },

        // ⚠ <b>`blur-*` was the last row of A6's "paint the renderer has no channel for", and it left
        // that list the way `fill-*` and `ring-*` did — by the row being wrong about the disease.</b>
        // It named `--blur`, a property of this engine's own invention that nothing assembled and
        // nothing could read, so the debt was filed against a name that could never come due. It is a
        // composed family now: the fragment carries the length and an `Alongside` assembles a real
        // `filter`, which `DrawListBuilder` reads and both executors render. Asserted on `filter`
        // rather than on the fragment, because the fragment alone is what the old row already proved
        // is not evidence of anything.
        { "blur-2", "filter", "blur(8px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0)" },

        // ⚠ <b>Eight functions where the row above used to expect one, and the seven that joined it
        // are all identities.</b> That is what `UtilityComposition.Filter` assembles: every filter
        // family writes the same declaration and differs only in which fragment it sets, so a
        // `blur-2` on its own resolves the other seven through their `var()` fallbacks and gets
        // `brightness(1) contrast(1) …` for free. The alternative — emitting only the functions
        // somebody wrote — is the thing a per-class generator cannot do, and is argued at length in
        // `UtilityComposition`'s own remarks.
        //
        // ⚠ <b>And the order is fixed here rather than following the class list</b>, because classes
        // on an element are a set. `invert brightness-200` and `brightness-200 invert` are different
        // pictures in CSS and the same element here; v4 picks an order and this picks v4's.
        { "brightness-125", "filter", "blur(0px) brightness(1.25) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0)" },
        { "contrast-75", "filter", "blur(0px) brightness(1) contrast(0.75) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0)" },
        { "grayscale", "filter", "blur(0px) brightness(1) contrast(1) grayscale(1) hue-rotate(0deg) invert(0) saturate(1) sepia(0)" },
        { "hue-rotate-90", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(90deg) invert(0) saturate(1) sepia(0)" },
        { "invert", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(1) saturate(1) sepia(0)" },
        { "saturate-150", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1.5) sepia(0)" },
        { "sepia-50", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0.5)" },

        // ⚠ Two families composing into one declaration is the case this theory cannot state — a row
        // here is one class — so it is
        // <see cref="Two_filter_families_compose_into_one_declaration_and_one_matrix" /> instead.
        { "shadow-elevation", "box-shadow", "0px 10px 26px rgba(12, 14, 18, 0.22)" },

        // ⚠ <b>`fill-*` and `stroke-*` are the first rows here to move because a <i>consumer</i> was
        // found rather than written.</b> Everything the pair needed already existed: `IconPath`
        // carries a fill paint and a stroke paint, `DrawContext` has `FillField` and `Stroke`, and
        // `IconPaintKind.Foreground` is SVG's `currentColor` marker by another name. What was missing
        // was two `Document.ColorOf` calls in `Icon.Resolve` — and the two names in
        // `InheritedProperties`, without which the family works only when the class is written on the
        // icon itself and silently does nothing where anyone actually writes it. See
        // <see cref="The_fill_and_stroke_families_paint_an_icon_and_reach_it_by_inheritance" />.
        //
        // ⚠ These two rows are also the reason the parity gate's own project grew a reference: `Icon`
        // is in `Vixen.Ui.Controls`, so until `UtilityConsumptionProbe` could build one, the gate
        // measured both properties inert with the reader in place — `grid-cols-3`'s missing grid,
        // exactly.
        { "fill-accent", "fill", "#2f6ecd" },
        { "stroke-accent", "stroke", "#2f6ecd" },

        // Borders, all four edges and all four corners. ⚠ The colours and the radii were in `Inert`
        // until the draw list learned the rest of the longhands: it interned `border-top-color` and
        // `border-top-left-radius` and nothing else, so seven of the eight per-edge colours computed
        // a value nothing read, and `rounded-tl` was not a family at all.
        { "border-2", "border-top-width", "2px" },
        { "border-b", "border-bottom-width", "1px" },
        { "border-border-active", "border-top-color", "#5f8ddb" },
        { "border-b-border-active", "border-bottom-color", "#5f8ddb" },
        { "border-l-accent", "border-left-color", "#2f6ecd" },
        { "border-x-accent", "border-right-color", "#2f6ecd" },
        { "border-y-accent", "border-top-color", "#2f6ecd" },

        // The logical block edges, physical for the reason `inset-bs-*` is: nothing interns
        // `border-block-start-width`, the block axis never mirrors, and `border-top-*` is what the
        // draw list and the layout both read. Unlike `border-s-*`/`border-e-*` — the table's one
        // genuinely partial pair, whose widths are read and whose colours are not — these are read
        // on both longhands, so a colour row belongs here beside the width one.
        { "border-bs-2", "border-top-width", "2px" },
        { "border-be-2", "border-bottom-width", "2px" },
        { "border-be-accent", "border-bottom-color", "#2f6ecd" },

        // ⚠ <b>The arbitrary form was the only form here, and it is not any more.</b> This block used
        // to carry a note saying the editor's tokens defined no `radius` scale at all: the three
        // radii lived in `EditorTheme` as `var(--radius-row)` and friends, `ThemeTokens.Radius` was a
        // dictionary of `float`, and a token holding a reference was rejected with a diagnostic — so
        // the choice was the same number in two files or no radius tokens. `Radius` holds text now.
        // Both spellings are kept, and the pair is the point: the arbitrary escape hatch still works,
        // and the two kinds of token a theme can now hold — a length from the engine's shipped v4
        // scale, and a reference the editor's own sheet resolves — both reach the same property.
        { "rounded-[4px]", "border-top-left-radius", "4px 4px" },
        { "rounded-tl-[6px]", "border-top-left-radius", "6px" },
        { "rounded-br-[2px]", "border-bottom-right-radius", "2px" },
        { "rounded-t-[6px]", "border-top-right-radius", "6px" },
        { "rounded-b-[4px]", "border-bottom-left-radius", "4px" },
        { "rounded-lg", "border-top-left-radius", "8px 8px" },
        { "rounded-tl-row", "border-top-left-radius", "4px" },

        // ⚠ <b>All three radius tokens, because for a while only two of them were there.</b>
        // `--radius-row` and `--radius-control` resolved and `--radius-panel` did not, and the
        // difference between them was a paragraph of prose: the comment above these three
        // declarations in `vixen.ui.vcss` spelled a glob containing `*` `/`, CSS comments do not nest,
        // and the sentence that escaped ran on as one declaration until the first semicolon after it
        // — which was `--radius-panel`'s. A row for the first token of a run is the cheap way to
        // notice a comment that ate it, and the reason the row exists is that a single row would have
        // been the second one.
        { "rounded-tl-panel", "border-top-left-radius", "5px" },
        { "rounded-tl-control", "border-top-left-radius", "4px" },

        // Overflow, all three properties and all four keywords. ⚠ `auto` is here because the layout
        // maps it onto `Overflow.Scroll` — the two differ only by a scrollbar gutter nothing draws.
        { "truncate", "overflow", "hidden" },
        { "overflow-scroll", "overflow", "scroll" },
        { "overflow-auto", "overflow", "auto" },
        { "overflow-x-scroll", "overflow-x", "scroll" },

        // ⚠ These were in `Inert` until the per-axis clip landed. `auto` maps onto `Scroll` in the
        // layout's keyword table rather than becoming a fourth member, because CSS gives the two the
        // same layout and differs only over reserving a scrollbar gutter — which nothing here draws.
        { "overflow-x-auto", "overflow-x", "auto" },
        { "overflow-y-auto", "overflow-y", "auto" },
        { "overflow-y-scroll", "overflow-y", "scroll" },

        // Interactivity and motion.
        //
        // ⚠ <b>The three `transition-*` rows left this table and have come back, which no other row
        // has done.</b> They were here originally because the cascade computed a value for each — the
        // reading this file's own first paragraph says is not enough — and the parity gate's first run
        // moved them to `Inert` on finding that nothing in the repository ever built an `Animator`.
        // Doc 43 A20 built one, on the style engine, and the frame loop now `Observe`s a replaced
        // style, `Advance`s on the tick and `Apply`s before any consumer reads. What holds them here
        // is `Vixen.Ui.Tests.TransitionTests`, which reads a width and a colour *between* the two
        // endpoints — the only measurement that can tell a fade from a jump, and therefore the only
        // one that could have failed for the original reason.
        //
        // ⚠ <b>And a row that is honest about only being a row about the property: `transition` on
        // its own still does nothing.</b> Vixen's family emits `transition-property` and stops, where
        // Tailwind's `transition` also emits a 150 ms duration and a timing function — and a
        // transition whose duration is the initial `0s` is declined by the animator, correctly. So the
        // class needs a `duration-*` beside it to do anything at all. That is a gap in what the family
        // *emits*, not in what the engine reads, which is why it is recorded here rather than left as
        // an `Inert` row: the property below is genuinely read, and `transition-none` would be
        // refused by the same animator that honours `transition-property: all`.
        { "transition", "transition-property", "all" },
        { "duration-150", "transition-duration", "150ms" },
        { "ease-out", "transition-timing-function", "ease-out" },
        { "cursor-pointer", "cursor", "pointer" },
        { "pointer-events-none", "pointer-events", "none" },
        { "aspect-video", "aspect-ratio", "16/9" },

        // Transforms. ⚠ <b>The two translations are the first rows in this file to arrive by way of
        // the <i>composition</i> mechanism, and the property they name is not the one their family
        // sets.</b> `translate-x-2` sets `--tw-translate-x` and emits a `translate` assembled out of
        // both axes' fragments; what the cascade ends up holding — and what the engine reads — is the
        // assembly, which is why the row names `translate`. Written out, the expectation is also the
        // proof that the fallback chain works: `translate-x-2` alone resolves the y half out of
        // `--tw-translate-y`'s initial value rather than dropping the whole declaration, which is
        // what a bare `var()` with no fallback would have done. See
        // <see cref="The_two_translate_families_compose_and_move_the_box_and_its_hit_test" />.
        //
        // ⚠ <b>`scale-*` and `rotate-*` are the same family group and stay in `Inert`</b>, which is
        // the sharpest three-way split this file has recorded: one third of a group real, two thirds
        // refused, and all three emitting valid CSS. See the `Inert` rows for why neither is a
        // missing reader.
        { "translate-x-2", "translate", "8px 0px" },
        { "translate-y-2", "translate", "0px 8px" },

        // ⚠ <b>`ring-*` moved without anything in the engine learning a thing, which no other row in
        // this file has done.</b> Every previous move was a reader arriving or an algorithm landing.
        // This one was an <i>emission</i> that was simply wrong: the family emitted `outline-color`,
        // and no version of Tailwind has ever emitted that property for `ring-*`. The plan's § D5
        // calls it "v3's reading" and that is wrong too — v3 is where the ring was introduced *as a
        // box-shadow*, and v3's `ring-<color>` set `--tw-ring-color`. So `outline-color` was this
        // engine's own invention, and the `InertProperties.txt` line under it could never have come
        // due: a reader for it would have closed the debt and changed nothing anybody could see.
        // Fourth instance of that failure after `grid-template-columns: 3`, `grid-column: 2` and
        // `--scale`/`--rotate`.
        //
        // ⚠ <b>And the framing this task arrived with — that an outline is not a border, is drawn
        // outside the box, and therefore needs its own draw path — was right about the geometry and
        // wrong about the conclusion.</b> A ring *is* drawn outside the box and *is* invisible to
        // layout, and `DrawListBuilder` has expressed exactly that since it learned about spread:
        // `box-shadow: 0 0 0 2px` folds the spread into the command's rectangle and grows every
        // corner radius by it, giving a rounded box two points larger in every direction, painted
        // behind the background. No new draw path, no `outline` property, no fourth edge.
        //
        // Two rows because the family is composed the way the translations are — a width fragment, a
        // colour fragment, both classes assemblers. `ring-2` alone resolves its colour out of
        // `--tw-ring-color`'s initial, which is `currentcolor`, the one keyword `EmitShadow` learned.
        // `ring-accent` alone resolves a zero width and paints a shadow exactly the size of the box,
        // which the background then covers — the same nothing v4 gives it. See
        // <see cref="A_ring_paints_outside_the_box_and_costs_the_layout_nothing" />.
        { "ring-2", "box-shadow", "0 0 0 2px currentcolor" },
        { "ring-accent", "box-shadow", "0 0 0 0px #2f6ecd" }
    };

    /// <summary>Utility, property — the families that compute a value nothing in the engine reads.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a bug list.</b> The utilities README's phrasing is the right one: a rule that
    ///         resolves to a property no consumer looks at is a utility waiting for an engine feature.
    ///         What makes it worth writing down is that nothing anywhere else says so — the class name
    ///         is spelled correctly, the generator emits it, the cascade computes it, and the picture
    ///         does not change.
    ///     </para>
    ///     <para>
    ///         <b>History: <c>overflow-x-*</c> and <c>overflow-y-*</c> used to be the dangerous two</b>,
    ///         because the unprefixed <c>overflow</c> was read and the per-axis pair looked like it must
    ///         be, and neither <c>overflow-x</c> nor <c>overflow-y</c> was interned by anything. They
    ///         are read now — <c>OverflowReader</c> resolves all three for the clip stack and the hit
    ///         test alike — and they have moved to <see cref="Supported" />, with
    ///         <see cref="The_per_axis_overflow_utilities_clip_the_axis_they_name_and_no_other" />
    ///         holding the draw list to it. Kept here as the worked example of what this table is for:
    ///         the rows above are not permanent, and one of them changing tables is the outcome the
    ///         file exists to make visible.
    ///     </para>
    ///     <para>
    ///         <b>History: <c>overflow-auto</c> used to be a third case in neither table.</b> The draw
    ///         list clips on any value that is not <c>visible</c>, so it always clipped; the layout's
    ///         keyword table had <c>visible</c>, <c>hidden</c> and <c>scroll</c> and not <c>auto</c>, so
    ///         the layout went on treating the box as visible and the advice was to write
    ///         <c>overflow-scroll</c> instead. <c>LayoutStyleBuilder</c> maps <c>auto</c> onto
    ///         <c>Overflow.Scroll</c> now, which is the same thing CSS means by it — the two keywords
    ///         disagree only about a scrollbar gutter, and nothing here draws one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A clip is still not a scrollbar.</b> <c>overflow-y-auto</c> cuts the content off
    ///         and nothing offers to scroll it; scrolling in this engine is <c>ScrollView</c>, a
    ///         control that owns its bars and offsets its content.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The per-edge border colours and the per-corner radii are the second family to
    ///         leave this table, and they were worse than inert.</b> Inert is what
    ///         <c>border-b-accent</c> looked like — a <c>border-bottom-color</c> the draw list never
    ///         interned. What it actually did was delete the border: the builder read
    ///         <c>border-top-color</c> as *the* border colour, so an element given a bottom colour and
    ///         no top one had no colour at all and drew nothing. The radii were the mirror image —
    ///         <c>border-top-left-radius</c> rounded all four corners and the other three longhands
    ///         were ignored. See <see cref="A_per_edge_border_colour_paints_only_the_edge_it_names" />
    ///         and <see cref="A_per_corner_radius_rounds_only_the_corner_it_names" />.
    ///     </para>
    /// </remarks>
    public static TheoryData<string, string> Inert => new() {
        // ⚠ <b>The three `inline*` rows were here and are now in `Supported`.</b> What they used to
        // say was true and was a *prediction* as much as a record: they were inert deliberately,
        // because mapping them onto `Block` and `Flex` would have given `inline-block` the whole
        // line. Doc 43 § B3 built the inline formatting context instead of the alias, and the
        // proof that the distinction is real is
        // `An_inline_block_shares_its_line_instead_of_taking_it` — which measures two boxes on one
        // line, an assertion no computed-value check could make and the exact assertion that would
        // have failed against the alias.

        // ⚠ <b>`grid-cols-*` and `col-span-*` were here and are now in `Supported`.</b> What this row
        // used to say was accurate and was still not the whole story. It named the bridge — a track
        // list is arbitrary-length, a `LayoutStyle` is a fixed-size unmanaged struct, the tracks
        // live in the tree's `TrackArena` behind a node id `Build` never sees — and that was one of
        // *three* things wrong. The second: `grid-cols-3` emitted `grid-template-columns: 3`, which
        // is not a track list in any engine, so the family would have gone on doing nothing even
        // once a reader existed. The third: no scene in `UtilityConsumptionProbe` contained a grid,
        // so the parity gate measured both properties inert either way and could not have reported
        // the first two. See the closed block in `InertProperties.txt`.

        // ⚠ <b>The three `A6` paint rows were here and are now in `Supported`, and the three left for
        // three different reasons — which is why this block is a paragraph and not a deletion.</b>
        // `fill-*` and `stroke-*` were honestly inert: the emission was already v4's, and the
        // renderer turned out to have had the channel all along in `IconPath`'s two paints, so the
        // fix was two property reads. `ring-*` was not inert at all in the sense this table means —
        // it emitted `outline-color`, a property no Tailwind has ever emitted for it, so the row was
        // accurate about the symptom and wrong about the disease. A reader would not have fixed it.
        //
        // What is left of A6 is `user-select`, below, and it is the one that is not waiting for a
        // reader either.


        // ⚠ <b>Transforms, and this group moved in two directions at once.</b> Four rows were here,
        // all four naming a `--`-prefixed property of the family's own invention. Every one of those
        // names was wrong twice over: `--scale` is not a CSS property, so no engine anywhere — this
        // one or a browser — would ever have read it, and it was not a composition fragment either,
        // because nothing assembled it. The debt was filed against a name that could not come due.
        // That is `grid-cols-3`'s failure exactly: an emission no engine could consume, sitting under
        // a row that correctly said "nothing reads this" and was therefore never going to be the
        // thing that told anybody.
        //
        // The two translations are in `Supported` now. These two are not, and the reason is not a
        // missing reader — it is that a `DrawCommand` is an axis-aligned rectangle:
        //
        //   `rotate` cannot be drawn or clipped. A rotated box is not a rectangle, and the clip stack
        //   intersects rectangles; the per-axis `overflow` trick of pushing one pair of edges past
        //   the viewport works *because* what comes out is still axis-aligned. Approximating with the
        //   bounding box would make `rotate-45` draw a 45-point square where a 32-point one was
        //   asked for — a rendering bug wearing a feature's name, which is what `GradientRefusal`
        //   exists not to do.
        //
        //   `scale` can scale the box and not the picture. Glyph advances are shaped at `run.Size`
        //   during layout, so a scaled subtree needs re-shaping — which would make a transform affect
        //   layout, the one thing CSS Transforms 1 §3 says it must never do. Border widths, radii and
        //   shadow offsets are the same story a field at a time. The half that is cheap is the half
        //   nobody would notice; the half that is visible is a compositor.
        //
        // What they emit is now the CSS those two things are actually called, at Tailwind's own
        // values — `scale-150` is `scale: 150%`, a ratio, not a hundred and fifty times — so the day
        // a compositor lands, the gate's expiry check on `InertProperties.txt` is what says so.
        { "scale-2", "scale" },
        { "rotate-45", "rotate" },

        // ⚠ <b>`align-middle` stays, and its three siblings left.</b> `vertical-align` is read now,
        // so this row is no longer "a property with no consumer" — it is a *value* the consumer
        // refuses. §10.8.1 defines `middle` as the parent's baseline plus half its x-height, and an
        // x-height is a font metric; `Vixen.Ui.Layout` is geometry and has no font, so
        // `LayoutStyleBuilder` drops the keyword rather than approximating it. Approximating it is
        // the tempting mistake: rounding `middle` to `baseline` looks almost right and reads as a
        // rendering quirk. Task #26, and `InlineKnownGaps.txt` says what it would take —
        // `align-text-top`, `align-text-bottom`, `align-sub` and `align-super` are the same story if
        // the families are ever registered.
        { "align-middle", "vertical-align" },

        // ⚠ <b>`select-none` stays, and the reason it used to give was disprovable in five
        // minutes.</b> Both this table and `InertProperties.txt` said "no selection model reads it",
        // and there is a selection model: `TextField` has `CaretIndex`, `SelectionAnchor`,
        // `SelectWord`, drag-to-select and a highlight it paints in its own `OnDraw`, and
        // `CodeEditor` has a second one. Anybody checking that sentence would have found them and
        // concluded the row was stale — which is the same trap `align-middle` sat in, a row that
        // names a missing consumer when what is actually missing is something else.
        //
        // What does not exist is the *document-wide* selection CSS is talking about. Both models are
        // per-control and cannot be otherwise: each captures the pointer for the length of its own
        // drag, gates its highlight on being focused, and hit-tests only its own `TextLayout`.
        // Nothing can drag a selection across a `TextBlock`, across a label, or across two sibling
        // elements — `TextBlock` has no pointer handler at all. So `select-none` on a button, which
        // is overwhelmingly how the class is written and what it is *for* — stopping a double-click
        // from selecting the caption — has nothing to suppress, because nothing would have selected.
        //
        // ⚠ <b>And the cheap close was available and is refused.</b> Teaching `TextField.Pointed` to
        // decline a drag under `user-select: none` would be a real reader, would expire the
        // `InertProperties.txt` line, and would leave the promise above exactly as unkept — a family
        // reported as supported that does nothing in every place a person writes it. Task #24 is the
        // document-wide model, not the reader.
        { "select-none", "user-select" },

        // ⚠ <b>The three `transition-*` rows were here and are now back in `Supported`.</b> They are
        // the only rows in this file to have made the trip in both directions, and the round trip is
        // worth more than either leg: they sat in `Supported` on the strength of the cascade computing
        // a value, the gate's first run moved them here, and A20 wiring the animator to the frame loop
        // moved them back — this time with a mid-flight width and a mid-flight colour behind them
        // rather than a computed value. See `Vixen.Ui.Tests.TransitionTests`, and the closed block in
        // `InertProperties.txt` for what the gate needed before it could see the third one.
    };

    /// <summary>Each supported family computes what the engine's own consumers go looking for.</summary>
    /// <param name="utility">The class name.</param>
    /// <param name="property">The longhand the cascade should end up holding.</param>
    /// <param name="expected">Its value.</param>
    [Theory]
    [MemberData(nameof(Supported))]
    public void A_supported_family_computes_the_property_the_engine_reads(string utility, string property, string expected) {
        using var ui = Sheet(utility);

        var element = ui.Create("probe", ui.Document.Root, null, utility);

        ui.Frame();

        Assert.Equal(expected, ui.StyleOf(element, property));
    }

    /// <summary>Each inert family computes a value too — which is exactly why the list has to exist.</summary>
    /// <param name="utility">The class name.</param>
    /// <param name="property">The property it sets, that nothing reads.</param>
    [Theory]
    [MemberData(nameof(Inert))]
    public void An_inert_family_still_computes_a_value(string utility, string property) {
        using var ui = Sheet(utility);

        var element = ui.Create("probe", ui.Document.Root, null, utility);

        ui.Frame();

        Assert.NotNull(ui.StyleOf(element, property));
    }

    /// <summary>
    ///     ⚠ <b>Support proved rather than asserted, for the pair somebody is most likely to reach
    ///     for.</b> Both utilities make the draw list push exactly one clip around the element's
    ///     children, so a count tells them apart from nothing and from each other. What does is the
    ///     rectangle: <c>overflow-hidden</c>'s is the element's box on both axes, and
    ///     <c>overflow-y-hidden</c>'s is that box vertically and a pair of edges past any viewport
    ///     horizontally. That is how one axis alone is expressed by a clip stack that only knows how
    ///     to cut with a rectangle, and it is the whole reason this engine can do what CSS cannot —
    ///     there, a lone <c>overflow-y</c> coerces its partner and clips both.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Counting clips is not enough — the axis has to be checked.</b> A per-axis clip is a
    ///     rectangle with one pair of edges past the viewport, so an implementation that clipped
    ///     <i>both</i> axes would push exactly one clip too and pass a count. The unbounded pair is
    ///     what says which axis was meant.
    /// </remarks>
    [Fact]
    public void The_per_axis_overflow_utilities_clip_the_axis_they_name_and_no_other() {
        using var ui = Sheet("overflow-hidden", "overflow-y-hidden", "w-8", "h-8");

        var both = Clip(ui, "overflow-hidden");
        var vertical = Clip(ui, "overflow-y-hidden");

        Assert.Equal(both.Y, vertical.Y);
        Assert.Equal(both.Height, vertical.Height);

        // ⚠ The unnamed axis is measured against the viewport rather than against
        // `DrawListBuilder.UnboundedClip`, which is internal to `Vixen.Ui` and shared with its own
        // test assembly and not with this one. "Off both ends of the document" is the claim that
        // matters anyway — the constant's exact value is that builder's business.
        Assert.True(vertical.X < 0f, "the unnamed axis begins left of the document");
        Assert.True(
            vertical.X + vertical.Width > ui.Document.Viewport.ViewportWidth,
            "and ends right of it"
        );
    }

    /// <summary>
    ///     ⚠ <b>A colour filter is only observable in a scene that has colour in it, and this is that
    ///     scene.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What makes this worth writing is that the consumption gate next door cannot make
    ///         the claim.</b> That gate's verdict is "the draw list changed", and a
    ///         <c>filter</c> changes it by opening a group — a <c>LayerPush</c> and a <c>LayerPop</c>
    ///         appear whatever the matrix says, so the gate would pass on a matrix no executor ever
    ///         reads and on a <c>grayscale</c> that came out the identity. Its scene list is not the
    ///         thing to fix either: no scene can reach past the draw list, because the draw list is
    ///         where the gate stops. So the observation belongs here, in the file whose job is
    ///         <i>what</i> happened.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the colour is the arrangement.</b> A <c>grayscale</c> over a scene with no
    ///         colour in it is the identity by coincidence, and would prove nothing while working
    ///         perfectly — so the panel is given <c>bg-accent</c>, which is blue-dominant, and the
    ///         assertion is that the matrix flattens <i>that</i> colour's channels and that they were
    ///         not flat to begin with. The second half is what stops the test passing on a grey
    ///         theme.
    ///     </para>
    ///     <para>
    ///         The pixels themselves are asserted in <c>Vixen.Graphics.Golden.Tests.UiCompositingTests</c>,
    ///         where the device and the software rasterizer are required to draw the same filtered
    ///         frame. This is the half that says the stylesheet reached the matrix at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_grayscale_utility_puts_the_group_it_opens_through_a_matrix_that_flattens_this_colour() {
        using var ui = Sheet("grayscale", "bg-accent", "w-8", "h-8");

        ui.Create("probe", ui.Document.Root, null, "grayscale", "bg-accent", "w-8", "h-8");
        ui.Frame();

        var push = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.LayerPush
        );

        // ⚠ The group exists *because* of the filter and not because of an opacity — its own alpha is
        // one. Without this the assertions below would hold on a frame where the filter did nothing
        // and something else opened the bracket.
        Assert.Equal(1f, push.Color.A);

        var matrix = Assert.NotNull(push.Filter);

        var panel = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Rectangle
        );

        // ⚠ Premultiplied on the way in, because that is what a layer surface holds and what
        // `UiColorMatrix.Apply` is defined on. An opaque panel makes that a no-op, which is the point
        // of using one: the arithmetic under test is the matrix and not the encoding.
        var before = panel.Color;

        Assert.True(
            MathF.Abs(before.B - before.R) > 0.05f,
            "the scene's own colour has to be off-grey or a grayscale that did nothing would pass"
        );

        var after = matrix.Apply(before);

        Assert.Equal(after.R, after.G, 4);
        Assert.Equal(after.G, after.B, 4);
        Assert.Equal(before.A, after.A);
    }

    /// <summary>
    ///     ⚠ <b>Two filter families on one element are one declaration and one matrix, which is the
    ///     whole reason the fragments exist.</b>
    /// </summary>
    /// <remarks>
    ///     Two classes are two rules with two selectors of equal weight, so a pair of families that
    ///     each emitted a whole <c>filter</c> would let the cascade keep one and silently drop the
    ///     other — the failure <c>translate-x</c>/<c>translate-y</c> had before they were composed,
    ///     and the reason <c>UtilityComposition</c> exists at all. Asserted on the group rather than
    ///     on the computed string, because a computed string that holds both functions is still
    ///     compatible with <c>DrawListBuilder</c> reading one and discarding the other — which is
    ///     exactly what it used to do.
    /// </remarks>
    [Fact]
    public void Two_filter_families_compose_into_one_declaration_and_one_matrix() {
        using var ui = Sheet("blur-2", "invert", "bg-accent", "w-8", "h-8");

        ui.Create("probe", ui.Document.Root, null, "blur-2", "invert", "bg-accent", "w-8", "h-8");
        ui.Frame();

        var push = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.LayerPush
        );

        Assert.Equal(8f, push.Blur);

        var matrix = Assert.NotNull(push.Filter);
        var white = matrix.Apply(new Color4(1f, 1f, 1f, 1f));

        // A full inversion takes white to black and leaves the coverage alone.
        Assert.Equal(0f, white.R, 4);
        Assert.Equal(0f, white.G, 4);
        Assert.Equal(0f, white.B, 4);
        Assert.Equal(1f, white.A);
    }

    /// <summary>
    ///     ⚠ <b>Support proved at the draw list, for a family that used to erase what it touched.</b>
    ///     A count is not enough and neither is a colour: what says the utility worked is <i>where</i>
    ///     the accent-coloured rectangle is. A bottom colour must produce a band along the bottom edge
    ///     of the border box, and the three red bands must survive beside it — the old builder's
    ///     failure was that they did not.
    /// </summary>
    [Fact]
    public void A_per_edge_border_colour_paints_only_the_edge_it_names() {
        using var ui = Sheet("border-2", "border-b-accent", "w-8", "h-8");

        var element = ui.Create("probe", ui.Document.Root, null, "border-2", "border-b-accent", "w-8", "h-8");

        ui.Frame();

        // ⚠ <b>No uniform `border-border` beside it, and that is the point of the arrangement.</b>
        // The two utilities have equal specificity, so which one owns `border-bottom-color` is decided
        // by the order the generator emitted them in — a real question, and somebody else's. With only
        // the per-edge colour set, three edges have a width and no colour and paint nothing, and the
        // one band that appears is unambiguously the one `border-b-accent` asked for.
        var band = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Rectangle
        );

        Assert.Equal(element.AbsoluteTop + element.Height - band.Height, band.Y);
        Assert.Equal(2f, band.Height);
        Assert.Equal(element.Width, band.Width);

        // The accent is blue-dominant; the surface and border tokens are not.
        Assert.True(band.Color.B > band.Color.R, "the band carries the accent colour");
    }

    /// <summary>
    ///     ⚠ <b>The same, for a corner.</b> <c>rounded-tl-[6px]</c> was not merely unread — it was not a
    ///     family, so the scanner reported it as an unrecognised class and the generator emitted
    ///     nothing. The proof is the side buffer: a box whose corners differ cannot be described by
    ///     <c>DrawCommand.Radius</c>, so an entry in <c>Boxes</c> existing at all is the evidence, and
    ///     the three square corners are what say the radius went to the corner it named.
    /// </summary>
    [Fact]
    public void A_per_corner_radius_rounds_only_the_corner_it_names() {
        using var ui = Sheet("rounded-tl-[6px]", "bg-surface", "w-8", "h-8");

        ui.Create("probe", ui.Document.Root, null, "rounded-tl-[6px]", "bg-surface", "w-8", "h-8");
        ui.Frame();

        var box = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Rectangle && command.HasStyle
        );

        var corners = ui.Document.Drawing.Boxes[box.Offset].Corners;

        Assert.True(corners.TopLeft.X > 0f, "the named corner is rounded");
        Assert.Equal(0f, corners.TopRight.X);
        Assert.Equal(0f, corners.BottomRight.X);
        Assert.Equal(0f, corners.BottomLeft.X);

        // ⚠ And the scalar stays zero rather than carrying the top-left. A consumer that reads only
        // `Radius` must not round the other three corners by the one that was set — which is exactly
        // what the old builder did to every box in the editor.
        Assert.Equal(0f, box.Radius);
    }

    /// <summary>
    ///     ⚠ <b>The same, for <c>display</c> — and this one has now inverted.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This test used to assert the opposite, and the assertion it used to make is worth
    ///         keeping in view: <c>block</c> was not in <c>LayoutStyleBuilder</c>'s keyword table, so
    ///         an element carrying it was laid out as the flex container everything in this engine
    ///         was, and its two children sat <i>side by side</i>. A test that only read the computed
    ///         <c>display</c> would have found <c>block</c> sitting there and concluded the opposite —
    ///         which is the whole reason this file resolves real elements and measures them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Inverted rather than deleted, on purpose.</b> That a family was inert and now is
    ///         not is precisely what this file exists to record, and a deleted test records nothing.
    ///         Doc 43 § B1 is what changed: <see cref="Display" /> grew a <c>Block</c> member and
    ///         <c>LayoutTree.Block</c> grew the algorithm behind it. The children stack now, and the
    ///         second assertion below is the one no computed-value check could ever have made —
    ///         their <i>margins collapse</i>, which is the difference between block layout and a flex
    ///         column and the reason this could not have been shipped as a flag on the old one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Display_block_stacks_its_children_and_collapses_their_margins() {
        using var ui = Sheet("block", "w-8", "h-8", "mb-2", "mt-2");

        var host = ui.Create("probe", ui.Document.Root, null, "block");
        var first = ui.Create("probe", host, null, "w-8", "h-8", "mb-2");
        var second = ui.Create("probe", host, null, "w-8", "h-8", "mt-2");

        ui.Frame();

        Assert.Equal("block", ui.StyleOf(host, "display"));
        Assert.Equal(first.AbsoluteLeft, second.AbsoluteLeft);
        Assert.True(second.AbsoluteTop > first.AbsoluteTop, "a flex row would have put them side by side");

        // `h-8` is 32 points and `mb-2`/`mt-2` are 8 each. Collapsed, the gap is 8 and the second
        // box starts at 40; unmerged it would be 16 and 48. Nothing about the computed style
        // distinguishes those two answers.
        Assert.Equal(first.AbsoluteTop + 40f, second.AbsoluteTop);
    }

    /// <summary>
    ///     ⚠ <b><c>inline-block</c> shares its line, which is the one thing the alias could not have
    ///     done.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The third row in this file to move from <see cref="Inert" /> to <see cref="Supported" />
    ///         because an algorithm arrived, after <c>block</c> and <c>grid</c> — and the one whose
    ///         old row was a <i>prediction</i> rather than only a record. It said the two keywords
    ///         were unmapped deliberately, because mapping them onto <c>Block</c> and <c>Flex</c>
    ///         would give <c>inline-block</c> the whole line. This is that sentence turned into an
    ///         assertion: the two boxes are on <b>one line</b>, at the same top and at different
    ///         lefts, and against the alias they would have been at the same left and different tops.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The width is the second half and the more easily lost one.</b> Neither box states
    ///         a width, so CSS 2.1 §10.3.9's shrink-to-fit is what decides it — an inline-block is as
    ///         wide as its contents. A block-level box with <c>width: auto</c> takes the containing
    ///         block's whole width under §10.3.3, so an implementation that got the line right and
    ///         the sizing wrong would put two 200-point boxes side by side and overflow. Asserting
    ///         only the tops would pass that.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_inline_block_shares_its_line_instead_of_taking_it() {
        using var ui = Sheet("block", "inline-block", "w-8", "h-8", "w-48");

        var host = ui.Create("probe", ui.Document.Root, null, "block", "w-48");
        var first = ui.Create("probe", host, null, "inline-block");
        var second = ui.Create("probe", host, null, "inline-block");

        var inFirst = ui.Create("probe", first, null, "block", "w-8", "h-8");
        ui.Create("probe", second, null, "block", "w-8", "h-8");

        ui.Frame();

        Assert.Equal("inline-block", ui.StyleOf(first, "display"));

        // One line: same top, different lefts. The alias gives the opposite of both.
        Assert.Equal(first.AbsoluteTop, second.AbsoluteTop);
        Assert.True(second.AbsoluteLeft > first.AbsoluteLeft, "a block-level box would have taken the whole line");

        // §10.3.9 rather than §10.3.3: `w-8` is 32 points, so each box is 32 wide inside a
        // 192-point container rather than 192 wide. That is what puts the second one at 32.
        Assert.Equal(32f, first.Width);
        Assert.Equal(first.AbsoluteLeft + 32f, second.AbsoluteLeft);
        Assert.Equal(inFirst.AbsoluteTop, first.AbsoluteTop);
    }

    /// <summary><c>grid-cols-3</c> divides the container into three tracks.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Inverted rather than deleted, like <c>block</c> above, and it took more than the
    ///         reader the old <c>Inert</c> row asked for.</b> That row named the bridge and was right
    ///         about it — but the family also emitted <c>grid-template-columns: 3</c>, which is not a
    ///         track list, so a reader alone would have changed nothing and the row would have stayed
    ///         accurate for a second reason nobody had written down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three equal columns is the assertion a computed-value check cannot make and the
    ///         one that would have caught the old emission.</b> `grid-template-columns: 3` resolves,
    ///         cascades and reads back perfectly — the inert proof below asserted exactly that and
    ///         passed throughout — while laying out as a single automatic column. The three left
    ///         edges are what tell the two apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Grid_cols_divides_the_container_into_equal_tracks() {
        using var ui = Sheet("grid", "grid-cols-3", "w-48", "h-8");

        var host = ui.Create("probe", ui.Document.Root, null, "grid", "grid-cols-3", "w-48");
        var first = ui.Create("probe", host, null, "h-8");
        var second = ui.Create("probe", host, null, "h-8");
        var third = ui.Create("probe", host, null, "h-8");

        ui.Frame();

        Assert.Equal("repeat(3, minmax(0, 1fr))", ui.StyleOf(host, "grid-template-columns"));

        // `w-48` is 192 points, so three even tracks are 64 apiece. A one-column grid — which is
        // what an unread or unparsed template gives — would stack all three at the same left edge.
        Assert.Equal(first.AbsoluteLeft + 64f, second.AbsoluteLeft);
        Assert.Equal(first.AbsoluteLeft + 128f, third.AbsoluteLeft);
        Assert.Equal(first.AbsoluteTop, third.AbsoluteTop);
    }

    /// <summary><c>col-span-2</c> makes an item cover two of those tracks.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The width is the assertion, and the sibling's position is what makes it mean
    ///         something.</b> An item that spans two 64-point tracks is 128 wide and the next item
    ///         starts at 128 — whereas the old emission, <c>grid-column: 2</c>, is a perfectly valid
    ///         line number that would have placed it in the second track at 64 wide. Both are grids,
    ///         both lay out, and only the measurement distinguishes them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both edges are named because the shorthand is gone by the time the cascade is
    ///         asked.</b> <c>ShorthandExpansion</c> splits <c>grid-column</c> at load — that is what
    ///         stopped a <c>grid-column-start</c> from another rule silently discarding a later
    ///         <c>grid-column</c> — so <c>StyleOf(wide, "grid-column")</c> is now null for a reason
    ///         that is the fix rather than a regression, and asserting the two halves is what keeps
    ///         the emission checked at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Col_span_covers_the_tracks_it_names() {
        using var ui = Sheet("grid", "grid-cols-3", "col-span-2", "w-48", "h-8");

        var host = ui.Create("probe", ui.Document.Root, null, "grid", "grid-cols-3", "w-48");
        var wide = ui.Create("probe", host, null, "col-span-2", "h-8");
        var next = ui.Create("probe", host, null, "h-8");

        ui.Frame();

        Assert.Equal("span 2", ui.StyleOf(wide, "grid-column-start"));
        Assert.Equal("span 2", ui.StyleOf(wide, "grid-column-end"));
        Assert.Equal(128f, wide.Width);
        Assert.Equal(wide.AbsoluteLeft + 128f, next.AbsoluteLeft);
    }

    /// <summary>
    ///     ⚠ <b>Support proved in the two lists that have to agree, for the first composed family
    ///     whose halves are on different classes.</b> Three separate things have to be true at once,
    ///     and each of them has its own way of being quietly false.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>One: the two classes compose.</b> CSS has one <c>translate</c> and Tailwind gives you
    ///         a class per axis, so <c>translate-x-4 translate-y-2</c> is two rules writing one
    ///         property. A utility system that emitted a declaration per class would let the later rule
    ///         win outright and silently zero the other axis — and the box would still move, just not
    ///         diagonally, which is the failure that looks like a design decision. Both offsets being
    ///         non-zero is what says the fragments were assembled rather than overwritten.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two: the draw list and the hit test agree.</b> This is the assertion the family
    ///         exists to earn. A transform is the classic place for a renderer and a pointer to drift
    ///         apart — the element is painted somewhere new and remains clickable where it used to be —
    ///         and the two are different code reading different arrays. Asserting the rectangle alone
    ///         passes that bug completely. The <i>vacated</i> corner is the load-bearing half: a point
    ///         inside the new box also sits inside the old one whenever the translation is smaller than
    ///         the element, which is every real case, so "the new place is clickable" is true of an
    ///         implementation that moved nothing at all.
    ///     </para>
    ///     <para>
    ///         <b>Three: it is not layout.</b> The sibling keeps the position flexbox gave it. CSS
    ///         Transforms 1 §3 applies a transform after layout, so a nudged element must not push its
    ///         neighbour along — and putting the resolution in <c>LayoutStyleBuilder</c>, which is the
    ///         obvious place for it because that is where <c>left</c> lives, would pass every other
    ///         assertion here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_translate_families_compose_and_move_the_box_and_its_hit_test() {
        using var ui = Sheet("translate-x-4", "translate-y-2", "w-8", "h-8", "bg-accent");

        var moved = ui.Create("probe", ui.Document.Root, null, "translate-x-4", "translate-y-2", "w-8", "h-8", "bg-accent");
        var beside = ui.Create("probe", ui.Document.Root, null, "w-8", "h-8");

        ui.Frame();

        // One: both axes survived, which neither class could have managed alone.
        Assert.Equal("16px 8px", ui.StyleOf(moved, "translate"));
        Assert.Equal(16f, moved.AbsoluteLeft);
        Assert.Equal(8f, moved.AbsoluteTop);

        // Two: painted there, and clicked there — and *not* clicked where it used to be.
        var box = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Rectangle
        );

        Assert.Equal(moved.AbsoluteLeft, box.X);
        Assert.Equal(moved.AbsoluteTop, box.Y);
        Assert.Same(moved, ui.Document.HitTest(box.X + 2f, box.Y + 2f));
        Assert.NotSame(moved, ui.Document.HitTest(2f, 2f));

        // Three: the sibling did not budge. `w-8` is 32 points, and the row is a flex row, so an
        // implementation that translated in the layout would have put it at 48.
        Assert.Equal(32f, beside.AbsoluteLeft);
    }

    /// <summary>
    ///     ⚠ <b>A ring paints outside the border box and does not move anything, and the second half
    ///     is the assertion the parity gate structurally cannot make.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The gate's verdict is a union over four channels — layout, paint, cursor, hit test — so
    ///         "something acted on <c>box-shadow</c>" is all it can report. A ring that painted
    ///         correctly <i>and</i> was wrongly folded into the layout would move two channels instead
    ///         of one and pass it just as cleanly. That is the whole reason this file exists beside it,
    ///         and it is the specific failure a ring invites: an outline looks like a border, and a
    ///         border has a width the layout must account for. CSS UI 4 § 2.1 is explicit that an
    ///         outline does not affect layout and may overlap other content.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rectangle is the first half and it has to be measured, not counted.</b> An
    ///         implementation that emitted the shadow at the border box — spread ignored — would still
    ///         produce exactly one <c>Shadow</c> command in the right colour, so a count and a colour
    ///         both pass it. Four points larger on each axis and two points up and to the left is what
    ///         says the spread was applied, and that is the difference between a ring and a rectangle
    ///         hidden entirely behind the element that cast it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_ring_paints_outside_the_box_and_costs_the_layout_nothing() {
        using var ui = Sheet("ring-2", "ring-accent", "w-8", "h-8");

        var ringed = ui.Create("probe", ui.Document.Root, null, "ring-2", "ring-accent", "w-8", "h-8");
        var beside = ui.Create("probe", ui.Document.Root, null, "w-8", "h-8");

        ui.Frame();

        // Both classes wrote the same declaration and neither zeroed the other, which is the whole
        // point of making both of them assemblers.
        Assert.Equal("0 0 0 2px #2f6ecd", ui.StyleOf(ringed, "box-shadow"));

        var ring = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Shadow
        );

        // Outside the box on all four sides: the spread grows the rectangle rather than insetting it.
        Assert.Equal(ringed.AbsoluteLeft - 2f, ring.X);
        Assert.Equal(ringed.AbsoluteTop - 2f, ring.Y);
        Assert.Equal(ringed.Width + 4f, ring.Width);
        Assert.Equal(ringed.Height + 4f, ring.Height);

        // And it is a ring rather than a shadow: no blur, so the edge is hard. `Thickness` is where
        // `EmitShadow` puts the falloff.
        Assert.Equal(0f, ring.Thickness);

        // ⚠ Not layout. `w-8` is 32 points in a flex row, so a ring accounted for as a border would
        // have put the sibling at 36 — and every other assertion here would still have passed.
        Assert.Equal(32f, beside.AbsoluteLeft);
        Assert.Equal(32f, ringed.Width);
    }

    /// <summary>
    ///     ⚠ <b>A bare <c>ring-2</c> takes the text colour, which is the case the family is actually
    ///     written in and the one that had no way to work.</b>
    /// </summary>
    /// <remarks>
    ///     v4's <c>--tw-ring-color</c> defaults to <c>currentcolor</c>, and Vixen's colour parser had
    ///     no such keyword — <c>NamedColors</c> is the basic sixteen plus <c>transparent</c>, and
    ///     <c>currentcolor</c> is not a name but a reference to the computed <c>color</c>. Every
    ///     concrete initial available instead was wrong: <c>transparent</c> would make <c>ring-2</c>
    ///     resolve, cascade and paint nothing — inert wearing a supported family's name — and any
    ///     literal would be a colour nobody chose. So <c>EmitShadow</c> learned the keyword, resolving
    ///     it through <c>UiDocument.ForegroundOf</c> exactly as CSS Color 4 § 6.2 says. Asserting the
    ///     colour against <c>ColorOf</c> rather than a hex literal is what makes this a test of the
    ///     resolution rather than of the editor's palette.
    /// </remarks>
    [Fact]
    public void A_ring_with_no_colour_of_its_own_is_the_current_colour() {
        using var ui = Sheet("ring-2", "text-accent", "w-8", "h-8");

        var ringed = ui.Create("probe", ui.Document.Root, null, "ring-2", "text-accent", "w-8", "h-8");

        ui.Frame();

        var ring = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Shadow
        );

        Assert.Equal(ui.ColorOf(ringed, "color"), ring.Color);
    }

    /// <summary>
    ///     ⚠ <b><c>fill-*</c> repaints an icon, and it is written on the ancestor — which is the half
    ///     a computed-value check would have missed and the half that is nearly always the real
    ///     case.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Nobody writes <c>fill-accent</c> on an <c>&lt;icon&gt;</c>. It goes on the button, the
    ///         toolbar or the row, and the icon is a child — so the property has to inherit to be worth
    ///         anything, and SVG 2 § 13.2 says it does. Reading it on the icon and forgetting the two
    ///         lines in <c>InheritedProperties</c> gives a family that works in a test written the
    ///         obvious way and does nothing in the editor, which is the worst of the available
    ///         outcomes because it looks intermittent rather than absent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured as a colour in the draw list, because the computed value proves nothing
    ///         here.</b> <c>fill</c> resolved on the icon is exactly what the inert row asserted for as
    ///         long as it stood, and it passed throughout. What the fill is actually painted in is a
    ///         <c>Field</c> command's colour, and it must be the accent rather than the inherited
    ///         <c>color</c> — so the host sets a <c>color</c> too, and the two are different, or an
    ///         implementation that ignored <c>fill</c> entirely and fell through to the foreground
    ///         would pass.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_fill_and_stroke_families_paint_an_icon_and_reach_it_by_inheritance() {
        using var ui = Sheet("fill-accent", "text-text-muted", "w-8", "h-8", "w-4", "h-4");

        var host = ui.Create("probe", ui.Document.Root, null, "fill-accent", "text-text-muted", "w-8", "h-8");

        // ⚠ Sized, because `Icon.OnDraw` gives up on a zero-area box before it looks at any paint —
        // an unsized icon emits nothing and `Single` would read that as the family failing.
        var icon = host.Add<Icon>(null, null, "w-4", "h-4");

        icon.Art = new IconArt(
            new IconPath(new PathBuilder().AddRectangle(new Rectangle(4f, 4f, 16f, 16f)), IconPaint.Foreground)
        );

        ui.Frame();

        var accent = ui.ColorOf(host, "fill");
        var inherited = ui.ColorOf(host, "color");

        Assert.NotEqual(accent, inherited);

        var painted = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Field
        );

        // The accent, and specifically *not* the foreground the icon would have used before.
        Assert.Equal(accent, painted.Color);
        Assert.NotEqual(inherited, painted.Color);
    }

    /// <summary>The one clip an element's subtree contributes to the frame.</summary>
    /// <remarks>
    ///     ⚠ Sized, because <c>DrawListBuilder</c> gives up on a zero-area box before it ever looks at
    ///     <c>overflow</c> — an unsized probe would emit no clip at all, and <c>Single</c> is what
    ///     stops that reading as a pass. The probe is removed and the frame run again so the caller
    ///     can measure a second utility against a list holding only that one's clip.
    /// </remarks>
    static DrawCommand Clip(UiTest ui, string utility) {
        var element = ui.Create("probe", ui.Document.Root, null, utility, "w-8", "h-8");

        ui.Frame();

        var push = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.ClipPush
        );

        element.Remove();
        ui.Frame();

        return push;
    }

    /// <summary>A document with just these utilities in it, generated against the editor's tokens.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>EditorTheme.Install</c>, and deliberately.</b> Only the utilities the editor's
    ///     markup already uses are in the editor's sheet — that is the whole point of the scanner —
    ///     so a table exercising the <i>family surface</i> has to generate its own. The tokens are
    ///     still the editor's, which is what makes <c>bg-surface</c> here the same declaration the
    ///     hand-written sheet writes.
    /// </remarks>
    static ThemeTokens Tokens() =>
        ThemeTokens.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "__fixtures__", "vixen.ui.vcss")));

    static UiTest Sheet(params string[] utilities) {
        var ui = UiTest.Create();

        // The token block only, so the `var(--…)` colours resolve without the hand-written rules
        // being present to win against — those have their own tests in `StylesheetTests`.
        ui.Document.Load(EditorTheme.Css, StyleOrigin.UserAgent);
        ui.Document.Load(new UtilityGenerator(Tokens()).Generate(utilities), StyleOrigin.UserAgent);

        return ui;
    }

    /// <summary>
    ///     ⚠ <b>Support proved in two lists at once, for a property that lives in both.</b>
    ///     <c>order-2</c> has to move the box <i>and</i> move the box's turn to be painted, and the
    ///     two are different arrays sorted by different code.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Position alone would not have caught the obvious half-implementation.</b> The layout
    ///     tree and the draw list keep separate child lists — the flexbox store sorts an arena of
    ///     ids, and <c>UiElement.PaintOrder</c> sorts elements — so teaching only the first one about
    ///     <c>order</c> gives boxes that sit in the new positions and paint in the old sequence.
    ///     That is invisible until two items overlap, which is precisely when somebody reaches for
    ///     the property. CSS Flexbox §5.4 is explicit that <c>order</c> moves both.
    ///
    ///     The paint half is read back by colour because a <c>DrawCommand</c> names no element, and
    ///     the colours come from <c>ColorOf</c> rather than from hex written here — the tokens are
    ///     <c>var(--…)</c> references and what they resolve to is <c>EditorTheme</c>'s business.
    /// </remarks>
    [Fact]
    public void An_ordered_item_is_laid_out_and_painted_in_its_ordinal_group() {
        using var ui = Sheet("order-2", "bg-accent", "bg-surface-sunken", "bg-surface-raised", "w-8", "h-8");

        var host = ui.Create("probe", ui.Document.Root);
        var moved = ui.Create("probe", host, null, "order-2", "bg-accent", "w-8", "h-8");
        var middle = ui.Create("probe", host, null, "bg-surface-sunken", "w-8", "h-8");
        var last = ui.Create("probe", host, null, "bg-surface-raised", "w-8", "h-8");

        ui.Frame();

        Assert.Equal("2", ui.StyleOf(moved, "order"));

        // Laid out last despite being declared first: the two defaulted items close up in front of
        // it, and they keep their own relative order while doing so.
        Assert.True(middle.AbsoluteLeft < last.AbsoluteLeft, "the defaulted pair keeps document order");
        Assert.True(last.AbsoluteLeft < moved.AbsoluteLeft, "order-2 goes behind both of them");

        // And painted last, which is a different list and a different sort.
        var painted = Painted(ui, [moved, middle, last]);

        Assert.Equal([middle, last, moved], painted);
    }

    /// <summary>Which of <paramref name="candidates" /> the frame filled, in the order it filled them.</summary>
    /// <remarks>
    ///     ⚠ Matched on the fill colour, so every candidate has to carry a distinct one — a shared
    ///     background would make this report the first match twice and pass a sequence check by
    ///     accident.
    /// </remarks>
    static List<UiElement> Painted(UiTest ui, UiElement[] candidates) {
        var colors = candidates.ToDictionary(candidate => ui.ColorOf(candidate, "background-color")!.Value);

        return ui.Document.Drawing.Commands
            .Where(command => command.Kind == DrawCommandKind.Rectangle && colors.ContainsKey(command.Color))
            .Select(command => colors[command.Color])
            .ToList();
    }
}
