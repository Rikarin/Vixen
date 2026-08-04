// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai;

/// <summary>How an action is getting on.</summary>
public enum ActionStatus : byte {
    /// <summary>Still going. Tick it again next time this agent gets a slot.</summary>
    Running,

    /// <summary>Done, and it worked.</summary>
    Succeeded,

    /// <summary>Done, and it did not. A composite above it decides what that means.</summary>
    Failed
}

/// <summary>Everything an action is given: which agent, in which world, with which data.</summary>
/// <param name="World">The world the agent lives in.</param>
/// <param name="Entity">The agent.</param>
/// <param name="Blackboard">Its own data. One agent owns one board.</param>
/// <param name="Shared">The board its group shares, if it is in one.</param>
/// <param name="Time">The frame's clock. <see cref="GameTime.DeltaSeconds" /> is the frame's step,
/// which is <i>not</i> the action's — see <c>IAgentAction.Tick</c>.</param>
/// <param name="Seed">
///     The agent's own random stream. Keyed on the agent rather than on its slot, so that a replay
///     of the same tick makes the same choice — see <see cref="AgentRandom" />.
/// </param>
/// <remarks>
///     A struct passed by <c>in</c>, so building one per agent per tick costs nothing and nothing is
///     tempted to keep one. It holds no per-action state of any kind; that is the
///     <c>Span&lt;byte&gt;</c>'s job, for the reason <c>IAgentAction</c> gives.
/// </remarks>
public readonly record struct AgentContext(
    World World,
    Entity Entity,
    Blackboard Blackboard,
    SharedBlackboard? Shared,
    GameTime Time,
    uint Seed
) {
    /// <summary>A random number in <c>[0,1)</c> from this agent's stream.</summary>
    /// <param name="salt">What it is for. Two uses of randomness on one agent must not agree.</param>
    /// <returns>The number.</returns>
    public float Random(uint salt) => AgentRandom.Value(Entity, Seed, salt);
}

/// <summary>
///     The one thing all three planners choose. A behaviour-tree task, a utility action and a GOAP
///     action are each this, which is what lets one project write <c>MoveToTask</c> once and get it
///     in all three.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The action does not own its state, and the interface is shaped to make that
///         impossible to get wrong.</b> One action object is shared by every agent running the asset
///         it belongs to, so a field on the action is a field a thousand agents write to. That is
///         the mistake every hand-rolled behaviour tree makes; it is invisible until the second
///         agent exists, and it produces the bug where two guards share one patrol index. Taking the
///         span is the only arrangement in which the mistake cannot be made.
///     </para>
///     <para>
///         The span is a window into the agent's memory block, sized by
///         <c>AgentActionRegistry.Register</c>'s <c>stateSize</c> and zeroed before
///         <see cref="Start" />. Put a struct in it with
///         <c>MemoryMarshal.AsRef&lt;T&gt;(state)</c>; a reference does not go in it, and a node that
///         needs one stores an <see cref="Entity" /> or an <c>AssetId</c>, both of which are values.
///     </para>
///     <para>
///         <b>Implementations must be safe to call from several agents at once.</b> Chunks are
///         stepped in parallel, so an action that touches anything outside its span and its
///         context's own agent is a data race the scheduler cannot see.
///     </para>
/// </remarks>
public interface IAgentAction {
    /// <summary>Begins. Called once before the first <see cref="Tick" />, on a zeroed span.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="state">Its memory for this action.</param>
    void Start(in AgentContext context, Span<byte> state);

    /// <summary>Advances it.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="state">Its memory for this action.</param>
    /// <param name="delta">
    ///     ⚠ Seconds since <i>this agent</i> last ticked, which under a governor is not the frame's
    ///     delta. An action that reaches for <c>context.Time.DeltaSeconds</c> instead runs at a
    ///     quarter speed the moment the population grows past the budget, and does it silently.
    /// </param>
    /// <returns>Whether it is finished, and how.</returns>
    ActionStatus Tick(in AgentContext context, Span<byte> state, float delta);

    /// <summary>Stops it early. The span is zeroed afterwards; this is for what is outside it.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="state">Its memory for this action.</param>
    /// <remarks>
    ///     Called when something above the action decided not to wait — an abort, a re-plan, a
    ///     higher-priority branch. Whatever the action told the rest of the world to do is what has
    ///     to be undone here: a destination cleared, a reservation released, an animation stopped.
    /// </remarks>
    void Abort(in AgentContext context, Span<byte> state);
}
