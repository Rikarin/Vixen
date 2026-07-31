// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>What the console shows, and what it costs to show it.</summary>
/// <remarks>
///     ⚠ <b>Doc 20's warning about the console is a performance claim, so one of these is a
///     performance test.</b> "A game logging per frame into a panel that keeps strings is a leak with
///     a UI" — the assertion that makes that not happen is that a hundred thousand lines produce a
///     bounded buffer and about thirty elements, and nothing else in the suite would notice if that
///     stopped being true.
/// </remarks>
public class ConsoleTests : IDisposable {
    readonly RingBufferSink sink = new(1024) { MinimumLevel = LogLevel.Trace };

    public void Dispose() {
        sink.Dispose();
        GC.SuppressFinalize(this);
    }

    ConsoleModel Model(int capacity = ConsoleModel.DefaultCapacity) => new(sink, capacity);

    /// <summary>
    ///     ⚠ Through the raw <c>ILogger.Log</c> rather than an extension, because the analyzers ban
    ///     a varying message template outside a <c>[LoggerMessage]</c> method — and a test whose
    ///     lines all read the same could not tell one row from another.
    /// </summary>
    void Log(LogLevel level, string category, string message) =>
        sink.CreateLogger(category).Log(level, default, message, null, static (state, _) => state);

    [Fact]
    public void Nothing_logged_before_the_console_opened_is_replayed_into_it() {
        Log(LogLevel.Information, "Editor", "an hour ago");

        var model = Model();
        model.Pull();

        // A panel that took a visible moment to open and showed a screenful of history nobody asked
        // for is what starting from zero would produce.
        Assert.Equal(0, model.Count);
    }

    [Fact]
    public void A_pull_takes_what_arrived_and_says_whether_anything_did() {
        var model = Model();

        Assert.False(model.Pull());

        Log(LogLevel.Information, "Editor", "hello");

        Assert.True(model.Pull());
        Assert.Equal(1, model.Count);
        Assert.Equal("hello", model[0].Record.Message);

        Assert.False(model.Pull());
    }

    [Fact]
    public void The_verbose_stream_is_off_until_it_is_asked_for() {
        var model = Model();

        Log(LogLevel.Debug, "Editor", "chatty");
        Log(LogLevel.Information, "Editor", "useful");
        model.Pull();

        Assert.Equal(1, model.Count);
        Assert.Equal("useful", model[0].Record.Message);

        model.Levels = ConsoleLevels.All;

        Assert.Equal(2, model.Count);
    }

    [Fact]
    public void The_badges_count_what_arrived_rather_than_what_is_shown() {
        var model = Model();

        Log(LogLevel.Error, "Editor", "bad");
        Log(LogLevel.Warning, "Editor", "iffy");
        Log(LogLevel.Warning, "Editor", "also iffy");
        Log(LogLevel.Information, "Editor", "fine");

        model.Pull();
        model.Levels = ConsoleLevels.Error;

        // ⚠ The question somebody clicks a badge to ask is "are there warnings", and a count that
        // went to zero because warnings are hidden answers the opposite.
        Assert.Equal(1, model.Count);
        Assert.Equal(1, model.Errors);
        Assert.Equal(2, model.Warnings);
        Assert.Equal(1, model.Infos);
    }

    [Fact]
    public void Critical_reads_as_an_error_and_trace_as_verbose() {
        var model = Model();

        Log(LogLevel.Critical, "Editor", "worst");
        Log(LogLevel.Trace, "Editor", "noise");
        model.Pull();

        Assert.Equal(1, model.Errors);
        Assert.Equal(1, model.Verbose);
    }

    [Fact]
    public void Search_matches_the_message_and_the_category_and_ignores_case() {
        var model = Model();

        Log(LogLevel.Information, "Vixen.Assets", "imported wood.png");
        Log(LogLevel.Information, "Vixen.Scene", "saved Main.vxscene");
        model.Pull();

        model.Search = "WOOD";
        Assert.Equal(1, model.Count);

        model.Search = "assets";
        Assert.Equal(1, model.Count);
        Assert.Equal("Vixen.Assets", model[0].Record.Category);

        model.Search = "   ";
        Assert.Equal(2, model.Count);
    }

