// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Ecs;
using Vixen.Editor.Debugger;
using Vixen.Editor.Diagnostics;
using Vixen.Editor.Profiler;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Net.Transport.Local;
using Vixen.Platform;
using Vixen.Rendering;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>What the editor can say about itself: the report, and the scopes its own loop is in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seven panels are not here any more — see <c>DiagnosticsModule</c>.</b> Pointing
///         the profiler, the GPU timeline and the statistics readout at a project, a scene and a
///         device is a joining job, and doc 36 § P3 moved it into an assembly of its own. What stays
///         is what is genuinely the <i>application</i>'s: a report that aggregates the project, the
///         open scene, the log ring and the last capture, and the profiling keys the editor's own
///         frame loop is measured in.
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
    /// <summary>The diagnostics module, which owns the panels and the sampling.</summary>
    /// <remarks>
    ///     ⚠ <b>Held because the host has to reach it.</b> The graphics device, the resolved GPU
    ///     frame and the frame-capture source are all things only a host with a window and a device
    ///     can supply, and they arrive several frames after the module has activated. This is the one
    ///     line that moves to the executable when it splits off.
    /// </remarks>
    readonly DiagnosticsModule diagnostics = new();

    /// <summary>The device the GPU timeline reads, when the host has one.</summary>
    /// <remarks>Forwarded to the module, which is what the panel reads.</remarks>
    public IGraphicsDevice? GraphicsDevice {
        get => diagnostics.GraphicsDevice;
        set => diagnostics.GraphicsDevice = value;
    }

    /// <summary>The frame's GPU regions, as the host resolved them.</summary>
    /// <remarks>
    ///     A property rather than a <see cref="GpuProfiler" /> this class owns, because the object
    ///     that records the timestamps has to be the one recording the frame — which is the host, not
    ///     the application.
    /// </remarks>
    public GpuFrame GpuFrame {
        get => diagnostics.GpuFrame;
        set => diagnostics.GpuFrame = value;
    }

    /// <summary>What a frame capture is taken from, when the host can take one.</summary>
    /// <remarks>
    ///     ⚠ <b>Null on a Vulkan host, and that is the honest state.</b> Doc 20's E4 names
    ///     <c>Vixen.Graphics.Null</c>'s recorder as the shape a capture takes, and it is the only
    ///     recording path the engine has — the Vulkan backend records into a command buffer and
    ///     keeps nothing. The panel says so rather than offering a button that would do nothing.
    /// </remarks>
    public Func<FrameCapture>? FrameCaptureSource {
        get => diagnostics.FrameCaptureSource;
        set => diagnostics.FrameCaptureSource = value;
    }

    /// <summary>Where a standalone play-mode process would listen for an inspector.</summary>
    /// <remarks>
    ///     ⚠ <b>Read by the module when it activates</b>, so a host that sets it after start-up sets
    ///     it too late. It is a constructor-time fact about the editor, not a setting.
    /// </remarks>
    public string? InspectorEndpoint {
        get => diagnostics.InspectorEndpoint;
        set => diagnostics.InspectorEndpoint = value;
    }

    /// <summary>The profiler's model, for a test and for the host's own frame samples.</summary>
    internal ProfilerModel Profiling => diagnostics.Profiling;

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

        foreach (var row in MemorySnapshot.Take(new() { Assets = diagnostics.AssetResidency }).Rows) {
            builder.Append("  ")
                .Append(row.Arena)
                .Append(" · ")
                .Append(row.Label)
                .Append(": ")
                .AppendLine(row.IsCount ? row.Bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : MemoryView.Bytes(row.Bytes));
        }

        builder.AppendLine().AppendLine("Statistics").AppendLine("----------");

        var statistics = SceneStatistics.Collect(scene.World, depth: diagnostics.Deepest());

        foreach (var row in statistics.Rows) {
            builder.Append("  ").Append(row.Label).Append(": ")
                .AppendLine(row.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var warning in statistics.Warnings) {
            builder.Append("  ⚠ ").AppendLine(warning);
        }

        builder.AppendLine().AppendLine("Profile").AppendLine("-------");

        if (diagnostics.Profiling.Capture.IsEmpty) {
            builder.AppendLine("  Nothing captured. Open the profiler and press Record.");
        } else {
            foreach (var entry in diagnostics.Profiling.Capture.Summary.Take(ReportedScopes)) {
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

    /// <summary>The one diagnostics verb that is the application's rather than the module's.</summary>
    /// <remarks>
    ///     ⚠ <b>The report stays here because it is the editor reporting on <i>itself</i>.</b> It
    ///     aggregates the project's name, the open scene's counts, the memory arenas, the log ring and
    ///     the last profile capture — four of those five are the application's and only the fifth is
    ///     the diagnostics module's, which it asks for. A report assembled inside the module would be
    ///     a module reaching back for the log and the project title.
    /// </remarks>
    void DiagnosticsCommands() =>
        Verb(
            "tools.diagnostics-report",
            new StringId("editor.command.tools.diagnostics-report", "Generate Diagnostics Report…"),
            CategoryTools,
            WriteReport,
            enabled: () => services.CanPick
        );

    /// <summary>Registers a command that brings a panel up.</summary>
    /// <remarks>
    ///     ⚠ <b>Each one opens its panel rather than doing the thing.</b> "Profiler" is not a verb
    ///     that profiles — it is the verb that shows you the profiler, and the Record button inside it
    ///     is the one that samples. Making the menu line sample directly would be a menu item with no
    ///     visible effect and no way to stop it.
    /// </remarks>
    void Panel(string id, StringId title, string panel, StringId? category = null) =>
        Verb(id, title, category ?? CategoryTools, () => Shell.Workspace.Toggle(panel));

    /// <summary>How many scopes the report lists before it stops being a report.</summary>
    const int ReportedScopes = 40;

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

/// <summary>The scene the editor is showing, which is the prefab when one is being inspected.</summary>
/// <remarks>
///     ⚠ <b>A small adapter rather than <c>EditorApplication</c> implementing the interface.</b> The
///     application is several partials wide and implements nothing; a member called <c>Current</c> on
///     it would be one more name in a class that already has too many.
/// </remarks>
sealed class ShownScene(EditorApplication editor) : IActiveScene {
    /// <inheritdoc />
    public SceneDocument Current => editor.Shown;
}

/// <summary>The view the focused pane draws through.</summary>
/// <remarks>
///     ⚠ <b><see cref="ShownScene" />'s twin, and asked every time for the same reason.</b> The
///     focused pane changes as somebody clicks between four of them, so a panel handed a view at
///     activation would keep answering for whichever pane happened to have focus when it loaded.
/// </remarks>
sealed class ShownView(EditorApplication editor) : IActiveView {
    /// <inheritdoc />
    public RenderView? Current => editor.Viewport?.View;
}

/// <summary>Deploying, for the one kind of device this editor can reach.</summary>
sealed class PlayerDeploy(EditorApplication editor) : IDeviceDeploy {
    /// <inheritdoc />
    public string? Refuse(DeviceEntry device) => editor.DeployRefusal(device);

    /// <inheritdoc />
    public void Deploy(DeviceEntry device) => editor.Deploy(device);
}
