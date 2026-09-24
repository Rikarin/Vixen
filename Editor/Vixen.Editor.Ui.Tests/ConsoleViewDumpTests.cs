// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Core.Imaging;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The console's port to markup (#758), held to the panel the hand-written control built.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Recorded before the port and not after it</b>, which is <c>MessageLogViewDumpTests</c>'
///         convention: every reference below was produced by the hand-written C# <c>ConsoleView</c>, in
///         a docked panel of a real <see cref="EditorShell" />, and the port is required to reproduce
///         it. The states are reached through the interface — a row is chosen by a pointer click, a
///         toggle by its own click, the list cleared by its own button — rather than by writing fields.
///     </para>
///     <para>
///         ⚠ <b>Two dumps per state, because a tree dump is blind.</b> <c>UiTest.Tree</c> prints tags,
///         classes, rectangles and text; the badges' counts, the toggles' checked bits and a row's
///         <c>Checked</c> live where only <c>UiTest.Flags</c> looks.
///     </para>
///     <para>
///         ⚠ <b>Deterministic by construction.</b> The sink's clock is fixed, so the time column is;
///         the exception is never thrown, so its text has no stack and no path; and the detail pane's
///         <c>thread N</c> is the one field no test can fix, so both dumps have it normalised.
///     </para>
/// </remarks>
[SuppressMessage("Trimming", "IL2026", Justification = "UiTest.Flags reads nine properties by name; tests are not trimmed.")]
public sealed partial class ConsoleViewDumpTests {
    /// <summary>Where a run writes its dumps and pictures, when somebody asks it to.</summary>
    /// <remarks>Unset in every gate. It is how the references were taken from the pre-port build.</remarks>
    static readonly string? Record = Environment.GetEnvironmentVariable("VIXEN_CONSOLE_RECORD");

    [Fact]
    public void Empty_the_console_shows_no_rows_and_says_nothing_is_chosen() {
        using var harness = new Harness();

        Check(harness, "empty", EmptyTree, EmptyFlags);
    }

    [Fact]
    public void Three_lines_are_three_rows_oldest_first_with_their_badges() {
        using var harness = new Harness();

        harness.Post();

        Check(harness, "posted", PostedTree, PostedFlags);
    }

    /// <summary>A click on the error's row puts the whole record in the pane under the list.</summary>
    [Fact]
    public void Choosing_a_row_shows_the_whole_record() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(0);