    [Fact]
    public void A_category_filter_shows_one_category_and_the_picker_lists_them_all() {
        var model = Model();

        Log(LogLevel.Information, "Vixen.Assets", "one");
        Log(LogLevel.Information, "Vixen.Scene", "two");
        Log(LogLevel.Information, "Vixen.Assets", "three");
        model.Pull();

        Assert.Equal(["Vixen.Assets", "Vixen.Scene"], model.Categories);

        model.Category = "Vixen.Assets";
        Assert.Equal(2, model.Count);

        model.Category = null;
        Assert.Equal(3, model.Count);
    }

    [Fact]
    public void Collapsing_folds_identical_lines_wherever_they_are() {
        var model = Model();

        Log(LogLevel.Warning, "Editor", "missing material");
        Log(LogLevel.Information, "Editor", "something else");
        Log(LogLevel.Warning, "Editor", "missing material");
        Log(LogLevel.Warning, "Editor", "missing material");

        model.Pull();
        Assert.Equal(4, model.Count);

        model.Collapse = true;

        // ⚠ Identical anywhere, not merely adjacent: a frame loop logging the same warning
        // interleaved with three others is the case the feature exists for, and folding only runs
        // would fold none of it.
        Assert.Equal(2, model.Count);
        Assert.Equal(3, model[0].Repeats);
        Assert.Equal(1, model[1].Repeats);
    }

    [Fact]
    public void A_collapsed_row_shows_the_newest_of_what_it_stands_for() {
        var model = Model();

        model.Collapse = true;

        Log(LogLevel.Warning, "Editor", "still happening");
        model.Pull();

        var first = model[0].Record;

        Log(LogLevel.Warning, "Editor", "still happening");
        model.Pull();

        // "Is this still happening" is what a collapsed line is read for, and a row frozen at the
        // first occurrence's timestamp answers the opposite question.
        Assert.Equal(1, model.Count);
        Assert.Equal(2, model[0].Repeats);
        Assert.NotSame(first, model[0].Record);
    }

    [Fact]
    public void Clearing_empties_the_ring_as_well_as_the_panel() {
        var model = Model();

        Log(LogLevel.Error, "Editor", "bad");
        model.Pull();

        model.Clear();

        Assert.Equal(0, model.Count);
        Assert.Equal(0, model.Errors);
        Assert.Empty(model.Categories);

        // ⚠ The sink too: a Clear that emptied only the panel would leave the crash reporter's ring
        // holding what the user believes they discarded.
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Logging_after_a_clear_still_arrives() {
        var model = Model();

        Log(LogLevel.Error, "Editor", "before");
        model.Pull();
        model.Clear();

        Log(LogLevel.Error, "Editor", "after");

        // The sequence number has to keep going up across a clear. Deriving it from the ring's own
        // counts would make it drop, and every record until it caught up would be skipped.
        Assert.True(model.Pull());
        Assert.Equal(1, model.Count);
        Assert.Equal("after", model[0].Record.Message);
    }

    [Fact]
    public void A_reader_that_falls_behind_the_ring_loses_the_difference_and_keeps_going() {
        var model = Model();

        for (var index = 0; index < sink.Capacity * 2; index++) {
            Log(LogLevel.Information, "Editor", "line " + index);
        }

        Assert.True(model.Pull());

        // The ring is the bound, and what survived is its tail. `DroppedCount` is how a reader
        // notices, which is why the model surfaces it.
        Assert.Equal(sink.Capacity, model.Count);
        Assert.True(model.Dropped > 0);
        Assert.Equal("line " + ((sink.Capacity * 2) - 1), model[model.Count - 1].Record.Message);
    }

    [Fact]
    public void The_buffer_is_bounded_however_much_is_logged() {
        var model = Model(capacity: 128);

        for (var index = 0; index < 5_000; index++) {
            Log(LogLevel.Information, "Editor", "line " + index);
            model.Pull();
        }

        // ⚠ This is the leak doc 20 names, asserted. A console that kept every line would hold five
        // thousand records here and a hundred thousand after a minute of a game logging per frame.
        Assert.True(model.Count <= 128, $"the console kept {model.Count} rows for a capacity of 128");
        Assert.Equal("line 4999", model[model.Count - 1].Record.Message);
    }
}

/// <summary>The console's chrome, which is the half that has rows and buttons in it.</summary>
public class ConsoleViewTests : IDisposable {
    readonly UiDocument document = new(900f, 500f);
    readonly RingBufferSink sink = new(1024) { MinimumLevel = LogLevel.Trace };
    readonly ConsoleView view;

