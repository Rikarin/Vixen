// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Ecs;
using Vixen.Editor.Debugger;
using Vixen.Editor.Profiler;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Net.Transport.Local;
using Vixen.Platform;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>Doc 20's E4: the six diagnostics panels, and what they are pointed at.</summary>
/// <remarks>
///     <para>
///         <b>The joining job, in the assembly whose job that is.</b> Neither
///         <c>Vixen.Editor.Profiler</c> nor <c>Vixen.Editor.Debugger</c> knows what a project, a
///         scene or a graphics device is — which is what lets both be tested against a bare
///         <c>UiDocument</c>. Deciding that the statistics panel counts <i>this</i> world, that the
///         GPU timeline reads <i>this</i> device and that the frame debugger captures <i>this</i>
///         frame is this file's.
///     </para>
///     <para>
///         ⚠ <b>Doc 20's "the profiler must be able to profile the editor" is the reason
///         <see cref="EditorKeys" /> exists at all.</b> A source selector is worth nothing if the
///         editor's own frame has no scopes in it, so the four phases of the loop are instrumented
///         here — and they are the four <c>EditorHost</c>'s own remarks name, so the chart matches
///         the sentence a reader has already been told.
///     </para>
///     <para>
///         ⚠ <b>Sampling is off until somebody presses Record.</b> An always-on profiler is the
///         right default for a <i>game</i>, where the interesting thirty seconds are the ones before
///         the crash; an editor left running for a day would fill sixteen thread rings with a day of
///         menu clicks that nobody will ever collect. <c>ProfilerModel.Start</c> turns it on and
///         empties the rings first, which is what makes a capture start where the button was pressed.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>The context id of the diagnostics panels.</summary>
    /// <remarks>
    ///     Declared for the reason <see cref="ConsoleContext" /> is: leaving a context matters as
    ///     much as entering one, so clicking a flame bar has to stop Delete meaning "delete the
    ///     selected entity".
    /// </remarks>
    internal const string DiagnosticsContext = "diagnostics";

    readonly ProfilerModel profiler = new();
    readonly DeviceManager devices = new();

    /// <summary>What the remote inspector talks over, once somebody attaches.</summary>
    /// <remarks>
    ///     ⚠ <b>Made on attach rather than at start-up, and it is a loopback transport.</b> The
    ///     editor cannot open a socket to a phone until there is a device provider that knows how to
    ///     find one — see <c>IDeviceProvider</c>, which has no <c>adb</c> implementation yet — and a
    ///     UDP client pointed at nothing would sit retrying for the life of the editor. Loopback
    ///     against a standalone play-mode process is the case that works today and is the one doc
    ///     20's exit criterion is written against.
    /// </remarks>
    LocalNetwork? inspectorNetwork;
    LocalTransport? inspectorTransport;
    RemoteInspectorClient? remoteInspector;

    /// <summary>The device the GPU timeline reads, when the host has one.</summary>
    /// <remarks>
    ///     Assigned by <c>EditorHost</c> once Vulkan is up, which is several frames after this object
    ///     exists — a headless run never assigns it at all, and the panel says the device cannot be
    ///     timed rather than drawing an empty chart.
    /// </remarks>
    public IGraphicsDevice? GraphicsDevice { get; set; }

    /// <summary>The frame's GPU regions, as the host resolved them.</summary>
    /// <remarks>
    ///     A property rather than a <see cref="GpuProfiler" /> this class owns, because the object
    ///     that records the timestamps has to be the one recording the frame — which is the host, not
    ///     the application.
    /// </remarks>
    public GpuFrame GpuFrame { get; set; } = GpuFrame.Empty;

    /// <summary>What a frame capture is taken from, when the host can take one.</summary>
    /// <remarks>
    ///     ⚠ <b>Null on a Vulkan host, and that is the honest state.</b> Doc 20's E4 names
    ///     <c>Vixen.Graphics.Null</c>'s recorder as the shape a capture takes, and it is the only
    ///     recording path the engine has — the Vulkan backend records into a command buffer and
    ///     keeps nothing. The panel says so rather than offering a button that would do nothing.
    /// </remarks>
    public Func<FrameCapture>? FrameCaptureSource { get; set; }

    /// <summary>Where a standalone play-mode process would listen for an inspector.</summary>
    public string? InspectorEndpoint { get; set; }

    /// <summary>The profiler's model, for a test and for the host's own frame samples.</summary>
    internal ProfilerModel Profiling => profiler;

    void DiagnosticsPanels() {
        ProfilerTheme.Install(Shell.Document);
        DebuggerTheme.Install(Shell.Document);

        // ⚠ Added here rather than in the panel's factory, because a factory runs again every time
        // the panel is reopened — and a second `LocalProfileSource` over the same static rings would
        // mean two readers of a `Collect` that empties them, which is half a capture each.
        profiler.Add(new LocalProfileSource("Editor"));

        devices.Add(new LocalDeviceProvider(InspectorEndpoint));
        devices.Discover();

        Shell.RegisterPanel(
            "profiler",
            new StringId("editor.panel.profiler", "Profiler"),
            panel => {
                Contextual(panel, DiagnosticsContext);

                panel.Add<ProfilerView>().Show(profiler);
            }
        );

        Shell.RegisterPanel(
            "gpu",
            new StringId("editor.panel.gpu", "GPU"),
            panel => {
                Contextual(panel, DiagnosticsContext);

                var timeline = panel.Add<GpuTimelineView>();

                // ⚠ Pulled rather than pushed, and every panel here follows the same rule. A
                // reference to a panel kept on this object outlives the panel — a factory runs again
                // on every reopen — so a frame pushed into the previous one lands on an element that
                // has been removed from the document, which throws the moment it reads its bounds.
                timeline.Unavailable = GpuUnavailable();
                timeline.Source = () => GpuFrame;
            }
        );

        Shell.RegisterPanel(
            "memory",
            new StringId("editor.panel.memory", "Memory"),
            panel => {
                Contextual(panel, DiagnosticsContext);

                var memory = panel.Add<MemoryView>();

                // ⚠ The asset provider is the editor's and the GPU one is not wired. A device
                // reports its heaps through `VK_EXT_memory_budget`, which the Vulkan backend does
                // not query — so the arena is absent rather than shown as zero, which is the
                // difference between "not measured" and "nothing allocated".
                memory.Providers.Assets = AssetResidency;
                memory.Take();
            }
        );

        Shell.RegisterPanel(
            "statistics",
            new StringId("editor.panel.statistics", "Statistics"),
            panel => {
                Contextual(panel, DiagnosticsContext);

                var statistics = panel.Add<StatisticsView>();

                // Whichever scene the inspector is following, so opening a prefab and pressing
                // Refresh counts the prefab rather than the level behind it.
                statistics.Source = () => SceneStatistics.Collect((inspected ?? scene).World, depth: Deepest());
                statistics.Take();
            }
        );

        Shell.RegisterPanel(
            "frame-debugger",
            new StringId("editor.panel.frame-debugger", "Frame Debugger"),
            panel => {
                Contextual(panel, DiagnosticsContext);

                var frames = panel.Add<FrameDebuggerView>();
                frames.Source = FrameCaptureSource;

                frames.Unavailable = FrameCaptureSource is null
                    ? "This host records into a real command buffer, which keeps nothing. A capture "
                    + "needs the recording backend — see NullFrameCapture."
                    : null;
            }
        );

        Shell.RegisterPanel(
            "remote-inspector",
            new StringId("editor.panel.remote-inspector", "Remote Inspector"),
            panel => {
                Contextual(panel, DiagnosticsContext);

                panel.Add<RemoteInspectorView>().Show(Inspector());
            }
        );

        Shell.RegisterPanel(
            "devices",
            new StringId("editor.panel.devices", "Devices"),
            panel => {
                Contextual(panel, DiagnosticsContext);

                var manager = panel.Add<DeviceManagerView>();
                manager.Show(devices);

                // Opening the remote inspector on the device somebody chose, which is the whole of
                // what "attach" means from this panel: the connection is the inspector's.
                manager.AttachRequested += (_, _) => {
                    Shell.Workspace.Toggle("remote-inspector");
                    Inspector().Attach();
                };
            }
        );
    }

    /// <summary>The verbs E4 owns: doc 20's five Tools lines, the GPU one, Deploy, and the report.</summary>
    /// <remarks>
    ///     ⚠ <b>Each one opens its panel rather than doing the thing.</b> "Profiler" is not a verb
    ///     that profiles — it is the verb that shows you the profiler, and the Record button inside
    ///     it is the one that samples. Making the menu line sample directly would be a menu item with
    ///     no visible effect and no way to stop it.
    /// </remarks>
    void DiagnosticsCommands() {
        Panel("tools.profiler", new StringId("editor.command.tools.profiler", "Profiler"), "profiler");
        Panel("tools.gpu", new StringId("editor.command.tools.gpu", "GPU Timeline"), "gpu");

        Panel(
            "tools.frame-debugger",
            new StringId("editor.command.tools.frame-debugger", "Frame Debugger"),
            "frame-debugger"
        );

        Panel("tools.memory", new StringId("editor.command.tools.memory", "Memory"), "memory");
        Panel("tools.statistics", new StringId("editor.command.tools.statistics", "Statistics"), "statistics");

        Panel(
            "tools.remote-inspector",
            new StringId("editor.command.tools.remote-inspector", "Remote Inspector"),
            "remote-inspector"
        );

        // ⚠ On the Build menu, where doc 20's Part C puts it, and it is the same window. A separate
        // "Deploy" dialog that listed the same devices would be a second list to keep in step.
        Panel("build.deploy", new StringId("editor.command.build.deploy", "Deploy…"), "devices", CategoryBuild);

        Verb(
            "tools.diagnostics-report",
            new StringId("editor.command.tools.diagnostics-report", "Generate Diagnostics Report…"),
            CategoryTools,
            WriteReport,
            enabled: () => services.CanPick
        );
    }

    /// <summary>Registers a command that brings a panel up.</summary>
    void Panel(string id, StringId title, string panel, StringId? category = null) =>
        Verb(id, title, category ?? CategoryTools, () => Shell.Workspace.Toggle(panel));

    /// <summary>
    ///     Writes what the editor knows about itself to a file: the log ring, the last capture's
    ///     summary, the memory arenas and the scene's counts.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Doc 20 said this needed "the profiler and the crash reporter", and half of that is
    ///     now true.</b> What a report can carry today is everything E4 built plus the ring the
    ///     console reads; the crash reporter's minidump and undo history are E6's and are named as
    ///     absent in the file rather than left out silently — a report that does not say what it is
    ///     missing is one somebody reads as complete.
    /// </remarks>
    void WriteReport() {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        // ⚠ Composed *before* the picker opens rather than in the continuation. A report is a
        // snapshot of what the editor looked like when somebody asked for it, and a picker is on
        // screen for as long as it takes to choose a folder — during which a background import can
        // change every number in it.
        var report = Report();

        deferred.When(
            dialogs.SaveFileAsync(
                new FileDialogOptions {
                    Title = "Generate Diagnostics Report",
                    InitialDirectory = dataDirectory,
                    SuggestedFileName = "vixen-diagnostics.txt",
                    Filters = [new FileFilter("Text", "txt")]
                }
            ),
            path => {
                if (path is null) {
                    return;
                }

                try {
                    File.WriteAllText(path, report);
                    Shell.Notifications.Success(Path.GetFileName(path) + " written");
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                    Shell.Notifications.Show(
                        "Could not write the report",
                        NotificationSeverity.Error,
                        exception.Message
                    );
                }
            },
            failure => Shell.Notifications.Show(
                "Could not write the report",
                NotificationSeverity.Error,
                failure.Message
            )
        );
    }

    string Report() {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine("Vixen diagnostics report")
            .AppendLine("========================")
            .Append("Project: ").AppendLine(project.Name)
            .Append("Scene: ").AppendLine(scene.Title.Value)
            .AppendLine();

        builder.AppendLine("Memory").AppendLine("------");

        foreach (var row in MemorySnapshot.Take(new() { Assets = AssetResidency }).Rows) {
            builder.Append("  ")
                .Append(row.Arena)
                .Append(" · ")
                .Append(row.Label)
                .Append(": ")
                .AppendLine(row.IsCount ? row.Bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : MemoryView.Bytes(row.Bytes));
        }

        builder.AppendLine().AppendLine("Statistics").AppendLine("----------");

        var statistics = SceneStatistics.Collect(scene.World, depth: Deepest());

        foreach (var row in statistics.Rows) {
            builder.Append("  ").Append(row.Label).Append(": ")
                .AppendLine(row.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var warning in statistics.Warnings) {
            builder.Append("  ⚠ ").AppendLine(warning);
        }

        builder.AppendLine().AppendLine("Profile").AppendLine("-------");

        if (profiler.Capture.IsEmpty) {
            builder.AppendLine("  Nothing captured. Open the profiler and press Record.");
        } else {
            foreach (var entry in profiler.Capture.Summary.Take(ReportedScopes)) {
                builder.Append("  ").Append(entry.Name).Append(": ")
                    .Append(entry.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" ms over ")
                    .Append(entry.Calls.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                    .AppendLine(" call(s)");
            }
        }

        builder.AppendLine().AppendLine("Log").AppendLine("---");

        foreach (var record in log.Sink.Snapshot()) {
            builder.Append("  [").Append(record.Level).Append("] ").Append(record.Category).Append(": ")
                .AppendLine(record.Message);
        }

        builder.AppendLine()
            .AppendLine("Not in this report: a minidump and the undo history, which are milestone E6.");

        return builder.ToString();
    }

    /// <summary>How many scopes the report lists before it stops being a report.</summary>
    const int ReportedScopes = 40;

    /// <summary>The remote inspector's client, made on first use.</summary>
    RemoteInspectorClient Inspector() {
        if (remoteInspector is not null) {
            return remoteInspector;
        }

        inspectorNetwork = new LocalNetwork();
        inspectorTransport = new LocalTransport(inspectorNetwork);

        return remoteInspector = new(inspectorTransport);
    }

    /// <summary>Detaches, and gives back the transport the inspector was talking over.</summary>
    /// <remarks>
    ///     ⚠ <b>The client does not own its transport and says so</b>, because a transport may be a
    ///     listen server the editor is using for something else — so closing it is this class's job,
    ///     as opening it was.
    /// </remarks>
    void DiagnosticsDispose() {
        remoteInspector?.Dispose();
        inspectorTransport?.Dispose();

        remoteInspector = null;
        inspectorTransport = null;
        inspectorNetwork = null;
    }

    /// <summary>Why the GPU timeline has nothing to show, or <see langword="null" />.</summary>
    string? GpuUnavailable() =>
        GraphicsDevice is null
            ? "No graphics device. A headless run has no GPU to time."
            : GraphicsDevice.Features.HasTimestampQueries
                ? null
                : $"'{GraphicsDevice.Adapter.Name}' reports no timestamp queries on its graphics queue, "
                + "so its frames cannot be timed.";

    /// <summary>What the project has loaded, as rows for the memory view.</summary>
    /// <remarks>
    ///     ⚠ <b>Counts rather than bytes, and the difference is what the asset database knows.</b> It
    ///     holds identities and paths; how many bytes a loaded texture occupies is the graphics
    ///     device's answer and nothing here can ask for it. A row showing a made-up size would be
    ///     worse than a row showing a true count.
    /// </remarks>
    IEnumerable<MemoryRow> AssetResidency() {
        yield return new(MemoryArena.Assets, "Indexed assets", project.Assets.Count, IsCount: true);
        yield return new(MemoryArena.Assets, "Open documents", project.Documents.Count, IsCount: true);
    }

    /// <summary>How deep the open scene's hierarchy goes.</summary>
    /// <remarks>
    ///     ⚠ <b>Computed here rather than in <c>SceneStatistics</c>, because the parent relation is
    ///     not the ECS's.</b> <c>Hierarchy</c> is an engine concept over components, and a statistics
    ///     model that reached for it would be a model that cannot count a world with no transforms
    ///     in it — which is every unit test it has.
    /// </remarks>
    int Deepest() {
        var document = inspected ?? scene;
        var deepest = 0;

        foreach (var entity in document.Roots) {
            deepest = Math.Max(deepest, Depth(document.World, entity, 1));
        }

        return deepest;
    }

    static int Depth(World world, Entity entity, int level) {
        var deepest = level;

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            deepest = Math.Max(deepest, Depth(world, child, level + 1));
        }

        return deepest;
    }

    /// <summary>Brings the diagnostics panels up to date, once a frame.</summary>
    void DiagnosticsUpdate(TimeSpan delta) {
        // ⚠ Drained every frame while a capture is running, whether or not the panel is open. The
        // rings overwrite, so a five-second capture of a busy thread would otherwise be the last
        // sixty milliseconds of it with the rest gone and nothing saying so.
        profiler.Tick();

        // ⚠ No panel is touched from here, and that is deliberate. Every one of them pulls from a
        // delegate or subscribes to a model, so closing a tab cannot leave this method pushing into
        // an element that has been removed from the document.
        remoteInspector?.Poll(delta);
    }

    /// <summary>The scopes the editor's own loop is measured in.</summary>
    /// <remarks>
    ///     ⚠ <b>Four, and they are <c>EditorHost</c>'s own four.</b> Its remarks name the loop as
    ///     "pump the platform's events into the document, run the layout and draw passes, turn the
    ///     draw lists into geometry, and record that geometry into a frame" — so a chart whose bars
    ///     are called something else would be a chart a reader has to translate. The fifth is this
    ///     application's own update, which is the one that is neither chrome nor GPU.
    /// </remarks>
    internal static class EditorKeys {
        /// <summary>The whole frame.</summary>
        public static readonly ProfilingKey Frame = ProfilingKey.Register("Editor.Frame");

        /// <summary>Pumping the platform's events into the document.</summary>
        public static readonly ProfilingKey Pump = ProfilingKey.Register("Editor.Pump");

        /// <summary>This application's own per-frame work.</summary>
        public static readonly ProfilingKey Update = ProfilingKey.Register("Editor.Update");

        /// <summary>Layout and draw over the element tree.</summary>
        public static readonly ProfilingKey Document = ProfilingKey.Register("Editor.Document");

        /// <summary>Turning draw lists into vertices.</summary>
        public static readonly ProfilingKey Geometry = ProfilingKey.Register("Editor.Geometry");

        /// <summary>Recording and submitting.</summary>
        public static readonly ProfilingKey Present = ProfilingKey.Register("Editor.Present");
    }
}
