// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A stylesheet in, a laid-out grid out.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These exist because the corpus cannot see them.</b> Taffy's 1 526 passing grid
///         fixtures reach the store through <c>TaffyStyleMap</c>, which builds a tree by calling
///         setters directly — not one of them parses a stylesheet, so every one would still pass with
///         the CSS side of grid deleted entirely. What is checked here is the half the corpus is
///         blind to: that a declaration written in CSS arrives at §12 as the tracks it names.
///     </para>
///     <para>
///         ⚠ <b>They assert geometry, not computed values.</b> That a property resolved to the string
///         the author wrote is the thing that was already true while <c>grid-cols-3</c> did nothing
///         for months — the cascade stored it perfectly and nothing read it. So every test below
///         measures a box.
///     </para>
/// </remarks>
public class GridFromCssTests {
    const float Tolerance = 0.001f;

    static UiDocument Laid(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build(document);
        document.Update();

        return document;
    }

    // ── The grammar reaches the algorithm ───────────────────────────────────────────────────────

    [Fact]
    public void A_repeat_of_three_flexible_tracks_divides_the_container_in_three() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 1fr); }
            .cell { height: 10px; }
            """,
            document => {
                var host = document.Root.Add("div", classNames: "grid");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
            }
        );

        var cells = document.Root.ChildList[0].ChildList;

        Assert.Equal(0f, cells[0].AbsoluteLeft, Tolerance);
        Assert.Equal(100f, cells[1].AbsoluteLeft, Tolerance);
        Assert.Equal(200f, cells[2].AbsoluteLeft, Tolerance);
        Assert.Equal(100f, cells[0].Width, Tolerance);
    }

    [Fact]
    public void An_explicit_track_list_gives_each_track_the_width_it_names() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: 40px 120px auto; }
            .cell { height: 10px; }
            """,
            document => {
                var host = document.Root.Add("div", classNames: "grid");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
            }
        );

        var cells = document.Root.ChildList[0].ChildList;

        Assert.Equal(40f, cells[0].Width, Tolerance);
        Assert.Equal(120f, cells[1].Width, Tolerance);
        Assert.Equal(40f, cells[1].AbsoluteLeft, Tolerance);
        Assert.Equal(160f, cells[2].AbsoluteLeft, Tolerance);
    }

    [Fact]
    public void Rows_are_read_as_well_as_columns() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid {
                display: grid;
                width: 100px;
                height: 200px;
                grid-template-columns: 100px;
                grid-template-rows: 50px 150px;
            }
            .cell { }
            """,
            document => {
                var host = document.Root.Add("div", classNames: "grid");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
            }
        );

        var cells = document.Root.ChildList[0].ChildList;

        Assert.Equal(0f, cells[0].AbsoluteTop, Tolerance);
        Assert.Equal(50f, cells[1].AbsoluteTop, Tolerance);
        Assert.Equal(150f, cells[1].Height, Tolerance);
    }

    // ── The sabotage tests ──────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>repeat(3, 1fr)</c> and <c>repeat(3, minmax(0, 1fr))</c> must not collapse to one thing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the test that catches a grammar which parses <c>minmax()</c> by throwing its
    ///     arguments away.</b> The two declarations are identical in every grid whose content fits,
    ///     which is most of them — so a parser that reads <c>minmax(0, 1fr)</c> as a bare <c>1fr</c>
    ///     looks completely correct until something is too wide. §7.2.3 is what separates them: a
    ///     bare <c>1fr</c> is <c>minmax(auto, 1fr)</c>, whose <c>auto</c> floor is the track's
    ///     min-content size, while an explicit <c>0</c> floor lets the track be crushed. So a 200px
    ///     child in a 300px three-column grid holds the first column open at 200 under <c>1fr</c> and
    ///     is overflowed at 100 under <c>minmax(0, 1fr)</c>.
    /// </remarks>
    [Fact]
    public void A_bare_fr_and_a_minmax_zero_fr_are_not_the_same_track() {
        // ⚠ `flex-shrink: 0` on the two grids because they are 300 apiece in a 400-wide flex row,
        // and CSS's initial shrink squeezes both to 200 — correctly, and it would turn every number
        // below into a measurement of the outer row rather than of the track list.
        const string Sheet = """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; flex-shrink: 0; }
            .bare { grid-template-columns: repeat(3, 1fr); }
            .clamped { grid-template-columns: repeat(3, minmax(0, 1fr)); }
            .wide { width: 200px; height: 10px; }
            .cell { height: 10px; }
            """;

        using var document = Laid(
            Sheet,
            document => {
                foreach (var variant in (string[]) ["bare", "clamped"]) {
                    var host = document.Root.Add("div", classNames: ["grid", variant]);
                    host.Add("div", classNames: "wide");
                    host.Add("div", classNames: "cell");
                    host.Add("div", classNames: "cell");
                }
            }
        );

        var bare = document.Root.ChildList[0].ChildList;
        var clamped = document.Root.ChildList[1].ChildList;

        // The automatic floor holds the first column open at its content's width, so the second
        // column starts at 200 rather than at a third of the container.
        Assert.Equal(200f, bare[1].AbsoluteLeft - bare[0].AbsoluteLeft, Tolerance);

        // An explicit zero floor lets it be crushed to its flexible share, and the child overflows.
        Assert.Equal(100f, clamped[1].AbsoluteLeft - clamped[0].AbsoluteLeft, Tolerance);

        Assert.NotEqual(bare[1].AbsoluteLeft, clamped[1].AbsoluteLeft);
    }

    /// <summary>A malformed track list is refused out loud and changes nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves matter and the second is the one that is easy to get wrong.</b> A parser
    ///     that gives up partway through <c>repeat(3, 1fr) 4furlongs</c> and applies the three tracks
    ///     it managed produces a grid that looks deliberate; a parser that gives up and applies
    ///     nothing produces a one-column grid, which also looks deliberate. Neither is acceptable on
    ///     its own — what makes this survivable is that the refusal is *named*, so the declaration
    ///     can be found. CSS's own rule decides the layout half: an invalid declaration is dropped
    ///     whole, so the element lays out exactly as if the line had never been written.
    /// </remarks>
    [Theory]
    [InlineData("4furlongs")]
    [InlineData("repeat(3, 1fr) 4furlongs")]
    [InlineData("minmax(0)")]
    [InlineData("repeat(3")]
    [InlineData("repeat(notanumber, 1fr)")]
    [InlineData("fit-content(auto)")]
    [InlineData("subgrid")]
    [InlineData("[full-start] 1fr [full-end]")]
    public void A_malformed_track_list_is_refused_by_name_and_not_half_applied(string tracks) {
        using var document = Laid(
            $$"""
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: {{tracks}}; }
            .cell { height: 10px; }
            """,
            document => {
                var host = document.Root.Add("div", classNames: "grid");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
            }
        );

        // Loud: the refusal names the property and the value a human wrote.
        var refusal = Assert.Single(document.Builder.Diagnostics);
        Assert.StartsWith("grid-template-columns:", refusal.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Reason));

        // Dropped whole: with no template at all, auto-placement puts both children in one column,
        // so the second is *below* the first rather than beside it. A half-applied list would have
        // put them side by side and said nothing.
        var cells = document.Root.ChildList[0].ChildList;

        Assert.Equal(cells[0].AbsoluteLeft, cells[1].AbsoluteLeft, Tolerance);
        Assert.True(cells[1].AbsoluteTop > cells[0].AbsoluteTop, "a surviving partial track list would have made a row");
    }

    [Fact]
    public void A_well_formed_track_list_is_refused_by_nothing() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid {
                display: grid;
                width: 300px;
                grid-template-columns: repeat(2, minmax(10px, 1fr)) fit-content(40px) 10%;
                grid-template-rows: repeat(auto-fill, 20px);
                grid-auto-rows: 30px;
                grid-auto-columns: 15px;
            }
            """,
            document => document.Root.Add("div", classNames: "grid")
        );

        Assert.Empty(document.Builder.Diagnostics);
    }

    // ── Placement ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_column_span_covers_the_tracks_it_names() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 100px); }
            .wide { grid-column: span 2; height: 10px; }
            .cell { height: 10px; }
            """,
            document => {
                var host = document.Root.Add("div", classNames: "grid");
                host.Add("div", classNames: "wide");
                host.Add("div", classNames: "cell");
            }
        );

        var cells = document.Root.ChildList[0].ChildList;

        Assert.Equal(200f, cells[0].Width, Tolerance);
        Assert.Equal(200f, cells[1].AbsoluteLeft, Tolerance);
    }

    [Fact]
    public void A_slash_shorthand_names_both_edges() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 100px); }
            .placed { grid-column: 2 / 4; height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "placed")
        );

        var placed = document.Root.ChildList[0].ChildList[0];

        Assert.Equal(100f, placed.AbsoluteLeft, Tolerance);
        Assert.Equal(200f, placed.Width, Tolerance);
    }

    /// <summary>Whichever of the shorthand and the longhand was written last wins — both ways round.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both orders, or this guard does not exist.</b> The bug it was written for was
    ///         <c>ApplyPlacements</c> applying the shorthand and then overwriting each half from the
    ///         longhands, in an order fixed in code — so the longhand-last case passed while the
    ///         engine was at its most broken, and a test that checked only that direction would have
    ///         been green throughout. The shorthand-last row is the whole assertion; the other row is
    ///         there so that a future fix cannot buy it by inverting the hard-coded order.
    ///     </para>
    ///     <para>
    ///         The real shape is a utility class losing to a theme rule: <c>row-span-full</c> emits
    ///         <c>grid-row: 1 / -1</c>, and on any element whose sheet also set <c>grid-row-start</c>
    ///         it was discarded in silence — no diagnostic, and the item auto-placed into a real cell,
    ///         so the grid looked built rather than broken. What fixed it is
    ///         <c>ShorthandExpansion</c> splitting the shorthand at load, which is why the assertion
    ///         is here and the cascade does the ordering.
    ///     </para>
    /// </remarks>
    /// <param name="declarations">The two declarations, in the order they are written.</param>
    /// <param name="left">Where the item should start.</param>
    /// <param name="width">How wide it should be.</param>
    [Theory]
    [InlineData("grid-column: 1 / 2; grid-column-end: 4;", 0f, 300f)]
    [InlineData("grid-column-end: 4; grid-column: 1 / 2;", 0f, 100f)]
    [InlineData("grid-column: 2 / 4; grid-column-start: 1;", 0f, 300f)]
    [InlineData("grid-column-start: 1; grid-column: 2 / 4;", 100f, 200f)]
    public void The_placement_written_last_wins_whichever_kind_it_is(string declarations, float left, float width) {
        using var document = Laid(
            $$"""
              root { width: 400px; height: 300px; }
              .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 100px); }
              .placed { {{declarations}} height: 10px; }
              """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "placed")
        );

        var placed = document.Root.ChildList[0].ChildList[0];

        Assert.Empty(document.Builder.Diagnostics);
        Assert.Equal(left, placed.AbsoluteLeft, Tolerance);
        Assert.Equal(width, placed.Width, Tolerance);
    }

    /// <summary>The same, across two rules, which is where it actually bit.</summary>
    /// <remarks>
    ///     ⚠ Two rules of equal specificity rather than one declaration list, because that is the
    ///     arrangement the failure has in a real document: a theme sheet names <c>grid-row-start</c>
    ///     and a utility class emitted later names <c>grid-row</c>. Equal specificity so that document
    ///     order is the only thing deciding, which is the property being asserted.
    /// </remarks>
    [Theory]
    [InlineData(".placed { grid-row-start: 2; } .placed { grid-row: 1 / -1; }", 0f, 60f)]
    [InlineData(".placed { grid-row: 1 / -1; } .placed { grid-row-start: 2; }", 20f, 40f)]
    public void A_utility_shorthand_beats_a_theme_longhand_declared_before_it(string rules, float top, float height) {
        using var document = Laid(
            $$"""
              root { width: 400px; height: 300px; }
              .grid { display: grid; width: 300px; height: 60px;
                      grid-template-columns: 100px; grid-template-rows: repeat(3, 20px); }
              {{rules}}
              """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "placed")
        );

        var placed = document.Root.ChildList[0].ChildList[0];

        Assert.Empty(document.Builder.Diagnostics);
        Assert.Equal(top, placed.AbsoluteTop, Tolerance);
        Assert.Equal(height, placed.Height, Tolerance);
    }

    /// <summary>
    ///     A placement value that is neither a line nor an area's name is refused rather than
    ///     auto-placed in silence.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The diagnostic names <c>grid-column-start</c> and not <c>grid-column</c>, which is
    ///         a real cost of expanding at load and is worth stating rather than leaving to be
    ///         discovered.</b> <c>ShorthandExpansion</c> divides the shorthand before anything can
    ///         judge it, so what reaches the bridge is the half that failed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This test used to write <c>grid-column: sidebar</c>, and that value is no longer
    ///         a refusal.</b> A bare identifier is an area's name now, and a name that matches no area
    ///         is auto-placed — legal CSS with a defined meaning, so refusing it would be reporting a
    ///         correct declaration. What is left loud is a value that is neither, and a single
    ///         diagnostic is asserted because the four placement longhands are now read twice, once
    ///         as a line and once as a name; the second reader is the only one that speaks.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_placement_that_is_neither_a_line_nor_a_name_is_refused() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 100px); }
            .placed { grid-column: 4px; height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "placed")
        );

        var refusal = Assert.Single(document.Builder.Diagnostics);
        Assert.StartsWith("grid-column-start: 4px", refusal.Text, StringComparison.Ordinal);
    }

    /// <summary>A shorthand the expander will not divide is still read, by the bridge, as before.</summary>
    /// <remarks>
    ///     ⚠ <b>The residue, asserted so that it stays this small.</b> A <c>var()</c> with no slash
    ///     beside it may be holding the slash, so <c>ShorthandExpansion</c> refuses it rather than
    ///     calling the whole value a start edge and turning a working declaration into a refused one.
    ///     The bridge's own shorthand branches then read the substituted value exactly as they did
    ///     before anything was expanded. This is the only shape in which those branches still fire,
    ///     and so the only shape in which precedence against a longhand is decided in code rather
    ///     than by the cascade.
    /// </remarks>
    [Fact]
    public void A_var_holding_a_whole_placement_is_still_read_by_the_bridge() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 100px); }
            .placed { --place: 2 / 4; grid-column: var(--place); height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "placed")
        );

        var placed = document.Root.ChildList[0].ChildList[0];

        Assert.Equal(100f, placed.AbsoluteLeft, Tolerance);
        Assert.Equal(200f, placed.Width, Tolerance);
    }

    // ── Auto flow ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Auto_flow_column_fills_down_the_other_axis() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid {
                display: grid;
                width: 300px;
                height: 60px;
                grid-auto-flow: column;
                grid-template-rows: 30px 30px;
            }
            .cell { }
            """,
            document => {
                var host = document.Root.Add("div", classNames: "grid");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
            }
        );

        var cells = document.Root.ChildList[0].ChildList;

        // Column flow fills the two rows first, then starts a second column.
        Assert.Equal(cells[0].AbsoluteLeft, cells[1].AbsoluteLeft, Tolerance);
        Assert.True(cells[2].AbsoluteLeft > cells[0].AbsoluteLeft, "the third item should start a new column");
    }

    // ── The reset half ──────────────────────────────────────────────────────────────────────────

    /// <summary>A template that stops being declared stops applying.</summary>
    /// <remarks>
    ///     ⚠ <b>The one failure in this file that no amount of parsing gets right.</b> A track list
    ///     lives in the tree's arena and is written only by its own setter, so an element whose
    ///     <c>grid-template-columns</c> disappears from the cascade keeps the tracks it had — for
    ///     the rest of its life, because nothing else will ever clear them. Absent has to mean a
    ///     write, and this is what says so.
    /// </remarks>
    [Fact]
    public void A_template_that_stops_being_declared_is_cleared_rather_than_remembered() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; }
            .three { grid-template-columns: repeat(3, 100px); }
            .cell { height: 10px; }
            """,
            document => {
                var host = document.Root.Add("div", id: "host", classNames: ["grid", "three"]);
                host.Add("div", classNames: "cell");
                host.Add("div", classNames: "cell");
            }
        );

        var host = document.Root.ChildList[0];
        var cells = host.ChildList;

        Assert.Equal(100f, cells[1].AbsoluteLeft, Tolerance);

        host.RemoveClass("three");
        document.Update();

        // Back to a single automatic column, so the second child stacks under the first.
        Assert.Equal(cells[0].AbsoluteLeft, cells[1].AbsoluteLeft, Tolerance);
        Assert.True(cells[1].AbsoluteTop > cells[0].AbsoluteTop, "the tracks outlived the declaration");
    }

    // ── Named areas ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>grid-template-areas</c> and <c>grid-area</c>, end to end: the CSS half of a feature the
    ///     layout corpus can see no part of.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every step between the stylesheet and §8 is new here, and each is invisible on its
    ///         own.</b> The declaration reaches a <c>VariableLengthProperty</c> rather than
    ///         <c>LayoutStyleBuilder.Build</c>, because a template belongs to a node and a
    ///         <c>LayoutStyle</c> never sees one; <c>grid-area: head</c> is expanded into four
    ///         longhands, three of which §8.4 fills in by duplication; and each longhand is read once
    ///         as a line and once as a name. Take away the duplication alone and <c>head</c> is one
    ///         cell rather than two, which lays out and reads as a track-sizing bug.
    ///     </para>
    ///     <para>
    ///         The template is <c>"head head" "nav main"</c> over two 100-point columns and two
    ///         50-point rows, so the numbers are the three rectangles and nothing else.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_item_lands_in_the_area_its_grid_area_names() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid {
                display: grid;
                width: 200px;
                grid-template-columns: 100px 100px;
                grid-template-rows: 50px 50px;
                grid-template-areas: "head head" "nav main";
            }
            .head { grid-area: head; }
            .nav { grid-area: nav; }
            .main { grid-area: main; }
            """,
            document => {
                var host = document.Root.Add("div", classNames: "grid");
                host.Add("div", classNames: "head");
                host.Add("div", classNames: "nav");
                host.Add("div", classNames: "main");
            }
        );

        var cells = document.Root.ChildList[0].ChildList;

        Assert.Empty(document.Builder.Diagnostics);

        Assert.Equal(0f, cells[0].AbsoluteLeft, Tolerance);
        Assert.Equal(200f, cells[0].Width, Tolerance);
        Assert.Equal(50f, cells[0].Height, Tolerance);

        Assert.Equal(0f, cells[1].AbsoluteLeft, Tolerance);
        Assert.Equal(50f, cells[1].AbsoluteTop, Tolerance);
        Assert.Equal(100f, cells[1].Width, Tolerance);

        Assert.Equal(100f, cells[2].AbsoluteLeft, Tolerance);
        Assert.Equal(50f, cells[2].AbsoluteTop, Tolerance);
    }

    /// <summary>The rows a template names are explicit rows even where no track list sizes them.</summary>
    /// <remarks>
    ///     ⚠ <b>§7.1's "larger of", measured through CSS.</b> This grid states
    ///     <c>grid-template-columns</c> and no rows at all, so both rows come from the template and
    ///     are sized by <c>grid-auto-rows</c>. A bridge that stored the template and a store that
    ///     ignored it would put the item in row one.
    /// </remarks>
    [Fact]
    public void The_rows_a_template_names_exist_without_a_grid_template_rows() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid {
                display: grid;
                width: 200px;
                grid-template-columns: 100px 100px;
                grid-auto-rows: 30px;
                grid-template-areas: "a b" "c c";
            }
            .c { grid-area: c; }
            """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "c")
        );

        var placed = document.Root.ChildList[0].ChildList[0];

        Assert.Equal(0f, placed.AbsoluteLeft, Tolerance);
        Assert.Equal(30f, placed.AbsoluteTop, Tolerance);
        Assert.Equal(200f, placed.Width, Tolerance);
    }

    /// <summary>An area template that stops being declared stops applying.</summary>
    /// <remarks>
    ///     ⚠ <b>The same failure the track lists have, one property over, and reachable only through
    ///     the reset half of the registry.</b> An element whose <c>grid-template-areas</c> disappears
    ///     from the cascade would otherwise keep its areas for the rest of its life, and every item
    ///     naming one would keep landing in a template the stylesheet no longer holds.
    /// </remarks>
    [Fact]
    public void An_area_template_that_stops_being_declared_is_cleared_rather_than_remembered() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 200px; grid-template-columns: 100px 100px; grid-auto-rows: 30px; }
            .named { grid-template-areas: "a b" "c c"; }
            .c { grid-area: c; }
            """,
            document => document.Root.Add("div", id: "host", classNames: ["grid", "named"]).Add("div", classNames: "c")
        );

        var host = document.Root.ChildList[0];
        var placed = host.ChildList[0];

        Assert.Equal(30f, placed.AbsoluteTop, Tolerance);

        host.RemoveClass("named");
        document.Update();

        // With no areas the name matches nothing, so the item is auto-placed into the first cell —
        // which is a different place, and the only reason this assertion can fail.
        Assert.Equal(0f, placed.AbsoluteTop, Tolerance);
        Assert.Equal(100f, placed.Width, Tolerance);
    }

    /// <summary>An invalid template is dropped whole and reported.</summary>
    /// <remarks>
    ///     ⚠ <b>A non-rectangular area is the case worth carrying as far as the bridge</b>, because it
    ///     is the one that lays out if it is accepted. <c>"a b" "b a"</c> gives each name a bounding
    ///     box holding the other, and an implementation that took the box and asked no further
    ///     question draws two items over each other and says nothing.
    /// </remarks>
    [Fact]
    public void A_template_whose_areas_are_not_rectangles_is_refused() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid {
                display: grid;
                width: 200px;
                grid-template-columns: 100px 100px;
                grid-auto-rows: 30px;
                grid-template-areas: "a b" "b a";
            }
            .a { grid-area: a; }
            """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "a")
        );

        var refusal = Assert.Single(document.Builder.Diagnostics);
        Assert.StartsWith("grid-template-areas:", refusal.Text, StringComparison.Ordinal);

        // Dropped whole: no areas, so the item is auto-placed at the origin.
        var placed = document.Root.ChildList[0].ChildList[0];
        Assert.Equal(0f, placed.AbsoluteLeft, Tolerance);
        Assert.Equal(0f, placed.AbsoluteTop, Tolerance);
    }
}