    public ConsoleViewTests() {
        // All three, in order, as `EditorShell` installs them: the console's rules are written
        // against tokens the two below it declare.
        ControlTheme.Install(document);
        Vixen.Ui.Controls.Advanced.AdvancedTheme.Install(document);
        EditorTheme.Install(document);

        view = document.Root.Add<ConsoleView>();
        view.Show(new ConsoleModel(sink));
    }

    public void Dispose() {
        document.Dispose();
        sink.Dispose();

        GC.SuppressFinalize(this);
    }

    void Log(LogLevel level, string message, Exception? failure = null) =>
        sink.CreateLogger("Vixen.Editor.Tests").Log(level, default, message, failure, static (state, _) => state);

    void Frame() {
        view.Tick();
        document.Update();
    }

    /// <summary>Clicks the middle of an element the way the platform layer would.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>Dispatch</c> rather than by raising an event on the row.</b> The rows used
    ///     to listen for <c>ClickEvent</c>, which only a <c>Control</c> ever raises — so every test
    ///     here passed while no click a user could make ever reached them. A test that hands the
    ///     document a press and a release asks the question the user is asking.
    /// </remarks>
    void Click(UiElement element, int count = 1) {
        var x = element.AbsoluteLeft + element.Width / 2f;
        var y = element.AbsoluteTop + element.Height / 2f;

        for (var tap = 0; tap < count; tap++) {
            document.Dispatch(new PointerEvent { X = x, Y = y, Action = PointerAction.Moved, Timestamp = clock });
            document.Dispatch(
                new PointerEvent {
                    X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary, Timestamp = clock
                }
            );

            document.Dispatch(
                new PointerEvent {
                    X = x, Y = y, Action = PointerAction.Released, Button = PointerButton.Primary, Timestamp = clock
                }
            );

            // Inside the double-tap window, so that two of these are a double-click rather than two
            // singles — which is the difference the activation test turns on.
            clock += TimeSpan.FromMilliseconds(20);
        }

        Frame();
    }

    TimeSpan clock;

    [Fact]
    public void A_row_shows_the_time_the_category_and_the_message() {
        Log(LogLevel.Error, "it went wrong");
        Frame();

        var row = view.List.RowOf(0);

        Assert.NotNull(row);

        // The category column is the last segment of the type name: a column showing
        // `Vixen.Editor.Assets.Content.ContentPipeline` pushes the message off the panel.
        Assert.Equal("Tests", row.Children[2].Text);
        Assert.Equal("it went wrong", row.Children[3].Text);
        Assert.True(row.Children[0].HasClass("level-error"));
    }

    [Fact]
    public void A_multi_line_message_is_one_row_and_the_rest_is_in_the_detail_pane() {
        Log(LogLevel.Error, "first line" + Environment.NewLine + "second line");
        Frame();

        // ⚠ Virtualisation needs every row the same height — row 40 000 is at 40 000 × height — so a
        // message that wrapped to four lines would put every row after it in the wrong place.
        Assert.Equal("first line …", view.List.RowOf(0)?.Children[3].Text);
    }

