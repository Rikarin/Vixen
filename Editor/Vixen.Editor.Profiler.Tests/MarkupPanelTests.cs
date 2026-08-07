// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Profiler.Tests;

/// <summary>
///     The two panels doc 36 § F7's first wave moved into <c>.vxml</c>, asserted through the element
///     tree they built rather than through the model they were handed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here reads elements, and that is the whole point of the file.</b> The
///         hand-written panels wrote their rows inside <c>Show</c>, so "the model was assigned" and
///         "the screen followed" were the same statement and neither needed testing. A markup panel
///         renders through effects: <c>Show</c> writes a signal, the scheduler runs the <c>@for</c>
///         on the next flush, and the two statements come apart. A panel whose model is replaced with
///         a plain field assignment still passes every test that reads <c>view.Statistics</c> — it
///         draws the first count and never a second, silently, for as long as the editor is open.
///         That is the defect this port introduces and the only thing that finds it is counting
///         elements.
///     </para>
///     <para>
///         ⚠ <b>Two of these are sabotage-verified.</b> Replacing <c>statistics.Value = counted</c> in
///         <c>StatisticsView.vxml</c> with a plain field write fails
///         <see cref="A_second_count_replaces_the_rows_on_screen" /> and
///         <see cref="A_row_that_stops_being_over_budget_loses_its_colour" />; the same edit to
///         <c>MemoryView.vxml</c> fails <see cref="A_second_reading_replaces_the_lines_on_screen" />
///         and <see cref="An_arena_that_appears_brings_a_heading_with_it" />. Recorded because a
///         reactivity test that would pass without the reactivity is the usual way this goes wrong.
///     </para>
/// </remarks>
public sealed class MarkupPanelTests : IDisposable {
    readonly UiTest test = UiTest.Create();

    public MarkupPanelTests() {
        ControlTheme.Install(test.Document);
        AdvancedTheme.Install(test.Document);
        ProfilerTheme.Install(test.Document);
    }

    public void Dispose() => test.Dispose();

    // ============================================================ Statistics

    /// <summary>
    ///     The rows on screen are the count's, and a second count replaces them — which is the
    ///     assertion the whole port rests on.
    /// </summary>
    [Fact]
    public void A_second_count_replaces_the_rows_on_screen() {
        var view = Build<StatisticsView>();

        using World empty = new("Empty");
        view.Show(SceneStatistics.Collect(empty));
        test.Frames(2);

        var before = Rows(view);
        Assert.NotEmpty(before);

        // The default budget is in the string, because a count against its ceiling is the thing the
        // panel exists to show — "0" alone would be a number nobody can act on.
        Assert.Equal("0 / 100,000", ValueOf(view, "Entities"));

        using World populated = new("Populated");

        for (var index = 0; index < 7; index++) {
            populated.Create(new Position());
        }

        view.Show(SceneStatistics.Collect(populated));
        test.Frames(2);

        // The number on screen moved, which a panel that rendered once would not have done.
        Assert.Equal("7 / 100,000", ValueOf(view, "Entities"));
        Assert.Equal(before.Length, Rows(view).Length);
    }

    /// <summary>
    ///     And a count with more rows in it than the last grows the list, which is the
    ///     <c>@for</c> reconciling rather than an effect rewriting text in place.
    /// </summary>
    /// <remarks>
    ///     Hierarchy depth is the row that comes and goes: the model leaves it out entirely when
    ///     nobody says how deep the scene is, rather than showing a zero that reads as "flat".
    /// </remarks>
    [Fact]
    public void A_count_with_an_extra_row_grows_the_list() {
        var view = Build<StatisticsView>();

        using World world = new("World");
        world.Create(new Position());

        view.Show(SceneStatistics.Collect(world));
        test.Frames(2);

        var without = Rows(view).Length;
        Assert.Null(ValueOf(view, "Hierarchy depth"));

        view.Show(SceneStatistics.Collect(world, depth: 4));
        test.Frames(2);

        Assert.Equal(without + 1, Rows(view).Length);
        Assert.Equal("4 / 32", ValueOf(view, "Hierarchy depth"));
    }

    /// <summary>
    ///     A budget crossed puts a class on the row, and a budget that stops being crossed takes it
    ///     off again — the second half being the one a panel that only ever adds gets wrong.
    /// </summary>
    [Fact]
    public void A_row_that_stops_being_over_budget_loses_its_colour() {
        var view = Build<StatisticsView>();

        using World world = new("World");

        for (var index = 0; index < 10; index++) {
            world.Create(new Position());
        }

        view.Show(SceneStatistics.Collect(world, new() { Entities = 5 }));
        test.Frames(2);

        Assert.True(RowFor(view, "Entities").HasClass("over"));

        view.Show(SceneStatistics.Collect(world, new() { Entities = 100_000 }));
        test.Frames(2);

        var row = RowFor(view, "Entities");
        Assert.False(row.HasClass("over"));
        Assert.False(row.HasClass("near"));
    }

