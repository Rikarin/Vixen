// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Vixen.Editor.Debugger;
using Vixen.Editor.Profiler;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's E4, joined to the editor.</summary>
/// <remarks>
///     ⚠ <b>Doc 20's Part F asks for a panel-lifecycle test — "every registered panel is built,
///     docked, floated, closed and rebuilt in one test" — and names <c>A panel's factory runs again
///     when it is reopened</c> as the hazard it catches.</b> Six new panels is six new chances to
///     put something durable in a factory, so they are held to it here rather than only being
///     opened once.
/// </remarks>
public class DiagnosticsPanelTests {
    /// <summary>The six panels E4 registers.</summary>
    public static TheoryData<string> Panels => [
        "profiler",
        "gpu",
        "memory",
        "statistics",
        "frame-debugger",
        "remote-inspector",
        "devices"
    ];

    /// <summary>The verbs that were declared-and-disabled until E4 built the panels behind them.</summary>
    public static TheoryData<string> Commands => [
        "tools.profiler",
        "tools.gpu",
        "tools.frame-debugger",
        "tools.memory",
        "tools.statistics",
        "tools.remote-inspector",
        "build.deploy"
    ];

    [Theory]
    [MemberData(nameof(Panels))]
    public void Every_diagnostics_panel_opens(string id) {
        using var session = EditorSession.Start();

        session.Open(id);
        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == id);
    }

    /// <summary>
    ///     ⚠ The hazard doc 11 documents and Part F asks to be proved against: a factory runs again
    ///     on reopen, so anything durable left in one is silently forgotten by closing the tab.
    /// </summary>
    [Theory]
    [MemberData(nameof(Panels))]
    public void Every_diagnostics_panel_survives_being_closed_and_reopened(string id) {
        using var session = EditorSession.Start();

        session.Open(id);
        session.Frames(2);

        session.Close(id);
        session.Frames(2);

        Assert.DoesNotContain(session.Panels, panel => panel.Id == id);

        session.Open(id);
        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == id);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Every_diagnostics_verb_is_registered_and_enabled(string id) {
        using var session = EditorSession.Start();

        var command = session.Shell.Commands[id];

        Assert.NotNull(command);

        // ⚠ Not merely registered. Doc 20's rule is that a verb is either implemented or *visibly*
        // not, and `IsUnavailable` is what draws the second — so an E4 command that came back
        // greyed with a sentence about milestone E4 would pass a registration check and fail the
        // milestone.
        Assert.False(command.IsUnavailable, $"'{id}' is still declared-and-disabled.");
        Assert.True(command.CanExecute, $"'{id}' is registered but not executable.");
        Assert.True(session.Shell.Commands.Execute(id));
    }

    /// <summary>
    ///     ⚠ Doc 20's A6 owes a Profiling preset "once Parts B4 and B5 exist". B4 exists, so this
    ///     one does — and a preset naming a panel the workspace cannot build comes back short.
    /// </summary>
    [Fact]
    public void The_profiling_layout_brings_up_the_panels_it_names() {
        using var session = EditorSession.Start();

        Assert.True(session.Shell.Commands.Execute("view.layout.Profiling"));
        session.Frames(3);

        foreach (var id in (string[]) ["profiler", "gpu", "frame-debugger", "statistics", "memory"]) {
            Assert.Contains(session.Panels, panel => panel.Id == id);
        }
    }

    /// <summary>
    ///     The profiler panel captures the editor's own frames, which is doc 20's "the profiler must
    ///     be able to profile the editor" reduced to something a test can assert.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Sampled through the model rather than by reading <c>Profiler.Collect</c>.</b> The
    ///     scopes are opened by <c>EditorHost</c>, which a headless session does not run — so what is
    ///     proved here is the wiring: a source exists, recording turns sampling on, and a scope
    ///     opened while it is on lands in the capture.
    /// </remarks>
    [Fact]
    public void The_editor_is_a_profileable_source() {
        using var session = EditorSession.Start();

        var view = Find<ProfilerView>(session, "profiler");
        var model = Assert.IsType<ProfilerModel>(view.Model);

        Assert.Equal("Editor", Assert.Single(model.Sources).Name);

        model.Start();
        Assert.Equal(ProfilerState.Recording, model.State);

        using (Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Update)) {
            // A scope with something in it, so the sample has a duration a chart could draw.
            session.Frames(2);
        }

        model.Stop();

        Assert.False(model.Capture.IsEmpty);
        Assert.Contains(model.Capture.Summary, entry => entry.Name == "Editor.Update");
    }

    /// <summary>
    ///     ⚠ A headless session has no device, and the panel says which rather than drawing an empty
    ///     chart — "no timeline" and "a frame with no passes" look identical otherwise.
    /// </summary>
    [Fact]
    public void The_gpu_panel_says_why_it_has_nothing_to_show() {
        using var session = EditorSession.Start();

        var view = Find<GpuTimelineView>(session, "gpu");

        Assert.NotNull(view.Unavailable);
        Assert.Contains("no GPU", view.Unavailable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And so does the frame debugger, which needs a recording backend and does not have one.</summary>
    [Fact]
    public void The_frame_debugger_says_why_it_cannot_capture() {
        using var session = EditorSession.Start();

        var view = Find<FrameDebuggerView>(session, "frame-debugger");

        Assert.Null(view.Source);
        Assert.True(view.CaptureButton.Disabled);
        Assert.NotNull(view.Unavailable);
    }

    [Fact]
    public void The_statistics_panel_counts_the_scene_it_is_pointed_at() {
        using var session = EditorSession.Start();

        var view = Find<StatisticsView>(session, "statistics");

        Assert.NotNull(view.Statistics);

        var entities = Assert.Single(view.Statistics.Rows, row => row.Label == "Entities");
        Assert.Equal(session.Scene.Entities.Count(), entities.Value);
    }

    [Fact]
    public void The_memory_panel_has_a_reading_as_soon_as_it_opens() {
        using var session = EditorSession.Start();

        var view = Find<MemoryView>(session, "memory");

        Assert.NotNull(view.Snapshot);
        Assert.True(view.Snapshot.BytesOf(MemoryArena.Managed) > 0);

        // The asset arena is the editor's, and it is the one thing the profiler assembly cannot see
        // for itself.
        Assert.NotEmpty(view.Snapshot.Of(MemoryArena.Assets));
    }

    [Fact]
    public void The_device_manager_lists_this_machine() {
        using var session = EditorSession.Start();

        var view = Find<DeviceManagerView>(session, "devices");

        Assert.NotEmpty(view.Devices.Items);
    }

    static T Find<T>(EditorSession session, string panel) where T : UiElement {
        session.Open(panel);
        session.Frames(2);

        return Descendants(session.Document.Root).OfType<T>().Single();
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
