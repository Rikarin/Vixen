// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Ai.Diagnostics;
using Vixen.Core;

namespace Vixen.Ai;

/// <summary>The scalar part of one agent's tree state, at the front of its memory block.</summary>
[StructLayout(LayoutKind.Sequential)]
struct BehaviorTreeState {
    /// <summary>The running task's node index, or <c>-1</c>.</summary>
    public int Active;

    /// <summary>A composite waiting to be restarted at the top of the next step, or <c>-1</c>.</summary>
    public int Restart;

    /// <summary>A node whose branch is to be failed at the top of the next step, or <c>-1</c>.</summary>
    public int Interrupt;

    /// <summary>How many nodes the last step entered or left.</summary>
    public int Transitions;

    /// <summary>Whether the tree has been entered at all.</summary>
    public int Started;
}

/// <summary>One composite's state: where it is in its children, and what its parallel is doing.</summary>
[StructLayout(LayoutKind.Sequential)]
struct BehaviorCompositeState {
    /// <summary>Which child is running, as an absolute node index, or <c>-1</c>.</summary>
    public int Current;

    /// <summary>What a parallel's main task returned, once it has.</summary>
    public byte MainStatus;

    /// <summary>0 not started, 1 running, 2 finished. Parallels only.</summary>
    public byte MainState;

    /// <summary>Which children a random selector has already tried, one bit each.</summary>
    public ulong Tried;
}

/// <summary>
///     One agent running one tree: the active node, its memory block, and the observers that wake it.
/// </summary>
/// <remarks>
///     <para>
///         <b>A step is not a traversal.</b> A classic behaviour tree walks from the root every frame.
///         This one keeps the active node and does nothing at all when nothing has changed: the
///         active task ticks, active services tick if their interval has elapsed, and pending aborts
///         are serviced. An agent walking across a courtyard costs one <c>MoveTo.Tick</c> and a
///         service every 0.4 s — which is the measurable claim doc 37 § D7 makes, and
///         <c>BehaviorTreeCostTests</c> checks it both ways in one test.
///     </para>
///     <para>
///         ⚠ <b>An abort never happens inside a tick.</b> A blackboard write during a task's own tick
///         is the ordinary case — tasks write their results — and acting on it there would tear down
///         the state of the thing currently executing. A notification therefore only sets a bit; the
///         tree works out what that means at the top of the next step, before any node updates. The
///         cost is stated rather than hidden: <b>a condition that goes false during a task's tick
///         takes effect at the start of the next tick.</b>
///     </para>
///     <para>
///         ⚠ <b>Observers are registered once, on the union of every key any observing decorator
///         reads, and not per branch.</b> Registering per branch is what Unreal does and it buys
///         nothing here, because the scope test — is the decorator's parent composite on the active
///         path, and is the active node inside or after its subtree — is a pair of integer
///         comparisons made when the change is <i>resolved</i>. Paying registration churn on every
///         branch change to save setting a bit would be the wrong way round.
///     </para>
///     <para>
///         This is the one managed object per agent. Everything else — every node's state, every
///         decorator's, every service's timer — is a window into the block, so a thousand agents on
///         one tree is a thousand blocks sized at load with nothing allocated in a step.
///     </para>
/// </remarks>
public sealed class BehaviorTreeInstance : IBlackboardObserver {
    /// <summary>How many node transitions one step may make before it is called a runaway.</summary>
    /// <remarks>
    ///     ⚠ <b>A step keeps descending until something is actually running</b>, because otherwise a
    ///     sequence of three instant tasks would take three steps — and under a governor at one tick
    ///     in sixteen, that is three-quarters of a second to set three blackboard keys. The bound is
    ///     what stops a loop over instant tasks from hanging the frame instead, and hitting it is
    ///     reported through <see cref="Overran" /> rather than swallowed.
    /// </remarks>
    public const int MaximumTransitionsPerStep = 256;

    readonly BehaviorTreeTemplate template;
    readonly AgentMemoryPool memory;
    readonly ulong[] changedKeys;
    readonly BlackboardObserverHandle[] registrations;
    readonly BehaviorTreeInstance?[] nested;
    readonly float[] tagCooldowns;

    Blackboard? watching;
    bool dirty;

    /// <summary>Creates an instance of a template.</summary>
    /// <param name="template">The tree to run.</param>
    /// <param name="memory">Where its state block comes from.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public BehaviorTreeInstance(BehaviorTreeTemplate template, AgentMemoryPool memory) {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(memory);

        this.template = template;
        this.memory = memory;

        Handle = memory.Rent(template.MemorySize);
        changedKeys = new ulong[(Math.Max(template.Layout.Count, 1) + 63) / 64];
        registrations = new BlackboardObserverHandle[template.ObservedKeys.Length];
        nested = new BehaviorTreeInstance?[template.NestedSlotCount];
        tagCooldowns = new float[template.CooldownTags.Length];