    /// <summary>
    ///     The warnings block is absent when there is nothing to say and present when there is —
    ///     an <c>@if</c> arm rather than a <c>display: none</c> the C# toggled.
    /// </summary>
    [Fact]
    public void The_warnings_block_comes_and_goes_with_the_warnings() {
        var view = Build<StatisticsView>();

        using World world = new("World");

        for (var index = 0; index < 10; index++) {
            world.Create(new Position());
        }

        view.Show(SceneStatistics.Collect(world, new() { Entities = 100_000 }));
        test.Frames(2);

        Assert.Empty(Tagged(view.Root, "statistics-warnings"));

        view.Show(SceneStatistics.Collect(world, new() { Entities = 5 }));
        test.Frames(2);

        Assert.Single(Tagged(view.Root, "statistics-warnings"));
        Assert.NotEmpty(Tagged(view.Root, "statistic-warning"));
    }

    /// <summary>
    ///     ⚠ <b>The bar is a real <see cref="ProgressBar" /> now, and only rows with a budget have
    ///     one.</b> The hand-written panel kept a track in every row and hid the ones with nothing to
    ///     show; the markup builds none, which is what makes "no budget" and "a budget of zero"
    ///     visibly different from "measured at nothing".
    /// </summary>
    [Fact]
    public void Only_a_row_with_a_budget_has_a_bar() {
        var view = Build<StatisticsView>();

        using World world = new("World");

        for (var index = 0; index < 50; index++) {
            world.Create(new Position());
        }

        view.Show(SceneStatistics.Collect(world, new() { Entities = 100 }));
        test.Frames(2);

        var entities = RowFor(view, "Entities");
        var bar = Assert.IsType<ProgressBar>(Assert.Single(Descendants(entities).OfType<ProgressBar>()));

        Assert.Equal(0.5f, bar.Value, 0.001f);

        // And it follows the count, rather than being set once when the row was built.
        view.Show(SceneStatistics.Collect(world, new() { Entities = 200 }));
        test.Frames(2);

        Assert.Equal(0.25f, Descendants(RowFor(view, "Entities")).OfType<ProgressBar>().Single().Value, 0.001f);
    }

    // ============================================================ Memory

    /// <summary>A second reading replaces the lines on screen.</summary>
    [Fact]
    public void A_second_reading_replaces_the_lines_on_screen() {
        var view = Build<MemoryView>();

        view.Providers.Gpu = () => [new(MemoryArena.Gpu, "Device-local", 512 * 1024 * 1024)];
        view.Take();
        test.Frames(2);

        Assert.Equal("512 MiB", ValueOfLine(view, "Device-local"));

        view.Providers.Gpu = () => [new(MemoryArena.Gpu, "Device-local", 256 * 1024 * 1024)];
        view.Take();
        test.Frames(2);

        Assert.Equal("256 MiB", ValueOfLine(view, "Device-local"));
    }

    /// <summary>
    ///     An arena with nothing in it is absent, heading and all — and appears when it fills.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The heading is the part worth asserting.</b> It is a sibling of its rows rather than
    ///     a wrapper around them, because <c>ProfilerTheme</c> writes <c>memory-line.memory-heading</c>
    ///     — compound, so both names have to land on the same element — and a port that nested the
    ///     arena's rows under a group element would lose every one of those rules silently.
    /// </remarks>
    [Fact]
    public void An_arena_that_appears_brings_a_heading_with_it() {
        var view = Build<MemoryView>();

        view.Take();
        test.Frames(2);

        Assert.DoesNotContain("Asset residency", Labels(view));

        view.Providers.Assets = () => [new(MemoryArena.Assets, "Textures", 12, IsCount: true)];
        view.Take();
        test.Frames(2);

        Assert.Contains("Asset residency", Labels(view));

        var heading = Lines(view).Single(line => TextOf(line).StartsWith("Asset residency", StringComparison.Ordinal));

        Assert.True(heading.HasClass("memory-heading"));
        Assert.False(heading.HasClass("memory-row"));

        // A count is not bytes, which is the flag the arena total also has to honour.
        Assert.Equal("12", ValueOfLine(view, "Textures"));
    }

