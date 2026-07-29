// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What the editor does when it is asked to go, and what the frame says while it is here.</summary>
/// <remarks>
///     ⚠ <b>Doc 20 is blunt about the first half: an editor that loses an afternoon once is one
///     nobody opens again.</b> Every document already knew whether it was dirty; what was missing was
///     the thing that asks. These tests are about the asking, and about the answer that leaves the
///     window open being honoured.
/// </remarks>
public class ClosingTests {
    [Fact]
    public void A_clean_editor_closes_without_being_asked_anything() {
        using var fixture = new EditorFixture();

        fixture.Editor.RequestClose();

        Assert.True(fixture.Editor.IsClosing);
        Assert.False(fixture.Editor.Shell.Dialogs.IsOpen);
    }

    [Fact]
    public void A_dirty_editor_asks_before_it_goes() {
        using var fixture = new EditorFixture();

        Dirty(fixture);
        fixture.Editor.RequestClose();

        // Not yet: the prompt is queued and opens on the next pump, which the frame does.
        Assert.False(fixture.Editor.IsClosing);

        fixture.Frame();

        Assert.True(fixture.Editor.Shell.Dialogs.IsOpen);
        Assert.False(fixture.Editor.IsClosing);
    }

    [Fact]
    public void Backing_out_of_the_prompt_leaves_the_editor_open() {
        using var fixture = new EditorFixture();

        Dirty(fixture);
        fixture.Editor.RequestClose();
        fixture.Frame();

        Press(fixture, "Cancel");
        fixture.Frames(2);

        Assert.False(fixture.Editor.IsClosing);
        Assert.True(fixture.Editor.Scene.IsDirty.Value);
    }

    [Fact]
    public void Discarding_closes_and_leaves_the_file_alone() {
        using var fixture = new EditorFixture();

        Dirty(fixture);
        fixture.Editor.RequestClose();
        fixture.Frame();

        Press(fixture, "Discard");
        fixture.Frames(2);

        Assert.True(fixture.Editor.IsClosing);
        Assert.True(fixture.Editor.Scene.IsDirty.Value);
    }

    [Fact]
    public void Saving_writes_before_it_closes() {
        using var fixture = new EditorFixture();

        Dirty(fixture);
        fixture.Editor.RequestClose();
        fixture.Frame();

        Press(fixture, "Save");
        fixture.Frames(2);

        Assert.True(fixture.Editor.IsClosing);
        Assert.False(fixture.Editor.Scene.IsDirty.Value);
    }

    /// <summary>
    ///     ⚠ Asked once, however many times the button is pressed — a second prompt queued behind the
    ///     first is one the user meets on answering it.
    /// </summary>
    [Fact]
    public void Asking_twice_puts_up_one_prompt() {
        using var fixture = new EditorFixture();

        Dirty(fixture);

        fixture.Editor.RequestClose();
        fixture.Editor.RequestClose();
        fixture.Editor.RequestClose();

        fixture.Frame();

        Assert.True(fixture.Editor.Shell.Dialogs.IsOpen);
        Assert.Equal(0, fixture.Editor.Shell.Dialogs.Pending);
    }

    [Fact]
    public void The_title_names_the_scene_the_project_and_the_engine_and_marks_it_dirty() {
        using var fixture = new EditorFixture();

        var project = fixture.Editor.Shell.Status;

        Assert.Equal($"Main — {project} — Vixen", fixture.Editor.Shell.Title);

        Dirty(fixture);
        fixture.Frame();

        // The one affordance that answers "which window is which" when three projects are open, and
        // the asterisk is the one that answers "did I save that".
        Assert.Equal($"Main* — {project} — Vixen", fixture.Editor.Shell.Title);
    }

    [Fact]
    public void The_status_bar_counts_the_selection_and_reports_the_editors_own_frame_time() {
        using var fixture = new EditorFixture();

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        Assert.Equal(1, fixture.Editor.Shell.SelectionCount?.Invoke());

        var cells = Cells(fixture.Editor.Shell.StatusBar);

        Assert.Contains(cells, text => text == "1 selected");

        // ⚠ The number doc 00's editor-shell performance bar is about. The fixture's frames are a
        // fixed sixteen milliseconds, so the mean is exactly that — which is the assertion that the
        // cell is measuring the frame rather than showing a constant.
        Assert.Equal(16d, fixture.Editor.Shell.FrameTime, 1);
        Assert.Contains(cells, text => text == "16.0 ms");
    }

    static void Dirty(EditorFixture fixture) {
        fixture.Editor.Scene.Create("Dirty", LocalTransform.Identity);
        fixture.Frame();

        Assert.True(fixture.Editor.Scene.IsDirty.Value);
    }

    static void Press(EditorFixture fixture, string label) {
        var dialog = fixture.Editor.Shell.Dialogs.Current
            ?? throw new InvalidOperationException("no dialog is open");

        var button = dialog.Footer.Children.OfType<Button>().FirstOrDefault(candidate => candidate.Label == label)
            ?? throw new InvalidOperationException($"the prompt has no '{label}' button");

        button.Activate();
    }

    static List<string> Cells(UiElement bar) => [.. bar.Children.Select(child => child.Text ?? string.Empty)];
}
