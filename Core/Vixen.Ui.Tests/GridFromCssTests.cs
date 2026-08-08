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
        const string Sheet = """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; }
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

    [Fact]
    public void A_longhand_beats_the_shorthand_it_overlaps() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 100px); }
            .placed { grid-column: 1 / 2; grid-column-end: 4; height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "placed")
        );

        var placed = document.Root.ChildList[0].ChildList[0];

        Assert.Equal(0f, placed.AbsoluteLeft, Tolerance);
        Assert.Equal(300f, placed.Width, Tolerance);
    }

    [Fact]
    public void A_named_line_is_refused_rather_than_auto_placed_in_silence() {
        using var document = Laid(
            """
            root { width: 400px; height: 300px; }
            .grid { display: grid; width: 300px; height: 60px; grid-template-columns: repeat(3, 100px); }
            .placed { grid-column: sidebar; height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "grid").Add("div", classNames: "placed")
        );

        var refusal = Assert.Single(document.Builder.Diagnostics);
        Assert.StartsWith("grid-column:", refusal.Text, StringComparison.Ordinal);
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
}
