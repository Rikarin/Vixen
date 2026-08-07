// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     The task centre, which is the editor's first panel written in VXML rather than in C#.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything here is asserted about the element tree, not about the markup.</b> A test
///     that read the <c>.vxml</c> would be testing the compiler, which
///     <c>Vixen.Ui.Markup.Tests</c> already does; what is worth holding this file to is that the
///     panel it produces shows what is running and stops it when asked.
/// </remarks>
public class TaskCenterTests : IDisposable {
    readonly UiDocument document = new(400f, 400f);
    readonly BackgroundTaskManager tasks = new();
    readonly TaskCenter centre;

    public TaskCenterTests() {
        ControlTheme.Install(document);
        EditorTheme.Install(document);

        centre = BuildContext.Build<TaskCenter>(document, document.Root);
        centre.Show(tasks);
    }

    public void Dispose() {
        tasks.CancelAll();
        document.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     The host tag is written out in the markup, because the stylesheet says
    ///     <c>task-center</c> and a name taken from the type would be <c>taskcenter</c>.
    /// </summary>
    [Fact]
    public void The_panel_answers_to_the_tag_its_stylesheet_names() {
        Assert.Equal("task-center", centre.Root.Tag);
        Assert.Equal("task-heading", centre.Root.Children[0].Tag);
    }

    [Fact]
    public void Nothing_running_says_so() {
        Flush();

        Assert.Empty(Rows());
        Assert.Contains(centre.Root.Children, child => child.Tag == "text");
    }

    [Fact]
    public void A_task_gets_a_row_and_losing_it_takes_the_row_away() {
        var task = tasks.Begin("Importing");
        Flush();

        var row = Assert.Single(Rows());
        Assert.Equal("Importing", Text(row, "task-title"));
        Assert.DoesNotContain(centre.Root.Children, child => child.Tag == "text");

        tasks.Complete(task);
        tasks.Pump();
        Flush();

        Assert.Empty(Rows());
    }

    /// <summary>
    ///     The numbers arrive on the manager's own <c>Changed</c>, which <c>Pump</c> raises after it
    ///     applies a batch — not on a per-frame rebind of every row.
    /// </summary>
    [Fact]
    public void A_row_follows_the_task_it_was_built_for() {
        var task = tasks.Begin("Baking");
        Flush();

        var bar = (ProgressBar) Rows()[0].Children.Single(child => child.Tag == "progress-bar");
        Assert.True(bar.IsIndeterminate);

        task.Report(0.25f, "lighting");
        tasks.Pump();
        Flush();

        Assert.False(bar.IsIndeterminate);
        Assert.Equal(0.25f, bar.Value);
        Assert.Equal("lighting", Text(Rows()[0], "task-status"));
    }

    /// <summary>
    ///     A key is the task itself, so a task that is still running keeps the elements it had —
    ///     which is what stops the row under the pointer changing identity as the list moves.
    /// </summary>
    [Fact]
    public void A_surviving_task_keeps_its_row() {
        var first = tasks.Begin("First");
        tasks.Begin("Second");
        Flush();

        var kept = Rows()[1];

        tasks.Complete(first);
        tasks.Pump();
        Flush();

        Assert.Same(kept, Assert.Single(Rows()));
    }

    /// <summary>
    ///     Cancelling is the panel's one verb, and the button going quiet is what says the request
    ///     landed on work that has not noticed yet.
    /// </summary>
    [Fact]
    public void The_cancel_button_asks_the_task_to_stop_and_then_goes_quiet() {
        var task = tasks.Begin("Baking");
        Flush();

        var button = Cancel(Rows()[0]);
        Assert.False(button.Disabled);

        button.Activate();
        Flush();

        Assert.True(task.IsCancellationRequested);
        Assert.True(button.Disabled);
    }

    /// <summary>
    ///     ⚠ <b>The row's handler is the row's.</b> A loop that closed over the wrong item — or a
    ///     view that walked up from the event's source and found the first row rather than the one
    ///     that was pressed — would cancel the wrong task, and with one task in the list neither
    ///     mistake is visible.
    /// </summary>
    [Fact]
    public void Each_rows_button_cancels_that_row() {
        var first = tasks.Begin("First");
        var second = tasks.Begin("Second");
        Flush();

        Cancel(Rows()[1]).Activate();
        Flush();

        Assert.False(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    /// <summary>
    ///     ⚠ <b>And it stops following the manager once its host has left the document.</b> This
    ///     panel writes its <c>@for</c> at the top level, so its loop was always inside the region
    ///     the host owns — what was missing is that nothing ended a component built onto a mount at
    ///     all. Removing the host is now the unmount, so a popover that is thrown away takes its
    ///     subscriptions with it instead of watching the task manager for the life of the editor.
    /// </summary>
    /// <remarks>
    ///     ⚠ Asserted on the scheduler: writing the model queues every effect that read it, and
    ///     assigning to a removed element does not complain, so the elements cannot say.
    /// </remarks>
    [Fact]
    public void A_panel_that_leaves_the_document_stops_following_the_manager() {
        var task = tasks.Begin("Importing");
        Flush();

        Assert.NotEmpty(Rows());
        centre.Root.Remove();
        Flush();

        task.Report(0.5f);
        tasks.Begin("Building");

        Assert.Equal(0, document.Effects.PendingCount);
    }

    void Flush() => document.Effects.Flush();

    IReadOnlyList<UiElement> Rows() => [.. centre.Root.Children.Where(child => child.Tag == "task-row")];

    static IconButton Cancel(UiElement row) =>
        row.Children.Single(child => child.Tag == "task-line")
            .Children.OfType<IconButton>()
            .Single();

    /// <summary>
    ///     What a part says, which is the <c>text</c> element the markup put inside it: an
    ///     interpolation is a text node, and a text node here is an element.
    /// </summary>
    static string Text(UiElement root, string tag) =>
        Find(root, tag).Children.Single(child => child.Tag == "text").Text ?? string.Empty;

    static UiElement Find(UiElement element, string tag) =>
        Found(element, tag) ?? throw new InvalidOperationException($"no '{tag}' under '{element.Tag}'");

    static UiElement? Found(UiElement element, string tag) {
        if (element.Tag == tag) {
            return element;
        }

        foreach (var child in element.Children) {
            if (Found(child, tag) is { } found) {
                return found;
            }
        }

        return null;
    }
}