        Assert.Equal("Could not import wood.png", harness.View.Selected?.Message);
        Check(harness, "chosen", ChosenTree, ChosenFlags);
    }

    /// <summary>
    ///     ⚠ <b>A second choice moves the pane and the checked bit off the first</b> — the test a pane
    ///     that is an <c>@if</c> arm needs, since an arm survives while its predicate stays true.
    /// </summary>
    [Fact]
    public void Choosing_a_second_row_moves_the_pane_off_the_first() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(0);
        harness.ClickRow(1);

        Assert.Equal("Texture cache is 90% full", harness.View.Selected?.Message);
        Assert.Equal("Texture cache is 90% full", harness.View.Detail.Children[0].Text);

        var rows = harness.View.List.Scroller.Content.Children;

        Assert.False((rows[0].State & ElementState.Checked) != 0, "the first row is still checked");
        Assert.True((rows[1].State & ElementState.Checked) != 0, "the second row is not checked");
    }

    /// <summary>
    ///     ⚠ <b>The detail pane holds a record, not a row</b> — so a search that hides the chosen
    ///     line leaves its stack on screen. That is the console's own rule (see its remarks), and the
    ///     opposite of the message log's.
    /// </summary>
    [Fact]
    public void A_search_that_hides_the_chosen_line_keeps_its_detail() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(0);

        harness.View.Search.Value = "cache";
        harness.Settle();

        Assert.Equal(1, harness.View.Model?.Count);
        Assert.Equal("Could not import wood.png", harness.View.Selected?.Message);
        Check(harness, "searched", SearchedTree, SearchedFlags);
    }

    /// <summary>Collapse through its own toggle: three identical lines are one row saying 3.</summary>
    [Fact]
    public void Collapsing_folds_identical_lines_into_one_row_with_a_count() {
        using var harness = new Harness();

        harness.Post();
        harness.Repeat();

        harness.Ui.Get("console-toolbar toggle-button").Nth(0).Click();
        harness.Settle();

        Assert.True(harness.View.Model?.Collapse);
        Check(harness, "collapsed", CollapsedTree, CollapsedFlags);
    }

    /// <summary>A level badge clicked off hides its lines and keeps counting them.</summary>
    [Fact]
    public void A_level_badge_clicked_off_hides_its_lines_and_keeps_its_count() {
        using var harness = new Harness();

        harness.Post();

        harness.Ui.Get("console-toolbar .level-warning").First().Click();
        harness.Settle();

        Assert.Equal(2, harness.View.Model?.Count);
        Check(harness, "levels", LevelsTree, LevelsFlags);
    }

    /// <summary>Clear through its own button, with a line chosen.</summary>
    [Fact]
    public void Clearing_under_a_selection_leaves_an_empty_list_and_an_empty_pane() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(0);

        harness.Ui.Get("console-toolbar button").First().Click();
        harness.Settle();

        Assert.Equal(0, harness.View.Model?.Count);
        Assert.Null(harness.View.Selected);
        Check(harness, "cleared", ClearedTree, ClearedFlags);
    }

    /// <summary>A double click on a row asks the host to open the source, once.</summary>
    [Fact]
    public void A_double_click_on_a_row_activates_its_record() {
        using var harness = new Harness();
        var activated = new List<string>();

        harness.View.Activated += (_, record) => activated.Add(record.Message);
        harness.Post();

        var row = harness.Ui.Get("console-row").Nth(1);

        row.Click();
        harness.Ui.Advance(TimeSpan.FromMilliseconds(20));
        row.Click();
        harness.Settle();

        Assert.Equal(["Texture cache is 90% full"], activated);
    }

    static void Check(Harness harness, string state, string tree, string flags) {
        var actualTree = harness.Ui.Tree(harness.View);
        var actualFlags = harness.Ui.Flags(harness.View);

        if (Record is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{state}.tree.txt"), actualTree);
            File.WriteAllText(Path.Combine(directory, $"{state}.flags.txt"), actualFlags);
            PngCodec.Save(Path.Combine(directory, $"{state}.png"), harness.Ui.Capture());
        }

        Assert.Equal(Normalise(tree), Normalise(actualTree));
        Assert.Equal(Normalise(flags), Normalise(actualFlags));
    }

    static string Normalise(string text) => ThreadId().Replace(text.ReplaceLineEndings("\n").Trim(), "thread N");

    [GeneratedRegex(@"thread \d+")]
    private static partial Regex ThreadId();

    /// <summary>A clock that says what it is told.</summary>
    sealed class FixedClock : TimeProvider {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The shell, the console opened in a docked panel, and a fixed clock.</summary>
    sealed class Harness : IDisposable {
        const string Panel = "console";

        readonly FixedClock clock = new();

        ConsoleView? view;

        public Harness() {
            Sink = new RingBufferSink(256) { MinimumLevel = LogLevel.Trace, TimeProvider = clock };
            Model = new ConsoleModel(Sink);

            Shell = new EditorShell(720f, 420f, ThemeMode.Dark);
            Font(Shell.Document);

            // The application's registration, restated: `EditorApplication` is in an assembly this
            // one cannot reference, and what it does is exactly this.
            Shell.RegisterPanel(
                Panel,
                new StringId("test.panel.console", "Console"),
                panel => {
                    view = panel.Add<ConsoleView>();
                    view.Show(Model);
                }
            );

            Shell.RegisterLayout("Console", new StringId("test.layout.console", "Console"), () => LayoutPresets.Single(Panel));
            Shell.Workspace.Reset();

            Ui = UiTest.Adopt(Shell.Document);
            View = view ?? throw new InvalidOperationException("the shell did not build the console");

            Settle();
        }

        public EditorShell Shell { get; }

        public UiTest Ui { get; }

        public ConsoleView View { get; }

        public RingBufferSink Sink { get; }

        public ConsoleModel Model { get; }

        /// <summary>An error with an exception and an event id, a warning, and a two-line info.</summary>
        public void Post() {
            clock.Now = new(2026, 9, 24, 1, 1, 1, 250, TimeSpan.Zero);
            Log("Vixen.Editor.Import", LogLevel.Error, "Could not import wood.png", new InvalidOperationException("The file is not a PNG."), 2001);

            clock.Now = new(2026, 9, 24, 1, 2, 5, 500, TimeSpan.Zero);
            Log("Vixen.Editor.Cache", LogLevel.Warning, "Texture cache is 90% full");

            clock.Now = new(2026, 9, 24, 1, 3, 10, 0, TimeSpan.Zero);
            Log("Vixen.Editor.Import", LogLevel.Information, "Imported 12 assets\nThree were skipped as unchanged.");

            Settle();
        }

        /// <summary>The warning twice more, which is what collapse has to fold.</summary>
        public void Repeat() {
            clock.Now = new(2026, 9, 24, 1, 4, 0, 0, TimeSpan.Zero);
            Log("Vixen.Editor.Cache", LogLevel.Warning, "Texture cache is 90% full");

            clock.Now = new(2026, 9, 24, 1, 5, 0, 0, TimeSpan.Zero);
            Log("Vixen.Editor.Cache", LogLevel.Warning, "Texture cache is 90% full");

            Settle();
        }

        /// <summary>Clicks a row by its position from the top, the way a person picks one.</summary>
        public void ClickRow(int index) {
            Ui.Get("console-row").Nth(index).Click();
            Settle();
        }

        /// <summary>What <c>EditorApplication</c> does each frame for the console, then two frames.</summary>
        public void Settle() {
            View.Tick();
            Ui.Frames(2);
        }

        /// <summary>⚠ The shell and no more — see <c>EditorChromeVisualTests.ChromeFixture.Dispose</c>.</summary>
        public void Dispose() {
            Shell.Dispose();
            Sink.Dispose();
        }

        void Log(string category, LogLevel level, string message, Exception? failure = null, int eventId = 0) =>
            Sink.CreateLogger(category).Log(level, eventId, message, failure, static (state, _) => state);

        static void Font(UiDocument document) {
            var face = FontFace.Load(File.ReadAllBytes(TypefacePath()), name: "OpenSans");

            document.Fonts.Register(face.Name, face);
            document.Fonts.Default = face;
        }

        static string TypefacePath() {
            const string Relative = "Editor/Vixen.Editor.App/Fonts/OpenSans-Regular.ttf";

            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
                var candidate = Path.Combine(directory.FullName, Relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(candidate)) {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"'{Relative}' was not found above '{AppContext.BaseDirectory}'.");
        }
    }

    // ── The reference dumps, taken from the hand-written control ─────────────

    const string EmptyTree = """
        <console-view .size-md .variant-default> 3,3 710×319
          <console-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 55×28
              <label> 10,4 35×20 "Clear"
            <toggle-button .size-sm .variant-default> 65,4 77×28
              <label> 10,4 57×20 "Collapse"
            <toggle-button .size-sm .variant-default> 146,4 108×28
              <label> 10,4 88×20 "Clear on Play"
            <search-box .empty .size-md .variant-default> 258,5 140×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .empty .size-sm .variant-default> 402,4 150×32
              <select-field> 10,5 112×22 "All Categories"
              <icon .size-md .variant-default> 128,10 12×12
            <toggle-button .console-level .level-error .size-sm .variant-default> 556,4 34×26
              <label> 12,3 9×20 "0"
            <toggle-button .console-level .level-warning .size-sm .variant-default> 594,4 34×26
              <label> 12,3 9×20 "0"
            <toggle-button .console-level .level-info .size-sm .variant-default> 632,4 34×26
              <label> 12,3 9×20 "0"
            <toggle-button .console-level .level-verbose .size-sm .variant-default> 670,4 34×26
              <label> 12,3 9×20 "0"
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <console-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a line to see the whole record."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string EmptyFlags = """
        <button .size-sm .variant-subtle> Label="Clear"
        <toggle-button .size-sm .variant-default> Label="Collapse"
        <toggle-button .size-sm .variant-default> Label="Clear on Play"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .empty .size-sm .variant-default> State=Valid Placeholder="All Categories"
        <toggle-button .console-level .level-error .size-sm .variant-default> State=Checked IsChecked=True Label="0"
        <toggle-button .console-level .level-warning .size-sm .variant-default> State=Checked IsChecked=True Label="0"
        <toggle-button .console-level .level-info .size-sm .variant-default> State=Checked IsChecked=True Label="0"
        <toggle-button .console-level .level-verbose .size-sm .variant-default> Label="0"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string PostedTree = """
        <console-view .size-md .variant-default> 3,3 710×319
          <console-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 55×28
              <label> 10,4 35×20 "Clear"
            <toggle-button .size-sm .variant-default> 65,4 77×28
              <label> 10,4 57×20 "Collapse"
            <toggle-button .size-sm .variant-default> 146,4 108×28
              <label> 10,4 88×20 "Clear on Play"
            <search-box .empty .size-md .variant-default> 258,5 140×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .empty .size-sm .variant-default> 402,4 150×32
              <select-field> 10,5 112×22 "All Categories"
              <icon .size-md .variant-default> 128,10 12×12
            <toggle-button .console-level .level-error .size-sm .variant-default> 556,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-warning .size-sm .variant-default> 594,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-info .size-sm .variant-default> 632,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-verbose .size-sm .variant-default> 670,4 34×26
              <label> 12,3 9×20 "0"
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×66
                <console-row> 0,0 710×22
                  <console-level-mark .level-error> 8,5 3×12
                  <console-time> 19,2 74×18 "01:01:01.250"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Could not import wood.png"
                  <console-repeats> 682,11 20×0
                <console-row> 0,22 710×22
                  <console-level-mark .level-warning> 8,5 3×12
                  <console-time> 19,2 74×18 "01:02:05.500"
                  <console-category> 101,1 112×20 "Cache"
                  <console-message> 221,1 453×20 "Texture cache is 90% full"
                  <console-repeats> 682,11 20×0
                <console-row> 0,44 710×22
                  <console-level-mark .level-info> 8,5 3×12
                  <console-time> 19,2 74×18 "01:03:10.000"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Imported 12 assets …"
                  <console-repeats> 682,11 20×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <console-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a line to see the whole record."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string PostedFlags = """
        <button .size-sm .variant-subtle> Label="Clear"
        <toggle-button .size-sm .variant-default> Label="Collapse"
        <toggle-button .size-sm .variant-default> Label="Clear on Play"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .empty .size-sm .variant-default> State=Valid Placeholder="All Categories"
        <toggle-button .console-level .level-error .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-warning .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-info .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-verbose .size-sm .variant-default> Label="0"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string ChosenTree = """
        <console-view .size-md .variant-default> 3,3 710×319
          <console-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 55×28
              <label> 10,4 35×20 "Clear"
            <toggle-button .size-sm .variant-default> 65,4 77×28
              <label> 10,4 57×20 "Collapse"
            <toggle-button .size-sm .variant-default> 146,4 108×28
              <label> 10,4 88×20 "Clear on Play"
            <search-box .empty .size-md .variant-default> 258,5 140×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .empty .size-sm .variant-default> 402,4 150×32
              <select-field> 10,5 112×22 "All Categories"
              <icon .size-md .variant-default> 128,10 12×12
            <toggle-button .console-level .level-error .size-sm .variant-default> 556,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-warning .size-sm .variant-default> 594,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-info .size-sm .variant-default> 632,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-verbose .size-sm .variant-default> 670,4 34×26
              <label> 12,3 9×20 "0"
          <virtualizing-panel .size-md .variant-default> 0,41 710×146
            <scroll-view .size-md .variant-default> 0,0 710×146
              <scroll-content .virtual-content> 0,0 710×66
                <console-row> 0,0 710×22
                  <console-level-mark .level-error> 8,5 3×12
                  <console-time> 19,2 74×18 "01:01:01.250"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Could not import wood.png"
                  <console-repeats> 682,11 20×0
                <console-row> 0,22 710×22
                  <console-level-mark .level-warning> 8,5 3×12
                  <console-time> 19,2 74×18 "01:02:05.500"
                  <console-category> 101,1 112×20 "Cache"
                  <console-message> 221,1 453×20 "Texture cache is 90% full"
                  <console-repeats> 682,11 20×0
                <console-row> 0,44 710×22
                  <console-level-mark .level-info> 8,5 3×12
                  <console-time> 19,2 74×18 "01:03:10.000"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Imported 12 assets …"
                  <console-repeats> 682,11 20×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×146
              <scrollbar .horizontal .size-md .variant-default> 0,136 8×10
          <console-detail .size-md .variant-default> 0,187 710×132
            <scroll-content> 0,1 710×80
              <console-detail-heading> 9,7 692×22 "Could not import wood.png"
              <console-detail-meta> 9,32 692×19 "Error · Vixen.Editor.Import · 01:01:01.250 · thread 12 · #2001"
              <console-detail-stack> 9,54 692×19 "System.InvalidOperationException: The file is not a PNG."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×131
            <scrollbar .horizontal .size-md .variant-default> 0,122 710×10
        """;

    const string ChosenFlags = """
        <console-view .size-md .variant-default> State=Hover
        <button .size-sm .variant-subtle> Label="Clear"
        <toggle-button .size-sm .variant-default> Label="Collapse"
        <toggle-button .size-sm .variant-default> Label="Clear on Play"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .empty .size-sm .variant-default> State=Valid Placeholder="All Categories"
        <toggle-button .console-level .level-error .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-warning .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-info .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-verbose .size-sm .variant-default> Label="0"
        <virtualizing-panel .size-md .variant-default> State=Hover
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content .virtual-content> State=Hover
        <console-row> State=Hover, Checked
        <console-message> State=Hover
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string SearchedTree = """
        <console-view .size-md .variant-default> 3,3 710×319
          <console-toolbar> 0,0 710×45
            <button .size-sm .variant-subtle> 6,4 55×28
              <label> 10,4 35×20 "Clear"
            <toggle-button .size-sm .variant-default> 65,4 77×28
              <label> 10,4 57×20 "Collapse"
            <toggle-button .size-sm .variant-default> 146,4 108×28
              <label> 10,4 88×20 "Clear on Play"
            <search-box .size-md .variant-default> 258,4 140×36
              <icon .size-md .variant-default> 10,11 14×14
              <field-placeholder> 0,0 0×0 "Filter…"
              <field-text> 30,7 44×22 "cache"
              <icon-button .size-md .variant-subtle> 80,5 26×26
                <icon .size-md .variant-default> 6,6 14×14
                <label> 0,0 0×0 "Clear"
            <select .empty .size-sm .variant-default> 402,6 150×32
              <select-field> 10,5 112×22 "All Categories"
              <icon .size-md .variant-default> 128,10 12×12
            <toggle-button .console-level .level-error .size-sm .variant-default> 556,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-warning .size-sm .variant-default> 594,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-info .size-sm .variant-default> 632,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-verbose .size-sm .variant-default> 670,4 34×26
              <label> 12,3 9×20 "0"
          <virtualizing-panel .size-md .variant-default> 0,45 710×142
            <scroll-view .size-md .variant-default> 0,0 710×142
              <scroll-content .virtual-content> 0,0 710×22
                <console-row> 0,0 710×22
                  <console-level-mark .level-warning> 8,5 3×12
                  <console-time> 19,2 74×18 "01:02:05.500"
                  <console-category> 101,1 112×20 "Cache"
                  <console-message> 221,1 453×20 "Texture cache is 90% full"
                  <console-repeats> 682,11 20×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-warning> 0,0 0×0
                  <console-time> 0,0 0×0 "01:02:05.500"
                  <console-category> 0,0 0×0 "Cache"
                  <console-message> 0,0 0×0 "Texture cache is 90% full"
                  <console-repeats> 0,0 0×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-info> 0,0 0×0
                  <console-time> 0,0 0×0 "01:03:10.000"
                  <console-category> 0,0 0×0 "Import"
                  <console-message> 0,0 0×0 "Imported 12 assets …"
                  <console-repeats> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×142
              <scrollbar .horizontal .size-md .variant-default> 0,132 8×10
          <console-detail .size-md .variant-default> 0,187 710×132
            <scroll-content> 0,1 710×80
              <console-detail-heading> 9,7 692×22 "Could not import wood.png"
              <console-detail-meta> 9,32 692×19 "Error · Vixen.Editor.Import · 01:01:01.250 · thread 12 · #2001"
              <console-detail-stack> 9,54 692×19 "System.InvalidOperationException: The file is not a PNG."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×131
            <scrollbar .horizontal .size-md .variant-default> 0,122 710×10
        """;

    const string SearchedFlags = """
        <console-view .size-md .variant-default> State=Hover
        <button .size-sm .variant-subtle> Label="Clear"
        <toggle-button .size-sm .variant-default> Label="Collapse"
        <toggle-button .size-sm .variant-default> Label="Clear on Play"
        <search-box .size-md .variant-default> State=Valid Value="cache" Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .empty .size-sm .variant-default> State=Valid Placeholder="All Categories"
        <toggle-button .console-level .level-error .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-warning .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-info .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-verbose .size-sm .variant-default> Label="0"
        <virtualizing-panel .size-md .variant-default> State=Hover
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content .virtual-content> State=Hover
        <console-row> State=Hover
        <console-message> State=Hover
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string CollapsedTree = """
        <console-view .size-md .variant-default> 3,3 710×319
          <console-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 55×28
              <label> 10,4 35×20 "Clear"
            <toggle-button .size-sm .variant-default> 65,4 77×28
              <label> 10,4 57×20 "Collapse"
            <toggle-button .size-sm .variant-default> 146,4 108×28
              <label> 10,4 88×20 "Clear on Play"
            <search-box .empty .size-md .variant-default> 258,5 140×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .empty .size-sm .variant-default> 402,4 150×32
              <select-field> 10,5 112×22 "All Categories"
              <icon .size-md .variant-default> 128,10 12×12
            <toggle-button .console-level .level-error .size-sm .variant-default> 556,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-warning .size-sm .variant-default> 594,4 34×26
              <label> 12,3 9×20 "3"
            <toggle-button .console-level .level-info .size-sm .variant-default> 632,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-verbose .size-sm .variant-default> 670,4 34×26
              <label> 12,3 9×20 "0"
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×66
                <console-row> 0,0 710×22
                  <console-level-mark .level-error> 8,5 3×12
                  <console-time> 19,2 74×18 "01:01:01.250"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Could not import wood.png"
                  <console-repeats> 682,11 20×0
                <console-row> 0,22 710×22
                  <console-level-mark .level-warning> 8,5 3×12
                  <console-time> 19,2 74×18 "01:05:00.000"
                  <console-category> 101,1 112×20 "Cache"
                  <console-message> 221,1 453×20 "Texture cache is 90% full"
                  <console-repeats> 682,2 20×17 "3"
                <console-row> 0,44 710×22
                  <console-level-mark .level-info> 8,5 3×12
                  <console-time> 19,2 74×18 "01:03:10.000"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Imported 12 assets …"
                  <console-repeats> 682,11 20×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-warning> 0,0 0×0
                  <console-time> 0,0 0×0 "01:04:00.000"
                  <console-category> 0,0 0×0 "Cache"
                  <console-message> 0,0 0×0 "Texture cache is 90% full"
                  <console-repeats> 0,0 0×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-warning> 0,0 0×0
                  <console-time> 0,0 0×0 "01:05:00.000"
                  <console-category> 0,0 0×0 "Cache"
                  <console-message> 0,0 0×0 "Texture cache is 90% full"
                  <console-repeats> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <console-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a line to see the whole record."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string CollapsedFlags = """
        <console-view .size-md .variant-default> State=Hover, FocusWithin
        <console-toolbar> State=Hover, FocusWithin
        <button .size-sm .variant-subtle> Label="Clear"
        <toggle-button .size-sm .variant-default> State=Hover, Focus, Checked, FocusWithin IsChecked=True Label="Collapse"
        <label> State=Hover
        <toggle-button .size-sm .variant-default> Label="Clear on Play"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .empty .size-sm .variant-default> State=Valid Placeholder="All Categories"
        <toggle-button .console-level .level-error .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-warning .size-sm .variant-default> State=Checked IsChecked=True Label="3"
        <toggle-button .console-level .level-info .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-verbose .size-sm .variant-default> Label="0"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string LevelsTree = """
        <console-view .size-md .variant-default> 3,3 710×319
          <console-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 55×28
              <label> 10,4 35×20 "Clear"
            <toggle-button .size-sm .variant-default> 65,4 77×28
              <label> 10,4 57×20 "Collapse"
            <toggle-button .size-sm .variant-default> 146,4 108×28
              <label> 10,4 88×20 "Clear on Play"
            <search-box .empty .size-md .variant-default> 258,5 140×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .empty .size-sm .variant-default> 402,4 150×32
              <select-field> 10,5 112×22 "All Categories"
              <icon .size-md .variant-default> 128,10 12×12
            <toggle-button .console-level .level-error .size-sm .variant-default> 556,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-warning .size-sm .variant-default> 594,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-info .size-sm .variant-default> 632,4 34×26
              <label> 12,3 9×20 "1"
            <toggle-button .console-level .level-verbose .size-sm .variant-default> 670,4 34×26
              <label> 12,3 9×20 "0"
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×44
                <console-row> 0,0 710×22
                  <console-level-mark .level-error> 8,5 3×12
                  <console-time> 19,2 74×18 "01:01:01.250"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Could not import wood.png"
                  <console-repeats> 682,11 20×0
                <console-row> 0,22 710×22
                  <console-level-mark .level-info> 8,5 3×12
                  <console-time> 19,2 74×18 "01:03:10.000"
                  <console-category> 101,1 112×20 "Import"
                  <console-message> 221,1 453×20 "Imported 12 assets …"
                  <console-repeats> 682,11 20×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-info> 0,0 0×0
                  <console-time> 0,0 0×0 "01:03:10.000"
                  <console-category> 0,0 0×0 "Import"
                  <console-message> 0,0 0×0 "Imported 12 assets …"
                  <console-repeats> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <console-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a line to see the whole record."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string LevelsFlags = """
        <console-view .size-md .variant-default> State=Hover, FocusWithin
        <console-toolbar> State=Hover, FocusWithin
        <button .size-sm .variant-subtle> Label="Clear"
        <toggle-button .size-sm .variant-default> Label="Collapse"
        <toggle-button .size-sm .variant-default> Label="Clear on Play"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .empty .size-sm .variant-default> State=Valid Placeholder="All Categories"
        <toggle-button .console-level .level-error .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-warning .size-sm .variant-default> State=Hover, Focus, FocusWithin Label="1"
        <label> State=Hover
        <toggle-button .console-level .level-info .size-sm .variant-default> State=Checked IsChecked=True Label="1"
        <toggle-button .console-level .level-verbose .size-sm .variant-default> Label="0"
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string ClearedTree = """
        <console-view .size-md .variant-default> 3,3 710×319
          <console-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 55×28
              <label> 10,4 35×20 "Clear"
            <toggle-button .size-sm .variant-default> 65,4 77×28
              <label> 10,4 57×20 "Collapse"
            <toggle-button .size-sm .variant-default> 146,4 108×28
              <label> 10,4 88×20 "Clear on Play"
            <search-box .empty .size-md .variant-default> 258,5 140×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .empty .size-sm .variant-default> 402,4 150×32
              <select-field> 10,5 112×22 "All Categories"
              <icon .size-md .variant-default> 128,10 12×12
            <toggle-button .console-level .level-error .size-sm .variant-default> 556,4 34×26
              <label> 12,3 9×20 "0"
            <toggle-button .console-level .level-warning .size-sm .variant-default> 594,4 34×26
              <label> 12,3 9×20 "0"
            <toggle-button .console-level .level-info .size-sm .variant-default> 632,4 34×26
              <label> 12,3 9×20 "0"
            <toggle-button .console-level .level-verbose .size-sm .variant-default> 670,4 34×26
              <label> 12,3 9×20 "0"
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-error> 0,0 0×0
                  <console-time> 0,0 0×0 "01:01:01.250"
                  <console-category> 0,0 0×0 "Import"
                  <console-message> 0,0 0×0 "Could not import wood.png"
                  <console-repeats> 0,0 0×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-warning> 0,0 0×0
                  <console-time> 0,0 0×0 "01:02:05.500"
                  <console-category> 0,0 0×0 "Cache"
                  <console-message> 0,0 0×0 "Texture cache is 90% full"
                  <console-repeats> 0,0 0×0
                <console-row .parked> 0,0 0×0
                  <console-level-mark .level-info> 0,0 0×0
                  <console-time> 0,0 0×0 "01:03:10.000"
                  <console-category> 0,0 0×0 "Import"
                  <console-message> 0,0 0×0 "Imported 12 assets …"
                  <console-repeats> 0,0 0×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <console-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a line to see the whole record."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string ClearedFlags = """
        <console-view .size-md .variant-default> State=Hover, FocusWithin
        <console-toolbar> State=Hover, FocusWithin
        <button .size-sm .variant-subtle> State=Hover, Focus, FocusWithin Label="Clear"
        <label> State=Hover
        <toggle-button .size-sm .variant-default> Label="Collapse"
        <toggle-button .size-sm .variant-default> Label="Clear on Play"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .empty .size-sm .variant-default> State=Valid Placeholder="All Categories"
        <toggle-button .console-level .level-error .size-sm .variant-default> State=Checked IsChecked=True Label="0"
        <toggle-button .console-level .level-warning .size-sm .variant-default> State=Checked IsChecked=True Label="0"
        <toggle-button .console-level .level-info .size-sm .variant-default> State=Checked IsChecked=True Label="0"
        <toggle-button .console-level .level-verbose .size-sm .variant-default> Label="0"
        <console-row .parked> State=Checked
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;
}
