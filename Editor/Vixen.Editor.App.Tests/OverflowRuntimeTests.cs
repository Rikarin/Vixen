// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Diagnostics;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>No box in the editor, with every panel open, asks to scroll and gets a clip.</summary>
/// <remarks>
///     <para>
///         <b>The run-time half of <c>OverflowLedgerTests</c>, over the editor as it is built.</b> The
///         ledger reads the sheets and the utility classes written in source, and is empty; this reads
///         the 7009 event <c>UiDocument</c> logs for whatever box the running editor actually styled
///         as a scroll container — a class added in a loop, a rule a plugin's sheet brings, a style set
///         from code (#1275).
///     </para>
///     <para>
///         ⚠ <b>Red before this batch's conversions</b>, for the ones that are registered panels: the
///         settings rail and page and the input debug view each logged a 7009 on every open, and the
///         Console showed them. The document views — sprite list, compiled scene, mixer, override grid
///         — open over an asset rather than as a panel, so they are pictured in
///         <c>ScrollingPanelPictureTests</c> instead of swept here.
///     </para>
/// </remarks>
public class OverflowRuntimeTests {
    [Fact]
    public void With_every_panel_open_no_box_asks_to_scroll_and_gets_a_clip() {
        using var fixture = EditorSession.Start();

        var opened = 0;

        foreach (var descriptor in fixture.Shell.Workspace.Panels.ToList()) {
            fixture.Open(descriptor.Id);
            opened++;
        }

        fixture.Frames(2);

        Assert.True(opened >= 10, $"only {opened} panels were registered, so this saw almost none of the editor.");

        var clipped = Sink(fixture)
            .Snapshot()
            .Where(record => record.EventId.Id == 7009)
            .Select(record => record.Message)
            .ToList();

        Assert.True(
            clipped.Count == 0,
            "These boxes declare a scroll container, and in this UI that clips and does not scroll. Put a "
            + "ScrollView there (Rikarin/Vixen#1275):\n  "
            + string.Join("\n  ", clipped)
        );
    }

    /// <summary>The editor's log ring, which the console reads and the shell's document logs into.</summary>
    static RingBufferSink Sink(EditorSession fixture) {
        var field = typeof(EditorApplication).GetField("log", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw fixture.Fail("EditorApplication has no `log` field for the console to read");

        return ((EditorLog)field.GetValue(fixture.Editor)!).Sink;
    }
}
