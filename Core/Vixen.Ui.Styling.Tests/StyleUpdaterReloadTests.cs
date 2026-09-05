// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     That a <see cref="StyleUpdater" /> which outlives a <see cref="StyleEngine.Replace" /> is
///     asking the new stylesheet what a change reaches, in both directions the rule count can move.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The updater's <c>Refresh</c> is here already; what was missing is anything that
///         could tell you so.</b> The invalidator is built over <see cref="StyleEngine.Selectors" />
///         and holds a cursor into <see cref="StyleEngine.Rules" />, and a reload replaces both. The
///         two failures that leaves are not one failure: a reload with <i>more</i> rules walks the
///         new selectors' compound indices against the previous table and reads off the end, and a
///         reload with <i>fewer</i> leaves the cursor past the end of the new set, so the loop that
///         reads rules does not run at all and the map still describes the sheet that is gone.
///     </para>
///     <para>
///         ⚠ <b>Only one of the two throws, which is why both are written.</b> A test in the growing
///         direction alone would be satisfied by any change that merely stopped the exception —
///         re-anchoring the cursor without rebuilding the map, say — and the shrinking case is
///         silent: the wrong subtree is skipped, the pass reports a plausible small count, and the
///         element that should have restyled simply keeps the style it had. That is the half a
///         crash-shaped test cannot see.
///     </para>
///     <para>
///         <b>What these print on the day the styling engine does not run.</b> Nothing here takes a
///         device, a frame or a clock, so there is no arrangement in which they skip. Both assert a
///         resolved <i>value</i> rather than a pass count, because a count is satisfied by an
///         invalidator that gave up and restyled everything.
///     </para>
/// </remarks>
public class StyleUpdaterReloadTests {
    /// <summary>Six rules, none of which lets <c>.selected</c> reach anything below it.</summary>
    const string Before = """
        .cell { background: plain }
        .row { padding-left: 1px }
        .gutter { padding-left: 2px }
        .header { padding-left: 3px }
        .footer { padding-left: 4px }
        .grid { padding-left: 5px }
        """;

    /// <summary>Two rules, and the second is the one the change has to be able to follow.</summary>
    const string FewerAfter = """
        .cell { background: plain }
        .selected .cell { background: highlighted }
        """;

    [Fact]
    public void A_reload_that_adds_rules_does_not_read_the_new_selectors_against_the_old_table() {
        var fixture = new CascadeFixture();

        // One rule, so every compound index the replacement introduces is past the end of the table
        // this updater's invalidator was built over.
        var sheet = fixture.Engine.Load(".cell { background: plain }");

        var (row, cell) = Grid(fixture);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Engine.Replace(sheet, Before + '\n' + FewerAfter);
        fixture.Tree.AddClass(row, "selected");

        // ⚠ The throw is the loud half and is not what is asserted. `Compound` indexes a `List<>`, so
        // reading off the end raises before any assertion can run — an exception here fails the test
        // on its own, and pinning the value keeps the test honest if the read ever becomes tolerant.
        updater.ClassChanged(row, "selected");

        Assert.Equal("highlighted", fixture.Read(updater.StyleOf(cell), "background"));
    }

    [Fact]
    public void A_reload_that_drops_rules_still_reads_the_ones_the_new_sheet_has() {
        var fixture = new CascadeFixture();

        // Six rules in, two out. A cursor left at six is past the end of a two-rule set, so the loop
        // that reads rules is a no-op and the map still describes `Before` — where `.selected`
        // appears in no selector at all and therefore reaches nothing.
        var sheet = fixture.Engine.Load(Before);

        var (row, cell) = Grid(fixture);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Equal("plain", fixture.Read(updater.StyleOf(cell), "background"));

        fixture.Engine.Replace(sheet, FewerAfter);
        fixture.Tree.AddClass(row, "selected");

        updater.ClassChanged(row, "selected");

        Assert.Equal("highlighted", fixture.Read(updater.StyleOf(cell), "background"));
    }

    /// <summary>A row with one cell under it, which is the smallest tree a descendant rule needs.</summary>
    static (StyleNodeId Row, StyleNodeId Cell) Grid(CascadeFixture fixture) {
        var grid = fixture.Tree.CreateElement("div", classNames: ["grid"]);
        var row = fixture.Tree.CreateElement("div", grid, classNames: ["row"]);
        var cell = fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        return (row, cell);
    }
}
