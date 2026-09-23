// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Imaging;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The message log's port (#89), held to the panel the hand-written control built.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Recorded before the port and not after it.</b> Every dump below was produced by the
///         hand-written C# <c>MessageLogView</c>, through the shell's own registration —
///         <c>EditorShell.RegisterShellPanels</c> is the one production caller, so the panel is opened
///         the way a person opens it rather than added to a bare document. The port is then required
///         to reproduce them byte for byte, in six states reached through the interface: a row is
///         chosen by a real pointer click, not by writing a field.
///     </para>
///     <para>
///         ⚠ <b>And the first thing the recording found was that the three states with a chosen row
///         could not be reached.</b> The row waited for a <c>ClickEvent</c>, which only a
///         <c>Control</c> raises, so a click on a bare <c>message-row</c> chose nothing and
///         <see cref="Choosing_a_row_shows_the_whole_message" /> read <c>Selected</c> as
///         <see langword="null" />. The C# was fixed to take a <c>TapEvent</c> — <c>ConsoleView</c>'s
///         fix for the same defect — before the reference was taken.
///     </para>
///     <para>
///         ⚠ <b>Two dumps per state, because a tree dump is blind.</b> <c>UiTest.Tree</c> prints tags,
///         classes, rectangles and text; the Clear button's label, the picker's value and a row's
///         checked bit live where only <c>UiTest.Flags</c> looks.
///     </para>
///     <para>
///         ⚠ <b>And the detail pane is the part that moved from imperative to reactive</b>, so it is
///         the part the states are chosen to exercise: nothing chosen, a message chosen with a
///         detail, the chosen message filtered away (the pane must fall back rather than keep showing
///         a message the list no longer has), and the history cleared under a selection.
///     </para>
/// </remarks>
[SuppressMessage("Trimming", "IL2026", Justification = "UiTest.Flags reads nine properties by name; tests are not trimmed.")]
public sealed class MessageLogViewDumpTests {
    /// <summary>Where a run writes its dumps and pictures, when somebody asks it to.</summary>
    /// <remarks>
    ///     Unset in every gate. It is how the reference strings below were taken from the pre-port
    ///     build, and how the before-and-after pictures in the port's commit were made.
    /// </remarks>
    static readonly string? Record = Environment.GetEnvironmentVariable("VIXEN_MESSAGE_LOG_RECORD");

    [Fact]
    public void Empty_the_panel_says_nothing_is_chosen() {
        using var harness = new Harness();

        Check(harness, "empty", EmptyTree, EmptyFlags);
    }

    [Fact]
    public void Three_messages_are_three_rows_newest_first() {
        using var harness = new Harness();

        harness.Post();

        Check(harness, "posted", PostedTree, PostedFlags);
    }

    /// <summary>A click on the error's row puts the whole of it in the pane under the list.</summary>
    [Fact]
    public void Choosing_a_row_shows_the_whole_message() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(2);

