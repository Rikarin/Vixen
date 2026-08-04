// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;

namespace Vixen.Ai;

/// <summary>A ticket for a resolve that has been asked for.</summary>
/// <param name="Index">Which slot.</param>
/// <param name="Generation">Which use of it, so a stale ticket cannot read somebody else's plan.</param>
public readonly record struct GoapPlanRequest(int Index, uint Generation) {
    /// <summary>No request.</summary>
    public static GoapPlanRequest Null => new(-1, 0);

    /// <summary>Whether it names nothing.</summary>
    public bool IsNull => Index < 0;
}

/// <summary>How a request is getting on.</summary>
public enum GoapRequestState : byte {
    /// <summary>The ticket names nothing, or names a slot that has been used again since.</summary>
    Unknown,

    /// <summary>Asked for, not searched yet.</summary>
    Waiting,

    /// <summary>Searched. The plan is waiting to be taken.</summary>
    Ready
}

/// <summary>Resolves, queued and run a budget at a time.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A GOAP resolve does not run on the frame that asked for it</b> — doc 37 § D16, and
///         exactly <c>NavPathQueue</c>'s arrangement for exactly its reason. A behaviour-tree step is
///         cheap and a utility score is bounded; a search over an action graph is neither, and an
///         agent that changed its mind must not be able to spend the frame.
///     </para>
///     <para>
///         <b>The world is read at <see cref="Submit" />, on the thread that owns the agent.</b> What
///         reaches the search is a <see cref="GoapSnapshot" /> — a few arrays of numbers — so a
///         resolve may run on a worker thread without touching a <c>World</c> or a
///         <c>Blackboard</c> from it.
///     </para>
///     <para>
///         ⚠ <b>The per-frame cost is the two numbers together</b>: how many resolves an
///         <see cref="Update" /> runs, and what each of them may expand. Neither alone bounds
///         anything, and a project that raises one should know it is raising the product.
///     </para>
///     <para>
///         A request whose plan is never taken holds its slot until the queue runs out and starts
///         refusing new ones. <see cref="Cancel" /> is how an agent that changed its mind again gives
///         the slot back.
///     </para>
/// </remarks>
public sealed class GoapPlanQueue {
    readonly GoapPlanner[] planners;
    readonly Slot[] slots;
    readonly Queue<int> waiting = new();

    uint nextGeneration = 1;

    /// <summary>Creates a queue over a domain.</summary>
    /// <param name="domain">The domain to search.</param>
    /// <param name="settings">What bounds a search, or null for the shipped bounds.</param>
    /// <param name="capacity">How many requests may be outstanding at once.</param>
    /// <param name="parallelSearches">
    ///     How many searches may run at once. Each is a planner of its own, which is a node pool and
    ///     an open list — a handful is plenty, because the budget is what limits throughput.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="domain" /> is null.</exception>
    public GoapPlanQueue(GoapDomain domain, GoapSettings? settings = null, int capacity = 64, int parallelSearches = 4) {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelSearches, 1);

        Domain = domain;
        Settings = settings ?? GoapSettings.Default;
        planners = new GoapPlanner[parallelSearches];
        slots = new Slot[capacity];

        for (var index = 0; index < parallelSearches; index++) {
            planners[index] = new(domain, Settings);
        }

