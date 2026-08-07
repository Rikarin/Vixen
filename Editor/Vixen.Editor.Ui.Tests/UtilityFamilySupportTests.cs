// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
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
/// </remarks>
public class UtilityFamilySupportTests {
    /// <summary>Utility, property, and the value the cascade must compute for it.</summary>
    /// <remarks>
    ///     One row per family the engine reads, against the editor's own tokens — so
    ///     <c>bg-surface</c> here is the same <c>var(--surface)</c> the hand-written sheet uses, and
    ///     the spacing rows are in steps of two because that is the editor's rhythm.
    /// </remarks>
    public static TheoryData<string, string, string> Supported => new() {
        // Layout. ⚠ Only `flex` and `none` are in LayoutStyleBuilder's `Displays` table; the other
        // five display utilities are in `Inert` below.
        { "flex", "display", "flex" },
        { "hidden", "display", "none" },
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
        { "gap-3", "row-gap", "6px" },
        { "gap-x-2", "column-gap", "4px" },
        { "gap-y-2", "row-gap", "4px" },
        { "p-3", "padding-top", "6px" },
        { "px-2", "padding-left", "4px" },
        { "pt-1", "padding-top", "2px" },
        { "ps-2", "padding-inline-start", "4px" },
        { "m-2", "margin-top", "4px" },
        { "me-1", "margin-inline-end", "2px" },

        // Sizing.
        { "w-full", "width", "100%" },
        { "h-4", "height", "8px" },
        { "min-w-0", "min-width", "0" },
        { "max-w-40", "max-width", "80px" },

        // Position.
        { "absolute", "position", "absolute" },
        { "relative", "position", "relative" },
        { "top-0", "top", "0" },
        { "inset-x-1", "left", "2px" },
        { "start-2", "inset-inline-start", "4px" },
        { "z-10", "z-index", "10" },
        { "box-border", "box-sizing", "border-box" },

        // Typography. `text-` is alignment, then size, then colour, resolved in that order.
        { "text-center", "text-align", "center" },
        { "text-sm", "font-size", "11px" },
        { "text-text-muted", "color", "#5c616b" },
        { "font-semibold", "font-weight", "600" },
        { "leading-8", "line-height", "16px" },
        { "leading-tight", "line-height", "1.25" },
        { "tracking-px", "letter-spacing", "1px" },
        { "whitespace-nowrap", "white-space", "nowrap" },

        // Paint.
        { "bg-surface-raised", "background-color", "#f2f3f6" },
        { "opacity-50", "opacity", "0.5" },
        { "shadow-elevation", "box-shadow", "0px 10px 26px rgba(12, 14, 18, 0.22)" },

        // Borders. ⚠ The widths are read per edge; the colours are not — see `Inert`.
        { "border-2", "border-top-width", "2px" },
        { "border-b", "border-bottom-width", "1px" },
        { "border-border-active", "border-top-color", "#5f8ddb" },

        // Overflow, all three properties and all four keywords. ⚠ `auto` is here because the layout
        // maps it onto `Overflow.Scroll` — the two differ only by a scrollbar gutter nothing draws.
        { "truncate", "overflow", "hidden" },
        { "overflow-scroll", "overflow", "scroll" },
        { "overflow-auto", "overflow", "auto" },
        { "overflow-x-scroll", "overflow-x", "scroll" },
        { "overflow-y-auto", "overflow-y", "auto" },

        // Interactivity and motion.
        { "cursor-pointer", "cursor", "pointer" },
        { "pointer-events-none", "pointer-events", "none" },
        { "transition", "transition-property", "all" },
        { "duration-150", "transition-duration", "150ms" },
        { "ease-out", "transition-timing-function", "ease-out" },
        { "aspect-video", "aspect-ratio", "16/9" }
    };

