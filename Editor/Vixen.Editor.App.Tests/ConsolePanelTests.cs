// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
        using var fixture = new EditorFixture();

        fixture.Open("console");

        var view = Console(fixture);

        fixture.Editor.Shell.Notifications.Show("saved Main.vxscene");
        fixture.Frames(2);

        // A notification is the editor deciding something is worth saying; a toast says it for four
        // seconds, and after that the console is the only place it exists.
        Assert.Contains(Rows(view), text => text.Contains("saved Main.vxscene", StringComparison.Ordinal));
    }

    [Fact]
    public void An_error_notification_arrives_as_an_error_row_with_its_detail() {
        using var fixture = new EditorFixture();

        fixture.Open("console");

        var view = Console(fixture);

        fixture.Editor.Shell.Notifications.Error("Could not save the scene", "the disk is full");
        fixture.Frames(2);

        Assert.Equal(1, view.Model?.Errors);
        Assert.Contains(Rows(view), text => text.Contains("the disk is full", StringComparison.Ordinal));
    }

    [Fact]
    public void The_buffer_survives_the_panel_being_closed_and_reopened() {
        using var fixture = new EditorFixture();

        fixture.Open("console");
        fixture.Editor.Shell.Notifications.Show("before");
        fixture.Frames(2);

        fixture.Editor.Shell.Workspace.Close("console");
        fixture.Frames(2);

        fixture.Open("console");

        // ⚠ A panel's factory runs again when it is reopened, and a model made in it would start at
        // the sink's current end — so closing the console would empty it, silently, for ever.
        Assert.Contains(Rows(Console(fixture)), text => text.Contains("before", StringComparison.Ordinal));
    }

    [Fact]
    public void Clearing_the_console_from_the_menu_empties_it() {
        using var fixture = new EditorFixture();

        fixture.Open("console");
        fixture.Editor.Shell.Notifications.Show("something");
        fixture.Frames(2);

        Assert.True(fixture.Editor.Shell.Commands.Execute("view.clear-console"));
        fixture.Frames(2);

        Assert.Equal(0, Console(fixture).Model?.Count);
    }

    [Fact]
    public void Clear_on_play_is_one_setting_the_menu_ticks_and_the_panel_toggles() {
        using var fixture = new EditorFixture();

        fixture.Open("console");

        var view = Console(fixture);
        var command = fixture.Editor.Shell.Commands["play.clear-console"];

        Assert.NotNull(command);
        Assert.False(command.IsChecked);

        Assert.True(fixture.Editor.Shell.Commands.Execute("play.clear-console"));

        // Two writers to one setting is how a menu tick and a panel's toggle come to disagree.
        Assert.True(view.ClearsOnPlay);
        Assert.True(command.IsChecked);
    }

    [Fact]
    public void Entering_play_mode_empties_the_console_when_it_was_asked_to() {
        using var fixture = new EditorFixture();

        fixture.Open("console");

        var view = Console(fixture);

        view.ClearsOnPlay = true;
        fixture.Editor.Shell.Notifications.Show("from before play");
        fixture.Frames(2);

        Assert.True(fixture.Editor.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);

        var rows = Rows(view).ToList();

        Assert.DoesNotContain(rows, text => text.Contains("from before play", StringComparison.Ordinal));

        // And the line saying what play mode does is the first thing in the emptied console rather
        // than the last thing before it was emptied.
        Assert.Contains(rows, text => text.Contains("discarded", StringComparison.Ordinal));
    }

    [Fact]
    public void Clicking_a_console_row_takes_the_focus_out_of_the_outliner() {
        using var fixture = new EditorFixture();

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        Assert.Equal("scene", fixture.Editor.Shell.Context);

        fixture.Open("console");
        fixture.Click(Console(fixture));

        // Leaving a context is as meaningful as entering one: a Delete pressed here must not delete
        // the entity that is still selected in the tree.
        Assert.Equal("console", fixture.Editor.Shell.Context);
        Assert.False(fixture.Editor.Shell.Commands.CanExecute("edit.delete"));
    }

    static ConsoleView Console(EditorFixture fixture) =>
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
