// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Ecs;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>#470: Play in the editor steps a project's declared agents, and the agent debugger sees them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Asserted on the panel's model, not on the session.</b> A test that found an
///         <c>AiSystem</c> in the session would pass with the debugger still asking only
///         <c>PluginServices</c> — which is how the panel was dark in the shipping editor while every
///         piece around it was tested. The model listing an agent that exists only inside the play
///         session, and holding the very breakpoint set the session's system runs with, is what a
///         person looking at the panel would see.
///     </para>
///     <para>
///         ⚠ <b>The declaration is made by the test, under its own assembly, and evicted after.</b>
///         That is what a project's module initialiser does, and <c>AgentDeclarations</c> is static,
///         so leaving it behind would put "agents" into every later session's running list.
///     </para>
/// </remarks>
public class PlayAiTests {
    [Fact]
    public void A_play_session_steps_the_declared_agents_and_the_debugger_follows_them() {
        var origin = typeof(PlayAiTests).Assembly;

        try {
            Assert.True(
                AgentDeclarations.DeclareAction("play-ai-test.wait", _ => new WaitTask(60f), WaitTask.StateSize, origin)
            );

            using var session = EditorSession.Start();

            session.Open("scene");

            var model = session.Control<AgentDebuggerView>(AssetEditorsModule.AgentDebuggerPanelId).Model;

            Assert.NotNull(model);

            session.Frames(2);

            Assert.Empty(model.Agents);
            Assert.True(session.Plugins.Services.TryGet<IActivePlaySession>(out var seam));
            Assert.Null(seam!.Current);

            session.Run("play.play");
            session.Frames(2);

            var play = session.Editor.PlayMode.Session;

            Assert.NotNull(play);
            Assert.Same(play, seam.Current);
            Assert.Contains("agents", play.Running);
            Assert.True(play.TryGet<AiSystem>(out var system));

            // An agent that exists only inside the session: Stop restores the snapshot over it.
            var agent = play.World.Create(AiAgent.Running(0));

            session.Frames(3);

            Assert.True(system!.Steps > 0, "the session's loop never stepped the agent system");
            Assert.Contains(agent, model.Agents);
            Assert.Same(model.Breakpoints, system.Breakpoints);

            session.Run("play.stop");
            session.Frames(2);

            Assert.Null(seam.Current);
        } finally {
            AgentDeclarations.Evict(origin);
        }
    }

    /// <summary>And a project that declares nothing gets no agent system, rather than an empty one.</summary>
    /// <remarks>
    ///     The negative the first test needs: "agents" in the running list is a claim that can be false.
    ///     An <c>AiSystem</c> over an empty registry would satisfy a project system that asks for one
    ///     and then run agents whose every action is missing.
    /// </remarks>
    [Fact]
    public void A_project_that_declares_no_actions_gets_no_agent_system() {
        using var session = EditorSession.Start();

        session.Open("scene");
        session.Run("play.play");
        session.Frames(2);

        var play = session.Editor.PlayMode.Session;

        Assert.NotNull(play);
        Assert.DoesNotContain("agents", play.Running);
        Assert.False(play.TryGet<AiSystem>(out _));

        session.Run("play.stop");
        session.Frames(2);
    }
}
