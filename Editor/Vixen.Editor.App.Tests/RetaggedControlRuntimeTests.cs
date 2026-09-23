// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Diagnostics;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>No control in the editor, with every panel open, has been renamed out of its own rule.</summary>
/// <remarks>
///     <para>
///         <b>The run-time half of <c>RetaggedControlTests</c>, over the editor as it is built.</b> That
///         census reads committed literal tags — <c>Add&lt;T&gt;("…")</c>, <c>tag="…"</c> — and this
///         reads the 7010 event <c>UiDocument</c> logs about whatever actually ran: a tag held in a
///         variable, a panel a plugin contributes, a control a view builds in a loop. Every panel the
///         shell and the editor register is opened, because a panel nobody opened built nothing to
///         report on (#1327).
///     </para>
///     <para>
///         ⚠ <b>Read from the editor's log ring and not from <c>UiDocument.Refusals</c></b>, because
///         7010 is deliberately not a refusal: <c>HotReloadHost</c> rolls back a saved sheet that adds
///         to that list, and deleting a rule that restated a control's declarations is a legitimate
///         edit. The ring is what the Console panel shows, so it is also where a person would see it.
///     </para>
///     <para>
///         ⚠ <b>Red before the viewport's two readouts restated <c>display</c></b>:
///         <c>viewport-stats</c> and <c>viewport-readout</c> are <c>TextBlock</c>s under tags of their
///         own, so <c>text { display: inline }</c> never reached them and each start of the editor
///         logged a 7010 for both.
///     </para>
/// </remarks>
public class RetaggedControlRuntimeTests {
    [Fact]
    public void With_every_panel_open_no_control_has_lost_its_own_rule() {
        using var fixture = EditorSession.Start();

        var opened = 0;

        foreach (var descriptor in fixture.Shell.Workspace.Panels.ToList()) {
            fixture.Open(descriptor.Id);
            opened++;
        }

        fixture.Frames(2);

        // The instrument: a sweep that opened nothing reports nothing.
        Assert.True(opened >= 10, $"only {opened} panels were registered, so this saw almost none of the editor.");

        var renamed = Sink(fixture)
            .Snapshot()
            .Where(record => record.EventId.Id == 7010)
            .Select(record => record.Message)
            .ToList();

        Assert.True(
            renamed.Count == 0,
            "These controls are under a tag of their own and have none of what their own tag's rules "
            + "declare. Restate the declarations on the new tag's rule (Rikarin/Vixen#1327):\n  "
            + string.Join("\n  ", renamed)
        );
    }

    /// <summary>The editor's log ring, which the console reads and the shell's document logs into.</summary>
    static RingBufferSink Sink(EditorSession fixture) {
        var field = typeof(EditorApplication).GetField("log", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw fixture.Fail("EditorApplication has no `log` field for the console to read");

        return ((EditorLog)field.GetValue(fixture.Editor)!).Sink;
    }
}