        for (var index = 0; index < capacity; index++) {
            slots[index] = new() { Snapshot = new(domain), Plan = new() };
        }
    }

    /// <summary>The domain being searched.</summary>
    public GoapDomain Domain { get; }

    /// <summary>What bounds a search.</summary>
    public GoapSettings Settings { get; }

    /// <summary>Where to run the searches, or null to run them on the caller's thread.</summary>
    /// <remarks>
    ///     Off by default, for <c>NavPathQueue</c>'s reason: a scheduler is a process-wide resource
    ///     with worker threads attached, and a planner is not the thing that should decide to create
    ///     one. A game that has one hands it over.
    /// </remarks>
    public JobScheduler? Scheduler { get; set; }

    /// <summary>How many requests are waiting or have not been taken.</summary>
    public int PendingCount { get; private set; }

    /// <summary>How many searches the last <see cref="Update" /> ran.</summary>
    public int LastResolves { get; private set; }

    /// <summary>How many nodes the last <see cref="Update" /> expanded, across every search.</summary>
    public int LastExpanded { get; private set; }

    /// <summary>Asks for a plan, reading the world now and searching later.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="goal">Which goal, or <c>-1</c> for the highest-priority unmet one.</param>
    /// <param name="costs">What an action costs, or null for the straight-line model.</param>
    /// <param name="sensors">Where actions happen, or null for nowhere.</param>
    /// <param name="capabilities">Which actions this agent may use.</param>
    /// <returns>The ticket, or <see cref="GoapPlanRequest.Null" /> when the queue is full.</returns>
    public GoapPlanRequest Submit(
        in AgentContext context,
        int goal = -1,
        IActionCostModel? costs = null,
        GoapTargetSensors? sensors = null,
        GoapCapabilities capabilities = default
    ) {
        var index = Free();

        if (index < 0) {
            return GoapPlanRequest.Null;
        }

        var slot = slots[index];

        slot.Generation = nextGeneration++;
        slot.State = GoapRequestState.Waiting;
        slot.Plan.Clear();

        if (!slot.Snapshot.Take(in context, goal, costs, sensors, capabilities, Settings.DistanceCost)) {
            // Nothing to plan for. Answered at once rather than queued, because an agent whose goals
            // are all met should not wait a frame to be told so.
            slot.Plan.Failure = PlanFailure.AlreadyMet;
            slot.State = GoapRequestState.Ready;
            PendingCount++;

            return new(index, slot.Generation);
        }

        waiting.Enqueue(index);
        PendingCount++;

        return new(index, slot.Generation);
    }

    /// <summary>Gives a slot back.</summary>
    /// <param name="request">The ticket.</param>
    /// <returns>Whether it named a live request.</returns>
    public bool Cancel(GoapPlanRequest request) {
        if (!TryResolve(request, out var index)) {
            return false;
        }

        Release(index);

        return true;
    }

    /// <summary>How a request is getting on.</summary>
    /// <param name="request">The ticket.</param>
    /// <returns>Its state.</returns>
    public GoapRequestState GetState(GoapPlanRequest request) =>
        TryResolve(request, out var index) ? slots[index].State : GoapRequestState.Unknown;

    /// <summary>Runs some of the waiting searches.</summary>
    /// <param name="resolves">How many to run, at most.</param>
    /// <remarks>
    ///     Bounded by count rather than by nodes, because each search is already bounded by
    ///     <see cref="GoapSettings.NodeBudget" /> — so the frame's cost is the product of the two, and
    ///     it is a number a project can state.
    /// </remarks>
    public void Update(int resolves = 4) {
        LastResolves = 0;
        LastExpanded = 0;

        if (resolves <= 0 || waiting.Count == 0) {
            return;
        }

        var batch = Math.Min(resolves, waiting.Count);
        var taken = new int[batch];

        for (var index = 0; index < batch; index++) {
            taken[index] = waiting.Dequeue();
        }

        if (Scheduler is { } scheduler && batch > 1) {
            var job = new SearchJob { Queue = this, Taken = taken };

            scheduler.ScheduleParallel(job, batch).Complete();
        } else {
            for (var index = 0; index < batch; index++) {
                Run(taken, index);
            }
        }

        for (var index = 0; index < batch; index++) {
            slots[taken[index]].State = GoapRequestState.Ready;
            LastExpanded += slots[taken[index]].Plan.Expanded;
        }

        LastResolves = batch;
    }

    /// <summary>Takes a finished plan, and gives the slot back.</summary>
    /// <param name="request">The ticket.</param>
    /// <param name="plan">Where to copy the plan.</param>
    /// <returns><see langword="false" /> if the search has not finished.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    public bool TryTakeResult(GoapPlanRequest request, GoapPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);

        if (!TryResolve(request, out var index) || slots[index].State != GoapRequestState.Ready) {
            return false;
        }

        plan.Copy(slots[index].Plan);
        Release(index);

        return true;
    }

    /// <summary>Runs one search. ⚠ Touches only its own slot and its own planner.</summary>
    void Run(int[] taken, int index) {
        // One planner per parallel slot, round-robin over the batch: a planner is a node pool and an
        // open list, and two searches in one would be the race this arrangement exists to avoid.
        var planner = planners[index % planners.Length];
        var slot = slots[taken[index]];

        planner.Search(slot.Snapshot, slot.Plan);
    }

    int Free() {
        for (var index = 0; index < slots.Length; index++) {
            if (slots[index].State == GoapRequestState.Unknown) {
                return index;
            }
        }

        return -1;
    }

    void Release(int index) {
        slots[index].State = GoapRequestState.Unknown;
        slots[index].Generation = 0;
        PendingCount = Math.Max(0, PendingCount - 1);
    }

    bool TryResolve(GoapPlanRequest request, out int index) {
        index = request.Index;

        return !request.IsNull
            && (uint)request.Index < (uint)slots.Length
            && slots[request.Index].Generation == request.Generation
            && slots[request.Index].State != GoapRequestState.Unknown;
    }

    sealed class Slot {
        public uint Generation;
        public GoapRequestState State;
        public GoapSnapshot Snapshot = null!;
        public GoapPlan Plan = null!;
    }

    /// <summary>
    ///     ⚠ Batched by slot index, so two searches never share a planner and never share a slot —
    ///     which is the whole of what makes this safe to schedule.
    /// </summary>
    readonly struct SearchJob : IJobParallelFor {
        public GoapPlanQueue Queue { get; init; }

        public int[] Taken { get; init; }

        public void Execute(int index) => Queue.Run(Taken, index);
    }
}
