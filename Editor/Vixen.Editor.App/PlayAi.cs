// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Ecs;
using Vixen.Editor.SceneView;

namespace Vixen.Editor.App;

/// <summary>The agents an in-editor play session steps, built from what the project declared.</summary>
/// <remarks>
///     <para>
///         <b>#470's producer, and what made it possible is <c>AgentDeclarations</c>.</b> An
///         <c>AiSystem</c> needs an <c>AgentActionRegistry</c> and a <c>BlackboardLayout</c>, and
///         until an assembly could declare its actions the only way to build either was a game's own
///         boot code — which the editor never runs. Now the project's module initialisers have
///         declared them by the time its assembly is loaded, so this is the same two-phase build a
///         game would do: every key, then every action against the layout the keys made.
///     </para>
///     <para>
///         ⚠ <b>Nothing when nothing is declared, and that is honesty rather than economy.</b> An
///         <c>AiSystem</c> over an empty registry would satisfy a project's
///         <c>[GameSystem]</c> that asks for one and then run agents whose every action is missing
///         — a gameplay bug the editor manufactured. Left out, that system is reported by name as
///         one whose services are not here, which is the true sentence.
///     </para>
///     <para>
///         ⚠ <b>Provided to the session and never published</b>, for <c>PlayPhysics</c>' reason: a
///         system made on Play is a dead object after Stop, and <c>PluginServices</c> has no
///         removal. The agent debugger reaches it through <see cref="IActivePlaySession" />. And no
///         <c>[Provides]</c>: nothing sorts after it, the project's declared systems are resolved
///         after every contribution regardless, and a <c>[Provides]</c> this declines to honour
///         when nothing is declared would be reported as broken on every such Play.
///     </para>
///     <para>
///         ⚠ <b>What it does not do: fill <c>Trees</c>, <c>Sets</c> or <c>Domains</c>, or place
///         agents.</b> <c>AiAgent</c> is deliberately not scene-placeable — its remarks defer the
///         authored form to the behaviour-tree asset — so the agents a session runs are the ones a
///         project's own <c>[GameSystem]</c> spawns, handed this system to do it with.
///     </para>
/// </remarks>
sealed class PlayAi : IPlaySystems {
    /// <inheritdoc />
    public void Attach(PlaySession session) {
        ArgumentNullException.ThrowIfNull(session);

        if (AgentDeclarations.Actions.Count == 0) {
            return;
        }

        var layout = AgentDeclarations.BuildLayout();
        var system = new AiSystem(AgentDeclarations.BuildRegistry(layout), layout);

        session.Loop.Add(system);
        session.Provide(system);
        session.Runs("agents");
    }
}
