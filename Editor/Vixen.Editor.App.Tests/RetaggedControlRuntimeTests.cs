// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
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
///         own, so <c>text { display: inline }</c> never reached them. Every start of the editor logged
///         a 7010 for <c>viewport-stats</c>; <c>viewport-readout</c> logged one the first time it was
///         shown.
///     </para>
///     <para>
///         ⚠ <b>So a measurement is taken before the sweep reads the ring.</b> The readout is built
///         with class <c>hidden</c>, and <c>viewport-readout.hidden { display: none }</c> supplies
///         the one property the rename lost — so an idle editor never reports it, and a sweep that
///         only opened panels stayed green with the readout's restated <c>display</c> deleted. What
///         matters is the element people see, which is the one in the middle of a pane mid-gesture.
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

        // Shows the middle-of-the-pane readout, which is hidden on an idle editor and therefore
        // styled by `.hidden` rather than by its own rule until somebody measures or drags.
        ShowReadout(fixture);

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

    /// <summary>Takes a two-point measurement in the scene pane so its readout is on screen.</summary>
    /// <param name="fixture">The editor.</param>
    static void ShowReadout(EditorSession fixture) {
        fixture.Open("scene");
        fixture.Run("scene.measure");

        var pane = fixture.Viewport ?? throw fixture.Fail("the scene panel has no viewport");

        pane.Measure.Add(Vector3.Zero);
        pane.Measure.Add(new Vector3(0f, 0f, 6f));
        fixture.Frames(2);

        // The instrument: a readout still hidden is one whose own rule was never asked about.
        var readouts = Descendants(fixture.Document.Root)
            .OfType<TextBlock>()
            .Where(element => element.Tag == "viewport-readout")
            .ToList();

        Assert.Contains(readouts, readout => !readout.HasClass("hidden") && !string.IsNullOrEmpty(readout.Text));
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    /// <summary>The editor's log ring, which the console reads and the shell's document logs into.</summary>
    static RingBufferSink Sink(EditorSession fixture) {
        var field = typeof(EditorApplication).GetField("log", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw fixture.Fail("EditorApplication has no `log` field for the console to read");

        return ((EditorLog)field.GetValue(fixture.Editor)!).Sink;
    }
}
