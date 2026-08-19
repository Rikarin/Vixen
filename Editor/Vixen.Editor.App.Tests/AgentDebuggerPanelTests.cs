// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 37 § P7's panel, opened — which nothing could do until it was registered.</summary>
/// <remarks>
///     <para>
///         <b>The gap these tests close is not a bug, and that is what made it survive.</b> P7's six
///         panel tests all build an <c>AgentDebuggerView</c> by hand under a bare UI document and
///         assert what it draws; every one of them passed while
///         <c>docs/overview.md</c> recorded the row as <i>"none of it is reachable"</i>. A test that
///         constructs the thing it is testing cannot notice that nobody else does — so these go
///         through <see cref="EditorSession" />, which starts the real shell with the real module
///         list, and ask the workspace rather than the view.
///     </para>
///     <para>
///         ⚠ <b><see cref="The_agent_debugger_keeps_its_model_across_a_close_and_reopen" /> is the
///         one that would have caught the obvious way to write this wrong.</b> A panel's factory runs
///         again every time it is reopened, so <c>panel.Add&lt;AgentDebuggerView&gt;().Show(new
///         AgentDebugModel())</c> compiles, opens, draws, passes every other test here — and throws
///         away the breakpoints and the selected agent every time somebody closes the tab. Asserting
///         <see cref="Assert.Same(object, object)" /> on the model across a reopen is the only form
///         of that check that fails.
///     </para>
/// </remarks>
public class AgentDebuggerPanelTests {
    [Fact]
    public void The_agent_debugger_panel_opens() {
        using var session = EditorSession.Start();

        session.Open(AssetEditorsModule.AgentDebuggerPanelId);
        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == AssetEditorsModule.AgentDebuggerPanelId);
    }

    /// <summary>⚠ Doc 20's Part F hazard: a factory runs again on reopen.</summary>
    [Fact]
    public void The_agent_debugger_panel_survives_being_closed_and_reopened() {
        using var session = EditorSession.Start();

        session.Open(AssetEditorsModule.AgentDebuggerPanelId);
        session.Frames(2);

        session.Close(AssetEditorsModule.AgentDebuggerPanelId);
        session.Frames(2);

        Assert.DoesNotContain(session.Panels, panel => panel.Id == AssetEditorsModule.AgentDebuggerPanelId);

        session.Open(AssetEditorsModule.AgentDebuggerPanelId);
        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == AssetEditorsModule.AgentDebuggerPanelId);
    }

    /// <summary>Registering a panel registers its verb, and the View menu is built from those.</summary>
    [Fact]
    public void The_agent_debugger_has_a_verb_that_opens_it() {
        using var session = EditorSession.Start();

        var id = EditorShell.PanelCommand(AssetEditorsModule.AgentDebuggerPanelId);
        var command = session.Shell.Commands[id];

        Assert.NotNull(command);
        Assert.False(command.IsUnavailable, $"'{id}' is declared-and-disabled.");
        Assert.True(session.Shell.Commands.Execute(id));

        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == AssetEditorsModule.AgentDebuggerPanelId);
    }

    /// <summary>The panel really is the P7 view, and it really has a model.</summary>
    [Fact]
    public void The_agent_debugger_panel_shows_the_view_and_a_model() {
        using var session = EditorSession.Start();

        var view = session.Control<AgentDebuggerView>(AssetEditorsModule.AgentDebuggerPanelId);

        session.Frames(2);

        Assert.NotNull(view.Model);

        // ⚠ Empty rather than absent. The editor owns no AiSystem, so there is nothing to
        // photograph — and the panel says so by showing no agents rather than by failing to open.
        Assert.Empty(view.Model.Agents);
    }

    /// <summary>
    ///     ⚠ <b>The sabotage test.</b> A model built inside the panel's factory passes everything
    ///     above and loses a debugging session every time the tab is closed.
    /// </summary>
    [Fact]
    public void The_agent_debugger_keeps_its_model_across_a_close_and_reopen() {
        using var session = EditorSession.Start();

        var before = session.Control<AgentDebuggerView>(AssetEditorsModule.AgentDebuggerPanelId).Model;

        Assert.NotNull(before);

        // A breakpoint is the durable thing a debugging session accumulates, and it belongs to the
        // session rather than to any asset — so it is exactly what a rebuilt model would drop.
        Assert.True(before.ToggleBreakpoint(Symbol.Intern("guard"), 3));

        session.Frames(2);
        session.Close(AssetEditorsModule.AgentDebuggerPanelId);
        session.Frames(2);

        var after = session.Control<AgentDebuggerView>(AssetEditorsModule.AgentDebuggerPanelId).Model;

        session.Frames(2);

        Assert.NotNull(after);
        Assert.Same(before, after);
        Assert.True(after.Breakpoints.Contains(Symbol.Intern("guard"), 3));
    }

    /// <summary>
    ///     ⚠ <b>Both buttons were built by <c>OnCreated</c> and connected to nothing.</b> A panel
    ///     nobody registered is also a panel whose controls nobody ever wired, and pressing one
    ///     silently doing nothing is the failure that outlives the registration fix.
    /// </summary>
    [Fact]
    public void Pressing_the_agent_debuggers_buttons_does_not_throw() {
        using var session = EditorSession.Start();

        var view = session.Control<AgentDebuggerView>(AssetEditorsModule.AgentDebuggerPanelId);

        session.Frames(2);

        // Nothing is selected, because the editor steps no agents — so Open reports there is
        // nothing to open and Continue resumes nobody. Neither may throw.
        Assert.False(view.OpenAsset());

        // ⚠ Through the real click path rather than by invoking Clicked. MilestoneE5Tests records
        // why: a test that raises the event itself passes against a button that was never wired to
        // anything and never laid out, which is precisely the defect this panel started with.
        // Closest("button") because the label is what carries the text and the button is what is on
        // top of it — clicking the label's own box is a miss the harness reports rather than hides.
        session.Ui.Get("agent-debugger").Find("label").Contains("Continue").Closest("button").Click();
        session.Frames(2);
        session.Ui.Get("agent-debugger").Find("label").Contains("Open asset").Closest("button").Click();

        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == AssetEditorsModule.AgentDebuggerPanelId);
    }
}