        Reset();
    }

    /// <summary>The tree being run.</summary>
    public BehaviorTreeTemplate Template => template;

    /// <summary>Its state block.</summary>
    public AgentMemoryHandle Handle { get; }

    /// <summary>The running task's node index, or <c>-1</c>.</summary>
    public int ActiveNode => Header.Active;

    /// <summary>How many nodes the last step entered or left.</summary>
    /// <remarks>Zero on a step where nothing changed, which is the point of the whole arrangement.</remarks>
    public int LastTransitions => Header.Transitions;

    /// <summary>Whether the last step ran out of transitions before anything settled.</summary>
    public bool Overran { get; private set; }

    /// <summary>What the tree returned the last time it finished.</summary>
    public ActionStatus LastResult { get; private set; } = ActionStatus.Running;

    /// <summary>How many times it has run to completion.</summary>
    public int Completions { get; private set; }

    ref BehaviorTreeState Header => ref MemoryMarshal.AsRef<BehaviorTreeState>(memory.Resolve(Handle));

    /// <summary>Puts the tree back to never having run, without telling anything it was interrupted.</summary>
    /// <remarks>For an agent recycled out of a pool, whose predecessor's branch is not its business.</remarks>
    public void Reset() {
        memory.Resolve(Handle).Clear();

        ref var header = ref Header;

        header.Active = -1;
        header.Restart = -1;
        header.Interrupt = -1;
        header.Started = 0;
        Overran = false;
        LastResult = ActionStatus.Running;
        Completions = 0;
        Array.Clear(changedKeys);
        Array.Clear(tagCooldowns);
        dirty = false;
    }

    /// <summary>How long is left on a named cooldown, in seconds.</summary>
    /// <param name="tag">Its name.</param>
    /// <param name="now">The clock.</param>
    /// <returns>Seconds remaining, or zero when it is not running.</returns>
    /// <remarks>
    ///     A flat scan over the tags the compiler found in this tree, which is a handful. A dictionary
    ///     would be a hash per test on a decorator that is re-tested every step, and an allocation on
    ///     an agent whose whole point is that it does not make any.
    /// </remarks>
    public float TagCooldownRemaining(Symbol tag, float now) {
        var tags = template.CooldownTags;

        for (var index = 0; index < tags.Length; index++) {
            if (tags[index] == tag) {
                return Math.Max(tagCooldowns[index] - now, 0f);
            }
        }

        return 0f;
    }

    /// <summary>Starts a named cooldown.</summary>
    /// <param name="tag">Its name.</param>
    /// <param name="until">When it runs out, on the same clock.</param>
    public void StartTagCooldown(Symbol tag, float until) {
        var tags = template.CooldownTags;

        for (var index = 0; index < tags.Length; index++) {
            if (tags[index] == tag) {
                tagCooldowns[index] = Math.Max(tagCooldowns[index], until);

                return;
            }
        }
    }

    /// <summary>Runs the tree for one of this agent's turns.</summary>
    /// <param name="agent">The agent, its world and its blackboard.</param>
    /// <param name="delta">
    ///     Seconds since this agent last had a turn — not the frame's step, for the reason
    ///     <see cref="IAgentAction.Tick" /> gives.
    /// </param>
    /// <returns><see cref="ActionStatus.Running" />, or what the tree returned if it finished.</returns>
    public ActionStatus Step(in AgentContext agent, float delta) {
        Attach(agent.Blackboard);
        Header.Transitions = 0;
        Overran = false;

        // ⚠ In this order, and the order is the design. Aborts first, before any node updates, so a
        // task is never torn down half-way through its own tick. Then the parked parallels, so a
        // background branch gets at most one pass per step. Then services, because a service exists
        // to change what the tree decides *this* step rather than next one. Then the task.
        ServiceObservers(in agent);
        ServiceContinuous(in agent);
        ResumeParallels(in agent);
        TickServices(in agent, delta);

        var elapsed = delta;

        for (var guard = 0; guard < MaximumTransitionsPerStep; guard++) {
            if (Header.Interrupt >= 0) {
                var interrupt = Header.Interrupt;

                Header.Interrupt = -1;
                AbortThrough(in agent, interrupt);

                // Unwound as *gated*, because AbortThrough has already told this node's decorators
                // it is over. The parent then resumes — moves to its next child — rather than
                // restarting from child zero, which is the difference between "this branch is
                // finished" and "something higher-priority became available".
                if (Settle(in agent, Unwind(in agent, interrupt, ActionStatus.Failed, gated: true))) {
                    return LastResult;
                }

                continue;
            }

            if (Header.Restart >= 0) {
                var restart = Header.Restart;

                Header.Restart = -1;
                AbortThrough(in agent, restart);

                if (Settle(in agent, Enter(in agent, restart))) {
                    return LastResult;
                }

                continue;
            }

            if (Header.Started == 0) {
                Header.Started = 1;

                if (Settle(in agent, Enter(in agent, 0))) {
                    return LastResult;
                }

                continue;
            }

            var active = Header.Active;

            if (active < 0) {
                Header.Started = 0;

                continue;
            }

            var outcome = TickTasks(in agent, active, elapsed);

            // Only the first tick of a step is given the elapsed time. A task started part-way
            // through this step has had no time pass, and handing it the same delta again would run
            // a chain of timers at the speed of the chain rather than of the clock.
            elapsed = 0f;

            if (outcome == ActionStatus.Running) {
                return ActionStatus.Running;
            }

            Header.Active = -1;

            if (Settle(in agent, Unwind(in agent, active, outcome, gated: false))) {
                return LastResult;
            }
        }

        Overran = true;

        return ActionStatus.Running;
    }

    /// <summary>Stops whatever is running and tells each part it was interrupted.</summary>
    /// <param name="agent">The agent.</param>
    public void Abort(in AgentContext agent) {
        AbortThrough(in agent, -1);

        ref var header = ref Header;

        header.Started = 0;
        header.Restart = -1;
        header.Interrupt = -1;
    }

    /// <summary>Gives the memory block and every nested instance back.</summary>
    /// <param name="agent">The agent, so that what is running can be told first.</param>
    public void Release(in AgentContext agent) {
        Abort(in agent);
        Detach();

        foreach (var child in nested) {
            child?.Release(in agent);
        }

        Array.Clear(nested);
        memory.Return(Handle);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Records, and returns.</b> This runs inside somebody else's write — very often the
    ///     running task's own — so anything more than a bit-set would be acting on the world from
    ///     inside a notification about it.
    /// </remarks>
    public void OnBlackboardChanged(Blackboard blackboard, BlackboardKey key) {
        changedKeys[key.Index >> 6] |= 1UL << (key.Index & 63);
        dirty = true;
    }

    /// <summary>The nested instance a dynamic-subtree task owns, creating it if it must.</summary>
    /// <param name="node">The task's node index.</param>
    /// <param name="child">The tree it should be running.</param>
    /// <param name="agent">The agent, so that a tree being swapped out can be told.</param>
    /// <returns>The instance, or null when the node was not compiled as a nested one.</returns>
    public BehaviorTreeInstance? Nested(int node, BehaviorTreeTemplate? child, in AgentContext agent) {
        var slot = template[node].NestedSlot;

        if (slot < 0 || child is null) {
            return null;
        }

        if (nested[slot] is { } existing) {
            if (ReferenceEquals(existing.Template, child)) {
                return existing;
            }

            existing.Release(in agent);
        }

        return nested[slot] = new(child, memory);
    }

    /// <summary>One node's state, for a task, the debugger and a test.</summary>
    /// <param name="node">Its index.</param>
    /// <returns>Its bytes.</returns>
    public Span<byte> StateOf(int node) {
        ref readonly var record = ref template[node];

        return memory.Resolve(Handle).Slice(record.MemoryOffset, record.MemorySize);
    }

    /// <summary>The path from the root to the active node, root first.</summary>
    /// <param name="path">Where to write it.</param>
    /// <returns>How many were written.</returns>
    /// <remarks>What the debug overlay draws and what a test asserts. Nothing in a step calls it.</remarks>
    public int ActivePath(Span<int> path) {
        var active = Header.Active;

        if (active < 0) {
            return 0;
        }

        var depth = 0;

        for (var walk = active; walk >= 0; walk = template[walk].Parent) {
            depth++;
        }

        var index = depth;

        for (var walk = active; walk >= 0; walk = template[walk].Parent) {
            index--;

            if (index < path.Length) {
                path[index] = walk;
            }
        }

        return Math.Min(depth, path.Length);
    }

    /// <summary>Records what the tree is doing, in the one shape all three planners fill.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="recorder">Where to put it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recorder" /> is null.</exception>
    public void Record(in AgentContext agent, AgentDebugRecorder recorder) {
        ArgumentNullException.ThrowIfNull(recorder);

        if (!recorder.Enabled) {
            return;
        }

        var active = Header.Active;
        var parent = active >= 0 ? template[active].Parent : -1;

        recorder.Record(
            new(
                agent.Entity,
                agent.Time.FrameCount,
                AiPlanner.BehaviorTree,
                active >= 0 ? template[active].Name : template.Name,
                active >= 0 ? ActionStatus.Running : LastResult,
                Math.Max(active, 0),
                parent >= 0 ? template[parent].ChildCount : 1,
                template.Name,
                Header.Transitions
            )
        );
    }

    /// <summary>Whether a result from <see cref="Enter" /> or <see cref="Unwind" /> ended the tree.</summary>
    bool Settle(in AgentContext agent, ActionStatus result) {
        _ = agent;

        if (result == ActionStatus.Running) {
            return false;
        }

        LastResult = result;
        Completions++;

        ref var header = ref Header;

        header.Started = 0;
        header.Active = -1;

        // Returned rather than looping straight back into the root, so that a tree whose root fails
        // instantly costs one step rather than two hundred and fifty-six transitions and an overrun.
        return true;
    }

    void Attach(Blackboard blackboard) {
        if (ReferenceEquals(watching, blackboard)) {
            return;
        }

        Detach();
        watching = blackboard;

        var keys = template.ObservedKeys;

        for (var index = 0; index < keys.Length; index++) {
            registrations[index] = blackboard.AddObserver(keys[index], this);
        }
    }

    void Detach() {
        if (watching is null) {
            return;
        }

        foreach (var registration in registrations) {
            watching.RemoveObserver(registration);
        }

        Array.Clear(registrations);
        watching = null;
    }

    /// <summary>Turns whichever keys changed since the last step into at most one pending restart.</summary>
    /// <remarks>
    ///     Walks the active path and tests only the decorators in scope, which is Unity's rule: an
    ///     observer affects the siblings under its own parent composite, and that composite restarts
    ///     from child zero. The lowest-index composite wins, because a tie breaks on the index and
    ///     never on which decorator happened to be visited first.
    /// </remarks>
    void ServiceObservers(in AgentContext agent) {
        if (!dirty) {
            return;
        }

        dirty = false;

        var active = Header.Active;

        if (active < 0) {
            Array.Clear(changedKeys);

            return;
        }

        var restart = int.MaxValue;

        // From the active node up. Only an open composite can hold a decorator in scope, so this is
        // the whole search space — depth times branching, not the tree.
        for (var scope = active; scope >= 0; scope = template[scope].Parent) {
            ref readonly var composite = ref template[scope];

            if (composite.Kind != BehaviorNodeKind.Composite) {
                continue;
            }

            for (var child = composite.FirstChild; child > 0 && child <= composite.LastDescendant;) {
                ref readonly var node = ref template[child];

                for (var slot = node.DecoratorStart; slot < node.DecoratorStart + node.DecoratorCount; slot++) {
                    if (Reconsider(in agent, slot, child, active)) {
                        restart = Math.Min(restart, scope);
                    }
                }

                child = node.LastDescendant + 1;
            }
        }

        Array.Clear(changedKeys);

        if (restart != int.MaxValue) {
            Header.Restart = restart;
        }
    }

    /// <summary>The abort test: did this decorator flip, and does the flip reach the active node.</summary>
    bool Reconsider(in AgentContext agent, int slot, int node, int active) {
        ref readonly var decorator = ref template.Decorators[slot];

        if (decorator.Aborts == ObserverAborts.None || !Touched(template.KeysOf(in decorator))) {
            return false;
        }

        var block = memory.Resolve(Handle);
        var passes = decorator.Decorator.Evaluate(
            new(agent, this, node),
            block.Slice(decorator.MemoryOffset, decorator.MemorySize)
        );

        if (!Flipped(block, slot, passes)) {
            return false;
        }

        var last = template[node].LastDescendant;

        // Two integer comparisons, and this is the whole of it. `Self` reaches inside the decorated
        // node's subtree; `LowerPriority` reaches everything after it.
        return ((decorator.Aborts & ObserverAborts.Self) != 0 && !passes && active >= node && active <= last)
            || ((decorator.Aborts & ObserverAborts.LowerPriority) != 0 && passes && active > last);
    }

    /// <summary>
    ///     Re-tests the conditions no observer can see, and lets a priority composite change its mind.
    /// </summary>
    void ServiceContinuous(in AgentContext agent) {
        var active = Header.Active;

        if (active < 0 || Header.Restart >= 0) {
            return;
        }

        var restart = int.MaxValue;
        var interrupt = int.MaxValue;
        var block = memory.Resolve(Handle);

        for (var walk = active; walk >= 0; walk = template[walk].Parent) {
            ref readonly var node = ref template[walk];

            // A continuous decorator anywhere on the active path — a time limit expiring, a target
            // that has walked out of a cone. Nothing wrote a key, so no observer could have seen it.
            for (var slot = node.DecoratorStart; slot < node.DecoratorStart + node.DecoratorCount; slot++) {
                ref readonly var decorator = ref template.Decorators[slot];

                if (!decorator.Decorator.Continuous) {
                    continue;
                }

                var passes = decorator.Decorator.Evaluate(
                    new(agent, this, walk),
                    block.Slice(decorator.MemoryOffset, decorator.MemorySize)
                );

                Flipped(block, slot, passes);

                if (!passes) {
                    // ⚠ Fails the branch rather than restarting its parent, and the two are genuinely
                    // different. An observer restarts because something *else* became more worth
                    // doing, and re-entering from child zero finds it. A continuous condition going
                    // false says *this* branch is over — and restarting from child zero would walk
                    // straight back into it, because the decorator's clock resets when the branch
                    // ends. That is an infinite re-entry, and it is the shape a time limit takes.
                    interrupt = Math.Min(interrupt, walk);
                }
            }

            if (node.Kind != BehaviorNodeKind.Composite || node.Composite != BehaviorCompositeKind.Priority) {
                continue;
            }

            // A priority composite does not resume: anything before the running child whose
            // decorators now pass takes over. That is the difference between it and a selector, and
            // it is a separate composite because "does a selector resume" is the question every
            // implementation answers differently and silently.
            var current = CompositeStateOf(walk).Current;

            for (var child = node.FirstChild; child > 0 && child < current; child = template[child].LastDescendant + 1) {
                if (EvaluateDecorators(in agent, child)) {
                    restart = Math.Min(restart, walk);

                    break;
                }
            }
        }

        if (interrupt != int.MaxValue) {
            Header.Interrupt = interrupt;
        } else if (restart != int.MaxValue) {
            Header.Restart = restart;
        }
    }

    /// <summary>
    ///     Gives a parked parallel's background branch its next pass.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>At the top of a step, and never inside one.</b> Unreal's simple parallel re-runs its
    ///     background branch for as long as the main task lasts; doing that inside a step means a
    ///     branch of instant tasks spins until the transition budget stops it. Parking on the main
    ///     task and resuming here bounds it at one pass per step, which for a "keep checking while
    ///     this runs" branch is the rate anybody wanted anyway.
    /// </remarks>
    void ResumeParallels(in AgentContext agent) {
        var active = Header.Active;

        if (active < 0 || Header.Restart >= 0 || Header.Interrupt >= 0) {
            return;
        }

        var parent = template[active].Parent;

        if (parent < 0
            || template[parent].Composite != BehaviorCompositeKind.Parallel
            || template[parent].Kind != BehaviorNodeKind.Composite
            || template[parent].FirstChild != active) {
            return;
        }

        ref var state = ref CompositeStateOf(parent);

        if (state.MainState != 1 || state.Current >= 0) {
            return;
        }

        var background = template[template[parent].FirstChild].LastDescendant + 1;

        state.Current = background;
        Settle(in agent, Enter(in agent, background));
    }

    void TickServices(in AgentContext agent, float delta) {
        var active = Header.Active;

        if (active < 0 || template.Services.Length == 0) {
            return;
        }

        var block = memory.Resolve(Handle);
        var timers = Timers(block);

        for (var walk = active; walk >= 0; walk = template[walk].Parent) {
            ref readonly var node = ref template[walk];

            for (var slot = node.ServiceStart; slot < node.ServiceStart + node.ServiceCount; slot++) {
                timers[slot] -= delta;

                if (timers[slot] > 0f) {
                    continue;
                }

                ref readonly var service = ref template.Services[slot];
                var period = Period(in agent, slot, in service);

                service.Service.Tick(
                    new(agent, this, walk),
                    block.Slice(service.MemoryOffset, service.MemorySize),
                    period - timers[slot]
                );

                timers[slot] = period;
            }
        }
    }

    /// <summary>A service's next interval, jittered from the agent's own stream.</summary>
    /// <remarks>
    ///     ⚠ The deviation is what stops every agent spawned in the same frame from ticking its
    ///     service in the same frame for ever, which turns a 0.5 s perception update into a spike
    ///     every thirty frames. Drawn from the agent's stream rather than a shared one, so a replay
    ///     jitters identically.
    /// </remarks>
    static float Period(in AgentContext agent, int slot, ref readonly BehaviorServiceSlot service) {
        if (service.RandomDeviation <= 0f) {
            return service.Interval;
        }

        var draw = AgentRandom.Value(agent.Entity, agent.Seed, (uint)(0x5E000000 + slot));

        return service.Interval + ((draw - 0.5f) * 2f * service.RandomDeviation);
    }

    /// <summary>Ticks the main task of every open parallel, then the active task itself.</summary>
    ActionStatus TickTasks(in AgentContext agent, int active, float delta) {
        var block = memory.Resolve(Handle);

        // Every open parallel's main task runs beside whatever the background branch is doing. The
        // path is short, so finding them is a walk up the parents rather than a list to maintain.
        for (var walk = template[active].Parent; walk >= 0; walk = template[walk].Parent) {
            ref readonly var node = ref template[walk];

            if (node.Kind != BehaviorNodeKind.Composite
                || node.Composite != BehaviorCompositeKind.Parallel
                || node.FirstChild == active) {
                // Skipped when the main task *is* the active node — a parked parallel, whose main
                // task is ticked below like any other leaf.
                continue;
            }

            ref var state = ref CompositeStateOf(walk);

            if (state.MainState != 1) {
                continue;
            }

            var main = node.FirstChild;
            var inMain = Running(in agent, main);
            var status = agent.Actions![template[main].Action]
                .Tick(in inMain, block.Slice(template[main].MemoryOffset, template[main].MemorySize), delta);

            if (status == ActionStatus.Running) {
                continue;
            }

            state.MainState = 2;
            state.MainStatus = (byte)status;

            if (node.FinishMode != ParallelFinishMode.Immediate) {
                continue;
            }

            // Immediate: the background branch is torn down at once and the parallel finishes with
            // the main task's result.
            AbortThrough(in agent, walk, inclusive: false);
            Header.Active = -1;
            Settle(in agent, Unwind(in agent, walk, status, gated: false));

            return ActionStatus.Running;
        }

        ref readonly var task = ref template[active];
        var running = Running(in agent, active);

        return agent.Actions![task.Action]
            .Tick(in running, block.Slice(task.MemoryOffset, task.MemorySize), delta);
    }

    /// <summary>Enters a node, descending until something is running or everything has failed.</summary>
    ActionStatus Enter(in AgentContext agent, int index) {
        var walk = index;

        while (true) {
            Header.Transitions++;

            if (!EvaluateDecorators(in agent, walk)) {
                return Unwind(in agent, walk, ActionStatus.Failed, gated: true);
            }

            EnterDecorators(in agent, walk);

            ref readonly var node = ref template[walk];

            if (node.Kind == BehaviorNodeKind.Task) {
                StartTask(in agent, walk);
                Header.Active = walk;

                return ActionStatus.Running;
            }

            EnterServices(in agent, walk);

            ref var state = ref CompositeStateOf(walk);

            state.Current = -1;
            state.Tried = 0;
            state.MainState = 0;
            state.MainStatus = (byte)ActionStatus.Running;

            if (node.Composite == BehaviorCompositeKind.Parallel) {
                var background = template[node.FirstChild].LastDescendant + 1;

                StartTask(in agent, node.FirstChild);
                state.MainState = 1;
                state.Current = background;
                walk = background;

                continue;
            }

            var first = NextChild(in agent, walk, -1);

            if (first < 0) {
                return Unwind(
                    in agent,
                    walk,
                    node.Composite == BehaviorCompositeKind.Sequence ? ActionStatus.Succeeded : ActionStatus.Failed,
                    gated: false
                );
            }

            CompositeStateOf(walk).Current = first;
            walk = first;
        }
    }

    /// <summary>
    ///     Walks a result up through decorators and composites until a new node starts or the root
    ///     answers.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="index">The node that finished.</param>
    /// <param name="status">What it returned.</param>
    /// <param name="gated">
    ///     Whether it failed on its own decorators, in which case they neither exit nor repeat — the
    ///     node was never entered.
    /// </param>
    ActionStatus Unwind(in AgentContext agent, int index, ActionStatus status, bool gated) {
        var walk = index;
        var result = status;
        var skip = gated;

        while (true) {
            Header.Transitions++;

            if (!skip) {
                result = ExitDecorators(in agent, walk, result);

                if (RepeatDecorators(in agent, walk, result)) {
                    return Enter(in agent, walk);
                }

                if (template[walk].Kind == BehaviorNodeKind.Composite) {
                    ExitServices(in agent, walk);
                }
            }

            skip = false;

            var parent = template[walk].Parent;

            if (parent < 0) {
                return result;
            }

            ref readonly var composite = ref template[parent];
            ref var state = ref CompositeStateOf(parent);

            if (composite.Composite == BehaviorCompositeKind.Parallel) {
                if (composite.FirstChild == walk) {
                    // The main task finished as the active node, which only happens while the
                    // background branch is parked. The parallel is done, with the main task's result.
                    state.MainState = 2;
                    state.MainStatus = (byte)result;
                    walk = parent;

                    continue;
                }

                if (state.MainState == 2) {
                    walk = parent;
                    result = (ActionStatus)state.MainStatus;

                    continue;
                }

                // The background branch ran out while the main task is still going. Park on the main
                // task; ResumeParallels gives the branch another pass at the top of the next step.
                state.Current = -1;
                Header.Active = composite.FirstChild;

                return ActionStatus.Running;
            }

            var carries = composite.Composite switch {
                BehaviorCompositeKind.Sequence => result == ActionStatus.Failed,
                _ => result == ActionStatus.Succeeded
            };

            if (carries) {
                walk = parent;

                continue;
            }

            var next = NextChild(in agent, parent, state.Current);

            if (next < 0) {
                walk = parent;
                result = composite.Composite == BehaviorCompositeKind.Sequence
                    ? ActionStatus.Succeeded
                    : ActionStatus.Failed;

                continue;
            }

            state.Current = next;

            return Enter(in agent, next);
        }
    }

    /// <summary>Which child a composite should try next, given the one it has just finished.</summary>
    int NextChild(in AgentContext agent, int composite, int current) {
        ref readonly var node = ref template[composite];

        if (node.ChildCount == 0) {
            return -1;
        }

        if (node.Composite == BehaviorCompositeKind.RandomSelector) {
            return NextRandomChild(in agent, composite);
        }

        if (current < 0) {
            return node.FirstChild;
        }

        var next = template[current].LastDescendant + 1;

        return next <= node.LastDescendant ? next : -1;
    }

    /// <summary>A weighted draw among the children a random selector has not tried yet.</summary>
    /// <remarks>
    ///     Without replacement, so the selector still walks every child before failing — and from the
    ///     agent's own stream salted by the node and the attempt, so it is a shuffle rather than the
    ///     same child four times, and the same shuffle on every machine.
    /// </remarks>
    int NextRandomChild(in AgentContext agent, int composite) {
        ref readonly var node = ref template[composite];
        ref var state = ref CompositeStateOf(composite);

        var total = 0f;
        var attempt = 0;
        var child = node.FirstChild;

        for (var index = 0; index < node.ChildCount; index++) {
            if ((state.Tried & (1UL << index)) == 0) {
                total += template[child].Weight;
            } else {
                attempt++;
            }

            child = template[child].LastDescendant + 1;
        }

        if (total <= 0f) {
            return -1;
        }

        var draw = AgentRandom.Value(agent.Entity, agent.Seed, (uint)((composite << 8) | (attempt & 0xFF))) * total;

        child = node.FirstChild;

        for (var index = 0; index < node.ChildCount; index++) {
            if ((state.Tried & (1UL << index)) == 0) {
                draw -= template[child].Weight;

                if (draw <= 0f) {
                    state.Tried |= 1UL << index;

                    return child;
                }
            }

            child = template[child].LastDescendant + 1;
        }

        return -1;
    }

    /// <summary>Tears down what is running, up to and optionally including a node.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="scope">Where to stop, or <c>-1</c> for the whole tree.</param>
    /// <param name="inclusive">Whether <paramref name="scope" /> is torn down as well.</param>
    void AbortThrough(in AgentContext agent, int scope, bool inclusive = true) {
        var active = Header.Active;

        if (active < 0) {
            Header.Active = -1;

            return;
        }

        var block = memory.Resolve(Handle);
        var walk = active;

        while (walk >= 0) {
            if (walk == scope && !inclusive) {
                break;
            }

            ref readonly var node = ref template[walk];

            if (node.Kind == BehaviorNodeKind.Task) {
                var running = Running(in agent, walk);

                agent.Actions![node.Action].Abort(in running, block.Slice(node.MemoryOffset, node.MemorySize));
            } else {
                if (node.Composite == BehaviorCompositeKind.Parallel && CompositeStateOf(walk).MainState == 1) {
                    var main = node.FirstChild;
                    var inMain = Running(in agent, main);

                    agent.Actions![template[main].Action]
                        .Abort(in inMain, block.Slice(template[main].MemoryOffset, template[main].MemorySize));
                    CompositeStateOf(walk).MainState = 0;
                }

                ExitServices(in agent, walk);
            }

            ExitDecorators(in agent, walk, ActionStatus.Failed);
            Header.Transitions++;

            if (walk == scope) {
                break;
            }

            walk = node.Parent;
        }

        Header.Active = -1;
    }

    bool EvaluateDecorators(in AgentContext agent, int index) {
        ref readonly var node = ref template[index];
        var block = memory.Resolve(Handle);

        // Top to bottom, and the first failure stops the rest: putting the cheap test above the
        // expensive one is the author's decision and the tree has to honour the order they chose.
        for (var slot = node.DecoratorStart; slot < node.DecoratorStart + node.DecoratorCount; slot++) {
            ref readonly var decorator = ref template.Decorators[slot];
            var passes = decorator.Decorator.Evaluate(
                new(agent, this, index),
                block.Slice(decorator.MemoryOffset, decorator.MemorySize)
            );

            Flipped(block, slot, passes);

            if (!passes) {
                return false;
            }
        }

        return true;
    }

    void EnterDecorators(in AgentContext agent, int index) {
        ref readonly var node = ref template[index];
        var block = memory.Resolve(Handle);

        for (var slot = node.DecoratorStart; slot < node.DecoratorStart + node.DecoratorCount; slot++) {
            ref readonly var decorator = ref template.Decorators[slot];

            decorator.Decorator.Enter(new(agent, this, index), block.Slice(decorator.MemoryOffset, decorator.MemorySize));
        }
    }

    ActionStatus ExitDecorators(in AgentContext agent, int index, ActionStatus status) {
        ref readonly var node = ref template[index];
        var block = memory.Resolve(Handle);
        var result = status;

        // Bottom to top on the way out, so that an inverter under a force-success reads the way it is
        // drawn: the innermost decorator sees the node's own answer first.
        for (var slot = node.DecoratorStart + node.DecoratorCount - 1; slot >= node.DecoratorStart; slot--) {
            ref readonly var decorator = ref template.Decorators[slot];

            result = decorator.Decorator.Finish(
                new(agent, this, index),
                block.Slice(decorator.MemoryOffset, decorator.MemorySize),
                result
            );
        }

        return result;
    }

    bool RepeatDecorators(in AgentContext agent, int index, ActionStatus status) {
        ref readonly var node = ref template[index];
        var block = memory.Resolve(Handle);

        for (var slot = node.DecoratorStart; slot < node.DecoratorStart + node.DecoratorCount; slot++) {
            ref readonly var decorator = ref template.Decorators[slot];

            if (decorator.Decorator.ShouldRepeat(
                    new(agent, this, index),
                    block.Slice(decorator.MemoryOffset, decorator.MemorySize),
                    status
                )) {
                return true;
            }
        }

        return false;
    }

    void EnterServices(in AgentContext agent, int index) {
        ref readonly var node = ref template[index];

        if (node.ServiceCount == 0) {
            return;
        }

        var block = memory.Resolve(Handle);
        var timers = Timers(block);

        for (var slot = node.ServiceStart; slot < node.ServiceStart + node.ServiceCount; slot++) {
            ref readonly var service = ref template.Services[slot];
            var state = block.Slice(service.MemoryOffset, service.MemorySize);
            var context = new BehaviorContext(agent, this, index);

            service.Service.Enter(in context, state);

            // ⚠ Run at once, here, rather than left for the next pass of TickServices. This is
            // during the descent, so the branch below has not chosen its child yet and gets to see
            // what the service just wrote. Zeroing the timer instead looks equivalent and is not:
            // TickServices has already run for this step, so the branch would spend its whole first
            // step deciding on data an interval old.
            service.Service.Tick(in context, state, 0f);
            timers[slot] = Period(in agent, slot, in service);
        }
    }

    void ExitServices(in AgentContext agent, int index) {
        ref readonly var node = ref template[index];

        if (node.ServiceCount == 0) {
            return;
        }

        var block = memory.Resolve(Handle);

        for (var slot = node.ServiceStart; slot < node.ServiceStart + node.ServiceCount; slot++) {
            ref readonly var service = ref template.Services[slot];

            service.Service.Leave(new(agent, this, index), block.Slice(service.MemoryOffset, service.MemorySize));
        }
    }

    void StartTask(in AgentContext agent, int index) {
        ref readonly var node = ref template[index];
        var state = memory.Resolve(Handle).Slice(node.MemoryOffset, node.MemorySize);

        var running = Running(in agent, index);

        state.Clear();
        agent.Actions![node.Action].Start(in running, state);
    }

    /// <summary>
    ///     The agent's context with this tree and this node stamped on it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>How a task reaches the tree without the action surface knowing about trees.</b> A task
    ///     is an <see cref="IAgentAction" /> so that one object serves a tree, a utility set and a GOAP
    ///     plan — which means it cannot take a tree-shaped context. The two nodes that genuinely need
    ///     the tree, both of them subtree runners, read it off here; every other task ignores it.
    /// </remarks>
    AgentContext Running(in AgentContext agent, int node) =>
        agent with { RunningTree = this, RunningNode = node };

    bool Touched(ReadOnlySpan<BlackboardKey> keys) {
        foreach (var key in keys) {
            if ((changedKeys[key.Index >> 6] & (1UL << (key.Index & 63))) != 0) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records what a decorator answered, and says whether that differs from last time.</summary>
    bool Flipped(Span<byte> block, int slot, bool passes) {
        var words = (template.Decorators.Length + 63) / 64 * 8;
        var results = MemoryMarshal.Cast<byte, ulong>(block.Slice(template.ResultBitsOffset, words));
        var evaluated = MemoryMarshal.Cast<byte, ulong>(block.Slice(template.EvaluatedBitsOffset, words));
        var word = slot >> 6;
        var bit = 1UL << (slot & 63);
        var was = (results[word] & bit) != 0;
        var known = (evaluated[word] & bit) != 0;

        results[word] = passes ? results[word] | bit : results[word] & ~bit;
        evaluated[word] |= bit;

        return known && was != passes;
    }

    Span<float> Timers(Span<byte> block) =>
        MemoryMarshal.Cast<byte, float>(
            block.Slice(template.ServiceTimerOffset, template.Services.Length * sizeof(float))
        );

    ref BehaviorCompositeState CompositeStateOf(int index) =>
        ref MemoryMarshal.AsRef<BehaviorCompositeState>(StateOf(index));

    /// <summary>The size of a composite's state, so the compiler and the stepper cannot disagree.</summary>
    internal static int CompositeStateSize => Unsafe.SizeOf<BehaviorCompositeState>();

    /// <summary>The size of the header, ditto.</summary>
    internal static int HeaderSize => Unsafe.SizeOf<BehaviorTreeState>();
}
