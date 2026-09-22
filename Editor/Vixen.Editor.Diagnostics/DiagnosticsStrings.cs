// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Diagnostics;

/// <summary>Every word the diagnostics toolset shows, declared once.</summary>
/// <remarks>
///     <para>
///         <b>One family for eight commands that were declared by their own helper.</b>
///         <c>DiagnosticsModule.Panel</c> took an id, a string id and a title and built a
///         <see cref="StringId" /> out of all three, so each of the eight labels was written at a
///         call site rather than declared — and an id assembled there is in no <c>All</c> list, so
///         <c>Strings.Template</c> never exported one of them.
///     </para>
///     <para>
///         ⚠ <b>The helper's third argument was the same string every time.</b> Every call passed
///         <c>"editor.command." + id</c> spelled out in full, which is the family's prefix and its
///         key — so the helper now takes the id alone and indexes this.
///     </para>
/// </remarks>
public static class DiagnosticsStrings {
    /// <summary>Every command the module registers, by its command id.</summary>
    public static StringFamily Commands { get; } = new(
        "editor.command.",
        [
            new("tools.profiler", "Profiler"),
            new("tools.gpu", "GPU Timeline"),
            new("tools.frame-debugger", "Frame Debugger"),
            new("tools.memory", "Memory"),
            new("tools.statistics", "Statistics"),
            new("tools.network", "Network"),
            new("tools.remote-inspector", "Remote Inspector"),
            new("build.deploy", "Deploy…")
        ]
    );

    // ── The names the shell used to keep a copy of ─────────────────────────
    //
    // ⚠ Declared in EditorStrings until #1301, one assembly up from the only code that reads them.
    // The shell cannot name this class (StringContributions.cs says why), so the ids it held for
    // this toolset were a copy the toolset could not own — a translator saw them under the
    // editor's own words, and a toolset shipped out of tree would have had no way to add its own.
    // The ids themselves are unchanged, so a catalogue written against the old table still finds
    // every one of them.

    /// <summary>The <c>Profiler</c> panel.</summary>
    public static StringId PanelProfiler { get; } = new("editor.panel.profiler", "Profiler");

    /// <summary>The <c>GPU</c> panel.</summary>
    public static StringId PanelGpu { get; } = new("editor.panel.gpu", "GPU");

    /// <summary>The <c>Memory</c> panel.</summary>
    public static StringId PanelMemory { get; } = new("editor.panel.memory", "Memory");

    /// <summary>The <c>Statistics</c> panel.</summary>
    public static StringId PanelStatistics { get; } = new("editor.panel.statistics", "Statistics");

    /// <summary>The <c>Network</c> panel.</summary>
    public static StringId PanelNetwork { get; } = new("editor.panel.network", "Network");

    /// <summary>The <c>Frame Debugger</c> panel.</summary>
    public static StringId PanelFrameDebugger { get; } = new("editor.panel.frame-debugger", "Frame Debugger");

    /// <summary>The <c>Remote Inspector</c> panel.</summary>
    public static StringId PanelRemoteInspector { get; } = new("editor.panel.remote-inspector", "Remote Inspector");

    /// <summary>The <c>Devices</c> panel.</summary>
    public static StringId PanelDevices { get; } = new("editor.panel.devices", "Devices");

    /// <summary>What a translator's template for this toolset holds.</summary>
    public static IReadOnlyList<StringId> All { get; } = [
        .. Commands.All,
        PanelProfiler,
        PanelGpu,
        PanelMemory,
        PanelStatistics,
        PanelNetwork,
        PanelFrameDebugger,
        PanelRemoteInspector,
        PanelDevices
    ];
}