    /// <summary>Utility, property — the families that compute a value nothing in the engine reads.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A bug list with a deadline, which is not the same as no bug list.</b> A rule that
    ///         resolves to a property no consumer looks at is a utility waiting for an engine feature —
    ///         and <a href="../../../docs/plan/43-web-styling-parity.md">doc 43</a> is why that is a
    ///         task rather than a state of affairs: Tailwind's index is the specification, so every row
    ///         below owes a task number and expires when the property lands. What makes it worth
    ///         writing down is that nothing anywhere else says so — the class name is spelled
    ///         correctly, the generator emits it, the cascade computes it, and the picture does not
    ///         change.
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
    ///         <b>History: <c>order-2</c> used to be in this table, filed under <i>grid</i>.</b> It is
    ///         a flexbox property and always was — the misfiling is itself the tell, because a family
    ///         nothing reads gets grouped by whoever last guessed why. <c>LayoutStyle</c> now carries
    ///         an <c>Order</c>, and it is the one layout property that also moves the draw list, since
    ///         CSS Flexbox §5.4 makes <c>order</c> modify painting order as well as layout order.
    ///         <see cref="An_ordered_item_is_laid_out_and_painted_in_its_ordinal_group" /> is the same
    ///         test this file used to hold inverted, and it checks both halves: an implementation that
    ///         reordered the boxes and left the painting alone would pass a position-only assertion.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Yoga has no <c>order</c>, so none of the 534 conformance fixtures covers it.</b>
    ///         That is recorded in <c>Core/Vixen.Ui.Layout/README.md</c> rather than here, because it
    ///         is a fact about the oracle rather than about the utility surface.
    ///     </para>
    /// </remarks>
    public static TheoryData<string, string> Inert => new() {
        // Display: LayoutStyleBuilder maps `flex` and `none` and nothing else.
        { "block", "display" },
        { "inline", "display" },
        { "inline-block", "display" },
        { "inline-flex", "display" },
        { "grid", "display" },

        // Grid, which the layout is flexbox-only and has no reading of at all.
        { "grid-cols-3", "grid-template-columns" },
        { "col-span-2", "grid-column" },

        // Paint the renderer has no channel for.
        { "ring-accent", "outline-color" },
        { "fill-accent", "fill" },
        { "stroke-accent", "stroke" },
        { "blur-2", "--blur" },

        // Transforms: custom properties waiting for a transform stage.
        { "translate-x-2", "--translate-x" },
        { "translate-y-2", "--translate-y" },
        { "scale-2", "--scale" },
        { "rotate-45", "--rotate" },

        // Text and interaction properties with no consumer.
        { "align-middle", "vertical-align" },
        { "select-none", "user-select" }
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
    ///     ⚠ <b>Support proved rather than asserted, for a family that used to be inert.</b> This
    ///     test replaces the row <c>order-2</c> occupied in <see cref="Inert" />, and it is
    ///     deliberately two assertions rather than one.
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

    /// <summary>
    ///     ⚠ <b>The same, for <c>display</c>.</b> <c>block</c> is not in the layout's keyword table,
    ///     so an element carrying it is still laid out as the flex container everything in this
    ///     engine is — two children sit side by side rather than stacking. A test that only read the
    ///     computed <c>display</c> would find <c>block</c> sitting there and conclude the opposite.
    /// </summary>
    [Fact]
    public void Display_block_does_not_stop_an_element_being_a_flex_row() {
        using var ui = Sheet("block", "w-8", "h-8");

        var host = ui.Create("probe", ui.Document.Root, null, "block");
        var first = ui.Create("probe", host, null, "w-8", "h-8");
        var second = ui.Create("probe", host, null, "w-8", "h-8");

        ui.Frame();

        Assert.Equal("block", ui.StyleOf(host, "display"));
        Assert.Equal(first.AbsoluteTop, second.AbsoluteTop);
        Assert.True(second.AbsoluteLeft > first.AbsoluteLeft, "a block would have stacked them");
    }

    /// <summary>
    ///     ⚠ <b>A per-edge border width is read by the layout and ignored by the draw list, and that
    ///     is worse than either half being missing.</b> <c>LayoutStyleBuilder</c> interns all seven
    ///     border-width names and the flexbox honours each edge, so <c>border-l-2</c> really does
    ///     inset the content box by two pixels. <c>DrawListBuilder</c> then takes <i>one</i> thickness
    ///     — <c>GetComputedBorder(node, Edge.Top)</c> — so a left border alone paints nothing at all.
    ///     The geometry moves and the picture does not follow, which is the hardest kind of gap to
    ///     find: neither table in this file would hold it, because the property is neither unread nor
    ///     read.
    /// </summary>
    [Fact]
    public void A_left_border_insets_the_layout_and_paints_nothing() {
        using var ui = Sheet("border-l-2", "border-border", "w-8", "h-8");

        var host = ui.Create("probe", ui.Document.Root, null, "border-l-2", "border-border", "w-8", "h-8");
        var child = ui.Create("probe", host, null, "w-8", "h-8");

        ui.Frame();

        Assert.Equal("2px", ui.StyleOf(host, "border-left-width"));
        Assert.Equal(host.AbsoluteLeft + 2f, child.AbsoluteLeft);
        Assert.DoesNotContain(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.Border);
    }

    /// <summary>
    ///     ⚠ <b>And the other half of the same fact: a top border paints all four sides.</b> The draw
    ///     list emits one stroke around the whole element box, so the edge the class names decides the
    ///     thickness of every edge. Asserting the command's rectangle is the element's own box is what
    ///     distinguishes "strokes the border box" from "strokes the top edge" — a thickness assertion
    ///     alone would pass either way.
    /// </summary>
    [Fact]
    public void A_top_border_paints_the_whole_box() {
        using var ui = Sheet("border-t-2", "border-border", "w-8", "h-8");

        var host = ui.Create("probe", ui.Document.Root, null, "border-t-2", "border-border", "w-8", "h-8");

        ui.Frame();

        var stroke = Assert.Single(
            ui.Document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Border
        );

        Assert.Equal(2f, stroke.Thickness);
        Assert.Equal(host.AbsoluteLeft, stroke.X);
        Assert.Equal(host.AbsoluteTop, stroke.Y);
        Assert.Equal(host.Width, stroke.Width);
        Assert.Equal(host.Height, stroke.Height);
    }

    /// <summary>
    ///     ⚠ <b><c>truncate</c> does not truncate.</b> Tailwind's is three declarations —
    ///     <c>overflow: hidden</c>, <c>text-overflow: ellipsis</c>, <c>white-space: nowrap</c> — and
    ///     this emits the first. Nothing in <c>Vixen.Ui.Text</c> implements <c>text-overflow</c>, so
    ///     the name promises an ellipsis the engine cannot draw, and the wrapping the other two would
    ///     have suppressed still happens. Asserted as the two absences rather than as a picture,
    ///     because the picture is the thing that does not exist yet.
    /// </summary>
    [Fact]
    public void Truncate_emits_neither_text_overflow_nor_nowrap() {
        using var ui = Sheet("truncate");

        var element = ui.Create("probe", ui.Document.Root, null, "truncate");

        ui.Frame();

        Assert.Equal("hidden", ui.StyleOf(element, "overflow"));
        Assert.Null(ui.StyleOf(element, "text-overflow"));
        Assert.Null(ui.StyleOf(element, "white-space"));
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
        ThemeTokens.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "__fixtures__", "vixen.ui.yaml")));

    static UiTest Sheet(params string[] utilities) {
        var ui = UiTest.Create();

        // The token block only, so the `var(--…)` colours resolve without the hand-written rules
        // being present to win against — those have their own tests in `StylesheetTests`.
        ui.Document.Load(EditorTheme.Css, StyleOrigin.UserAgent);
        ui.Document.Load(new UtilityGenerator(Tokens()).Generate(utilities), StyleOrigin.UserAgent);

        return ui;
    }
}