    [Fact]
    public void Clicking_a_row_puts_the_whole_record_and_its_stack_in_the_detail_pane() {
        Log(LogLevel.Error, "it went wrong", new InvalidOperationException("because of this"));
        Frame();

        var row = view.List.RowOf(0);

        Assert.NotNull(row);
        Click(row);

        Assert.Same(view.Selected, view.Model![0].Record);
        Assert.True((row.State & ElementState.Checked) != 0, "the clicked row does not show as selected");

        var text = string.Join(" ", Texts(view.Detail));

        Assert.Contains("it went wrong", text, StringComparison.Ordinal);
        Assert.Contains("because of this", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_double_click_asks_the_host_to_open_the_source() {
        LogRecord? activated = null;

        view.Activated += (_, record) => activated = record;

        Log(LogLevel.Error, "it went wrong");
        Frame();

        var row = view.List.RowOf(0);

        Assert.NotNull(row);
        Click(row, 2);

        Assert.Equal("it went wrong", activated?.Message);
    }

    [Fact]
    public void The_level_buttons_carry_the_counts_and_toggle_the_stream() {
        Log(LogLevel.Error, "one");
        Log(LogLevel.Warning, "two");
        Log(LogLevel.Warning, "three");
        Frame();

        var buttons = view.Toolbar.Children.OfType<ToggleButton>().Where(button => button.HasClass("console-level")).ToList();
        var warnings = Assert.Single(buttons, button => button.HasClass("level-warning"));

        Assert.Equal("2", warnings.Label);
        Assert.True(warnings.IsChecked);

        warnings.IsChecked = false;
        Frame();

        Assert.Equal(1, view.Model?.Count);

        // Still two: the badge counts what arrived.
        Assert.Equal("2", warnings.Label);
    }

    [Fact]
    public void A_hundred_thousand_lines_are_about_thirty_elements() {
        for (var index = 0; index < 100_000; index++) {
            Log(LogLevel.Information, "line " + index);
        }

        Frame();
        Frame();

        // ⚠ Doc 20's "it must not allocate per line", as an assertion. The pool is sized to the
        // viewport plus overscan and nothing else; a console that realised a row per record would
        // have a thousand elements here and be unusable long before a hundred thousand.
        Assert.True(view.List.Rows.Count < 64, $"the console realised {view.List.Rows.Count} rows");
    }

    /// <summary>
    ///     ⚠ Following the tail is a mode the user leaves by scrolling, not a checkbox — a console
    ///     that kept snapping back would be unreadable while anything was being logged, and one that
    ///     never followed would need scrolling after every line.
    /// </summary>
    [Fact]
    public void The_newest_line_stays_on_screen_until_somebody_scrolls_up() {
        for (var index = 0; index < 200; index++) {
            Log(LogLevel.Information, "line " + index);
        }

        Frame();
        Frame();
        Frame();

        Assert.NotNull(view.List.RowOf(199));

        // Scrolling up leaves the mode, and the tail stops chasing.
        view.List.Scroller.ScrollTop = 0f;
        Frame();

        Log(LogLevel.Information, "line 200");
        Frame();
        Frame();

        Assert.Equal(0f, view.List.Scroller.ScrollTop);
        Assert.NotNull(view.List.RowOf(0));
    }

    [Fact]
    public void The_category_picker_grows_as_categories_appear_and_keeps_the_choice() {
        Log(LogLevel.Information, "one");
        Frame();

        sink.CreateLogger("Vixen.Other").Log(LogLevel.Information, default, "two", null, static (state, _) => state);
        Frame();

        var values = view.Categories.Options.Select(option => option.Value).ToList();

        Assert.Contains("Vixen.Editor.Tests", values);
        Assert.Contains("Vixen.Other", values);

        // The empty option is "all of them", and it has to be reachable — a filter you can enter and
        // not leave is what a picker without it would be.
        Assert.Contains(string.Empty, values);
    }

    static IEnumerable<string> Texts(UiElement element) {
        if (element.Text is { Length: > 0 } text) {
            yield return text;
        }

        foreach (var child in element.Children) {
            foreach (var found in Texts(child)) {
                yield return found;
            }
        }
    }
}
