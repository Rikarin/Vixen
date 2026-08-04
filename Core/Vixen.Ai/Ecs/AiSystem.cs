// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Diagnostics;
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;

namespace Vixen.Ai.Ecs;

/// <summary>
///     Joins the entities that have an <see cref="AiAgent" /> to their memory and their blackboard,
///     asks the governor who may think, and steps the ones it named.
/// </summary>
/// <remarks>
///     <para>
///         <b>One system for all three planners.</b> A behaviour-tree step, a utility score and a
///         GOAP plan's current action are three ways of arriving at one <see cref="IAgentAction" />,
///         so there is one join, one governor, one memory pool, one debug record and one place where
///         an agent's delta is worked out. What the planners add is how the action index is chosen;
///         everything around that is here.
///     </para>
///     <para>
///         <b>The blackboard is a side table, not a component.</b> An agent's board is a managed
///         object with observer lists in it, which is not a thing that belongs in a column — and
///         keeping it here is also what makes "one agent owns one blackboard" enforceable rather
///         than merely stated. It is indexed by <see cref="AiAgent.ScheduleIndex" />, which is the
///         same number the governor schedules on, so an agent's data and its turn are one lookup
///         apart.
///     </para>
///     <para>
///         <b>Membership is a query, not a list.</b> An entity that gains the component joins and
///         one that loses it or is destroyed leaves, with nobody calling anything —
///         <c>NavigationSystem</c>'s arrangement, and for its reason. Departure is detected by
///         absence, because an entity can leave a query by being destroyed, by having a component
///         removed, or by moving to another world, and the ECS calls nobody about any of the three.
///     </para>
///     <para>
///         Runs in <see cref="SystemPhase.Update" />, before animation and before the transform pass,
///         so that whatever an action writes this frame is resolved in the same frame.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Update)]
public sealed class AiSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription agents = new QueryDescription().WithAll<AiAgent>();

    readonly List<Blackboard?> blackboards = [];
    readonly List<Entity> owners = [];
    readonly List<long> seen = [];

    // Kept beside the slot as well as on the component, because reaping happens after the entity is
    // gone: by the time absence is noticed there is no component left to read the handle out of, and
    // a block nobody hands back is a leak that only shows up in a game that spawns.
    readonly List<AgentMemoryHandle> rentals = [];

    // The one managed object a tree agent needs, kept beside its board and keyed on the same slot.
    readonly List<BehaviorTreeInstance?> trees = [];
    readonly Stack<int> freeSlots = new();

    long tick;

    /// <summary>Creates the system.</summary>
    /// <param name="actions">Every action its agents may run.</param>
    /// <param name="layout">The shape of each agent's blackboard.</param>
    /// <param name="memory">Where per-agent state comes from, or null for one of its own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="actions" /> or <paramref name="layout" /> is null.</exception>
    public AiSystem(AgentActionRegistry actions, BlackboardLayout layout, AgentMemoryPool? memory = null) {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(layout);

        Actions = actions;
        Layout = layout;
        Memory = memory ?? new AgentMemoryPool();
    }

    /// <summary>The actions its agents may run.</summary>
    public AgentActionRegistry Actions { get; }

    /// <summary>The shape of every agent's blackboard.</summary>
    public BlackboardLayout Layout { get; }

    /// <summary>Where per-agent state comes from.</summary>
    public AgentMemoryPool Memory { get; }

    /// <summary>The trees its agents may run, by index.</summary>
    /// <remarks>
    ///     Filled by whatever compiled them — a test, a spawner, or P2's asset pipeline. An
    ///     <see cref="AiAgent" /> names one by index for the same reason it names an action by index:
    ///     a component is a handle and a few numbers, and never a reference.
    /// </remarks>
    public BehaviorTreeLibrary Trees { get; } = new();

    /// <summary>Who gets to think. Replaceable, because who is worth a tick is a game's decision.</summary>
    public IAgentGovernor Governor { get; set; } = new RoundRobinGovernor();

    /// <summary>The board this world's agents share, if they share one.</summary>
    /// <remarks>
    ///     ⚠ Written in a scope of its own, in a single-threaded phase, and read freely here. The
    ///     system does not open that scope — whatever produces the group's data does, before this
    ///     phase — because a shared board written from inside an agent step is the cross-agent edge
    ///     that makes the whole pass unparallelisable.
    /// </remarks>
    public SharedBlackboard? Shared { get; set; }

    /// <summary>The last decisions, when it is turned on.</summary>
    public AgentDebugRecorder Debug { get; } = new();

    /// <summary>What the governor decided last step.</summary>
    public AgentSchedule LastSchedule { get; private set; }

    /// <summary>How many agents have joined.</summary>
    public int Population { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>NavigationSystem</c> gives: naming a
    ///     component in a generic call is what assigns it an id, and an attribute can only look one
    ///     up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare().Write<AiAgent>().Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Step(context.World, context.Time);

        return dependency;
    }

    /// <summary>Runs one step against a world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or a tool can step the agents without standing up a runner.</remarks>
    public void Step(World world, GameTime time) {
        ArgumentNullException.ThrowIfNull(world);

        Join(world);

        var schedule = Governor.Plan(tick, Population);

        LastSchedule = schedule;
        Advance(world, time, schedule);
        Reap(world);
        tick++;
    }

    /// <summary>An agent's own data, or null if it has not joined.</summary>
    /// <param name="agent">The agent's component.</param>
    /// <returns>Its blackboard.</returns>
    /// <remarks>How a game, an editor panel or a test reads what an agent is thinking with.</remarks>
    public Blackboard? BlackboardOf(in AiAgent agent) =>
        (uint)agent.ScheduleIndex < (uint)blackboards.Count ? blackboards[agent.ScheduleIndex] : null;

    /// <summary>An agent's running tree, or null if it is not running one.</summary>
    /// <param name="agent">The agent's component.</param>
    /// <returns>Its instance.</returns>
    /// <remarks>What the debugger reads the active path off, and what a test asserts against.</remarks>
    public BehaviorTreeInstance? TreeOf(in AiAgent agent) =>
        (uint)agent.ScheduleIndex < (uint)trees.Count ? trees[agent.ScheduleIndex] : null;

    /// <summary>Gives every entity that has not got one a slot, a block and a board.</summary>
    void Join(World world) {
        foreach (var chunk in world.Chunks(agents)) {
            var values = chunk.Values<AiAgent>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                ref var agent = ref values[index];

                // Rejoined rather than assumed live: the handle may have come in on a prefab or a
                // save file, where it names a block that never existed in this run.
                if (Live(in agent, entities[index])) {
                    seen[agent.ScheduleIndex] = tick;

                    continue;
                }

                var slot = freeSlots.Count > 0 ? freeSlots.Pop() : NewSlot();

                agent.ScheduleIndex = slot;
                agent.Seed = AgentRandom.SeedOf(entities[index]);
                agent.Started = false;
                agent.Status = ActionStatus.Running;
                agent.Accumulated = 0f;

                blackboards[slot] ??= new(Layout);
                blackboards[slot]!.Reset();

                if (agent.Planner == AiPlanner.BehaviorTree && agent.Tree < Trees.Count) {
                    // A tree's block is sized by its template, so the instance rents it rather than
                    // the system: the size is not a property of the agent, it is a property of the
                    // asset the agent is running.
                    agent.Memory = AgentMemoryHandle.Null;
                    trees[slot] = Reuse(trees[slot], Trees[agent.Tree]);
                } else {
                    agent.Memory = Memory.Rent(Actions.StateSize(agent.Action));
                    trees[slot] = null;
                }

                owners[slot] = entities[index];
                rentals[slot] = agent.Memory;
                seen[slot] = tick;
                Population++;
            }
        }
    }

    /// <summary>Ticks whichever agents the governor named, and accumulates time for the rest.</summary>
    void Advance(World world, GameTime time, in AgentSchedule schedule) {
        var delta = time.DeltaSeconds;

        foreach (var chunk in world.Chunks(agents)) {
            var values = chunk.Values<AiAgent>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                ref var agent = ref values[index];

                if (!agent.Enabled) {
                    continue;
                }

                // Every agent accumulates every frame, and only the scheduled ones spend it. That is
                // one float add for an agent that is not thinking, against a timer that would
                // otherwise run at the rate of its own slot.
                agent.Accumulated += delta;

                if (!schedule.Includes(agent.ScheduleIndex)) {
                    continue;
                }

                Tick(world, time, entities[index], ref agent);
            }
        }
    }

    void Tick(World world, GameTime time, Entity entity, ref AiAgent agent) {
        var context = new AgentContext(
            world,
            entity,
            blackboards[agent.ScheduleIndex]!,
            Shared,
            time,
            agent.Seed,
            Actions,
            Debug
        );

        var elapsed = agent.Accumulated;

        agent.Accumulated = 0f;

        // ⚠ One switch, and it is the only place the three planners differ from each other. What each
        // of them produces is an IAgentAction, which doc 37 § D2 is the decision behind — so
        // everything around this line, the join, the governor, the delta, the memory and the debug
        // record, is written once for all three.
        if (trees[agent.ScheduleIndex] is { } tree) {
            agent.Status = tree.Step(in context, elapsed);
            tree.Record(in context, Debug);

            return;
        }

        if (!Memory.TryResolve(agent.Memory, out var state)) {
            // A stale handle: the entity was recreated from a save or a prefab and its block belongs
            // to somebody else now. Join will give it a fresh one next step; skipping is better than
            // stopping the frame over it.
            return;
        }

        var action = Actions[agent.Action];

        if (!agent.Started) {
            action.Start(in context, state);
            agent.Started = true;
            agent.Status = ActionStatus.Running;
        }

        agent.Status = action.Tick(in context, state, elapsed);

        if (agent.Status != ActionStatus.Running) {
            // Restarted rather than left finished. With nothing above it to decide what a finished
            // action means, an agent that stopped for ever would look like a bug in the substrate
            // rather than the absence of a planner. A tree agent goes through the branch above,
            // where its own root decides.
            agent.Started = false;
        }

        Debug.Record(
            new(
                entity,
                tick,
                AiPlanner.None,
                Actions.NameOf(agent.Action),
                agent.Status,
                agent.Action,
                Actions.Count,
                Symbol.None,
                0f
            )
        );
    }

    /// <summary>An instance for a template, reusing the slot's old one when it is the same tree.</summary>
    /// <remarks>
    ///     A recycled slot usually gets an agent of the same kind — a wave of guards, a respawn — so
    ///     reusing the instance is what keeps a spawn from allocating. A different template cannot
    ///     reuse the block, because the block is sized by the template.
    /// </remarks>
    BehaviorTreeInstance Reuse(BehaviorTreeInstance? existing, BehaviorTreeTemplate template) {
        if (existing is not null && ReferenceEquals(existing.Template, template)) {
            existing.Reset();

            return existing;
        }

        return new(template, Memory);
    }

    /// <summary>Releases the slots of the agents that are gone.</summary>
    void Reap(World world) {
        for (var slot = 0; slot < owners.Count; slot++) {
            if (owners[slot].IsNull || seen[slot] == tick) {
                continue;
            }

            // Checked as well as unseen, because an entity that merely missed a chunk walk — which
            // cannot happen today and could if the query ever grew a filter — must not lose its
            // memory block underneath it.
            if (world.IsAlive(owners[slot]) && world.Has<AiAgent>(owners[slot])) {
                continue;
            }

            // Told it was interrupted before its memory goes back, because an action that reserved
            // something outside itself is the case Abort exists for and a destroyed entity is still
            // an interruption.
            trees[slot]?.Abort(
                new(world, owners[slot], blackboards[slot]!, Shared, default, 0, Actions, Debug)
            );

            owners[slot] = Entity.Null;
            blackboards[slot]?.Reset();
            Memory.Return(rentals[slot]);
            rentals[slot] = AgentMemoryHandle.Null;
            freeSlots.Push(slot);
            Population--;
        }
    }

    /// <summary>Whether this component still names a slot this system is holding for it.</summary>
    /// <remarks>
    ///     ⚠ <b>A tree agent has no memory handle of its own</b> — its block belongs to the instance,
    ///     because the size is a property of the template rather than of the agent. Testing for one
    ///     anyway made every tree agent look like a stranger on every step, so it re-joined, its
    ///     instance was reset, and the tree started again from the root sixty times a second while
    ///     appearing to work.
    /// </remarks>
    bool Live(ref readonly AiAgent agent, Entity entity) =>
        (uint)agent.ScheduleIndex < (uint)owners.Count
        && owners[agent.ScheduleIndex] == entity
        && (!agent.Memory.IsNull || trees[agent.ScheduleIndex] is not null);

    int NewSlot() {
        blackboards.Add(null);
        owners.Add(Entity.Null);
        rentals.Add(AgentMemoryHandle.Null);
        trees.Add(null);
        seen.Add(-1);

        return blackboards.Count - 1;
    }
}
