// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Core;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 37 § Part 5's live tinting, joined — the three <c>Follow</c> methods had no non-test
///     caller and the model they take was held by a module that never handed it to anybody.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both halves were finished, which is why this survived.</b> <c>LiveEditorTests</c>
///         builds each view by hand, calls <c>Follow</c> and asserts the canvas tints;
///         <c>AgentDebuggerPanelTests</c> opens the panel and asserts it has a model. Every one of
///         them passed while opening a behaviour tree in the editor tinted nothing, because no
///         production line joined the two. So these go through <see cref="EditorSession" /> — the
///         real shell, the real module list, an asset opened the way a double-click opens one — and
///         assert on what a person would see.
///     </para>
///     <para>
///         ⚠ <b>The breakpoint badge rather than the active-path tint, and that is deliberate.</b>
///         The editor owns no <c>AiSystem</c>, so <c>AgentDebugModel.Instance</c> is null and nothing
///         is tinted by what an agent is <i>doing</i> — an assertion on the accents would be one that
///         cannot pass in this process whether or not the wiring exists, which is the shape of test
///         this file is here to stop being written. A breakpoint is the half of the model that is
///         real with no agent running: it is set on the panel, it belongs to the debugging session
///         rather than to the file, and <c>BehaviorTreeView.RefreshLive</c> stamps it on the box.
///     </para>
/// </remarks>
public class AgentFollowWiringTests {
    /// <summary>The tree named by the file below, which is what a breakpoint is keyed on.</summary>
    const string TreeName = "Guard";

    /// <summary>A behaviour tree opened in the editor follows the agent debugger's model.</summary>
    [Fact]
    public void A_behaviour_tree_opened_in_the_editor_shows_the_debuggers_breakpoints() {
        using var session = EditorSession.Start();
        var model = Debugger(session);

        Assert.True(model.ToggleBreakpoint(Symbol.Intern(TreeName), 0));

        var view = Open<BehaviorTreeView>(session, $"Assets/{TreeName}.vxbt", string.Empty);

        session.Frames(2);

        Assert.Contains(view.Canvas.Graph.Nodes, node => node.Badge.StartsWith('●'));
    }

    /// <summary>
    ///     ⚠ Following is not a one-shot: a breakpoint set <i>after</i> the editor was opened reaches
    ///     it too.
    /// </summary>
    /// <remarks>
    ///     The obvious way to write this wiring is to tint once at the moment the view joins its
    ///     panel, which passes the test above and leaves the canvas frozen at whatever the debugger
    ///     happened to hold when the tab was opened — the failure that reads most convincingly as
    ///     working. Its first half is also the negative the first test needs: with nothing set, no
    ///     box carries a badge, so "contains a badge" is a predicate that can be false.
    /// </remarks>
    [Fact]
    public void A_breakpoint_set_after_the_editor_is_open_still_reaches_it() {
        using var session = EditorSession.Start();
        var model = Debugger(session);

        var view = Open<BehaviorTreeView>(session, $"Assets/{TreeName}.vxbt", string.Empty);

        session.Frames(2);

        Assert.DoesNotContain(view.Canvas.Graph.Nodes, node => node.Badge.StartsWith('●'));
        Assert.True(model.ToggleBreakpoint(Symbol.Intern(TreeName), 0));

        session.Frames(2);

        Assert.Contains(view.Canvas.Graph.Nodes, node => node.Badge.StartsWith('●'));
    }

    /// <summary>A utility set opened in the editor draws the followed agent's scores.</summary>
    /// <remarks>
    ///     ⚠ <b>The picture is fed in as a <i>remote</i> snapshot</b>, because the editor process has
    ///     no <c>AiSystem</c> to photograph — and a remote capture is deliberately the same picture
    ///     as a local one, which is what makes debugging a dedicated server the same tool. What is
    ///     asserted is the join, not the transport: unfollowed, the bars are the author's typed
    ///     readings and <c>LiveScore</c> answers null.
    /// </remarks>
    [Fact]
    public void A_utility_set_opened_in_the_editor_shows_the_followed_agents_scores() {
        using var session = EditorSession.Start();
        var model = Debugger(session);

        var view = Open<UtilitySetView>(session, "Assets/Mood.vxutility", "name: Mood\n");

        session.Frames(2);

        Assert.Null(view.LiveScore("Flee"));

        var snapshot = new AiAgentSnapshot { Planner = AiPlanner.Utility, Asset = Symbol.Intern("Mood") };

        snapshot.Add(AiDebugRow.Of(AiDebugSection.Doing, "Flee", 0.75f, active: true));
        model.Show(snapshot);

        session.Frames(2);

        Assert.Equal(0.75f, view.LiveScore("Flee"));
    }

    /// <summary>A closed editor stops being followed, so it is not tinted after it is gone.</summary>
    /// <remarks>
    ///     ⚠ <b>Added because neutering the sweep left every other test in this file green.</b> The
    ///     other three all assert on editors that are still open, so a panel that never drops a
    ///     closed one satisfies all of them while re-tinting an element no longer in any document
    ///     and growing its follower list for the life of the session. The observable is the tint
    ///     itself rather than a count: this test is red the moment the sweep stops sweeping, and
    ///     the first assertion is the negative that makes the second one mean something.
    /// </remarks>
    [Fact]
    public void A_closed_editor_is_not_tinted_after_it_is_closed() {
        using var session = EditorSession.Start();
        var model = Debugger(session);

        var view = Open<BehaviorTreeView>(session, $"Assets/{TreeName}.vxbt", string.Empty, out var id);

        session.Frames(2);

        Assert.DoesNotContain(view.Canvas.Graph.Nodes, node => node.Badge.StartsWith('●'));

        session.Close(id);
        session.Frames(2);

        Assert.True(view.IsRemoved);
        Assert.True(model.ToggleBreakpoint(Symbol.Intern(TreeName), 0));

        session.Frames(2);

        Assert.DoesNotContain(view.Canvas.Graph.Nodes, node => node.Badge.StartsWith('●'));
    }

    /// <summary>The debugger's model, which is the one every followed editor has to be handed.</summary>
    static Vixen.Editor.Ai.AgentDebugModel Debugger(EditorSession session) {
        var model = session.Control<AgentDebuggerView>(AssetEditorsModule.AgentDebuggerPanelId).Model;

        Assert.NotNull(model);

        return model;
    }

    /// <summary>Writes an asset and opens it the way a double-click in the project browser does.</summary>
    static T Open<T>(EditorSession session, string relative, string content) where T : UiElement =>
        Open<T>(session, relative, content, out _);

    /// <inheritdoc cref="Open{T}(EditorSession, string, string)" />
    /// <param name="id">The workspace id the editor was opened under, which is what closes it.</param>
    static T Open<T>(EditorSession session, string relative, string content, out string id) where T : UiElement {
        var absolute = session.Project.Paths.Absolute(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);

        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath(relative, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        id = "asset." + entry.Guid;

        return session.Control<T>(id);
    }
}