        Assert.Equal("Could not import", harness.View.Selected?.Message);
        Check(harness, "chosen", ChosenTree, ChosenFlags);
    }

    /// <summary>
    ///     ⚠ <b>A second choice moves the pane off the first</b>, which is the test a panel whose
    ///     pane is an <c>@if</c> arm needs and a suite that only ever chooses one row cannot be.
    /// </summary>
    /// <remarks>
    ///     An arm survives while its predicate stays true, so a readout that closed over the arm's
    ///     pattern variable would keep describing the first message ever chosen — which is how
    ///     <c>VariationHarnessView</c> shipped past a suite in which every test selected exactly one
    ///     cell. Asserted on the elements, and on the checked bit moving with it.
    /// </remarks>
    [Fact]
    public void Choosing_a_second_row_moves_the_pane_off_the_first() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(2);
        harness.ClickRow(0);

        Assert.Equal("Saved", harness.View.Selected?.Message);

        Assert.Equal(
            ["message-detail-heading:Saved", "message-detail-meta:Success · 01:03:10"],
            harness.View.Detail.Children.Select(child => $"{child.Tag}:{child.Text}")
        );

        var rows = harness.View.List.Scroller.Content.Children;

        Assert.True((rows[0].State & ElementState.Checked) != 0);
        Assert.False((rows[2].State & ElementState.Checked) != 0);
    }

    /// <summary>
    ///     ⚠ The chosen message filtered away: the pane must fall back to "nothing chosen" rather
    ///     than keep describing a row the list no longer has.
    /// </summary>
    [Fact]
    public void Filtering_the_chosen_message_away_empties_the_pane() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(2);

        harness.View.Levels.Value = nameof(NotificationSeverity.Success);
        harness.Settle();

        Assert.Null(harness.View.Selected);
        Assert.Single(harness.View.Shown);
        Check(harness, "filtered", FilteredTree, FilteredFlags);
    }

    /// <summary>A search that matches the detail rather than the message still finds it.</summary>
    [Fact]
    public void A_search_reaches_the_detail_and_keeps_the_selection() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(2);

        harness.View.Search.Value = "wood";
        harness.Settle();

        Assert.Equal("Could not import", Assert.Single(harness.View.Shown).Message);
        Assert.Equal("Could not import", harness.View.Selected?.Message);
        Check(harness, "searched", SearchedTree, SearchedFlags);
    }

    /// <summary>Clear through its own button, with a message chosen.</summary>
    [Fact]
    public void Clearing_under_a_selection_leaves_an_empty_list_and_an_empty_pane() {
        using var harness = new Harness();

        harness.Post();
        harness.ClickRow(2);

        harness.Ui.Get("message-log-toolbar button").First().Click();
        harness.Settle();

        Assert.Empty(harness.View.Shown);
        Assert.Null(harness.View.Selected);
        Check(harness, "cleared", ClearedTree, ClearedFlags);
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

    static string Normalise(string text) => text.ReplaceLineEndings("\n").Trim();

    /// <summary>The shell, the panel opened through its own registration, and a fixed clock.</summary>
    sealed class Harness : IDisposable {
        public Harness() {
            Shell = new EditorShell(720f, 420f, ThemeMode.Dark);
            Font(Shell.Document);

            Shell.RegisterLayout(
                "Messages",
                new StringId("test.layout.messages", "Messages"),
                () => LayoutPresets.Single(EditorShell.MessageLogPanel)
            );

            Shell.Workspace.Reset();

            Ui = UiTest.Adopt(Shell.Document);
            View = Shell.Messages ?? throw new InvalidOperationException("the shell did not build its message log");

            Settle();
        }

        public EditorShell Shell { get; }

        public UiTest Ui { get; }

        public MessageLogView View { get; }

        /// <summary>
        ///     Three messages on the centre's own clock, then the clock moved past every toast's
        ///     expiry so the corner is empty in the picture.
        /// </summary>
        public void Post() {
            Shell.Notifications.Tick(TimeSpan.FromSeconds(3661));
            Shell.Notifications.Error("Could not import", "wood.png\nThe second line of a long diagnostic.");

            Shell.Notifications.Tick(TimeSpan.FromSeconds(3725));
            Shell.Notifications.Show("Texture cache is 90% full", NotificationSeverity.Warning);

            Shell.Notifications.Tick(TimeSpan.FromSeconds(3790));
            Shell.Notifications.Success("Saved");

            Shell.Notifications.Tick(TimeSpan.FromSeconds(4000));
            Settle();
        }

        /// <summary>Clicks a row by its position from the top, the way a person picks one.</summary>
        public void ClickRow(int index) {
            Ui.Get("message-row").Nth(index).Click();
            Settle();
        }

        public void Settle() => Ui.Frames(2);

        /// <summary>⚠ The shell and no more — see <c>EditorChromeVisualTests.ChromeFixture.Dispose</c>.</summary>
        public void Dispose() => Shell.Dispose();

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
        <message-log .size-md .variant-default> 3,3 710×319
          <message-log-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 75×28
              <label> 10,4 55×20 "Clear All"
            <search-box .empty .size-md .variant-default> 85,5 465×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 554,4 150×32
              <select-field> 10,5 112×22 "All Messages"
              <icon .size-md .variant-default> 128,10 12×12
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×0
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <message-log-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a message to see the whole of it."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string EmptyFlags = """
        <button .size-sm .variant-subtle> Label="Clear All"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string PostedTree = """
        <message-log .size-md .variant-default> 3,3 710×319
          <message-log-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 75×28
              <label> 10,4 55×20 "Clear All"
            <search-box .empty .size-md .variant-default> 85,5 465×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 554,4 150×32
              <select-field> 10,5 112×22 "All Messages"
              <icon .size-md .variant-default> 128,10 12×12
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×66
                <message-row> 0,0 710×22
                  <message-mark .level-success> 8,5 3×12
                  <message-time> 19,2 62×18 "01:03:10"
                  <message-text> 89,1 40×20 "Saved"
                  <message-detail-text> 137,11 565×0
                <message-row> 0,22 710×22
                  <message-mark .level-warning> 8,5 3×12
                  <message-time> 19,2 62×18 "01:02:05"
                  <message-text> 89,1 165×20 "Texture cache is 90% full"
                  <message-detail-text> 262,11 440×0
                <message-row> 0,44 710×22
                  <message-mark .level-error> 8,5 3×12
                  <message-time> 19,2 62×18 "01:01:01"
                  <message-text> 89,1 115×20 "Could not import"
                  <message-detail-text> 212,1 490×20 "wood.png …"
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <message-log-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a message to see the whole of it."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string PostedFlags = """
        <button .size-sm .variant-subtle> Label="Clear All"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string ChosenTree = """
        <message-log .size-md .variant-default> 3,3 710×319
          <message-log-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 75×28
              <label> 10,4 55×20 "Clear All"
            <search-box .empty .size-md .variant-default> 85,5 465×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 554,4 150×32
              <select-field> 10,5 112×22 "All Messages"
              <icon .size-md .variant-default> 128,10 12×12
          <virtualizing-panel .size-md .variant-default> 0,41 710×182
            <scroll-view .size-md .variant-default> 0,0 710×182
              <scroll-content .virtual-content> 0,0 710×66
                <message-row> 0,0 710×22
                  <message-mark .level-success> 8,5 3×12
                  <message-time> 19,2 62×18 "01:03:10"
                  <message-text> 89,1 40×20 "Saved"
                  <message-detail-text> 137,11 565×0
                <message-row> 0,22 710×22
                  <message-mark .level-warning> 8,5 3×12
                  <message-time> 19,2 62×18 "01:02:05"
                  <message-text> 89,1 165×20 "Texture cache is 90% full"
                  <message-detail-text> 262,11 440×0
                <message-row> 0,44 710×22
                  <message-mark .level-error> 8,5 3×12
                  <message-time> 19,2 62×18 "01:01:01"
                  <message-text> 89,1 115×20 "Could not import"
                  <message-detail-text> 212,1 490×20 "wood.png …"
              <scrollbar .size-md .variant-default .vertical> 700,0 10×182
              <scrollbar .horizontal .size-md .variant-default> 0,172 8×10
          <message-log-detail .size-md .variant-default> 0,223 710×96
            <scroll-content> 0,1 710×99
              <message-detail-heading> 9,7 692×22 "Could not import"
              <message-detail-meta> 9,32 692×19 "Error · 01:01:01"
              <message-detail-body> 9,54 692×38 "wood.png
        The second line of a long diagnostic."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×95
            <scrollbar .horizontal .size-md .variant-default> 0,86 710×10
        """;

    const string ChosenFlags = """
        <message-log .size-md .variant-default> State=Hover
        <button .size-sm .variant-subtle> Label="Clear All"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid
        <virtualizing-panel .size-md .variant-default> State=Hover
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content .virtual-content> State=Hover
        <message-row> State=Hover, Checked
        <message-detail-text> State=Hover
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string FilteredTree = """
        <message-log .size-md .variant-default> 3,3 710×319
          <message-log-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 75×28
              <label> 10,4 55×20 "Clear All"
            <search-box .empty .size-md .variant-default> 85,5 465×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 554,4 150×32
              <select-field> 10,5 112×22 "Successes"
              <icon .size-md .variant-default> 128,10 12×12
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×22
                <message-row> 0,0 710×22
                  <message-mark .level-success> 8,5 3×12
                  <message-time> 19,2 62×18 "01:03:10"
                  <message-text> 89,1 40×20 "Saved"
                  <message-detail-text> 137,11 565×0
                <message-row .parked> 0,0 0×0
                  <message-mark .level-warning> 0,0 0×0
                  <message-time> 0,0 0×0 "01:02:05"
                  <message-text> 0,0 0×0 "Texture cache is 90% full"
                  <message-detail-text> 0,0 0×0
                <message-row .parked> 0,0 0×0
                  <message-mark .level-error> 0,0 0×0
                  <message-time> 0,0 0×0 "01:01:01"
                  <message-text> 0,0 0×0 "Could not import"
                  <message-detail-text> 0,0 0×0 "wood.png …"
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <message-log-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a message to see the whole of it."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string FilteredFlags = """
        <message-log .size-md .variant-default> State=Hover
        <button .size-sm .variant-subtle> Label="Clear All"
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid Value="Success"
        <virtualizing-panel .size-md .variant-default> State=Hover
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content .virtual-content> State=Hover
        <message-row .parked> State=Hover, Checked
        <message-detail-text> State=Hover
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string SearchedTree = """
        <message-log .size-md .variant-default> 3,3 710×319
          <message-log-toolbar> 0,0 710×45
            <button .size-sm .variant-subtle> 6,4 75×28
              <label> 10,4 55×20 "Clear All"
            <search-box .size-md .variant-default> 85,4 465×36
              <icon .size-md .variant-default> 10,11 14×14
              <field-placeholder> 0,0 0×0 "Filter…"
              <field-text> 30,7 42×22 "wood"
              <icon-button .size-md .variant-subtle> 78,5 26×26
                <icon .size-md .variant-default> 6,6 14×14
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 554,6 150×32
              <select-field> 10,5 112×22 "All Messages"
              <icon .size-md .variant-default> 128,10 12×12
          <virtualizing-panel .size-md .variant-default> 0,45 710×178
            <scroll-view .size-md .variant-default> 0,0 710×178
              <scroll-content .virtual-content> 0,0 710×22
                <message-row> 0,0 710×22
                  <message-mark .level-error> 8,5 3×12
                  <message-time> 19,2 62×18 "01:01:01"
                  <message-text> 89,1 115×20 "Could not import"
                  <message-detail-text> 212,1 490×20 "wood.png …"
                <message-row .parked> 0,0 0×0
                  <message-mark .level-warning> 0,0 0×0
                  <message-time> 0,0 0×0 "01:02:05"
                  <message-text> 0,0 0×0 "Texture cache is 90% full"
                  <message-detail-text> 0,0 0×0
                <message-row .parked> 0,0 0×0
                  <message-mark .level-error> 0,0 0×0
                  <message-time> 0,0 0×0 "01:01:01"
                  <message-text> 0,0 0×0 "Could not import"
                  <message-detail-text> 0,0 0×0 "wood.png …"
              <scrollbar .size-md .variant-default .vertical> 700,0 10×178
              <scrollbar .horizontal .size-md .variant-default> 0,168 8×10
          <message-log-detail .size-md .variant-default> 0,223 710×96
            <scroll-content> 0,1 710×99
              <message-detail-heading> 9,7 692×22 "Could not import"
              <message-detail-meta> 9,32 692×19 "Error · 01:01:01"
              <message-detail-body> 9,54 692×38 "wood.png
        The second line of a long diagnostic."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×95
            <scrollbar .horizontal .size-md .variant-default> 0,86 710×10
        """;

    const string SearchedFlags = """
        <message-log .size-md .variant-default> State=Hover
        <button .size-sm .variant-subtle> Label="Clear All"
        <search-box .size-md .variant-default> State=Valid Value="wood" Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid
        <virtualizing-panel .size-md .variant-default> State=Hover
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content .virtual-content> State=Hover
        <message-row> State=Checked
        <message-row .parked> State=Hover, Checked
        <message-detail-text> State=Hover
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string ClearedTree = """
        <message-log .size-md .variant-default> 3,3 710×319
          <message-log-toolbar> 0,0 710×41
            <button .size-sm .variant-subtle> 6,4 75×28
              <label> 10,4 55×20 "Clear All"
            <search-box .empty .size-md .variant-default> 85,5 465×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 51×22 "Filter…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 554,4 150×32
              <select-field> 10,5 112×22 "All Messages"
              <icon .size-md .variant-default> 128,10 12×12
          <virtualizing-panel .size-md .variant-default> 0,41 710×245
            <scroll-view .size-md .variant-default> 0,0 710×245
              <scroll-content .virtual-content> 0,0 710×0
                <message-row .parked> 0,0 0×0
                  <message-mark .level-success> 0,0 0×0
                  <message-time> 0,0 0×0 "01:03:10"
                  <message-text> 0,0 0×0 "Saved"
                  <message-detail-text> 0,0 0×0
                <message-row .parked> 0,0 0×0
                  <message-mark .level-warning> 0,0 0×0
                  <message-time> 0,0 0×0 "01:02:05"
                  <message-text> 0,0 0×0 "Texture cache is 90% full"
                  <message-detail-text> 0,0 0×0
                <message-row .parked> 0,0 0×0
                  <message-mark .level-error> 0,0 0×0
                  <message-time> 0,0 0×0 "01:01:01"
                  <message-text> 0,0 0×0 "Could not import"
                  <message-detail-text> 0,0 0×0 "wood.png …"
              <scrollbar .size-md .variant-default .vertical> 700,0 10×245
              <scrollbar .horizontal .size-md .variant-default> 0,235 8×10
          <message-log-detail .empty .size-md .variant-default> 0,286 710×33
            <scroll-content> 0,1 710×32
              <text .size-md .variant-default> 9,5 692×22 "Select a message to see the whole of it."
            <scrollbar .size-md .variant-default .vertical> 700,1 10×32
            <scrollbar .horizontal .size-md .variant-default> 0,23 710×10
        """;

    const string ClearedFlags = """
        <message-log .size-md .variant-default> State=Hover, FocusWithin
        <message-log-toolbar> State=Hover, FocusWithin
        <button .size-sm .variant-subtle> State=Hover, Focus, FocusWithin Label="Clear All"
        <label> State=Hover
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid
        <message-row .parked> State=Checked
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;
}
