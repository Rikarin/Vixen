// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Vixen.Editor.Debugger;
using Vixen.Editor.Profiler;
using Vixen.Editor.Testing;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Null;
using Vixen.Ui;
using Vixen.Ui.Composition;
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
    /// <summary>The panels E4 registers, and the one doc 16's diagnostics section asked for.</summary>
    public static TheoryData<string> Panels => [
        "profiler",
        "gpu",
        "memory",
        "statistics",
        "frame-debugger",
        "remote-inspector",
        "devices",
        "network"
    ];

    /// <summary>The verbs that were declared-and-disabled until E4 built the panels behind them.</summary>
    public static TheoryData<string> Commands => [
        "tools.profiler",
        "tools.gpu",
        "tools.frame-debugger",
        "tools.memory",
        "tools.statistics",
        "tools.remote-inspector",
        "tools.network",
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
    /// <remarks>
    ///     ⚠ <b>The sentence is asserted and not merely its presence</b>
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1208">#1208</a>). Nothing in this tree
    ///     sets <c>FrameCaptureSource</c> and nothing can until a Vulkan command-stream hook exists —
    ///     the Null backend's recorder is the engine's only recording path — so this state is every
    ///     editor Vixen has ever built. What must not happen is that it starts reading as an editor
    ///     that <em>could</em> capture and did not bother.
    /// </remarks>
    [Fact]
    public void The_frame_debugger_says_why_it_cannot_capture() {
        using var session = EditorSession.Start();

        var view = Find<FrameDebuggerView>(session, "frame-debugger");

        Assert.Null(view.Source);
        Assert.True(view.CaptureButton.Disabled);
        Assert.NotNull(view.Unavailable);
        Assert.Contains("records into a real command buffer", view.Unavailable, StringComparison.Ordinal);
    }

    /// <summary>A capture source the host sets after start-up reaches the frame debugger.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument #1208 asks for, and the arrangement that could not see the
    ///         defect.</b> The panel's own suite sets <c>view.Source</c> itself
    ///         (<c>PortedPanelTests</c>), which asserts the panel and says nothing about whether
    ///         anything ever hands it one — and nothing did: the module read the property once, in a
    ///         factory, and no host in this tree writes it. A test that only opens the panel is
    ///         satisfied by a seam that has been cut.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Set <em>after</em> the panel is open, deliberately.</b> A host acquires a capture
    ///         path when it acquires a device, which is several frames after start-up and after a
    ///         restored layout has already opened this panel — so the only assignment that could ever
    ///         be useful is a late one. It was a no-op until the module started pushing, which is
    ///         exactly the bug <c>InspectorEndpoint</c> had.
    ///     </para>
    ///     <para>
    ///         And the capture is stepped rather than merely accepted: <c>Take</c> runs the delegate
    ///         through <c>NullFrameCapture</c>'s translation, so what is asserted is a draw the panel
    ///         can select rather than a non-null field.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_capture_source_set_after_start_up_reaches_the_frame_debugger() {
        using var session = EditorSession.Start();

        var view = Find<FrameDebuggerView>(session, "frame-debugger");

        Assert.Null(view.Source);
        Assert.True(view.CaptureButton.Disabled);

        RecordedCommand[] stream = [
            new(RecordedCommandKind.BeginRenderPass, 0, 1, 1, Text: "Main"),
            new(RecordedCommandKind.BindPipeline, 1, 7),
            new(RecordedCommandKind.Draw, 2, 3, 1, 0),
            new(RecordedCommandKind.EndRenderPass, 3)
        ];

        session.Editor.FrameCaptureSource = () => NullFrameCapture.From(stream, "Editor frame");
        session.Frames(2);

        Assert.NotNull(view.Source);
        Assert.Null(view.Unavailable);
        Assert.False(view.CaptureButton.Disabled);

        view.Take();

        Assert.Equal("Editor frame", view.Capture.Name);
        Assert.Equal(4, view.Capture.Commands.Count);

        // One draw, and the panel's stepping index is what makes it one press rather than forty.
        Assert.Equal(2, Assert.Single(view.Capture.Work));
    }

    /// <summary>
    ///     ⚠ And so does the network panel. A bare editor is running no session, so there is no
    ///     <c>BandwidthLedger</c> to read — and a table of zeroes would read as a game sending
    ///     nothing, which is the bug somebody would have opened the panel to find.
    /// </summary>
    [Fact]
    public void The_network_panel_says_there_is_no_ledger_rather_than_showing_zeroes() {
        using var session = EditorSession.Start();

        var view = Built<NetworkView>(session, "network");

        Assert.Contains(Descendants(view.Root), element => element.Tag == "empty-state");
        Assert.DoesNotContain(Descendants(view.Root), element => element.Tag == "network-row");
    }

    /// <summary>
    ///     ⚠ And the graph says the session is not running rather than that nobody wired one — which
    ///     is the assertion that the module wired the delegate at all. The two states produce the
    ///     same numbers, so the sentence is the only thing that tells them apart, and a panel opened
    ///     with the line missing is a panel the module forgot.
    /// </summary>
    [Fact]
    public void The_network_panel_is_pointed_at_a_session_even_when_there_is_none() {
        using var session = EditorSession.Start();

        var view = Built<NetworkView>(session, "network");

        session.Frames(2);

        var lines = Descendants(view.Root)
            .Where(element => element.Tag == "network-status")
            .Select(element => string.Concat(Descendants(element).Select(part => part.Text ?? string.Empty)))
            .ToArray();

        Assert.Contains(lines, line => line.Contains("No session running", StringComparison.Ordinal));
        Assert.DoesNotContain(Descendants(view.Root), element => element.Tag == "network-lane");
    }

    [Fact]
    public void The_statistics_panel_counts_the_scene_it_is_pointed_at() {
        using var session = EditorSession.Start();

        var view = Built<StatisticsView>(session, "statistics");

        Assert.NotNull(view.Statistics);

        var entities = Assert.Single(view.Statistics.Rows, row => row.Label == "Entities");
        Assert.Equal(session.Scene.Entities.Count(), entities.Value);
    }

    /// <summary>The statistics panel counts behaviours, and says which store it counted.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>BehaviorStore.Population</c> had one reader and it was a command-line verb</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1216">#1216</a>) — one that counts
    ///         what a scene <em>authors</em>, which is zero in all fourteen committed
    ///         <c>.vxscene</c> files because every behaviour instance in the samples is attached from
    ///         code. The number doc 04's authoring rule is about is a run-time one, so the place it
    ///         has to be visible is the editor while a level is running.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both stores are asserted, and the row's sentence is what tells them apart.</b> The
    ///         two counts happen to agree here — a play session takes the authored behaviours over —
    ///         and would not in a level whose code attaches a hundred more. A panel that showed the
    ///         authored number while a session was running would be answering a different question in
    ///         the same units, which is the failure the CLI verb refuses to make and the one an
    ///         assertion on the count alone cannot see.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_statistics_panel_counts_the_behaviours_and_says_which_store_it_counted() {
        SceneBehaviorRegistry.Register<Drifter>();

        using var session = EditorSession.Start();

        for (var index = 0; index < 3; index++) {
            session.Scene.Behaviors.Add(
                session.Scene.Add("Drifting" + index, LocalTransform.Identity),
                new Drifter()
            );
        }

        var view = Built<StatisticsView>(session, "statistics");

        view.Take();

        var authored = Assert.Single(view.Statistics!.Rows, row => row.Label == "Behaviours · Drifter");

        Assert.Equal(3, authored.Value);
        Assert.Equal(200, authored.Budget);
        Assert.Contains("authored", authored.Detail!, StringComparison.Ordinal);

        session.Run("play.play");
        session.Frames(4);

        view.Take();

        var live = Assert.Single(view.Statistics!.Rows, row => row.Label == "Behaviours · Drifter");

        Assert.Equal(3, live.Value);

        // ⚠ The enabled count, which is only honest inside a session: the bucket's enabled prefix is
        // a property of the loop, so a store that has never run a lifecycle drain reports every
        // behaviour disabled. Four frames is what puts these three through one.
        Assert.Equal("3 of them enabled, in this play session", live.Detail);
    }

    [Fact]
    public void The_memory_panel_has_a_reading_as_soon_as_it_opens() {
        using var session = EditorSession.Start();

        var view = Built<MemoryView>(session, "memory");

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

    /// <summary>An inspector endpoint a host sets after start-up reaches the device grid.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The end a "no consumer" claim about <c>DeviceEntry.Endpoint</c> could not see.</b>
    ///         The reader is <c>DeviceManagerView.vxml</c>'s Endpoint column and a view's
    ///         <c>&lt;code&gt;</c> block is production C#, so a <c>*.cs</c>-only grep finds the
    ///         declaration, the clear-on-unreachable, and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is set <em>after</em> the session is running on purpose.</b>
    ///         <c>DiagnosticsModule.InspectorEndpoint</c> was an auto-property read once in
    ///         <c>Activate</c>: assigning it afterwards changed nothing, which is what its own remark
    ///         called setting it too late. A player's port is known when the player starts, so the
    ///         only assignment that could ever be useful is this one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_endpoint_set_after_start_up_reaches_the_device_grid() {
        using var session = EditorSession.Start();

        var before = Find<DeviceManagerView>(session, "devices");

        // False first: nothing in this tree launches a player, so the column reads nothing until a
        // host says otherwise. `play.mode-standalone` is the producer, and it is declared planned.
        Assert.All(before.Devices.Items.Cast<DeviceEntry>(), device => Assert.Null(device.Endpoint));

        session.Editor.InspectorEndpoint = "127.0.0.1:34567";
        session.Frames(2);

        Assert.Contains(
            before.Devices.Items.Cast<DeviceEntry>(),
            device => string.Equals(device.Endpoint, "127.0.0.1:34567", StringComparison.Ordinal)
        );
    }

    static T Find<T>(EditorSession session, string panel) where T : UiElement {
        session.Open(panel);
        session.Frames(2);

        return Descendants(session.Document.Root).OfType<T>().Single();
    }

    /// <summary>The same, for a panel written in <c>.vxml</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A second finder rather than a wider one, because a component is not in the element
    ///     tree at all.</b> Doc 36 § F7's first wave made the memory and statistics panels markup, and
    ///     a markup <c>Component</c> builds elements without being one, so
    ///     <c>Descendants(…).OfType&lt;T&gt;()</c> cannot see it however the constraint is relaxed.
    ///     What links the two is <see cref="UiDocument.ComponentAt" />: every component registers
    ///     itself against the host element it drew into, which is the element the walk *does* find.
    ///
    ///     ⚠ And this is the cost <c>@inherits</c> exists to avoid, kept rather than removed. Wave 1b
    ///     ported two panels whose callers hold them as elements, and neither this finder nor any
    ///     assertion in <c>Vixen.Editor.AssetEditors.Tests</c> had to change for them. These two
    ///     panels have no public parts, so <c>Component</c> is still the right base and this is still
    ///     how a test reaches one.
    /// </remarks>
    static T Built<T>(EditorSession session, string panel) where T : Component {
        session.Open(panel);
        session.Frames(2);

        return Descendants(session.Document.Root)
            .Select(session.Document.ComponentAt)
            .OfType<T>()
            .Single();
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
