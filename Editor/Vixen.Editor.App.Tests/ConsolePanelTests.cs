// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The console in the editor rather than the console on its own.</summary>
/// <remarks>
///     ⚠ <b>The panel worked before any of this and showed nothing, which is the failure worth a
///     test.</b> The editor built no log sink at all — <c>RingBufferSink</c> is on in every game
///     because <c>VixenApp</c> makes one, and the editor is not built by that host — so a console
///     over it would have been a perfectly good panel over an empty ring. What these assert is that
///     something the editor does reaches a row.
/// </remarks>
public class ConsolePanelTests {
    [Fact]
    public void What_the_editor_says_reaches_the_console() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");

        var view = Console(fixture);

        fixture.Shell.Notifications.Show("saved Main.vxscene");
        fixture.Frames(2);

        // A notification is the editor deciding something is worth saying; a toast says it for four
        // seconds, and after that the console is the only place it exists.
        Assert.Contains(Rows(view), text => text.Contains("saved Main.vxscene", StringComparison.Ordinal));
    }

    [Fact]
    public void An_error_notification_arrives_as_an_error_row_with_its_detail() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");

        var view = Console(fixture);

        fixture.Shell.Notifications.Error("Could not save the scene", "the disk is full");
        fixture.Frames(2);

        Assert.Equal(1, view.Model?.Errors);
        Assert.Contains(Rows(view), text => text.Contains("the disk is full", StringComparison.Ordinal));
    }

    [Fact]
    public void The_buffer_survives_the_panel_being_closed_and_reopened() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");
        fixture.Shell.Notifications.Show("before");
        fixture.Frames(2);

        fixture.Shell.Workspace.Close("console");
        fixture.Frames(2);

        fixture.Open("console");

        // ⚠ A panel's factory runs again when it is reopened, and a model made in it would start at
        // the sink's current end — so closing the console would empty it, silently, for ever.
        Assert.Contains(Rows(Console(fixture)), text => text.Contains("before", StringComparison.Ordinal));
    }

    [Fact]
    public void Clearing_the_console_from_the_menu_empties_it() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");
        fixture.Shell.Notifications.Show("something");
        fixture.Frames(2);

        Assert.True(fixture.Shell.Commands.Execute("view.clear-console"));
        fixture.Frames(2);

        Assert.Equal(0, Console(fixture).Model?.Count);
    }

    [Fact]
    public void Clear_on_play_is_one_setting_the_menu_ticks_and_the_panel_toggles() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");

        var view = Console(fixture);
        var command = fixture.Shell.Commands["play.clear-console"];

        Assert.NotNull(command);
        Assert.False(command.IsChecked);

        Assert.True(fixture.Shell.Commands.Execute("play.clear-console"));

        // Two writers to one setting is how a menu tick and a panel's toggle come to disagree.
        Assert.True(view.ClearsOnPlay);
        Assert.True(command.IsChecked);
    }

    [Fact]
    public void Entering_play_mode_empties_the_console_when_it_was_asked_to() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");

        var view = Console(fixture);

        view.ClearsOnPlay = true;
        fixture.Shell.Notifications.Show("from before play");
        fixture.Frames(2);

        Assert.True(fixture.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);

        var rows = Rows(view).ToList();

        Assert.DoesNotContain(rows, text => text.Contains("from before play", StringComparison.Ordinal));

        // And the line saying what play mode does is the first thing in the emptied console rather
        // than the last thing before it was emptied.
        Assert.Contains(rows, text => text.Contains("discarded", StringComparison.Ordinal));
    }

    [Fact]
    public void Clicking_a_console_row_takes_the_focus_out_of_the_outliner() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        Assert.Equal("scene", fixture.Shell.Context);

        fixture.Open("console");
        fixture.Click(Console(fixture));

        // Leaving a context is as meaningful as entering one: a Delete pressed here must not delete
        // the entity that is still selected in the tree.
        Assert.Equal("console", fixture.Shell.Context);
        Assert.False(fixture.Shell.Commands.CanExecute("edit.delete"));
    }

    /// <summary>Two messages longer than the message column each stay inside their own row.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every row is a pool slot of one fixed height</b> — <c>VirtualizingPanel</c> places
    ///         row <i>n</i> at <i>n</i> × <c>RowHeight</c> and sets its height to match — so a message
    ///         that wraps is taller than its slot, is centred on it by <c>align-items: center</c>, and
    ///         draws over the rows above and below. Two wrapped warnings in a row were unreadable in
    ///         #1275's before-captures (#1391). The console keeps one line per record and ends a long
    ///         one in an ellipsis; the whole record is in the detail pane.
    ///     </para>
    ///     <para>
    ///         Asked of the boxes rather than of the text: each message's box inside its row's, and
    ///         the two messages' boxes apart. Red against a sheet whose <c>console-message</c> may
    ///         wrap, because the first message's box is then three lines tall in a 22 px row.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_message_longer_than_its_column_stays_inside_its_own_row() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");

        var view = Console(fixture);
        var sentence = string.Join(" ", Enumerable.Repeat("a message far longer than the console's message column", 8));

        fixture.Shell.Notifications.Show("first " + sentence);
        fixture.Shell.Notifications.Show("second " + sentence);
        fixture.Frames(2);

        var shown = view.List.Rows
            .Where(row => !row.HasClass("parked"))
            .Select(row => (Row: row, Message: row.Children.First(cell => cell.Tag == "console-message")))
            .Where(pair => pair.Message.Text?.Contains("far longer", StringComparison.Ordinal) == true)
            .OrderBy(pair => pair.Row.AbsoluteTop)
            .ToList();

        Assert.Equal(2, shown.Count);

        foreach (var (row, message) in shown) {
            Assert.True(
                message.AbsoluteTop >= row.AbsoluteTop - 0.5f
                && message.AbsoluteTop + message.Height <= row.AbsoluteTop + row.Height + 0.5f,
                $"a {message.Height:0.#} px message spills out of its {row.Height:0.#} px row "
                + $"({message.AbsoluteTop:0.#}–{message.AbsoluteTop + message.Height:0.#} in {row.AbsoluteTop:0.#}–{row.AbsoluteTop + row.Height:0.#})."
            );

            Assert.True(
                message.AbsoluteLeft + message.Width <= row.AbsoluteLeft + row.Width + 0.5f,
                "a message wider than its row pushes the row past the list."
            );
        }

        var (above, below) = (shown[0].Message, shown[1].Message);

        Assert.True(
            above.AbsoluteTop + above.Height <= below.AbsoluteTop + 0.5f,
            "the two messages' boxes overlap, so one is drawn over the other."
        );
    }

    static ConsoleView Console(EditorSession fixture) =>
        Find<ConsoleView>(fixture.Document.Root) ?? throw new InvalidOperationException("the console is not open");

    /// <summary>What every realised row says, joined per row.</summary>
    static IEnumerable<string> Rows(ConsoleView view) =>
        view.List.Rows
            .Where(row => !row.HasClass("parked"))
            .Select(row => string.Join(" ", row.Children.Select(cell => cell.Text ?? string.Empty)));

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