    /// <summary>
    ///     Before anything has been read the panel says so, rather than showing an empty list that
    ///     reads as a process using no memory.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This state exists because a component has no build-time hook.</b> The control took its
    ///     first reading in <c>OnCreated</c>; the markup cannot, so the host takes it — and between
    ///     the build and that call there is a frame with nothing in it. Naming it is cheaper than
    ///     pretending it cannot happen.
    /// </remarks>
    [Fact]
    public void A_panel_that_has_read_nothing_says_so() {
        var view = Build<MemoryView>();

        Assert.Null(view.Snapshot);
        Assert.Empty(Lines(view));
        Assert.Contains("Press Refresh.", Texts(view.Root));

        view.Take();
        test.Frames(2);

        Assert.NotEmpty(Lines(view));
        Assert.DoesNotContain("Press Refresh.", Texts(view.Root));
    }

    /// <summary>
    ///     ⚠ <b>The tag the stylesheet needs is on the host element the component built itself into,
    ///     not on some wrapper.</b> `@tag` is what says so, and getting it wrong is invisible until
    ///     somebody notices the panel has no padding: <c>ProfilerTheme</c> reaches these two by tag
    ///     name for every rule they have, including <c>memory-view &gt; scroll-view</c>, which is a
    ///     direct-child selector and therefore also an assertion about depth.
    /// </summary>
    [Fact]
    public void The_host_elements_answer_to_the_tags_the_theme_writes() {
        var memory = Build<MemoryView>();
        var statistics = Build<StatisticsView>();

        Assert.Equal("memory-view", memory.Root.Tag);
        Assert.Equal("statistics-view", statistics.Root.Tag);

        Assert.Single(memory.Root.Children.OfType<ScrollView>());
        Assert.Single(Tagged(memory.Root, "memory-toolbar"));
        Assert.Single(Tagged(statistics.Root, "statistics-toolbar"));
        Assert.Single(Tagged(statistics.Root, "statistics-body"));
    }

    // ============================================================ Harness

    /// <summary>A three-field component, so a chunk's column has a size worth counting.</summary>
    readonly record struct Position(float X, float Y, float Z);

    T Build<T>() where T : Component, new() {
        var built = BuildContext.Build<T>(test.Document, test.Document.Root);
        test.Frames(2);

        return built;
    }

    static UiElement[] Rows(StatisticsView view) => Tagged(view.Root, "statistic-row");

    static UiElement[] Lines(MemoryView view) => Tagged(view.Root, "memory-line");

    /// <summary>The row whose label is this, or a failure naming what was there instead.</summary>
    static UiElement RowFor(StatisticsView view, string label) =>
        Assert.Single(
            Tagged(view.Root, "statistic-row"),
            row => Tagged(row, "statistic-label").Any(part => TextOf(part) == label)
        );

    /// <summary>What a statistics row's value column reads, or null when there is no such row.</summary>
    static string? ValueOf(StatisticsView view, string label) {
        foreach (var row in Tagged(view.Root, "statistic-row")) {
            if (Tagged(row, "statistic-label").Any(part => TextOf(part) == label)) {
                return TextOf(Tagged(row, "statistic-value").Single());
            }
        }

        return null;
    }

    /// <summary>The same, for a memory line.</summary>
    static string? ValueOfLine(MemoryView view, string label) {
        foreach (var line in Tagged(view.Root, "memory-line")) {
            if (Tagged(line, "memory-label").Any(part => TextOf(part) == label)) {
                return TextOf(Tagged(line, "memory-value").Single());
            }
        }

        return null;
    }

    static string[] Labels(MemoryView view) =>
        [.. Tagged(view.Root, "memory-label").Select(TextOf)];

    static UiElement[] Tagged(UiElement root, string tag) =>
        [.. Descendants(root).Where(element => string.Equals(element.Tag, tag, StringComparison.Ordinal))];

    /// <summary>
    ///     Everything an element and its descendants say, joined.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A walk rather than <c>element.Text</c>, because markup text is its own element.</b>
    ///     <c>BuildContext.Text</c> creates a child tagged <c>text</c> and puts the string on that —
    ///     so <c>&lt;statistic-label&gt;@row.Label&lt;/statistic-label&gt;</c> leaves the label's own
    ///     <c>Text</c> null, and an assertion reading it would be null against every row however
    ///     right the panel was.
    /// </remarks>
    static string TextOf(UiElement element) =>
        string.Concat(Texts(element));

    static IEnumerable<string> Texts(UiElement element) {
        if (element.Text is { } own) {
            yield return own;
        }

        foreach (var child in Descendants(element)) {
            if (child.Text is { } text) {
                yield return text;
            }
        }
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var nested in Descendants(child)) {
                yield return nested;
            }
        }
    }
}
