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

    /// <summary>What a translator's template for this toolset holds.</summary>
    public static IReadOnlyList<StringId> All { get; } = [.. Commands.All];
}
