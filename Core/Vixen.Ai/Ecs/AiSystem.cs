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

    // And the utility agent's, which is smaller: what it chose, when, and each action's cooldown.
    readonly List<UtilityMemory?> scoring = [];

    // And the GOAP agent's: its plan, its outstanding request and when it last thought.
    readonly List<GoapMemory?> planning = [];

    // One resolve queue per domain, made when a domain's first agent joins. A queue holds planners
    // and slots sized by the domain, so one for all of them would be one that fits none of them.
    readonly List<GoapPlanQueue?> queues = [];
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

    /// <summary>The GOAP domains its agents may plan over, by index.</summary>
    public GoapDomainLibrary Domains { get; } = new();

    /// <summary>What bounds a GOAP search.</summary>
    /// <remarks>Read when a domain's queue is made, so changing it later does not reach an existing one.</remarks>
    public GoapSettings Goap { get; set; } = GoapSettings.Default;

    /// <summary>How many GOAP resolves may run per step, across every domain.</summary>
    /// <remarks>
    ///     ⚠ <b>The frame's planning cost is this times <see cref="GoapSettings.NodeBudget" /></b>, and
    ///     neither number bounds anything on its own.
    /// </remarks>
    public int ResolvesPerStep { get; set; } = 4;

    /// <summary>Where a GOAP action happens, and where the agent asking is.</summary>
    public GoapTargetSensors? Targets { get; set; }

    /// <summary>What a GOAP action costs to reach, or null for the straight-line model.</summary>
    public IActionCostModel? Costs { get; set; }

    /// <summary>When a GOAP agent thinks again.</summary>
    public IReplanPolicy ReplanPolicy { get; set; } = ReplanPolicies.Reactive;

    /// <summary>The utility sets its agents may score, by index.</summary>
    /// <remarks>Filled the same way <see cref="Trees" /> is, and named by an agent the same way.</remarks>
    public UtilitySetLibrary Sets { get; } = new();

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

    /// <summary>How many steps this system has run, which is what a record is stamped with.</summary>
    /// <remarks>
    ///     Its own count rather than the frame's, because a debug record has to be comparable against
    ///     a schedule and the schedule is a function of this number — see doc 37 § D18.
    /// </remarks>
    public long Steps => tick;

    /// <summary>Node breakpoints, when a debugger has set any.</summary>
    /// <remarks>
    ///     ⚠ <b>On the system rather than on the instance, so that a breakpoint outlives an agent.</b>
    ///     A recycled slot gets a fresh <see cref="BehaviorTreeInstance" />, and a breakpoint that
    ///     lived there would vanish the moment the thing being debugged respawned — which is when
    ///     somebody most wants it.
    /// </remarks>
    public AiBreakpoints? Breakpoints { get; set; }

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

        // ⚠ Before the agents think, not after. A resolve asked for last step lands at the start of
        // this one, so an agent waits one step for a plan rather than two — and the frame's planning
        // cost is spent in one place where it can be measured.
        Resolve();

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

                scoring[slot]?.Reset();
                planning[slot]?.Reset();

                if (agent.Planner == AiPlanner.BehaviorTree && agent.Asset < Trees.Count) {
                    // A tree's block is sized by its template, so the instance rents it rather than
                    // the system: the size is not a property of the agent, it is a property of the
                    // asset the agent is running. ⚠ The slot's old instance is passed in rather than
                    // cleared first — that is what lets a recycled slot keep its block.
                    agent.Memory = AgentMemoryHandle.Null;
                    trees[slot] = Reuse(trees[slot], Trees[agent.Asset]);
                } else if (agent.Planner == AiPlanner.Goap && agent.Asset < Domains.Count) {
                    trees[slot] = null;

                    // Sized for the widest action in the domain, for the reason a utility agent's
                    // block is: the plan changes which action runs without changing the block.
                    agent.Memory = Memory.Rent(Widest(Domains[agent.Asset]));
                    planning[slot] ??= new GoapMemory();
                    planning[slot]!.Reset();
                    Queue(agent.Asset);
                } else if (agent.Planner == AiPlanner.Utility && agent.Asset < Sets.Count) {
                    trees[slot] = null;
                    // ⚠ Sized for the *largest* action in the set, not for the one it starts on. A
                    // utility agent changes which action it runs without changing its block, so a
                    // block sized for the first choice would be too small for the second — and the
                    // overflow would be a span into the next agent's state.
                    agent.Memory = Memory.Rent(Widest(Sets[agent.Asset]));
                    scoring[slot] ??= new UtilityMemory();
                } else {
                    trees[slot] = null;
                    agent.Memory = Memory.Rent(Actions.StateSize(agent.Action));
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
            // Handed over every step rather than at join, so that setting a breakpoint reaches the
            // agents that are already running — which is the only time anybody sets one.
            tree.Breakpoints = Breakpoints;
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

        // ⚠ A planner that has not chosen anything must run *nothing*, and this is the line that
        // says so. An AiAgent's Action field is zero until something sets it, so an agent whose first
        // resolve has not landed yet would otherwise spend every frame running whichever action
        // happens to be registered first — which looks exactly like a plan, and is not one.
        var running = true;

        if (scoring[agent.ScheduleIndex] is { } memory) {
            running = Decide(in context, ref agent, memory, state, elapsed);
        }

        if (planning[agent.ScheduleIndex] is { } thinking) {
            running = Think(in context, ref agent, thinking, state, elapsed);
        }

        if (!running) {
            if (agent.Started) {
                Actions[agent.Action].Abort(in context, state);
                state.Clear();
                agent.Started = false;
            }

            agent.Status = ActionStatus.Running;

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

        if (planning[agent.ScheduleIndex] is { } planned && agent.Status != ActionStatus.Running) {
            // The step is over. Which way it went decides whether the plan advances or is thrown
            // away, and the policy decides what happens next — see doc 37 § D11.
            planned.Finished = true;
            planned.Failed = agent.Status == ActionStatus.Failed;

            if (planned.Failed) {
                planned.Plan.Clear();
            } else {
                planned.Plan.Advance();
            }
        }

        if (scoring[agent.ScheduleIndex] is { } chosen && agent.Status != ActionStatus.Running) {
            // ⚠ Told at once rather than at the next interval. The interval exists to stop an agent
            // changing its mind, and an action that is over is not a change of mind — waiting a fifth
            // of a second to notice would be a visible stall after every short action.
            Sets[agent.Asset].Finished(ref chosen.State, chosen.Cooldowns);
        }

        Debug.Record(
            new(
                entity,
                tick,
                Kind(agent.ScheduleIndex),
                Actions.NameOf(agent.Action),
                agent.Status,
                agent.Action,
                Actions.Count,
                Symbol.None,
                0f
            )
        );
    }

    /// <summary>Scores the set and swaps the running action if it chose a different one.</summary>
    /// <remarks>
    ///     ⚠ <b>The span is zeroed on a change and only on a change.</b> An action's state is its own,
    ///     and the block is shared by every action in the set — so an action that inherited the last
    ///     one's bytes would start half-way through whatever that one was doing. Zeroing every tick
    ///     would instead throw away the running action's own state, which is the same bug the other
    ///     way round.
    /// </remarks>
    bool Decide(in AgentContext context, ref AiAgent agent, UtilityMemory memory, Span<byte> state, float elapsed) {
        var set = Sets[agent.Asset];

        Span<float> scores = set.Count <= 32 ? stackalloc float[32] : new float[set.Count];

        memory.Fit(set.Count);

        var chosen = set.Choose(in context, ref memory.State, memory.Cooldowns, elapsed, scores);

        if (chosen < 0) {
            // Nothing scored above zero. An agent that was already doing something keeps doing it —
            // a set with no answer is one somebody has not finished authoring, and stopping mid-frame
            // is the least useful way to say so — but one that has never chosen runs nothing at all.
            return agent.Started;
        }

        var action = set[chosen].Action;

        if (action == agent.Action && agent.Started) {
            return true;
        }

        if (agent.Started) {
            Actions[agent.Action].Abort(in context, state);
        }

        state.Clear();
        agent.Action = action;
        agent.Started = false;
        agent.Status = ActionStatus.Running;

        return true;
    }

    /// <summary>Runs the GOAP queues for a step, inside the frame's budget.</summary>
    void Resolve() {
        var share = Math.Max(1, ResolvesPerStep / Math.Max(1, queues.Count(queue => queue is not null)));

        foreach (var queue in queues) {
            queue?.Update(share);
        }
    }

    /// <summary>The queue for a domain, made the first time one of its agents joins.</summary>
    /// <param name="domain">The domain's index.</param>
    /// <returns>Its queue.</returns>
    public GoapPlanQueue Queue(int domain) {
        while (queues.Count <= domain) {
            queues.Add(null);
        }

        return queues[domain] ??= new(Domains[domain], Goap);
    }

    /// <summary>Takes a landed plan, asks the policy, and commits the head.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the head is committed, and it is re-checked before it starts</b> — doc 37 § D11.
    ///     A plan is a picture of a world that has moved on since; the head's conditions are asked of
    ///     the <i>live</i> agent, and a head that is no longer runnable throws the plan away rather
    ///     than walking into the door that closed.
    /// </remarks>
    bool Think(in AgentContext context, ref AiAgent agent, GoapMemory memory, Span<byte> state, float elapsed) {
        var domain = Domains[agent.Asset];
        var queue = Queue(agent.Asset);

        memory.Elapsed += elapsed;

        if (!memory.Pending.IsNull && queue.TryTakeResult(memory.Pending, memory.Plan)) {
            memory.Pending = GoapPlanRequest.Null;
            memory.Elapsed = 0f;
            memory.Plans++;
            memory.Finished = false;
            memory.Failed = false;
        }

        // The head has to be runnable *now*, whatever the snapshot said when the plan was made.
        while (memory.Plan.Count > 0 && !Runnable(in context, domain, memory.Plan.Head)) {
            memory.Plan.Clear();
        }

        if (memory.Pending.IsNull && ReplanPolicy.ShouldReplan(memory.Describe())) {
            memory.Asked = false;
            memory.Finished = false;
            memory.Failed = false;
            memory.Pending = queue.Submit(
                in context,
                costs: Costs,
                sensors: Targets,
                capabilities: agent.Capabilities == 0 ? GoapCapabilities.All : new(agent.Capabilities)
            );
        }

        var head = memory.Plan.Head;

        if (head < 0) {
            memory.Current = -1;

            return false;
        }

        var action = domain[head].Action;

        if (head == memory.Current && action == agent.Action && agent.Started) {
            return true;
        }

        if (agent.Started) {
            Actions[agent.Action].Abort(in context, state);
        }

        state.Clear();
        memory.Current = head;
        agent.Action = action;
        agent.Started = false;
        agent.Status = ActionStatus.Running;

        return true;
    }

    /// <summary>Whether a domain action's conditions hold for this agent right now.</summary>
    bool Runnable(in AgentContext context, GoapDomain domain, int action) {
        Span<int> world = domain.Keys.Count <= 64 ? stackalloc int[64] : new int[domain.Keys.Count];

        domain.Keys.Project(in context, world);

        foreach (var condition in domain[action].Conditions) {
            if (!condition.Holds(world[..domain.Keys.Count])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The largest state any action in a domain needs.</summary>
    int Widest(GoapDomain domain) {
        var widest = 0;

        foreach (var action in domain.Actions) {
            widest = Math.Max(widest, Actions.StateSize(action.Action));
        }

        return widest;
    }

    /// <summary>Which planner is driving a slot, for the debug record.</summary>
    AiPlanner Kind(int slot) {
        if (trees[slot] is not null) {
            return AiPlanner.BehaviorTree;
        }

        if (planning[slot] is not null) {
            return AiPlanner.Goap;
        }

        return scoring[slot] is not null ? AiPlanner.Utility : AiPlanner.None;
    }

    /// <summary>The largest state any action in a set needs.</summary>
    int Widest(UtilitySet set) {
        var widest = 0;

        foreach (var action in set.Actions) {
            widest = Math.Max(widest, Actions.StateSize(action.Action));
        }

        return widest;
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

    /// <summary>An agent's plan, or null if it is not planning.</summary>
    /// <param name="agent">The agent's component.</param>
    /// <returns>Its memory.</returns>
    /// <remarks>What the plan viewer draws and what a test asserts against.</remarks>
    public GoapMemory? PlanningOf(in AiAgent agent) =>
        (uint)agent.ScheduleIndex < (uint)planning.Count ? planning[agent.ScheduleIndex] : null;

    /// <summary>What an agent chose last, or null if it is not scoring a set.</summary>
    /// <param name="agent">The agent's component.</param>
    /// <returns>Its memory.</returns>
    /// <remarks>What the editor's table reads its bars off, and what a test asserts against.</remarks>
    public UtilityMemory? ScoringOf(in AiAgent agent) =>
        (uint)agent.ScheduleIndex < (uint)scoring.Count ? scoring[agent.ScheduleIndex] : null;

    int NewSlot() {
        blackboards.Add(null);
        owners.Add(Entity.Null);
        rentals.Add(AgentMemoryHandle.Null);
        trees.Add(null);
        scoring.Add(null);
        planning.Add(null);
        seen.Add(-1);

        return blackboards.Count - 1;
    }
}
