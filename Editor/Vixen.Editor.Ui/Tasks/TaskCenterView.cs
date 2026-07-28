// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>One task's row: a title, what it is doing, how far along, and a way to stop it.</summary>
public sealed partial class TaskRow : Control {
    /// <inheritdoc />
    protected override string TagName => "task-row";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The title and the cancel button.</summary>
    /// <remarks>
    ///     ⚠ Named <c>TitleRow</c> rather than <c>Line</c>: <see cref="UiElement.Line" /> is the
    ///     element's own shaped text, and a property hiding it would make an inherited member
    ///     unreachable from a derived class for no reason but a shared word.
    /// </remarks>
    public UiElement TitleRow { get; private set; } = null!;

    /// <summary>What it is called.</summary>
    public UiElement TitlePart { get; private set; } = null!;

    /// <summary>What it is doing.</summary>
    public UiElement StatusPart { get; private set; } = null!;

    /// <summary>How far along it is.</summary>
    public ProgressBar Bar { get; private set; } = null!;

    /// <summary>What stops it.</summary>
    public IconButton CancelButton { get; private set; } = null!;

    /// <summary>Which task it is showing.</summary>
    public BackgroundTask? Task { get; internal set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        TitleRow = Part("task-line");
        TitlePart = TitleRow.Add<UiElement>("task-title");

        CancelButton = TitleRow.Add<IconButton>();
        CancelButton.LeadingIcon.Geometry = ControlIcons.Close;
        CancelButton.Variant = ControlVariant.Subtle;
        CancelButton.Size = ControlSize.Small;
        CancelButton.Label = EditorStrings.TasksCancel.Text;

        Bar = Part<ProgressBar>();
        StatusPart = Part("task-status");
    }
}

/// <summary>The list of what the editor is doing in the background.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Rows are pooled and rebound rather than rebuilt.</b> The list is refreshed every
///         frame — the numbers change every frame — and a view that recreated its elements would
///         hand the style engine a new subtree sixty times a second for the sake of two numbers.
///         It is the tree view's argument at a much smaller scale, and the scale is why the pool is
///         a plain list rather than a virtualiser.
///     </para>
///     <para>
///         <b>It shows only what is running.</b> A finished task's story is told by the notification
///         it raises, and a list that kept its completed entries would need a way to clear them —
///         which is a second history for a thing the notification centre already keeps.
///     </para>
/// </remarks>
public sealed partial class TaskCenterView : Control {
    readonly List<TaskRow> rows = [];

    BackgroundTaskManager? manager;

    /// <inheritdoc />
    protected override string TagName => "task-center";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The heading.</summary>
    public UiElement TitlePart { get; private set; } = null!;

    /// <summary>What it says when nothing is running.</summary>
    public TextBlock EmptyPart { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        TitlePart = Part("task-heading");
        TitlePart.Text = EditorStrings.TasksTitle.Text;

        EmptyPart = Part<TextBlock>();
        EmptyPart.Text = EditorStrings.TasksIdle.Text;

        AddHandler<ClickEvent>(static (element, args) => ((TaskCenterView) element).Chosen(args));
    }

    /// <summary>Shows a manager's tasks.</summary>
    /// <param name="tasks">The manager.</param>
    public void Show(BackgroundTaskManager tasks) {
        ArgumentNullException.ThrowIfNull(tasks);

        manager = tasks;
        Refresh();
    }

    /// <summary>Rebinds the rows to what is running now.</summary>
    public void Refresh() {
        var running = manager?.Tasks ?? [];

        while (rows.Count < running.Count) {
            rows.Add(Part<TaskRow>());
        }

        for (var i = 0; i < rows.Count; i++) {
            var row = rows[i];

            if (i >= running.Count) {
                row.Task = null;
                row.SetStyle("display", "none");

                continue;
            }

            var task = running[i];

            row.Task = task;
            row.SetStyle("display", "flex");
            row.TitlePart.Text = task.Title;
            row.StatusPart.Text = task.Status ?? string.Empty;

            row.Bar.IsIndeterminate = task.IsIndeterminate;
            row.Bar.Value = task.Progress * row.Bar.Maximum;

            // A task that has been asked to stop cannot be asked twice, and the button going quiet
            // is what tells the user the request landed on work that has not noticed yet.
            row.CancelButton.Disabled = task.IsCancellationRequested;
        }

        EmptyPart.SetStyle("display", running.Count == 0 ? "flex" : "none");
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (element is not TaskRow { Task: { } task }) {
                continue;
            }

            task.Cancel();
            Refresh();

            args.Handled = true;
            return;
        }
    }
}
