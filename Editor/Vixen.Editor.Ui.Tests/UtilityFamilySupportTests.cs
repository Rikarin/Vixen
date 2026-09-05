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
///         ⚠ <b>There is a third table, and the reason it is not a fourth column of the second is
///         the finding.</b> <see cref="Refused" /> holds the families whose property <i>is</i> read
///         and whose <i>value</i> the reader declines. The two are different claims and expire on
///         different conditions: an inert row can borrow <c>InertProperties.txt</c>'s expiry, which
///         is <see cref="No_inert_row_outlives_the_allow_list_entry_it_names" /> and is one line —
///         and a refused-value row cannot, because its property belongs in no exemption list. What
///         it is held to instead is a named behavioural fact. #532 is what a table with neither
///         costs: <c>scale-*</c> and <c>rotate-*</c> claimed nothing read them long after the
///         compositor landed, and <see cref="An_inert_family_still_computes_a_value" /> could not
///         see it, because a computed value is as true of a family with a reader as of one without.
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
///     <para>
///         ⚠ <b>There is a third signal and it is now consulted:</b> the parity ledger's
///         <c>engine_reads</c> column, remeasured on every run of
///         <c>Vixen.Ui.Styling.Utilities.Tests</c>, against which
///         <see cref="No_inert_row_survives_the_ledger_measuring_its_property_read" /> holds every
///         <see cref="Inert" /> row. That column knew about #532 the whole time and nothing here
///         asked it. Its neighbour column <c>state</c> is <em>not</em> linked and should not be —
///         the reasoning is on that method, and it is written down so the next sweep does not
///         rediscover the idea and try it on a row that is right.
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

        // ⚠ <b>The four that are here are the four CSS 2.1 has, and Tailwind's other four are
        // deliberately absent.</b> `float-start`, `float-end`, `clear-start` and `clear-end` emit the
        // logical `inline-start` / `inline-end`, which resolve against a writing mode; `FloatSide`
        // and `Clear` are physical and do not flip with `direction`. Adding rows for them here would
        // assert that the cascade computes a value, which it would — and the engine would then drop
        // it, which is exactly the reading this file's own first paragraph says is not enough. They
        // are not in `Inert` either, because they resolve to nothing at all rather than to a property
        // nobody reads. See `Core/Vixen.Ui.Styling.Utilities/README.md`.
        { "float-left", "float", "left" },
        { "float-right", "float", "right" },
        { "float-none", "float", "none" },
        { "clear-left", "clear", "left" },
        { "clear-right", "clear", "right" },
        { "clear-both", "clear", "both" },
        { "clear-none", "clear", "none" },

        // ⚠ The pair the two vocabularies make easy to confuse, which is why both are written out
        // here next to each other: `hidden` above is `display: none` and takes the box out of
        // layout; `invisible` is `visibility: hidden` and leaves it there, occupying its space and
        // painting nothing. `collapse` is the third keyword and reads as `hidden` on every box this
        // engine can build, there being no table rows for it to mean anything else on.
        { "visible", "visibility", "visible" },
        { "invisible", "visibility", "hidden" },
        { "collapse", "visibility", "collapse" },
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

        // ⚠ <b>The bare roots are the shorthand, and the second row is the load-bearing one.</b>
        // `col-3` is `grid-column: 3`, which `ShorthandExpansion` splits at load — so the class
        // states the start edge *and* resets the end edge to `auto`, which is the whole difference
        // between it and `col-start-3`. A family that emitted the two longhands itself would have
        // computed the same first row and nothing at all for the second.
        { "col-3", "grid-column-start", "3" },
        { "col-3", "grid-column-end", "auto" },
        { "row-3", "grid-row-start", "3" },
        { "row-3", "grid-row-end", "auto" },
        { "col-auto", "grid-column-start", "auto" },
        { "row-auto", "grid-row-start", "auto" },

        // ⚠ <b>The row half of everything above, plus the four roots that place implicit tracks —
        // and the asymmetry between `col-span-*` and `row-span-*` is the point of listing both.</b>
        // `grid-column` and `grid-row` are shorthands ExCSS has never heard of, so each depends on
        // `ShorthandExpansion` splitting it at load; the two are separate entries in that table, and
        // a split written for one axis and not the other is a defect no `col-*` row can see.
        // `grid-rows-*` is `grid-cols-*`'s twin for the same reason and emits the same `repeat()`
        // form, which is the emission that changed under it once already.
        { "grid-rows-2", "grid-template-rows", "repeat(2, minmax(0, 1fr))" },
        { "row-span-2", "grid-row-start", "span 2" },
        { "row-span-2", "grid-row-end", "span 2" },
        { "col-start-2", "grid-column-start", "2" },
        { "col-end-2", "grid-column-end", "2" },
        { "row-start-2", "grid-row-start", "2" },
        { "row-end-2", "grid-row-end", "2" },
        { "grid-flow-col", "grid-auto-flow", "column" },
        { "auto-cols-2", "grid-auto-columns", "8px" },
        { "auto-rows-2", "grid-auto-rows", "8px" },
        { "flex-col", "flex-direction", "column" },
        { "flex-wrap", "flex-wrap", "wrap" },
        { "items-center", "align-items", "center" },
        { "self-start", "align-self", "flex-start" },
        { "justify-between", "justify-content", "space-between" },
        { "content-center", "align-content", "center" },

        // ⚠ <b>Tailwind's suffix is CSS's prefix, so the expectation is the reversed pair.</b> These
        // rows only say the cascade holds `safe end` — that it *means* anything is
        // `Vixen.Ui.Tests.SafeAlignmentFromCssTests`, which measures a box, and
        // `A_safe_alignment_keeps_an_overflowing_child_inside_the_editor_s_own_row` below, which
        // measures one against the editor's tokens. The distinction is the point of this file: the
        // cascade held `safe end` perfectly for as long as the bridge dropped it.
        { "items-end-safe", "align-items", "safe end" },
        { "items-center-safe", "align-items", "safe center" },
        { "self-end-safe", "align-self", "safe end" },
        { "justify-end-safe", "justify-content", "safe end" },
        { "content-center-safe", "align-content", "safe center" },
        { "justify-items-end-safe", "justify-items", "safe end" },
        { "justify-self-center-safe", "justify-self", "safe center" },

        // `normal` is `justify-content`'s and `align-content`'s initial keyword written out. Not a
        // no-op: it is the only thing in this vocabulary that undoes a `justify-center` set earlier.
        { "justify-normal", "justify-content", "normal" },
        { "content-normal", "align-content", "normal" },
        { "justify-items-normal", "justify-items", "normal" },

        // ⚠ <b>The three `place-*` roots emit two longhands each and never the shorthand.</b> ExCSS
        // has never heard of `place-content`, and `ShorthandExpansion` does not take it apart, so a
        // family emitting it would have computed a value under a name no consumer asks for — the
        // `scroll-m-*` trade, one category over. Both halves are listed for each, because a family
        // that emitted into only its first property would satisfy either row alone.
        { "place-content-center", "align-content", "center" },
        { "place-content-center", "justify-content", "center" },
        { "place-items-center", "align-items", "center" },
        { "place-items-center", "justify-items", "center" },
        { "place-self-end", "align-self", "end" },
        { "place-self-end", "justify-self", "end" },
        { "place-content-end-safe", "align-content", "safe end" },
        { "place-content-end-safe", "justify-content", "safe end" },

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

        // ⚠ <b>The other edge of every root above, and the reason they are worth the lines is that
        // a family is one <c>Register</c> call per <i>spelling</i> and not per axis.</b> `p-3` and
        // `pt-1` above proved the shorthand and the top edge; nothing in this file said that `pb`,
        // `pl`, `pr` and `py` were even registered, and a family that had never been added — or
        // whose `Properties` array named the wrong longhand, which is the mistake `scroll-mbs-*`
        // below was written to catch — would have read here as silence rather than as a red test.
        // #629, and the whole of what it is: coverage of the reader existed for every one of these,
        // and the *inventory* did not.
        //
        // The axis roots take two rows apiece for `place-content-*`'s reason: a family emitting into
        // only the first member of its `Properties` array satisfies either row on its own.
        { "mt-2", "margin-top", "8px" },
        { "mb-2", "margin-bottom", "8px" },
        { "ml-2", "margin-left", "8px" },
        { "mr-2", "margin-right", "8px" },
        { "mx-2", "margin-left", "8px" },
        { "mx-2", "margin-right", "8px" },
        { "my-2", "margin-top", "8px" },
        { "my-2", "margin-bottom", "8px" },
        { "ms-2", "margin-inline-start", "8px" },
        { "pb-2", "padding-bottom", "8px" },
        { "pl-2", "padding-left", "8px" },
        { "pr-2", "padding-right", "8px" },
        { "py-2", "padding-top", "8px" },
        { "py-2", "padding-bottom", "8px" },
        { "pe-2", "padding-inline-end", "8px" },

        // ⚠ <b>The block logicals resolve to the <i>physical</i> longhand, and the property is what
        // these four rows are for</b> — `inset-bs-*`'s argument and `scroll-mbs-*`'s, arriving a
        // third time. `margin-block-start` is interned by nobody, so a later hand "correcting" these
        // to v4's spelling would leave four classes that parse, cascade and compute perfectly and
        // move no box at all. Asserting the value alone would not notice.
        { "mbs-2", "margin-top", "8px" },
        { "mbe-2", "margin-bottom", "8px" },
        { "pbs-2", "padding-top", "8px" },
        { "pbe-2", "padding-bottom", "8px" },

        // ⚠ <b>Scroll insets — doc 43 A18, and note what the shorthand rows are <i>not</i>.</b>
        // `scroll-m-2` emits four longhands where `m-2` emits the one shorthand `margin`, because
        // ExCSS expands `margin` on the way in and has never heard of `scroll-margin`. So a row here
        // asserting `scroll-m-2` → `scroll-margin` would be asserting the emission that does not
        // work; the per-edge value is the one that reaches `ScrollView`. The logical pair keeps CSS's
        // spelling for the reason `ms-*` does — `ScrollView.InsetOf` folds it against `direction` —
        // and the block pair emits the physical longhand, exactly as `inset-bs-*` records below.
        { "scroll-m-2", "scroll-margin-top", "8px" },
        { "scroll-mt-1", "scroll-margin-top", "4px" },
        { "scroll-mx-2", "scroll-margin-left", "8px" },
        { "scroll-ms-2", "scroll-margin-inline-start", "8px" },
        { "scroll-p-2", "scroll-padding-top", "8px" },
        { "scroll-pb-1", "scroll-padding-bottom", "4px" },
        { "scroll-pe-2", "scroll-padding-inline-end", "8px" },

        // ⚠ <b>The four block logicals, and the *property* is what these four rows are for.</b>
        // `ScrollView.EdgeIds.For` interns six names — the four physical edges and the two inline
        // logical ones — so a later hand "correcting" these to v4's `scroll-margin-block-start`
        // would leave four classes that parse, cascade and compute perfectly and move no scroll at
        // all. Asserting the value alone would not notice. Same guard, same reason, as the
        // `inset-bs-2` row below.
        { "scroll-mbs-2", "scroll-margin-top", "8px" },
        { "scroll-mbe-1", "scroll-margin-bottom", "4px" },
        { "scroll-pbs-2", "scroll-padding-top", "8px" },
        { "scroll-pbe-1", "scroll-padding-bottom", "4px" },

        // The remaining eleven spellings, for the reason the margin edges above are here: `ScrollView`
        // reads a per-edge longhand and nothing said which of these roots reach it. ⚠ The two inline
        // logicals keep CSS's spelling and the rest are physical, which is the same split as the
        // block above and is not a symmetry — `ScrollView.EdgeIds.For` interns exactly six names.
        { "scroll-mb-2", "scroll-margin-bottom", "8px" },
        { "scroll-ml-2", "scroll-margin-left", "8px" },
        { "scroll-mr-2", "scroll-margin-right", "8px" },
        { "scroll-my-2", "scroll-margin-top", "8px" },
        { "scroll-my-2", "scroll-margin-bottom", "8px" },
        { "scroll-me-2", "scroll-margin-inline-end", "8px" },
        { "scroll-pt-2", "scroll-padding-top", "8px" },
        { "scroll-pl-2", "scroll-padding-left", "8px" },
        { "scroll-pr-2", "scroll-padding-right", "8px" },
        { "scroll-px-2", "scroll-padding-left", "8px" },
        { "scroll-px-2", "scroll-padding-right", "8px" },
        { "scroll-py-2", "scroll-padding-top", "8px" },
        { "scroll-py-2", "scroll-padding-bottom", "8px" },
        { "scroll-ps-2", "scroll-padding-inline-start", "8px" },

        // The three keyword families that go with them. `scroll-smooth` is the one worth a row of its
        // own: it is the only member of any of these that changes *when* something happens rather
        // than where, and `ScrollView` animates it off `UiDocument.Ticked`.
        { "scroll-smooth", "scroll-behavior", "smooth" },
        { "overscroll-contain", "overscroll-behavior", "contain" },
        { "overscroll-y-none", "overscroll-behavior-y", "none" },
        { "overscroll-x-auto", "overscroll-behavior-x", "auto" },

        // ⚠ <b>Three rows for one family, because Tailwind spells four roots with the `snap-` prefix
        // and they set three different properties.</b> `snap-y` is the container's axis, `snap-start`
        // an item's alignment and `snap-always` an item's stop; a single row would have measured a
        // third of the family and read as the whole of it.
        //
        // ⚠ <b>`y proximity` and not `y`, and the second word came from a different class.</b> The
        // axis names its strictness through `--tw-scroll-snap-strictness`, whose fallback is CSS's
        // own `proximity` — so this row is also the assertion that the fragment resolves, and a
        // reference written without the fallback would drop the declaration whole and compute null.
        { "snap-y", "scroll-snap-type", "y proximity" },
        { "snap-start", "scroll-snap-align", "start" },
        { "snap-always", "scroll-snap-stop", "always" },

        // Sizing. ⚠ <b>The block and inline roots resolve to the physical longhand and the row is
        // about that.</b> `min-block-*` is `min-height` and `min-inline-*` is `min-width` — the
        // spelling `Vixen.Ui.Layout` interns — so the four logical roots are the block-edge trade
        // one more time, and `size-*` takes two rows because it is the one root here that writes
        // both axes from one value.
        { "w-full", "width", "100%" },
        { "h-4", "height", "16px" },
        { "min-w-0", "min-width", "0" },
        { "max-w-40", "max-width", "160px" },
        { "min-h-2", "min-height", "8px" },
        { "max-h-2", "max-height", "8px" },
        { "min-block-2", "min-height", "8px" },
        { "max-block-2", "max-height", "8px" },
        { "min-inline-2", "min-width", "8px" },
        { "max-inline-2", "max-width", "8px" },
        { "size-2", "width", "8px" },
        { "size-2", "height", "8px" },

        // Position.
        { "absolute", "position", "absolute" },
        { "relative", "position", "relative" },
        { "static", "position", "static" },
        { "top-0", "top", "0" },
        { "inset-x-1", "left", "4px" },
        { "start-2", "inset-inline-start", "8px" },

        // ⚠ <b>`inset-2` is four rows because a family emitting into three of its four longhands is
        // the defect the single-row form cannot see</b>, and it is the one root in this file whose
        // `Properties` array is that long. The other three are the edges `top-0` and `inset-x-1`
        // above left unstated; `end-*` and `inset-e-*` are two spellings of the same logical edge,
        // registered separately because Tailwind spells both, and `inset-be-*` is the block logical
        // resolving physical for `inset-bs-*`'s reason below.
        { "inset-2", "top", "8px" },
        { "inset-2", "right", "8px" },
        { "inset-2", "bottom", "8px" },
        { "inset-2", "left", "8px" },
        { "inset-y-2", "top", "8px" },
        { "inset-y-2", "bottom", "8px" },
        { "left-2", "left", "8px" },
        { "right-2", "right", "8px" },
        { "bottom-2", "bottom", "8px" },
        { "end-2", "inset-inline-end", "8px" },
        { "inset-e-2", "inset-inline-end", "8px" },
        { "inset-be-2", "bottom", "8px" },

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

        // ⚠ <b>`box-content` is not the absence of `box-border` and neither is `static`.</b> Both
        // emit the CSS initial, which makes them look like no-ops and is exactly why they need
        // rows: they are the only way to undo a `box-border` or an `absolute` set by an earlier
        // rule, which is `visible`'s argument two categories over.
        { "box-content", "box-sizing", "content-box" },

        // Typography. `text-` is alignment, then size, then colour, resolved in that order.
        { "text-center", "text-align", "center" },
        { "text-sm", "font-size", "11px" },
        { "text-text-muted", "color", "#5c616b" },
        { "font-semibold", "font-weight", "600" },
        { "leading-8", "line-height", "32px" },
        { "leading-tight", "line-height", "1.25" },
        { "tracking-px", "letter-spacing", "1px" },

        // ⚠ <b>Ten typographic roots with no row anywhere in this file until #629, and `normal-case`
        // is the one that shows why a row per <i>root</i> is the unit rather than a row per
        // family.</b> All four of the case classes are one `text-transform` family, so the
        // consumption gate goes green on any one of them — and `normal-case` emits `none`, a value
        // whose whole job is to undo an inherited `uppercase`. Register it under the wrong keyword
        // and three classes keep working while the fourth silently does nothing, which is the shape
        // the gate cannot see and this table can.
        { "capitalize", "text-transform", "capitalize" },
        { "uppercase", "text-transform", "uppercase" },
        { "lowercase", "text-transform", "lowercase" },
        { "normal-case", "text-transform", "none" },

        // ⚠ <b>Two of `hyphens`' three keywords, and the absent third is why the ledger measures the
        // root `partial`.</b> `hyphens-auto` is not registered — automatic hyphenation wants a
        // dictionary the text stack does not carry — so a row for it here would assert a value the
        // cascade never holds. What is registered is the pair an author can act on: `manual`, which
        // honours a soft hyphen already in the text, and `none`, which does not.
        { "hyphens-manual", "hyphens", "manual" },
        { "hyphens-none", "hyphens", "none" },
        { "line-clamp-2", "-webkit-line-clamp", "2" },
        { "tab-2", "tab-size", "2" },

        // ⚠ <b>The arbitrary form is the only form this family has, and the quoting is the reason
        // it needs a row.</b> An OpenType tag is a *string* in `font-feature-settings`, so the value
        // that reaches the shaper carries its double quotes through the class name, the generator
        // and the cascade — and a family that stripped them, or an escape that ate them, computes a
        // declaration the shaper declines while the property still measures read.
        { "font-features-[\"onum\"_1]", "font-feature-settings", "\"onum\" 1" },

        // ⚠ `text-indent` was one of doc 43 Part 0's seven interned-but-unread properties and moved
        // tables by being implemented rather than by finding a reader: `LineWrapper` had to learn a
        // *second* width, and `TextLine` an offset the draw list, the caret and the hit test all
        // honour. The negative row is not symmetry — CSS's hanging indent is a real thing to want,
        // and it is the sign travelling through the wrapper's arithmetic that makes it work.
        { "indent-4", "text-indent", "16px" },
        { "-indent-4", "text-indent", "-16px" },

        // ⚠ <b>All nine keywords of `font-variant-numeric`, and this is the family where the union
        // hides the most.</b> Every one of them is a different OpenType tag — `tnum`, `pnum`,
        // `onum`, `lnum`, `zero`, `ordn`, `frac`, `afrc` — so a mapping table with one wrong entry
        // asks the shaper for the wrong feature and looks, in the picture, exactly like a font that
        // does not have the right one. The tags themselves are asserted in
        // `Vixen.Ui.Tests.FontFeatureStyleTests`; these rows are the half that says the class
        // resolves. ⚠ Only four of the nine are *visible* even in a face that has them all — Open
        // Sans already draws lining proportional figures, so `lining-nums` and `proportional-nums`
        // are correctly invisible in it, and no embedded face implements `afrc` at all.
        // ⚠ <b>Only `normal-nums` is a row here now, and the other eight moved to
        // <see cref="NumericFigures" /> below because their emission stopped being a string.</b>
        // They compose through `--tw-*` fragments, so the value this table would compare against is
        // an assembled list with four empty slots in it — `   tabular-nums ` — and pinning that text
        // would be pinning the *mechanism* rather than the answer. What the engine reads is the
        // OpenType tag, so that is what the replacement asserts, and it is a stronger row than the
        // one it replaces.
        { "normal-nums", "font-variant-numeric", "normal" },

        { "whitespace-nowrap", "white-space", "nowrap" },

        // ⚠ <b>Every keyword of the three wrapping roots, one row each, and the completeness is the
        // point rather than thoroughness for its own sake.</b> The consumption gate's verdict is per
        // *property* and unions over the values a family can emit, so one live keyword makes the
        // whole family green — which is how `visibility` came to register a `collapse` that was
        // parsed as a `box-shadow: inset` and painted normally while the family scored green. A
        // multi-keyword family is exactly where that union hides things, and these three are all
        // multi-keyword.
        //
        // What each row is asserting differs, and the difference is worth reading:
        //
        //   `italic`/`not-italic` set a property `UiDocument.FontStyleOf` reads and
        //   `FontRegistry.Slanted` matches on. Pixels are in
        //   `Vixen.Ui.Controls.Tests.FontSlantPixelTests`, because a slant that reached the cascade
        //   and picked the wrong face would satisfy every assertion here.
        //
        //   `wrap-anywhere` and `wrap-break-word` are two spellings of one behaviour in this engine
        //   — CSS Sizing § 5.2 separates them only by a min-content contribution `Vixen.Ui.Layout`
        //   has no stage for — and both are registered because both are CSS. Asserted as one
        //   behaviour in `TextWrappingPixelTests` rather than left as an unstated deviation.
        //
        //   ⚠ `wrap-normal`, `break-normal` and `text-wrap` emit CSS's *initial* values, so on a bare
        //   element they are correctly indistinguishable from writing nothing and the gate measures
        //   all three inert. They are not no-ops: all three properties inherit, so each is how a
        //   descendant escapes a rule on its container — the same argument `text-clip` earns its
        //   place with, and the pixel tests assert it against an inherited declaration because that
        //   is the only arrangement in which any of them can do anything.
        { "italic", "font-style", "italic" },
        { "not-italic", "font-style", "normal" },
        { "wrap-anywhere", "overflow-wrap", "anywhere" },
        { "wrap-break-word", "overflow-wrap", "break-word" },
        { "wrap-normal", "overflow-wrap", "normal" },
        { "break-words", "overflow-wrap", "break-word" },
        { "break-normal", "overflow-wrap", "normal" },

        //   ⚠ `break-all` and `break-keep` are `word-break`, which is a different property at a
        //   different stage: `overflow-wrap` is consulted only where nothing fits, and `word-break`
        //   changes which breaks exist. Both rows are here because the union this table exists to
        //   defeat would otherwise hide `break-keep` behind `break-all` — the two keywords need
        //   different scripts to be visible in, and only one of them shows up in Latin at all.
        { "break-all", "word-break", "break-all" },
        { "break-keep", "word-break", "keep-all" },
        { "text-wrap", "text-wrap", "wrap" },
        { "text-nowrap", "text-wrap", "nowrap" },

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
        { "bg-position-[25%_75%]", "background-position", "25% 75%" },
        { "bg-size-[25%_75%]", "background-size", "25% 75%" },

        // ⚠ <b>The mask keyword families, which are the part of the `mask-*` cluster a computed
        // value can honestly state.</b> Each of these is one property, one keyword, read by name —
        // unlike the edge-ramp roots beside them in the registry, whose value is an assembled
        // `--tw-mask-*` composition and whose row would pin the mechanism rather than the answer,
        // exactly as <see cref="NumericFigures" /> found for `font-variant-numeric`. Those are still
        // uncovered here and #629 says so.
        //
        // ⚠ <b>The bare `mask` root is `mask-repeat` and not `mask-mode`</b>, which is worth the
        // line because the ledger carries the mode family under the root spelling `mask` too — two
        // rows, two meanings, one prefix. `mask-alpha` and its two siblings are the mode family, and
        // they are separate registry roots.
        { "mask-repeat", "mask-repeat", "repeat" },
        { "mask-none", "mask-image", "none" },
        { "mask-alpha", "mask-mode", "alpha" },
        { "mask-luminance", "mask-mode", "luminance" },
        { "mask-match", "mask-mode", "match-source" },
        { "mask-add", "mask-composite", "add" },
        { "mask-subtract", "mask-composite", "subtract" },
        { "mask-intersect", "mask-composite", "intersect" },
        { "mask-exclude", "mask-composite", "exclude" },
        { "mask-position-[25%_75%]", "mask-position", "25% 75%" },
        { "mask-size-[25%_75%]", "mask-size", "25% 75%" },

        // ⚠ <b>`blur-*` was the last row of A6's "paint the renderer has no channel for", and it left
        // that list the way `fill-*` and `ring-*` did — by the row being wrong about the disease.</b>
        // It named `--blur`, a property of this engine's own invention that nothing assembled and
        // nothing could read, so the debt was filed against a name that could never come due. It is a
        // composed family now: the fragment carries the length and an `Alongside` assembles a real
        // `filter`, which `DrawListBuilder` reads and both executors render. Asserted on `filter`
        // rather than on the fragment, because the fragment alone is what the old row already proved
        // is not evidence of anything.
        { "blur-2", "filter", "blur(8px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0) drop-shadow(0 0 transparent)" },

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
        { "brightness-125", "filter", "blur(0px) brightness(1.25) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0) drop-shadow(0 0 transparent)" },
        { "contrast-75", "filter", "blur(0px) brightness(1) contrast(0.75) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0) drop-shadow(0 0 transparent)" },
        { "grayscale", "filter", "blur(0px) brightness(1) contrast(1) grayscale(1) hue-rotate(0deg) invert(0) saturate(1) sepia(0) drop-shadow(0 0 transparent)" },
        { "hue-rotate-90", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(90deg) invert(0) saturate(1) sepia(0) drop-shadow(0 0 transparent)" },
        { "invert", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(1) saturate(1) sepia(0) drop-shadow(0 0 transparent)" },
        { "saturate-150", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1.5) sepia(0) drop-shadow(0 0 transparent)" },
        { "sepia-50", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0.5) drop-shadow(0 0 transparent)" },

        // ⚠ <b>The ninth function, and the row worth reading twice is what it does to the eight above
        // it.</b> Every one of them now carries a <c>drop-shadow(0 0 transparent)</c> it did not
        // carry before, because `UtilityComposition.Filter` assembles all nine into every `filter`
        // any filter family emits and the seven or eight nobody wrote resolve to their identities.
        // <c>drop-shadow</c>'s identity cannot be a number — there is no offset that means "no
        // shadow" — so it is a shadow in a colour that cannot be seen, which
        // <c>DrawListBuilder.Settle</c> discards before it costs a surface.
        //
        // ⚠ And it is <i>last</i>, which is v4's order and also the only one this engine could
        // execute: a drop shadow does not commute with a blur. See `UiLayer.Shadow`.
        { "drop-shadow-lg", "filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) saturate(1) sepia(0) drop-shadow(0 4px 4px rgb(0 0 0 / 0.15))" },

        // ⚠ <b>The keyword, and the row is here to record that it is <i>not</i> the eight functions at
        // their identities.</b> That spelling would draw the same picture and be a different
        // declaration — a `var()` chain resolving to `blur(0px) brightness(1) …`, which
        // `DrawListBuilder.Filter` reads as a real list, composes to the identity and only then
        // discards in `Settle`. `none` is refused by the list check one step earlier and needs no
        // fragment at all, which is why this is the one family in the block with nothing in
        // `Alongside`.
        { "filter-none", "filter", "none" },

        // ── The backdrop's ten ──────────────────────────────────────────────────────────
        //
        // ⚠ <b>A second declaration with a second set of fragments, and the rows are here to say that
        // it is not the first one's.</b> `filter` transforms what the element drew and
        // `backdrop-filter` transforms what is behind it, so an element may carry `blur-2` and
        // `backdrop-blur-lg` and mean two different pictures. Fragments shared between the two would
        // make that impossible to write and would look, from a stylesheet, exactly like the second
        // class having been ignored.
        //
        // ⚠ <b>Nine functions where the list above has nine, and one of them is different in each:
        // `opacity()` here, `drop-shadow()` there.</b> That is Tailwind's set and this engine's:
        // `backdrop-opacity-*` is one of the ten roots and `backdrop-drop-shadow-*` is not a class
        // anywhere. `DrawListBuilder.One` refuses each in the other's property.
        //
        // ⚠ <b>`opacity`'s identity is one and not zero</b>, which is the one initial in the table a
        // reader coming from `grayscale(0)` will guess wrong — and guessing it wrong would erase the
        // backdrop of every element carrying any `backdrop-*` class at all.
        //
        // ⚠ Only the unprefixed property is asserted here; every one of these families emits
        // `-webkit-backdrop-filter` with the identical value beside it, which nothing in this engine
        // reads and Safari would. See `UtilityFamilies.BackdropAlongside`.
        { "backdrop-blur-2", "backdrop-filter", "blur(8px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) opacity(1) saturate(1) sepia(0)" },
        { "backdrop-brightness-125", "backdrop-filter", "blur(0px) brightness(1.25) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) opacity(1) saturate(1) sepia(0)" },
        { "backdrop-contrast-75", "backdrop-filter", "blur(0px) brightness(1) contrast(0.75) grayscale(0) hue-rotate(0deg) invert(0) opacity(1) saturate(1) sepia(0)" },
        { "backdrop-grayscale", "backdrop-filter", "blur(0px) brightness(1) contrast(1) grayscale(1) hue-rotate(0deg) invert(0) opacity(1) saturate(1) sepia(0)" },
        { "backdrop-hue-rotate-90", "backdrop-filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(90deg) invert(0) opacity(1) saturate(1) sepia(0)" },
        { "backdrop-invert", "backdrop-filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(1) opacity(1) saturate(1) sepia(0)" },
        { "backdrop-opacity-50", "backdrop-filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) opacity(0.5) saturate(1) sepia(0)" },
        { "backdrop-saturate-150", "backdrop-filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) opacity(1) saturate(1.5) sepia(0)" },
        { "backdrop-sepia-50", "backdrop-filter", "blur(0px) brightness(1) contrast(1) grayscale(0) hue-rotate(0deg) invert(0) opacity(1) saturate(1) sepia(0.5)" },

        // ⚠ The keyword, for `filter-none`'s reason word for word — and it sets the prefixed copy too,
        // so that turning the feature off turns off the copy a browser would have read.
        { "backdrop-filter-none", "backdrop-filter", "none" },

        // ⚠ Two families composing into one declaration is the case this theory cannot state — a row
        // here is one class — so it is
        // <see cref="Two_filter_families_compose_into_one_declaration_and_one_matrix" /> instead.
        { "shadow-elevation", "box-shadow", "0 0 0 0px currentcolor, 0px 10px 26px rgba(12, 14, 18, 0.22)" },

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

        // ⚠ <b>And the two keywords, which are exactly the case these rows cannot prove.</b> `none`
        // is a *paint* and not a colour, so `Icon.Resolve`'s `ColorOf` answered `null` to it and to
        // an unset property alike and painted the foreground for both — `fill-none` resolved,
        // cascaded, and drew the glyph it was written to hide, with `fill` measuring read the whole
        // time. That is why they were refused for weeks rather than registered. Proved in pixels by
        // `Vixen.Ui.Controls.Tests.IconArtTests`, which is the only place the answer exists.
        { "fill-none", "fill", "none" },
        { "stroke-none", "stroke", "none" },

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

        // ⚠ <b>The paragraph above named `border-s-*`/`border-e-*` as "the table's one genuinely
        // partial pair" while the table held no row for either of them</b>, and the same was true of
        // `border-t-*` and `border-r-*` — prose asserting a fact the rows did not. That is #629 in
        // miniature: the finer claim this file exists to make was being made in a comment. The width
        // half is here; the colour half of the logical pair is in <see cref="Inert" />, which is what
        // makes the partiality a row rather than a sentence.
        { "border-t-2", "border-top-width", "2px" },
        { "border-r-2", "border-right-width", "2px" },
        { "border-t-accent", "border-top-color", "#2f6ecd" },
        { "border-r-accent", "border-right-color", "#2f6ecd" },
        { "border-e-2", "border-inline-end-width", "2px" },
        { "border-s-2", "border-inline-start-width", "2px" },

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

        // ⚠ <b>The two corners and the two physical sides the block above left unstated, and note
        // the shape of the value.</b> The bare `rounded-*` row above computes `8px 8px` — a pair,
        // because ExCSS expands the `border-radius` shorthand into the two-value form — where every
        // per-corner and per-side root computes the single length. A row copied from the shorthand's
        // expectation onto a per-corner root would be red for a reason that has nothing to do with
        // the corner.
        { "rounded-tr-2xl", "border-top-right-radius", "16px" },
        { "rounded-bl-2xl", "border-bottom-left-radius", "16px" },
        { "rounded-l-2xl", "border-top-left-radius", "16px" },
        { "rounded-l-2xl", "border-bottom-left-radius", "16px" },
        { "rounded-r-2xl", "border-top-right-radius", "16px" },
        { "rounded-r-2xl", "border-bottom-right-radius", "16px" },

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

        // ⚠ <b>The six logical radii, and what these rows can and cannot say.</b> They assert the
        // cascade carries the logical longhand — which is the half that was missing, since nothing
        // interned these four names at all — and they deliberately do not assert which physical
        // corner it lands on, because that is not a fact about the cascade. It is decided at paint
        // time against `direction`, and the test that pins it is
        // `RasterizerTests.A_logical_corner_radius_is_resolved_against_the_direction`, which reads
        // pixels under both directions. A row here claiming a corner would be claiming the `ltr`
        // answer is the only answer, which is the mistake the whole feature exists to avoid.
        { "rounded-ss-lg", "border-start-start-radius", "8px" },
        { "rounded-se-[6px]", "border-start-end-radius", "6px" },
        { "rounded-ee-lg", "border-end-end-radius", "8px" },
        { "rounded-es-[2px]", "border-end-start-radius", "2px" },
        { "rounded-s-[6px]", "border-start-start-radius", "6px" },
        { "rounded-s-[6px]", "border-end-start-radius", "6px" },
        { "rounded-e-[6px]", "border-start-end-radius", "6px" },
        { "rounded-e-[6px]", "border-end-end-radius", "6px" },

        // ⚠ <b>The outline, which is not the ring and not a border.</b> `outline-2` is a width and
        // `outline-accent` is a colour, one prefix told apart by the value's shape — `border`'s
        // ambiguity, and `ring`'s. The bare `outline` is one pixel, which is v4.
        //
        // ⚠ <b>No `outline-style` row beside the width, and its absence is the assertion.</b> v4
        // emits `outline-style: var(--tw-outline-style)` on every width class because a browser
        // defaults the style to `none`; this engine's border model has no style at all and
        // `EmitOutline` matches it, so a width alone is a ring. A row here would be asserting a
        // declaration the family deliberately does not emit.
        { "outline", "outline-width", "1px" },
        { "outline-2", "outline-width", "2px" },
        { "outline-[3px]", "outline-width", "3px" },
        { "outline-accent", "outline-color", "#2f6ecd" },
        { "outline-solid", "outline-style", "solid" },
        { "outline-none", "outline-style", "none" },
        { "outline-hidden", "outline-style", "none" },
        { "outline-offset-2", "outline-offset", "2px" },
        { "outline-offset-0", "outline-offset", "0px" },

        // Overflow, all three properties and all four keywords. ⚠ `auto` is here because the layout
        // maps it onto `Overflow.Scroll` — the two differ only over whether the gutter is reserved
        // when there is nothing to scroll, and no member of `Overflow` carries that distinction.
        { "truncate", "overflow", "hidden" },
        { "overflow-scroll", "overflow", "scroll" },
        { "overflow-auto", "overflow", "auto" },
        { "overflow-x-scroll", "overflow-x", "scroll" },

        // ⚠ These were in `Inert` until the per-axis clip landed. `auto` maps onto `Scroll` in the
        // layout's keyword table rather than becoming a fourth member, because CSS gives the two the
        // same layout and differs only over whether the gutter is reserved when nothing overflows.
        { "overflow-x-auto", "overflow-x", "auto" },
        { "overflow-y-auto", "overflow-y", "auto" },
        { "overflow-y-scroll", "overflow-y", "scroll" },

        // ⚠ The gutter, and the row worth reading beside the six above it. `scrollbar-width` was
        // filed under "nothing here draws a scrollbar" for as long as those comments were — but the
        // gutter is not paint, it is room taken out of the content box, and a `Scroll` axis now
        // reserves it. Lengths rather than the web's `auto | thin | none` because nothing here owns
        // the widget the keyword is a preference about; `auto` is the 10 points `ControlTheme.vcss`
        // gives `scrollbar.vertical`.
        { "scrollbar-auto", "scrollbar-width", "10px" },
        { "scrollbar-thin", "scrollbar-width", "6px" },
        { "scrollbar-none", "scrollbar-width", "0px" },

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

        // ⚠ <b>The fourth member of that set, and the unit is the assertion.</b> `delay-300`
        // computes `300ms` and not the bare count — the same shape `duration-150` above carries, and
        // the one thing about this family a row can state that its mere registration cannot.
        { "delay-300", "transition-delay", "300ms" },
        { "cursor-pointer", "cursor", "pointer" },

        // ⚠ <b>`cursor-help` is here because it was the one keyword of the eight the ledger lists
        // that did not resolve</b>, and the fix was a `UiCursor.Help` rather than anything in this
        // table — `UtilityFamilies` deliberately registers only the keywords `UiCursor` has a
        // reading of, since one it cannot map resolves to the host's default and is
        // indistinguishable from writing nothing.
        { "cursor-help", "cursor", "help" },
        { "pointer-events-none", "pointer-events", "none" },

        // ⚠ <b>`caret-*` is a reader that existed under a different name, which is a shape this
        // table had not seen before.</b> `TextField` and `CodeEditor` have drawn the insertion point
        // off Vixen's own `--caret-color` since they were written; the family emits CSS's
        // `caret-color` and both controls now ask that first. So the row asserts the *standard*
        // spelling: emitting `--caret-color` instead would have worked today and made every
        // downstream sheet's root token unoverridable by a class, which is backwards.
        { "caret-text-muted", "caret-color", "#5c616b" },
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
        // ⚠ <b>`scale-*` and `rotate-*` were in `Inert` under a paragraph saying they would leave it
        // "the day a compositor lands", and the compositor landed without them moving.</b> Their
        // refusal was the sharpest argument in this file — a rotated `DrawCommand` is not an
        // axis-aligned rectangle, a scaled subtree needs re-shaping and a transform must not touch
        // layout — and every clause of it survived being closed, because the answer was to composite
        // the subtree into a surface and transform the *quad*. Nothing here had to learn about
        // rotation.
        //
        // ⚠ <b>What that means for the table is the finding, not the feature.</b> The expiry the
        // paragraph named was `InertProperties.txt`'s, which governs that file and not this one, so
        // the rows sat here reading "nothing looks at this" while
        // `docs/plan/43-web-styling-parity.tsv` measured both roots `partial` with `engine_reads` set
        // — and those columns are recomputed on every run. `An_inert_family_still_computes_a_value`
        // could never have said so: it asserts the value is not null, which is as true of a family
        // with a reader as of one without. See #532 and #582.
        //
        // The values are Tailwind's own and are ratios rather than multipliers — `scale-2` is
        // `scale: 2%`, not twice — which is the half of these two rows that was always right.
        { "translate-x-2", "translate", "8px 0px" },
        { "translate-y-2", "translate", "0px 8px" },

        // ⚠ <b>The two-axis root and its `none` are rows because the inventory has no other way to
        // hold them</b>, and neither is a third fragment: `translate-2` writes *both* axis slots and
        // assembles the same `translate` the two rows above do, so it reads `8px 8px`; and
        // `translate-none` is its own registered family rather than a keyword value on that one,
        // because `Alongside` is appended on every resolution and would have re-assembled the
        // movement over the top of the `none` that was meant to turn it off.
        { "translate-2", "translate", "8px 8px" },
        { "translate-none", "translate", "none" },
        { "scale-2", "scale", "2%" },
        { "rotate-45", "rotate", "45deg" },

        // ⚠ <b>The per-axis scales are the composition again, and the <i>identity</i> in the slot
        // the class did not name is the whole of what these two rows say.</b> `scale-x-2` computes
        // `2% 1`, not `2%` and not `2% 2%`: the y half falls back to `--tw-scale-y`'s initial `1`,
        // which is a multiplier where the value written is a ratio. A fallback of `0` — the guess a
        // reader coming from `translate` makes, and the initial that would look right in the
        // fragment — collapses the box on the axis nobody touched.
        { "scale-x-2", "scale", "2% 1" },
        { "scale-y-2", "scale", "1 2%" },

        // The origin the two of them turn and scale about. Read by name and not composed, which is
        // why it is one row rather than a pair.
        { "origin-top", "transform-origin", "top" },

        // ⚠ <b><c>rotate-z-*</c>, whose family emits the shorthand rather than a longhand — the first
        // row here to name <c>transform</c>.</b> Composed the way the translations are: the class
        // writes <c>--tw-rotate-z</c> and emits a <c>transform</c> assembled out of it, so what the
        // cascade holds is the assembly. Written out, the expectation is also the proof that the
        // <i>function</i> lives in the assembler and the angle in the fragment — which is what makes
        // <c>-rotate-z-45</c> spellable, since `TryNegate` refuses a value that does not start with a
        // digit and would have refused <c>rotateZ(45deg)</c>.
        //
        // ⚠ <b>And it is the row that says the ledger's refusal was stale.</b> `rotate-z-*` was
        // recorded as waiting on a `<transform-function>` parser and declared its own expiry on
        // `StyleValueKind.Function`. The parser was written months ago in `TransformReader.Functions`
        // — over the declaration's text, not as a value kind — so the clause could never fire and the
        // row stayed `absent` over a capability the engine already had. See
        // <see cref="The_rotate_z_family_turns_the_box_the_way_the_rotate_property_does" />.
        { "rotate-z-45", "transform", "rotateZ(45deg)" },

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
        //
        // ⚠ <b>And both rows now carry a second, transparent item, which is the ring sharing the
        // property with `shadow-*` rather than winning it.</b> The two families wrote one longhand,
        // so `shadow-lg ring-2` resolved to whichever rule the cascade picked and the other class
        // did nothing — `filter`'s failure, in the one place the fragment table had not reached.
        // See <see cref="A_ring_and_an_elevation_shadow_on_one_element_are_both_painted" />.
        { "ring-2", "box-shadow", "0 0 0 2px currentcolor, 0 0 transparent" },
        { "ring-accent", "box-shadow", "0 0 0 0px #2f6ecd, 0 0 transparent" }
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
    ///         <b>History: <c>overflow-clip</c> was the fourth case, and it was <c>auto</c>'s exactly.</b>
    ///         The class was unregistered because <c>LayoutStyleBuilder</c> did not know the keyword,
    ///         so an author who wrote <c>overflow: clip</c> by hand got a box that clipped in the draw
    ///         list and kept the §4.5 content floor the <c>hidden</c> beside it drops. It maps onto
    ///         <c>Overflow.Hidden</c> — not a fourth member: CSS separates <c>clip</c> from
    ///         <c>hidden</c> by a scroll container and by programmatic scrolling, and the paragraph
    ///         below is the reason this engine grants <c>hidden</c> neither.
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


        // ⚠ <b>Transforms are gone from this table, and they left later than they should have.</b>
        // Four rows were here, all four naming a `--`-prefixed property of the family's own
        // invention — `--scale` is not a CSS property, so no engine anywhere would ever have read
        // it, and it was not a composition fragment either, because nothing assembled it. The debt
        // was filed against a name that could not come due, which is `grid-cols-3`'s failure exactly.
        // The two translations moved to `Supported` when the emissions were corrected; `scale-*` and
        // `rotate-*` stayed behind a paragraph that ended "the day a compositor lands, the gate's
        // expiry check on `InertProperties.txt` is what says so".
        //
        // ⚠ <b>The compositor landed and nothing said so, because that expiry governs a different
        // file.</b> `InertProperties.txt` closed its Transforms block, the parity ledger measured
        // both roots `partial` with `engine_reads` set — a recomputed column, so measurement rather
        // than claim — and these two rows went on reading "nothing looks at this" for as long as
        // anybody left them alone. `An_inert_family_still_computes_a_value` cannot notice: it asserts
        // the value is not null, which is as true of a family with a reader as of one without. That
        // is #532, and the table's missing mechanism is #582.

        // ⚠ <b>`align-middle` has left this table for <see cref="Refused" />, and it is the first
        // row to leave without the family changing at all.</b> It was never "a property with no
        // consumer": `vertical-align` is read. It is a *value* the consumer declines, and the two
        // are different claims with different expiry dates — which is #582, and is why the one-line
        // link this table now has could not simply be applied to every row it held.

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

        // ⚠ <b>Half of `border-s-*`/`border-e-*`, and the half is the row.</b> The width longhands
        // are in <see cref="Supported" /> above; these two colours are read by nothing, which is the
        // "one genuinely partial pair" that block's comment has been claiming since it was written
        // — against a table that until #629 held no row for either root at all. `align-*` is the
        // same shape for a different reason: a family is not a property, and half a family being
        // real is a state this file has to be able to express in rows rather than in prose.
        // `InertProperties.txt` carries both names against #21, which is what expires them.
        { "border-e-accent", "border-inline-end-color" },
        { "border-s-accent", "border-inline-start-color" },

        // ⚠ <b>The three `transition-*` rows were here and are now back in `Supported`.</b> They are
        // the only rows in this file to have made the trip in both directions, and the round trip is
        // worth more than either leg: they sat in `Supported` on the strength of the cascade computing
        // a value, the gate's first run moved them here, and A20 wiring the animator to the frame loop
        // moved them back — this time with a mid-flight width and a mid-flight colour behind them
        // rather than a computed value. See `Vixen.Ui.Tests.TransitionTests`, and the closed block in
        // `InertProperties.txt` for what the gate needed before it could see the third one.
    };

    /// <summary>
    ///     The families whose property <b>is</b> read and whose <i>value</i> the reader declines —
    ///     the class name, the longhand, the value it computes, and the fact that proves the refusal
    ///     is still real.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This table exists because the obvious mechanism for <see cref="Inert" /> does not
    ///         fit every row it used to hold.</b> #532 is what a refusal list with no mechanism
    ///         costs: <c>scale-*</c> and <c>rotate-*</c> sat in <see cref="Inert" /> saying "nothing
    ///         looks at this" while the parity ledger measured both roots <c>partial</c> with
    ///         <c>engine_reads</c> set. The fix is one line —
    ///         <see cref="No_inert_row_outlives_the_allow_list_entry_it_names" /> — and applied to
    ///         <c>align-middle</c> it would have gone red on a row that is correct and deliberate,
    ///         because <c>vertical-align</c> is not in <c>InertProperties.txt</c> and must not be:
    ///         it is read. An exemption for the exemption is the anti-pattern this repository has
    ///         spent a month removing, so the answer is a second table with a second contract.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ask what this half prints on the day it does not run.</b> A refused-value row
    ///         whose reader has quietly learnt the value looks <i>identical</i> to one whose reader
    ///         never will — both compute the value, and neither says anything about what the value
    ///         did. So a row here owes a named behavioural fact whose assertion is false the day the
    ///         refusal ends, and <see cref="Every_refused_value_names_a_fact_this_class_declares" />
    ///         is what stops a row being added without one. A not-null assertion is exactly the
    ///         instrument that could not see #532, and repeating it here would repeat #532.
    ///     </para>
    /// </remarks>
    public static TheoryData<string, string, string, string> Refused => new() {
        // ⚠ <b>`align-middle` moved here from `Inert`, and its three siblings are in `Supported`.</b>
        // §10.8.1 defines `middle` as the parent's baseline plus half its x-height, and an x-height
        // is a font metric; `Vixen.Ui.Layout` is geometry and has no font, so
        // `LayoutStyleBuilder.VerticalAligns` maps only `baseline`, `top` and `bottom` and the
        // keyword never reaches a `LayoutStyle`. Approximating it is the tempting mistake: rounding
        // `middle` to `baseline` looks almost right and reads as a rendering quirk. Task #26, and
        // `InlineKnownGaps.txt` says what it would take — `align-text-top`, `align-text-bottom`,
        // `align-sub` and `align-super` are the same story if the families are ever registered.
        {
            "align-middle", "vertical-align", "middle",
            nameof(The_refused_middle_moves_nothing_while_the_top_beside_it_moves)
        }
    };

    /// <summary>Roots this file has no row for, each with the reason it has none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exit criterion 3 of doc 43 says "a row per root", and until this list existed
    ///         nothing said whether that was true.</b> <see cref="Supported" />,
    ///         <see cref="Inert" />, <see cref="Refused" /> and <see cref="NumericFigures" /> are
    ///         hand-maintained and the registry was never enumerated against them — so <b>a root with
    ///         no row anywhere was not a red test, it was silence.</b> Ask what this file printed on
    ///         the day a root was missing from it: <c>Passed!</c>. That is how the five child-scoped
    ///         roots went unnoticed long enough to need #276, and the walk found 121 more of them the
    ///         first time it ran — #629, which is the work of writing the rows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A literal list of names and never a predicate, which is the whole design.</b> A
    ///         rule of the shape "roots whose family has a <c>Scope</c> are exempt" is satisfied by
    ///         precisely the defect it is meant to catch — a scoped root with no coverage at all
    ///         passes it — and so is every other rule that decides membership by looking at the
    ///         family. Membership here is decided by somebody typing the name, so a root added
    ///         tomorrow is not exempt by accident.
    ///     </para>
    ///     <para>
    ///         <b>The list may only shrink.</b>
    ///         <see cref="Every_registered_root_is_claimed_by_a_row_or_named_here" /> fails on an
    ///         entry that has since gained a row, and on an entry naming a root the registry no
    ///         longer has — so a line cannot outlive its reason the way <c>DocsExempt.txt</c>'s do.
    ///     </para>
    ///     <para>
    ///         ⚠ Only the first group is permanent. A <c>Family.Scope</c> root <i>cannot</i> be a row
    ///         here, because the declaration it emits is never on the element a row would ask about.
    ///         Every other entry is work nobody has done.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And most of it has since been done: 121 entries are 33.</b> #629 wrote the rows
    ///         for eighty-eight roots and six of this list's groups went with them — which is the
    ///         expiry above doing its job, since the rows and the lines arrived on two different
    ///         branches and neither knew about the other. What is left is the five scoped roots and
    ///         the mask and gradient cluster, and the cluster's own reason had to be rewritten: the
    ///         file it named as pinning it against pixels does not write a utility class at all.
    ///     </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Uncovered { get; } = BuildUncovered();

    static Dictionary<string, string> BuildUncovered() {
        var uncovered = new Dictionary<string, string>(StringComparer.Ordinal);

        UncoveredGroup[] groups = [
        // ── Scoped — the entries #276 found, and the only permanent ones ────────────────────────────
        new UncoveredGroup(
            "a `Family.Scope` root: the declaration lands on `& > :not(:last-child)` and never on the " +
            "element under test, so no computed-value row can express it at all. `ChildScopedFamilyTests` " +
            "is where these are held.",
            "divide", "divide-x", "divide-y", "space-x", "space-y"
        ),

        // ── Masks and gradients ─────────────────────────────────────────────────────────────────────
        //
        // ⚠ <b>Six groups used to stand between this one and the scoped roots above, and they were
        // deleted rather than shrunk.</b> #629 wrote their rows — every physical edge and axis, the
        // sizing spellings, the per-edge borders and per-corner radii, the row half of grid
        // placement, the scroll insets, the per-axis scales, `origin`, and ten typographic roots
        // whose only appearance anywhere was a line in this list. 121 entries became 33, and a line
        // that has since gained a row is one this test fails on rather than one somebody has to
        // notice.
        new UncoveredGroup(
            "the mask and gradient cluster, whose computed value is an assembled `--tw-*` list: a row here " +
            "would pin the mechanism rather than the answer, which is `NumericFigures`' finding one " +
            "category over. ⚠ And half the reason this group used to give is refuted: it claimed " +
            "`MaskGradientTests` pins the cluster against pixels, and that file writes hand-authored " +
            "`mask-image` declarations rather than one utility class — so what actually covers these " +
            "roots is their emission, which is the mechanism again and not the answer.",
            "bg-conic", "bg-linear", "bg-radial", "from", "mask-b-from",
            "mask-b-to", "mask-conic", "mask-conic-from", "mask-conic-to", "mask-l-from",
            "mask-l-to", "mask-linear", "mask-linear-from", "mask-linear-to", "mask-r-from",
            "mask-r-to", "mask-radial", "mask-radial-at", "mask-radial-from", "mask-radial-to",
            "mask-t-from", "mask-t-to", "mask-x-from", "mask-x-to", "mask-y-from",
            "mask-y-to", "to", "via"
        ),
        ];

        foreach (var (why, roots) in groups) {
            foreach (var root in roots) {
                uncovered[root] = why;
            }
        }

        return uncovered;
    }

    /// <summary>One reason and the roots that share it.</summary>
    readonly record struct UncoveredGroup(string Why, params string[] Roots);

    /// <summary>Every root the registry answers is in one of the tables, or is named in the list.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The count is asserted as well as the membership, and without it the whole test is
    ///         vacuous.</b> An enumeration that returned nothing agrees with every table ever
    ///         written — the same anti-vacuity anchor <c>SharedUiShaderTests</c> and the golden walk
    ///         both had to be given. The floor moves upward when families land and is never lowered
    ///         to make a run pass: a walk that suddenly sees fewer roots is a broken walk, which is
    ///         the failure this number exists to name.
    ///     </para>
    ///     <para>
    ///         The roots come from <see cref="UtilityFamilies.Surface" /> rather than from a list
    ///         here, for the reason that method's own remark gives: every hand-written inventory of
    ///         that table has rotted, and a second copy of the registry is a second thing to forget.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_registered_root_is_claimed_by_a_row_or_named_here() {
        var roots = UtilityFamilies.Surface(Tokens())
            .Select(utility => UtilityFamilies.SplitName(utility).Name)
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ Anti-vacuity, and it is two claims rather than one: a floor on how many roots the walk
        // saw, and four roots named outright. A `Surface` that answered an empty list would satisfy
        // every membership assertion below and report a table in perfect order.
        Assert.True(roots.Count >= 275, $"the walk saw only {roots.Count} roots, which is not the registry");
        Assert.Contains("flex", roots);
        Assert.Contains("select", roots);
        Assert.Contains("align", roots);
        Assert.Contains("snap", roots);

        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Supported) {
            claimed.Add(UtilityFamilies.SplitName(row.Data.Item1).Name);
        }

        foreach (var row in Inert) {
            claimed.Add(UtilityFamilies.SplitName(row.Data.Item1).Name);
        }

        foreach (var row in Refused) {
            claimed.Add(UtilityFamilies.SplitName(row.Data.Item1).Name);
        }

        foreach (var row in NumericFigures) {
            claimed.Add(UtilityFamilies.SplitName(row.Data.Item1).Name);
        }

        var unaccounted = roots
            .Where(root => !claimed.Contains(root) && !Uncovered.ContainsKey(root))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            $"""
             {unaccounted.Count} registered root(s) have no row in Supported, Inert, Refused or
             NumericFigures and no line in `Uncovered`. Doc 43's exit criterion 3 is a row per root;
             add the row, or add the name to `Uncovered` with the reason it cannot have one:

               {string.Join("\n  ", unaccounted)}
             """
        );

        // ⚠ The expiry, and it is what makes this a list rather than a shrug. A line whose root has
        // since gained a row is a line that outlived its reason, which is the failure mode
        // `docs/DocsExempt.txt` has and cannot notice.
        var landed = Uncovered.Keys.Where(claimed.Contains).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            landed.Count == 0,
            $"""
             {landed.Count} root(s) are named in `Uncovered` and now have a row. Delete the line:

               {string.Join("\n  ", landed)}
             """
        );

        // And a line naming a root the registry no longer answers is stale in the other direction.
        var gone = Uncovered.Keys.Where(root => !roots.Contains(root)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            gone.Count == 0,
            $"""
             {gone.Count} root(s) named in `Uncovered` are not registered at all. Delete the line:

               {string.Join("\n  ", gone)}
             """
        );
    }

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

    /// <summary>The eight composable <c>font-variant-numeric</c> keywords and the tag each becomes.</summary>
    /// <remarks>
    ///     ⚠ <b>Tags rather than the property's text, because the property's text is now an assembly
    ///     and the tag is what the shaper is handed.</b> The mapping itself is pinned without a font
    ///     in <c>Vixen.Ui.Tests.FontFeatureStyleTests</c>; these rows are the half that says the
    ///     <i>class</i> reaches it — through the generator, the cascade, <c>var()</c> substitution
    ///     and <c>UiDocument.ResolveText</c>, none of which that file exercises.
    /// </remarks>
    public static TheoryData<string, string> NumericFigures => new() {
        { "ordinal", "ordn" },
        { "slashed-zero", "zero" },
        { "lining-nums", "lnum" },
        { "oldstyle-nums", "onum" },
        { "proportional-nums", "pnum" },
        { "tabular-nums", "tnum" },
        { "diagonal-fractions", "frac" },
        { "stacked-fractions", "afrc" }
    };

    /// <summary>Each numeric class asks the shaper for its one feature and for nothing else.</summary>
    /// <param name="utility">The class name.</param>
    /// <param name="tag">The OpenType tag it should become.</param>
    /// <remarks>
    ///     ⚠ <b>"and for nothing else" is the half that catches the composition being wrong in the
    ///     other direction.</b> Four of the five fragments are unset on an element carrying one
    ///     class, and an assembler that referred to them without the empty fallback would either
    ///     drop the whole declaration — no tags at all — or, if the fallback were an identity rather
    ///     than nothing, ask for four features the author never wrote.
    /// </remarks>
    [Theory]
    [MemberData(nameof(NumericFigures))]
    public void A_numeric_class_asks_the_shaper_for_exactly_its_own_feature(string utility, string tag) {
        using var ui = Sheet(utility);

        var element = ui.Create("probe", ui.Document.Root, null, utility);

        ui.Frame();

        Assert.Equal([tag], Tags(element));
    }

    /// <summary>⚠ Two of them on one element keep both, which for a year they did not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect this composition was built for, and it was a silent wrong answer rather
    ///         than a refusal.</b> Every one of these classes emitted the whole property, so
    ///         <c>class="tabular-nums slashed-zero"</c> resolved to whichever declaration the cascade
    ///         happened to keep and the other class did nothing at all — no diagnostic, no
    ///         unrecognised candidate, nothing to look up. An author who wrote both saw one work.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the third row is the one that says the fragments are grouped by CSS's sets
    ///         rather than one per class.</b> <c>lining-nums</c> and <c>oldstyle-nums</c> are the two
    ///         values of a single set and cannot both apply; a fragment each would emit both tags,
    ///         which is a declaration CSS Fonts 4 § 6.6 does not allow and a shaper request nobody
    ///         meant.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Numeric_classes_compose_within_their_sets_and_overwrite_inside_one() {
        using var ui = Sheet("tabular-nums", "slashed-zero", "lining-nums", "oldstyle-nums", "ordinal");

        var both = ui.Create("probe", ui.Document.Root, null, "tabular-nums", "slashed-zero");
        var three = ui.Create("probe", ui.Document.Root, null, "tabular-nums", "slashed-zero", "ordinal");
        var oneSet = ui.Create("probe", ui.Document.Root, null, "lining-nums", "oldstyle-nums");

        ui.Frame();

        // Tag order is `FontFeatureSet.Of`'s, which sorts, and not the assembly's.
        Assert.Equal(["tnum", "zero"], Tags(both));
        Assert.Equal(["ordn", "tnum", "zero"], Tags(three));

        // One set, one keyword: the later class wins the slot and the earlier one is not also asked
        // for. Which of the two wins is the cascade's business — class order in the attribute does
        // not decide it — so the assertion is the count and the set, not the member.
        Assert.Single(Tags(oneSet));
        Assert.Contains(Tags(oneSet)[0], new[] { "lnum", "onum" });
    }

    static string[] Tags(UiElement element) =>
        element.FontFeatures.Features.Select(feature => Vixen.Ui.Text.FontFeature.Unpack(feature.Tag)).ToArray();

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
    ///     ⚠ <b>The mechanism <see cref="Inert" /> was missing, and the reason it took #532 to
    ///     notice.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>InertProperties.txt</c> expires on its condition — the consumption gate's
    ///         <c>No_allow_list_entry_outlives_the_gap_it_names</c> fails an exemption the moment
    ///         something reads the property. This table had no such condition at all, so
    ///         <c>scale-*</c> and <c>rotate-*</c> went on reading "nothing looks at this" through the
    ///         landing of the compositor, the closing of that file's Transforms block and the parity
    ///         ledger measuring both roots <c>partial</c> on a recomputed column. Borrowing the
    ///         neighbour's expiry costs one assertion and is the whole of the fix.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The paragraph that let #532 happen named this mechanism and named the wrong
    ///         file.</b> It said "the day a compositor lands, the gate's expiry check on
    ///         <c>InertProperties.txt</c> is what says so" — and that check governs <i>that file</i>.
    ///         Nothing carried its verdict here. This is the edge that was missing, written as an
    ///         assertion rather than as a sentence.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Inert))]
    public void No_inert_row_outlives_the_allow_list_entry_it_names(string utility, string property) {
        var path = Path.Combine(AppContext.BaseDirectory, "InertProperties.txt");

        Assert.True(File.Exists(path), $"{path} was not copied beside the test assembly.");

        var exempt = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            exempt.Contains(property),
            $"""
             '{utility}' is in `Inert`, which claims nothing reads '{property}' — and
             InertProperties.txt does not exempt it. Either something reads it now, in which case
             this row belongs in `Supported` or in `Refused`, or the two lists disagree about the
             same fact. ⚠ `Refused` is the answer when the property is read and the *value* is
             declined: that is what happened to `align-middle`, and a row like it must not be
             answered by adding a line to InertProperties.txt for a property that is read.
             """
        );
    }

    /// <summary>The parity ledger's own measurement of what the engine reads agrees with this table.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second independent signal, and the one that already knew about #532.</b>
    ///         <c>docs/plan/43-web-styling-parity.tsv</c>'s <c>engine_reads</c> column is
    ///         <em>recomputed on every run</em> of <c>Vixen.Ui.Styling.Utilities.Tests</c> from the
    ///         consumption probe — a hand edit to it is a failure there. It held
    ///         <c>vertical-align</c> against <c>align-*</c>, and <c>transform</c> against
    ///         <c>scale-*</c> and <c>rotate-*</c>, throughout the window in which <see cref="Inert" />
    ///         went on saying nothing looked at them. Nothing in this assembly consulted it.
    ///         <see cref="No_inert_row_outlives_the_allow_list_entry_it_names" /> borrows
    ///         <c>InertProperties.txt</c>'s expiry instead, which is a different file kept by
    ///         different hands: two signals, and a row has to be wrong in both to survive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The column linked is <c>engine_reads</c> and deliberately not <c>state</c>, which
    ///         is too coarse to link at all.</b> A root's state is one word for a family of values, so
    ///         <c>align-*</c> measures <c>partial</c> and is <i>correctly</i> <c>partial</c> — three of
    ///         its four registered keywords are supported and the fourth is refused at the bridge,
    ///         which is the distinction <see cref="Refused" /> exists to hold. A rule that fired on any
    ///         row whose root measured other than <c>absent</c> would go red on a row that is right;
    ///         and the narrower "<c>works</c> is a contradiction, <c>partial</c> is not" would have
    ///         been green through the whole of #532, because both roots measured <c>partial</c>. What
    ///         is not coarse is the property list: <see cref="Inert" /> claims a named longhand is read
    ///         by nothing, and that column says which longhands are read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A row that names no ledger class fails rather than passing quietly.</b> The lookup
    ///         is the instrument, and a lookup that matched nothing would agree with every table this
    ///         file could hold. The ledger already refuses a registered family that appears in no row,
    ///         so a miss here is either a class name that has drifted or a family that skipped that
    ///         guard — and both are worth a red run.
    ///     </para>
    /// </remarks>
    /// <param name="utility">The class name, as the ledger's <c>classes</c> column spells it.</param>
    /// <param name="property">The longhand this table claims nothing reads.</param>
    [Theory]
    [MemberData(nameof(Inert))]
    public void No_inert_row_survives_the_ledger_measuring_its_property_read(string utility, string property) {
        var (root, reads) = LedgerRow(utility);

        var read = reads
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        Assert.False(
            read.Contains(property),
            $"""
             '{utility}' is in `Inert`, which claims nothing reads '{property}' — and the parity
             ledger's `engine_reads` for '{root}' measures it read: [{reads}]. That column is
             recomputed from the consumption probe on every run, so it is a measurement and this
             table is a claim. `Refused` is the answer when the property is read and its *value* is
             declined; `Supported` is the answer when the value is honoured. ⚠ This is the signal
             that was already true throughout #532 and that nothing here consulted.
             """
        );
    }

    /// <summary>The parity ledger's root and measured <c>engine_reads</c> for one utility class.</summary>
    /// <remarks>
    ///     ⚠ <b>Walked up from the test binary, never down from the repository root.</b>
    ///     <c>.claude/worktrees</c> holds a full checkout per parallel agent, so a search from above
    ///     finds other branches' copies of this file and reports on a tree nobody is editing. The same
    ///     walk <c>ParityLedger.Locate</c> makes, re-stated rather than referenced: a test assembly
    ///     cannot be referenced by another test assembly, and eight lines of TSV reading is a smaller
    ///     price than the third project that would make it shareable.
    ///     <para>
    ///         ⚠ <b>The class-name lookup is not enough on its own, and finding out cost two red rows
    ///         rather than a silent pass, which is the guard working.</b> <c>classes</c> is Tailwind's
    ///         <i>static</i> set for a root as the original survey transcribed it and <c>example</c> is
    ///         one class — so a row keyed on a <i>functional</i> class the ledger never spells, which
    ///         <c>border-s-accent</c> is, matched nothing and failed as "the name has drifted". It had
    ///         not. The fallback is the <c>vixen_family</c> column, which holds registry root names and
    ///         is what <see cref="UtilityFamilies.SplitName" /> yields — several to a cell where one
    ///         ledger row covers several families. That keeps the anti-vacuity half intact: a family
    ///         that reached a table here without reaching the ledger at all still fails, which is the
    ///         only thing this lookup exists to catch.
    ///     </para>
    /// </remarks>
    static (string Root, string Reads) LedgerRow(string utility) {
        var lines = File.ReadAllLines(Ledger());
        var header = lines[0].Split('\t');

        var root = Array.IndexOf(header, "root");
        var reads = Array.IndexOf(header, "engine_reads");
        var classes = Array.IndexOf(header, "classes");
        var example = Array.IndexOf(header, "example");
        var families = Array.IndexOf(header, "vixen_family");

        Assert.True(
            root >= 0 && reads >= 0 && classes >= 0 && example >= 0 && families >= 0,
            "the parity ledger's header no longer names root, engine_reads, classes, example and "
            + "vixen_family, so this check is reading columns by a layout that has moved."
        );

        var family = UtilityFamilies.SplitName(utility).Name;
        (string Root, string Reads)? byFamily = null;

        foreach (var line in lines.Skip(1)) {
            var cells = line.Split('\t');

            if (cells.Length <= classes) {
                continue;
            }

            var named = cells[classes]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Append(cells[example])
                .ToHashSet(StringComparer.Ordinal);

            if (named.Contains(utility)) {
                return (cells[root], cells[reads]);
            }

            var registered = cells[families].Split(',', StringSplitOptions.RemoveEmptyEntries);

            if (byFamily is null && registered.Contains(family, StringComparer.Ordinal)) {
                byFamily = (cells[root], cells[reads]);
            }
        }

        if (byFamily is { } found) {
            return found;
        }

        Assert.Fail(
            $"no row of docs/plan/43-web-styling-parity.tsv names the class '{utility}' in its `classes` or "
            + $"`example` column, and none names its family '{family}' in `vixen_family`, so this check "
            + "compared nothing. Either the class name has drifted from the ledger's spelling of it, or a "
            + "family reached `Inert` without reaching the ledger — which its own completeness guard is "
            + "supposed to refuse."
        );

        return default;
    }

    /// <summary>Where the parity ledger is, found by walking up from the test binary.</summary>
    static string Ledger() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "docs", "plan", "43-web-styling-parity.tsv");

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"docs/plan/43-web-styling-parity.tsv was not found above '{AppContext.BaseDirectory}'."
        );
    }

    /// <summary>
    ///     A refused-value family computes exactly what it emits, which is more than "not null" and
    ///     is the half that notices the emission changing under the row.
    /// </summary>
    /// <param name="utility">The class name.</param>
    /// <param name="property">The longhand it sets, which the engine does read.</param>
    /// <param name="expected">The value the reader declines.</param>
    /// <param name="fact">The behavioural test that proves the refusal is still real.</param>
    [Theory]
    [MemberData(nameof(Refused))]
    public void A_refused_value_computes_exactly_what_the_family_emits(
        string utility,
        string property,
        string expected,
        string fact
    ) {
        Assert.NotEmpty(fact);

        using var ui = Sheet(utility);

        var element = ui.Create("probe", ui.Document.Root, null, utility);

        ui.Frame();

        Assert.Equal(expected, ui.StyleOf(element, property));
    }

    /// <summary>
    ///     ⚠ <b>Every <see cref="Refused" /> row owes a behavioural fact, and this is what makes
    ///     "owes" mean something.</b>
    /// </summary>
    /// <remarks>
    ///     A row here cannot borrow <c>InertProperties.txt</c>'s expiry, because the property it
    ///     names is read and belongs in no exemption list. What it can be held to is a named test
    ///     whose assertion is false on the day the reader learns the value — so the contract is that
    ///     the name in the fourth column resolves to a method on this class. A row added without one
    ///     fails here rather than passing quietly, which is the failure mode #532 was.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Refused))]
    public void Every_refused_value_names_a_fact_this_class_declares(
        string utility,
        string property,
        string expected,
        string fact
    ) {
        Assert.NotEmpty(property);
        Assert.NotEmpty(expected);

        var method = typeof(UtilityFamilySupportTests).GetMethod(fact);

        Assert.True(
            method is not null,
            $"'{utility}' names '{fact}' as the fact that keeps its refusal honest, and this class "
            + "declares no such method."
        );

        Assert.True(
            method!.GetCustomAttributes(typeof(FactAttribute), false).Length > 0
            || method.GetCustomAttributes(typeof(TheoryAttribute), false).Length > 0,
            $"'{fact}' is not a test, so nothing runs it."
        );
    }

    /// <summary>
    ///     ⚠ <b>The refusal, measured on a box rather than believed from a table.</b>
    ///     <c>align-middle</c> computes <c>vertical-align: middle</c> and moves the box by nothing;
    ///     <c>align-top</c> beside it, in the same line and on the same run, moves it by the whole
    ///     height difference.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>align-top</c> half is the instrument and not decoration.</b> "This class
    ///         changed nothing" is the assertion that passes on a scene where <i>no</i>
    ///         <c>vertical-align</c> could have changed anything — one line, one box, boxes of equal
    ///         height, a formatting context that is not inline at all. Asserting in the same frame
    ///         that a value the bridge <i>does</i> map moves the box by a computed amount is what
    ///         says the arrangement can see an alignment at all.
    ///     </para>
    ///     <para>
    ///         The amount is closed-form rather than eyeballed: an atomic inline with no in-flow line
    ///         boxes has its baseline synthesised at its bottom margin edge (§10.8.1), so two
    ///         baseline-aligned boxes sit bottom to bottom and the short one starts exactly the
    ///         height difference below the tall one. <c>align-top</c> puts it at the line's top
    ///         instead, which is that difference higher.
    ///     </para>
    ///     <para>
    ///         ⚠ On the day <c>middle</c> is implemented this goes red on its first assertion — half
    ///         an x-height is not zero — which is the property a refusal list needs and the one
    ///         <c>An_inert_family_still_computes_a_value</c> cannot have.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_refused_middle_moves_nothing_while_the_top_beside_it_moves() {
        using var ui = Sheet("block", "inline-block", "w-8", "h-8", "h-16", "w-48", "align-middle", "align-top");

        var host = ui.Create("probe", ui.Document.Root, null, "block", "w-48");

        var tall = ui.Create("probe", host, null, "inline-block", "w-8", "h-16");
        var plain = ui.Create("probe", host, null, "inline-block", "w-8", "h-8");
        var middle = ui.Create("probe", host, null, "inline-block", "w-8", "h-8", "align-middle");
        var top = ui.Create("probe", host, null, "inline-block", "w-8", "h-8", "align-top");

        ui.Frame();

        Assert.Equal("middle", ui.StyleOf(middle, "vertical-align"));
        Assert.Equal("top", ui.StyleOf(top, "vertical-align"));

        // All four are on one line, or the tops below are being compared across lines and mean
        // nothing.
        Assert.Equal(64f, tall.Height);
        Assert.Equal(32f, plain.Height);
        Assert.True(middle.AbsoluteLeft > plain.AbsoluteLeft, "the four boxes share a line");
        Assert.True(top.AbsoluteLeft > middle.AbsoluteLeft, "the four boxes share a line");

        // The refusal: `middle` lands where the box with no vertical-align at all lands.
        Assert.Equal(plain.AbsoluteTop, middle.AbsoluteTop);

        // The instrument: a value the bridge does map moves it, by the whole height difference.
        Assert.Equal(tall.AbsoluteTop, top.AbsoluteTop);
        Assert.Equal(top.AbsoluteTop + 32f, middle.AbsoluteTop);
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
    ///     ⚠ <b><c>drop-shadow-lg</c> reaches the compositor as a shadow and not merely as a longer
    ///     <c>filter</c> string, which the row in the table above cannot say.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         That row asserts the declaration computes. Every one of the eight filter families
    ///         computes the same declaration and differs only in one <c>var()</c>, so a
    ///         <c>drop-shadow</c> whose fragment landed in the wrong slot, or whose function
    ///         <c>DrawListBuilder.Filter</c> refused, would produce a string that matches character
    ///         for character and a frame with no shadow in it. Worse, refusal is <i>silent and total</i>
    ///         — a list carrying a function this cannot execute is dropped whole — so the failure
    ///         would be every other filter in the engine switching off, which no assertion about a
    ///         computed string can see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The blur beside it is the instrument.</b> With one class the test cannot tell a
    ///         working ninth function from a list that was refused and a group opened by something
    ///         else; with both, the <c>blur-2</c> is what proves the declaration survived being read
    ///         at all, and the shadow is what proves the new slot did.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Drop_shadow_reaches_the_compositor_and_does_not_refuse_the_list_around_it() {
        using var ui = Sheet("drop-shadow-lg", "blur-2", "bg-accent", "w-8", "h-8");

        ui.Create("probe", ui.Document.Root, null, "drop-shadow-lg", "blur-2", "bg-accent", "w-8", "h-8");
        ui.Frame();

        var push = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.LayerPush
        );

        // The blur survived, so the list was read rather than refused.
        Assert.Equal(8f, push.Blur);

        var shadow = Assert.NotNull(push.Shadow);

        // `--drop-shadow-lg: 0 4px 4px rgb(0 0 0 / 0.15)`.
        Assert.Equal(0f, shadow.Offset.X, 3);
        Assert.Equal(4f, shadow.Offset.Y, 3);
        Assert.Equal(4f, shadow.Blur, 3);
        Assert.Equal(0.15f, shadow.Colour.A, 3);
    }

    /// <summary>
    ///     ⚠ <b><c>filter-none</c> reaches the element as the keyword and opens no group, and the
    ///     first half is what stops the second half being vacuous.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An element with no <c>filter</c> at all opens no group either, so "no
    ///         <c>LayerPush</c>" on its own is a sentence about every element in the engine and would
    ///         pass with this family deleted — which is precisely the state it was in until now. What
    ///         separates the two is <see cref="UiTest.StyleOf" />: an unregistered class resolves to
    ///         nothing, the generator emits no rule, and the property is <c>null</c> rather than
    ///         <c>none</c>. Both assertions together say the declaration arrived <i>and</i> that
    ///         arriving cost nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>grayscale</c> beside it is the instrument.</b> Without it the fixture is
    ///         one that could not open a group whatever the styles said — no compositing, a zero-sized
    ///         box, a stylesheet that failed to load — and every such fixture passes this test. The
    ///         second element proves the same document does open one when something asks it to.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Filter_none_is_the_keyword_and_costs_no_group() {
        using var ui = Sheet("filter-none", "grayscale", "bg-accent", "w-8", "h-8");

        var off = ui.Create("off", ui.Document.Root, null, "filter-none", "bg-accent", "w-8", "h-8");
        ui.Create("on", ui.Document.Root, null, "grayscale", "bg-accent", "w-8", "h-8");
        ui.Frame();

        Assert.Equal("none", ui.StyleOf(off, "filter"));

        // One, and it is the `grayscale` element's. A second would mean `filter-none` opened one too.
        Assert.Single(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);
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

        // ⚠ <b>Held against the cascaded value, and it used to say "the accent is blue-dominant; the
        // surface and border tokens are not".</b> That second clause is false and always was: this
        // palette's greys are *cool* greys — `--border` is `#a9adb4` light and `#1e1e20` dark, and B
        // exceeds R in both, as it does for `--surface` and `--text`. The predicate was therefore true
        // of every colour the sheet can produce, so it distinguished nothing and would have passed on
        // a band painted in the hairline. Refuted by swapping the token under a test next door that
        // had copied the same oracle. What the draw list is actually held to is the value the cascade
        // computed for the edge — a band painted from anything else, a fallback included, fails.
        Assert.Equal(ui.ColorOf(element, "border-bottom-color"), band.Color);
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
    ///     ⚠ <b>The keyword the family table cannot tell apart from the one beside it.</b>
    ///     <c>items-end-safe</c> and <c>items-end</c> emit the same property, so the consumption gate
    ///     scores the family green off either — and for as long as the bridge dropped the prefix they
    ///     were the same class. What separates them is a box that does not fit.
    /// </summary>
    /// <remarks>
    ///     Both halves, because a <c>safe</c> alignment with room to spare is <i>defined</i> to be
    ///     indistinguishable from an <c>unsafe</c> one: a reader that answered "start" unconditionally
    ///     would satisfy the overflowing row on its own.
    /// </remarks>
    [Fact]
    public void A_safe_alignment_keeps_an_overflowing_child_inside_the_editor_s_own_row() {
        using var ui = Sheet("flex", "items-end", "items-end-safe", "h-8", "h-24", "w-4");

        var tight = ui.Create("probe", ui.Document.Root, null, "flex", "items-end", "h-8");
        var overflowing = ui.Create("probe", tight, null, "h-24", "w-4");

        var safe = ui.Create("probe", ui.Document.Root, null, "flex", "items-end-safe", "h-8");
        var rescued = ui.Create("probe", safe, null, "h-24", "w-4");

        var roomy = ui.Create("probe", ui.Document.Root, null, "flex", "items-end-safe", "h-24");
        var small = ui.Create("probe", roomy, null, "h-8", "w-4");

        ui.Frame();

        // `h-8` is 32 points and `h-24` is 96, so the child overflows its row by 64.
        Assert.Equal(-64f, overflowing.AbsoluteTop - tight.AbsoluteTop);
        Assert.Equal(0f, rescued.AbsoluteTop - safe.AbsoluteTop);

        // ⚠ And where it fits, `-safe` is `end` and says nothing: 96 − 32.
        Assert.Equal(64f, small.AbsoluteTop - roomy.AbsoluteTop);
    }

    /// <summary>
    ///     ⚠ <b>The bare <c>col-*</c> root, which is a line number and not a span.</b>
    ///     <c>col-span-3</c> and <c>col-3</c> both emit <c>grid-column</c> and the ledger joins them
    ///     to the same property, so nothing about the emission tells them apart — the third track is
    ///     where the item lands only if the value was read as a line.
    /// </summary>
    [Fact]
    public void Col_puts_the_item_in_the_track_its_number_names() {
        using var ui = Sheet("grid", "grid-cols-4", "col-3", "w-48", "h-8");

        var host = ui.Create("probe", ui.Document.Root, null, "grid", "grid-cols-4", "w-48");
        var placed = ui.Create("probe", host, null, "col-3", "h-8");

        ui.Frame();

        // Four tracks across 192 points is 48 apiece; line 3 is the start of the third one.
        Assert.Equal(48f, placed.Width);
        Assert.Equal(host.AbsoluteLeft + 96f, placed.AbsoluteLeft);
    }

    /// <summary>
    ///     ⚠ <b>A <c>place-*</c> class has to move <i>both</i> axes, and the half that fails silently
    ///     is the second one.</b> A family emitting into only its first property would centre the
    ///     item vertically, leave it at the inline start, and look like a working alignment utility
    ///     in every screenshot where the item happens to fill its track.
    /// </summary>
    [Fact]
    public void Place_items_centres_a_grid_item_on_both_axes() {
        using var ui = Sheet("grid", "grid-cols-1", "place-items-center", "w-48", "h-24", "w-4", "h-4");

        var host = ui.Create("probe", ui.Document.Root, null, "grid", "grid-cols-1", "place-items-center", "w-48", "h-24");
        var dot = ui.Create("probe", host, null, "w-4", "h-4");

        ui.Frame();

        // A 16-point box in a 192 × 96 area: (192 − 16) / 2 and (96 − 16) / 2.
        Assert.Equal(88f, dot.AbsoluteLeft - host.AbsoluteLeft);
        Assert.Equal(40f, dot.AbsoluteTop - host.AbsoluteTop);
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
    ///     <c>rotate-z-45</c> turns the box exactly as <c>rotate: 45deg</c> does, and the hit test
    ///     follows it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Against the <c>rotate</c> property rather than against numbers, because the claim
    ///         is a <i>sameness</i> and a number would only restate the arithmetic.</b>
    ///         <c>rotate-z-*</c> exists to spell in Tailwind's vocabulary the rotation the engine
    ///         already performs; the thing that could go wrong is the shorthand composing to a
    ///         different matrix from the longhand — a sign, an origin, a factor order — and every one
    ///         of those shows as a difference between these two elements and as nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ninety degrees, and the two probes are not square.</b> At forty-five the rotated
    ///         quad is symmetric about both diagonals, so a transposed matrix — <c>M12</c> and
    ///         <c>M21</c> swapped, which is the commonest way to write a rotation backwards — draws
    ///         the identical picture. A quarter turn of an oblong is the case that separates them:
    ///         the corner that was bottom-left is where a reversed rotation puts the top-right.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the hit test, for
    ///         <see cref="The_two_translate_families_compose_and_move_the_box_and_its_hit_test" />'s
    ///         reason with a sharper edge.</b> A rotation maps the pointer through the matrix
    ///         inverted, which is separate code from the geometry builder's forward map — so a
    ///         transform that painted correctly and inverted wrongly leaves a control drawn in one
    ///         place and clickable in another, and the vacated corner is the only probe that sees it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_rotate_z_family_turns_the_box_the_way_the_rotate_property_does() {
        using var ui = Sheet("rotate-z-90", "w-16", "h-8", "bg-accent");

        var spun = ui.Create("probe", ui.Document.Root, null, "rotate-z-90", "w-16", "h-8", "bg-accent");

        ui.Frame();

        // One: the shorthand assembled, fragment and all.
        Assert.Equal("rotateZ(90deg)", ui.StyleOf(spun, "transform"));

        // Two: a group was opened for it. A transform that reached the draw list as no layer at all
        // is a box drawn unrotated, and every geometric assertion below would then be about the
        // untransformed rectangle — which is where it started.
        var layer = Assert.Single(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);

        Assert.NotNull(layer.Transform);

        // Three: it is a quarter turn about the box's centre, which is `transform-origin`'s initial
        // value. The element is 64 by 32 at the origin, so its centre is (32, 16) and the corner at
        // (0, 0) lands at (48, -16). ⚠ A transposed matrix puts it at (16, 48) instead, which is the
        // sabotage this oblong exists to catch.
        var corner = layer.Transform!.Value.Apply(new Vector2(spun.AbsoluteLeft, spun.AbsoluteTop));

        Assert.Equal(48f, corner.X, 3);
        Assert.Equal(-16f, corner.Y, 3);

        // Four: the pointer went through the same matrix inverted. The rotated box covers (16, -16)
        // to (48, 48) about that centre, so a point just inside its new left edge is a hit and the
        // box's own old top-left corner is not — the latter being the half an implementation that
        // inverted nothing would still pass.
        Assert.Same(spun, ui.Document.HitTest(20f, 24f));
        Assert.NotSame(spun, ui.Document.HitTest(2f, 2f));
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
        // point of making both of them assemblers. ⚠ The second item is `shadow-*`'s slot resolving
        // to its initial, and the `Single` below is what says an unwritten slot costs no command.
        Assert.Equal("0 0 0 2px #2f6ecd, 0 0 transparent", ui.StyleOf(ringed, "box-shadow"));

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
    ///     ⚠ <b>A ring and an elevation shadow on one element are two commands, and until
    ///     `Rikarin/Vixen#279` item 4 they were one.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>shadow-lg ring-2</c> on a focused card is the way both classes are actually
    ///         written, and it did not work: two families emitted <c>box-shadow</c>, so the cascade
    ///         kept one rule and the other class silently did nothing at all. ⚠ <b>Nothing could see
    ///         it.</b> The consumption gate measures <c>box-shadow</c> read either way;
    ///         <see cref="Supported" /> holds one row per class and each row passed on its own; and
    ///         the property computed a perfectly good value. It is only with both classes on one
    ///         element that the defect exists, which is why this is a <c>Fact</c> and not a row.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two commands and their <i>order</i>, because emitting the list forwards is the
    ///         wrong half of the fix and passes any count.</b> A draw list paints later commands over
    ///         earlier ones and CSS Backgrounds 3 § 7.1.1 paints a shadow list front to back in the
    ///         order written, so <c>EmitShadow</c> emits backwards — and the ring, which is item one,
    ///         has to come out <i>last</i>. Asserting only that both are present would be green with
    ///         the elevation shadow painted over the ring, which is the picture anybody writing the
    ///         pair is trying to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the third assertion is the one that says the composition is not free by
    ///         default.</b> Every element carrying either class emits both slots, so the one the
    ///         author did not write arrives as a transparent shadow; <c>EmitOneShadow</c> drops it.
    ///         Without that, a sheet with <c>shadow-*</c> anywhere in it doubles its shadow commands
    ///         for a picture nobody can see, and no assertion about pixels would ever notice.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_ring_and_an_elevation_shadow_on_one_element_are_both_painted() {
        using var ui = Sheet("ring-2", "ring-accent", "shadow-elevation", "w-8", "h-8");

        var both = ui.Create(
            "probe", ui.Document.Root, null, "ring-2", "ring-accent", "shadow-elevation", "w-8", "h-8"
        );

        var ringOnly = ui.Create("probe", ui.Document.Root, null, "ring-2", "ring-accent", "w-8", "h-8");

        ui.Frame();

        var shadows = ui.Document.Drawing.Commands
            .Where(command => command.Kind == DrawCommandKind.Shadow)
            .ToArray();

        // Three, not four: two for the element carrying both classes, one for the element carrying
        // only the ring — whose `--tw-shadow` slot resolved to a transparent shadow and was dropped.
        Assert.Equal(3, shadows.Length);

        // The ring is the hard-edged one — no blur, so `Thickness` is zero — and the elevation shadow
        // is the blurred one. Both are present, which is the whole claim. ⚠ Selected by the left edge
        // and not only by the falloff, because the sibling's ring is the same shape and the same
        // width: a predicate that matched it too would be a `Single` failure rather than a wrong
        // answer, but only by luck.
        var ring = Assert.Single(
            shadows, command => command.Thickness == 0f && command.X == both.AbsoluteLeft - 2f
        );

        var elevation = Assert.Single(shadows, command => command.Thickness > 0f);

        // ⚠ Painted after, so it lands on top. Emitting the list forwards puts the elevation shadow
        // over the ring and passes every assertion above it.
        Assert.True(
            Array.IndexOf(shadows, ring) > Array.IndexOf(shadows, elevation),
            "the ring is the first item of the list, so it must be the last command emitted"
        );

        // And the ring the composition produced is the same ring it produces alone: sharing the
        // property cost the family nothing.
        Assert.Equal(ringOnly.Width + 4f, ring.Width);
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

    /// <summary>
    ///     ⚠ <b><c>fill-none</c> has to reach the icon from where the class is actually written,
    ///     which is the ancestor.</b> This is the shape <c>accent-*</c> was refused for: a property
    ///     the engine reads on one element and the author writes on another, where testing it on the
    ///     element that reads it passes and the class stays dead everywhere it is used. The colour
    ///     half is proved one test up; a keyword is a different value on the same property, so it
    ///     rides the same <c>InheritedProperties</c> entry — measured rather than assumed, because
    ///     "the property inherits" and "this value survives inheritance" are two claims and only one
    ///     of them had a test.
    /// </summary>
    [Fact]
    public void Fill_none_reaches_an_icon_from_the_ancestor_the_class_is_written_on() {
        using var ui = Sheet("fill-none", "text-text-muted", "w-8", "h-8", "w-4", "h-4");

        var host = ui.Create("probe", ui.Document.Root, null, "fill-none", "text-text-muted", "w-8", "h-8");
        var icon = host.Add<Icon>(null, null, "w-4", "h-4");

        icon.Art = new IconArt(
            new IconPath(new PathBuilder().AddRectangle(new Rectangle(4f, 4f, 16f, 16f)), IconPaint.Foreground)
        );

        ui.Frame();

        Assert.DoesNotContain(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.Field);

        // ⚠ The half that stops this passing against an icon that never drew at all: the same art at
        // the same size, one class away, does emit a field.
        host.RemoveClass("fill-none");
        ui.Frame();

        Assert.Contains(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.Field);
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

    /// <summary>
    ///     ⚠ <b>The five child-scoped roots, which are the one shape <see cref="Supported" /> cannot
    ///     hold a row for.</b> That theory puts the class on the element it then reads, and
    ///     <c>space-x-*</c>, <c>space-y-*</c>, <c>divide-x-*</c>, <c>divide-y-*</c> and
    ///     <c>divide-&lt;color&gt;</c> are the only families in the index that deliberately set nothing
    ///     there — they emit onto <c>&amp; &gt; :not(:last-child)</c>. A table row for any of them would
    ///     read <c>null</c> and be struck as unsupported, so the inventory exit criterion 3 is checked
    ///     against gets its row here instead, one per root, resolved against real elements.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This asserts the cascade, and the file's own opening paragraph says that is not
    ///         enough — so here is why it is enough for exactly these five.</b> The reader question is
    ///         already settled for every property they emit, by another family in the
    ///         <see cref="Supported" /> table above: <c>margin-inline-end</c> by <c>me-*</c>,
    ///         <c>margin-bottom</c> by <c>mb-*</c>, the border widths by <c>border-e-*</c> and
    ///         <c>border-b-*</c>, the colour by <c>border-*</c>. What is unproved for a scoped family
    ///         is never the reader — it is the <i>scope</i>, and the scope is a selector, which is a
    ///         thing the cascade can be asked about directly. The consumption gate is blind here for
    ///         the same reason and from the other side: it cannot fail on a property some other
    ///         family already moved, so a scoped family whose selector never matched would be
    ///         silently perfect by its measure.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three children, not two, and the container asserted as well as the last child.</b>
    ///         Those are the two ways this goes wrong and they fail differently. A family written as
    ///         an ordinary property family puts the value on the container and nothing on any child;
    ///         a scope written <c>&amp; &gt; *</c> puts it on all three. With two children the middle
    ///         case does not exist and "every child but the last" is indistinguishable from "the
    ///         first child".
    ///     </para>
    ///     <para>
    ///         The editor's own tokens are what make this different from
    ///         <c>Vixen.Ui.Styling.Utilities.Tests.ChildScopedFamilyTests</c>, which asserts the same
    ///         mechanism against <c>UtilityFixture</c>'s. <c>divide-accent</c> in particular has no
    ///         meaning at all without a theme to resolve <c>var(--accent)</c> out of, and it is the
    ///         only one of the five whose value is a colour.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_five_child_scoped_roots_reach_every_child_but_the_last_and_never_their_own_element() {
        string[] utilities = ["space-x-4", "space-y-4", "divide-x-2", "divide-y-2", "divide-accent"];

        using var ui = Sheet([.. utilities, "bg-accent", "bg-border"]);

        var container = ui.Create("probe", ui.Document.Root, null, utilities);
        var first = ui.Create("probe", container);
        var middle = ui.Create("probe", container);
        var last = ui.Create("probe", container);

        // ⚠ Before the frame, with everything else. `ColorOf` reads a resolved style, so a reference
        // element created after `Frame` has none and reads `null` — which the first draft of this did,
        // and `NotEqual(null, null)` is how it announced it.
        var accentSource = ui.Create("probe", ui.Document.Root, null, "bg-accent");
        var hairlineSource = ui.Create("probe", ui.Document.Root, null, "bg-border");

        ui.Frame();

        // One row per root, which is what exit criterion 3 asks for. The value is the editor's
        // spacing step of four rather than a number written here.
        (string Root, string Property, string Expected)[] rows = [
            ("space-x-4", "margin-inline-end", "16px"),
            ("space-y-4", "margin-bottom", "16px"),
            ("divide-x-2", "border-inline-end-width", "2px"),
            ("divide-y-2", "border-bottom-width", "2px"),
        ];

        foreach (var (root, property, expected) in rows) {
            // ⚠ Compared as one tuple carrying the root's name, because four bare `Assert.Equal`s in
            // a loop report `"16px" != null` and leave the reader guessing which of the five failed.
            Assert.Equal(
                (root, expected, expected, null, null),
                (root, ui.StyleOf(first, property), ui.StyleOf(middle, property), ui.StyleOf(container, property),
                    ui.StyleOf(last, property))
            );
        }

        // The fifth root. A colour, so it is read as one.
        var painted = ui.ColorOf(first, "border-bottom-color");

        Assert.NotNull(painted);
        Assert.Null(ui.ColorOf(last, "border-bottom-color"));
        Assert.Null(ui.ColorOf(container, "border-bottom-color"));

        // ⚠ <b>And it is *the accent*, held against the token itself rather than against a property
        // of the colour.</b> The obvious oracle here is "blue-dominant", which is what
        // `A_per_edge_border_colour_paints_only_the_edge_it_names` used to use — and it does not
        // discriminate: this assertion was written that way first and stayed green when
        // `divide-accent` was swapped for `divide-border`, because the editor's greys are *cool*
        // greys. `--border` is `#a9adb4` light and `#1e1e20` dark, and B exceeds R in both, as it
        // does for `--surface` and `--text`. So the accent is resolved a second way, through a family
        // whose own row is in `Supported`, and the divider is required to equal that and to differ
        // from the hairline it would most plausibly have fallen back to. That test was corrected in
        // the same change and now holds its band against the cascaded value.
        var accent = ui.ColorOf(accentSource, "background-color");
        var hairline = ui.ColorOf(hairlineSource, "background-color");

        Assert.NotNull(accent);
        Assert.NotEqual(accent, hairline);
        Assert.Equal(accent, painted);
    }

    /// <summary>
    ///     ⚠ <b>And the divider is painted, which is the only claim worth making about a border.</b>
    ///     The cascade half above says the selector matched; this says the frame did something with
    ///     it, in the file whose job is <i>what</i> happened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two bands and not three is the whole assertion.</b> A count alone would pass on a
    ///         scope of <c>&amp; &gt; *</c> if the last child were left out of the frame for some
    ///         other reason, so each band is also placed against the child it belongs to — the bottom
    ///         edge of its border box, two pixels tall, the child's full width, at the child's own
    ///         left.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The children lay out in a <i>row</i>, which is what makes the last child's
    ///         absence checkable at all.</b> The first draft of this test asserted that no band sat
    ///         at or below <c>last.AbsoluteTop</c>, and that was green for the wrong reason and then
    ///         red for the wrong reason: every child shares a top edge here, so the vertical
    ///         coordinate says nothing about which child a band belongs to. The horizontal one does.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_divider_paints_a_band_beside_every_child_but_the_last() {
        using var ui = Sheet("divide-y-2", "divide-accent", "w-8", "h-8");

        var container = ui.Create("probe", ui.Document.Root, null, "divide-y-2", "divide-accent");
        var first = ui.Create("probe", container, null, "w-8", "h-8");
        var middle = ui.Create("probe", container, null, "w-8", "h-8");
        var last = ui.Create("probe", container, null, "w-8", "h-8");

        ui.Frame();

        var bands = ui.Document.Drawing.Commands
            .Where(command => command.Kind == DrawCommandKind.Rectangle)
            .ToList();

        Assert.Equal(2, bands.Count);

        foreach (var (child, band) in new[] { first, middle }.Zip(bands)) {
            Assert.Equal(child.AbsoluteLeft, band.X);
            Assert.Equal(child.AbsoluteTop + child.Height - band.Height, band.Y);
            Assert.Equal(2f, band.Height);
            Assert.Equal(child.Width, band.Width);
        }

        // Nothing beside the last child. Redundant against the count above by arithmetic, and kept
        // because it is the claim the family is *about* and the one a reader comes here to find.
        Assert.DoesNotContain(bands, band => band.X == last.AbsoluteLeft);
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
