// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Diagnostics;
using Vixen.Core;

namespace Vixen.Ai.Ecs;

/// <summary>An entity that decides what to do.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A handle and a few numbers, and nothing else — that is the rule this component
///         exists to keep.</b> Everything an agent thinks with is in its memory block and its
///         blackboard, both of which the system owns and neither of which is here. A component that
///         grew a list, a template or a plan would be a component whose size depended on its content,
///         which is the thing an archetype ECS cannot have; it would also be per-agent state living
///         somewhere a thousand agents' worth of it has to be copied on every archetype change.
///     </para>
///     <para>
///         <b><c>[Component]</c> without <c>[DataContract]</c>, deliberately.</b> A scene cannot
///         place one of these, because <see cref="Memory" /> and <see cref="ScheduleIndex" /> are the
///         system's own bookkeeping and a saved copy of them would name a block that does not exist
///         in the process that loads it. The scene-placeable form is the authored one — an asset
///         reference and a planner kind — and it arrives with the behaviour-tree asset in P2.
///     </para>
///     <para>
///         <b>Nothing here is <c>[Replicated]</c>, and nothing ever will be.</b> AI runs on the
///         authority: a client planning from an interest-filtered, interpolated view of the world
///         would reach different conclusions, and reconciling two planners is not a thing anybody has
///         made work. What crosses the wire is what the agent's actions <i>write</i> — a destination,
///         a move intent, an animation state — all of which are replicated by their own subsystems.
///     </para>
/// </remarks>
[Component]
public struct AiAgent {
    /// <summary>What decides. <see cref="AiPlanner.None" /> runs <see cref="Action" /> and nothing else.</summary>
    public AiPlanner Planner;

    /// <summary>
    ///     Which action it runs, when nothing is choosing for it. An index into the world's
    ///     <see cref="AgentActionRegistry" />.
    /// </summary>
    /// <remarks>
    ///     P0's whole planner, and still the right thing for an agent that does one job — a turret, a
    ///     door, a scripted extra. A utility set and a GOAP plan choose one of these every time they
    ///     decide, which is the arrangement doc 37 § D2 exists to make possible.
    /// </remarks>
    public ushort Action;

    /// <summary>Which asset its planner runs. An index into the system's library for that planner.</summary>
    /// <remarks>
    ///     ⚠ <b>One field for all three planners, not one per planner.</b> A behaviour tree, a utility
    ///     set and a GOAP domain are three libraries, but an agent runs exactly one of them and which
    ///     one is what <see cref="Planner" /> says — so three fields would be two that are always
    ///     meaningless, in a component whose whole rule is that it is a handle and a few numbers.
    /// </remarks>
    public ushort Asset;

    /// <summary>Its state for that action. Owned by <c>AiSystem</c>.</summary>
    /// <remarks>
    ///     Null for a tree agent: a tree's block is sized by its template and owned by the instance
    ///     the system keeps beside the agent's blackboard, which is the same arrangement and one
    ///     level up.
    /// </remarks>
    public AgentMemoryHandle Memory;

    /// <summary>Its slot with the governor, and the index every tie breaks on. Owned by <c>AiSystem</c>.</summary>
    /// <remarks>
    ///     ⚠ Assigned on join and stable for the entity's life, <i>not</i> derived from where the
    ///     entity sits in a chunk. Chunk order changes whenever anything is added or removed
    ///     anywhere, so a schedule keyed on it would reshuffle itself on an unrelated spawn — and
    ///     doc 37 § D18 needs the schedule to be a pure function of the tick and this number.
    /// </remarks>
    public int ScheduleIndex;

    /// <summary>Its random stream. Assigned from the entity on join, so a replay reproduces it.</summary>
    public uint Seed;

    /// <summary>How the current action is getting on.</summary>
    public ActionStatus Status;

    /// <summary>Whether <see cref="IAgentAction.Start" /> has been called for the current action.</summary>
    public bool Started;

    /// <summary>Whether this agent thinks at all. A stunned or scripted agent sets it false.</summary>
    public bool Enabled;

    /// <summary>Seconds elapsed since this agent last ticked, waiting to be spent.</summary>
    /// <remarks>
    ///     ⚠ <b>Why an action is given its own delta rather than the frame's.</b> Under a governor an
    ///     agent updated one tick in four would otherwise run every timer at a quarter speed, and it
    ///     would do it silently — a <c>Wait(2 s)</c> that takes eight looks like a design decision
    ///     until somebody measures it. Accumulating here costs one float add per agent per frame,
    ///     which is nothing next to a decision, and it is what makes a governed agent behave like an
    ///     ungoverned one that reacts a little later.
    /// </remarks>
    public float Accumulated;

    /// <summary>An agent that runs one action and has not joined a system yet.</summary>
    /// <param name="action">Which action to run.</param>
    /// <returns>The component.</returns>
    public static AiAgent Running(ushort action) => new() {
        Planner = AiPlanner.None,
        Action = action,
        Memory = AgentMemoryHandle.Null,
        ScheduleIndex = -1,
        Status = ActionStatus.Running,
        Enabled = true
    };

    /// <summary>An agent that runs a behaviour tree and has not joined a system yet.</summary>
    /// <param name="tree">Its index in the system's <see cref="BehaviorTreeLibrary" />.</param>
    /// <returns>The component.</returns>
    public static AiAgent Thinking(int tree) => new() {
        Planner = AiPlanner.BehaviorTree,
        Asset = (ushort)tree,
        Memory = AgentMemoryHandle.Null,
        ScheduleIndex = -1,
        Status = ActionStatus.Running,
        Enabled = true
    };

    /// <summary>An agent that scores a utility set and has not joined a system yet.</summary>
    /// <param name="set">Its index in the system's <see cref="UtilitySetLibrary" />.</param>
    /// <returns>The component.</returns>
    public static AiAgent Scoring(int set) => new() {
        Planner = AiPlanner.Utility,
        Asset = (ushort)set,
        Memory = AgentMemoryHandle.Null,
        ScheduleIndex = -1,
        Status = ActionStatus.Running,
        Enabled = true
    };
}
