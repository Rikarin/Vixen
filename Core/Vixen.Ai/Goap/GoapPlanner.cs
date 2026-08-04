// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Ai;

/// <summary>Why a resolve produced no plan.</summary>
public enum PlanFailure : byte {
    /// <summary>It produced one.</summary>
    None,

    /// <summary>Nothing was asked for, or the goal index names nothing.</summary>
    NoGoal,

    /// <summary>The goal is already true. Not a failure, and worth telling apart from one.</summary>
    AlreadyMet,

    /// <summary>Nothing this agent can do leads to the goal.</summary>
    Unreachable,

    /// <summary>
    ///     The search ran out of nodes. ⚠ <b>A bound, not a bug</b> — see <see cref="GoapSettings" />.
    /// </summary>
    BudgetExhausted,

    /// <summary>Every chain reached the depth limit without finishing.</summary>
    DepthExceeded
}

/// <summary>Why a search declined to go further with an action.</summary>
public enum GoapRejection : byte {
    /// <summary>Its own conditions are not true yet, so the search looked for what would serve them.</summary>
    ConditionsUnmet,

    /// <summary>This agent's capability mask does not include it.</summary>
    NotCapable,

    /// <summary>It is already in the chain above, and an action twice over is an infinite descent.</summary>
    AlreadyInTheChain,

    /// <summary>The chain reached the depth limit here.</summary>
    TooDeep
}

/// <summary>One action a search looked at, and what it made of it.</summary>
/// <param name="Action">Its index in the domain.</param>
/// <param name="Why">What the search did not like.</param>
public readonly record struct GoapConsidered(int Action, GoapRejection Why);

/// <summary>What bounds a search.</summary>
/// <remarks>
///     ⚠ <b>A GOAP search is exponential in depth and the engine must not hang on a badly authored
///     action set.</b> That is doc 37 § D10's mandatory bound, and the two numbers are the whole of
///     it: exceeding either produces <see cref="PlanFailure.BudgetExhausted" /> or
///     <see cref="PlanFailure.DepthExceeded" /> naming the goal, which the debugger shows and a test
///     asserts.
///
///     The defaults target doc 28's stated scale — <i>"the few dozen agents where emergent behaviour
///     is the point, not the thousand critters"</i> — and a project that wants deeper plans says so
///     and pays for them.
/// </remarks>
public sealed record GoapSettings {

    /// <summary>The shipped bounds.</summary>
    public static GoapSettings Default { get; } = new();

    /// <summary>How many nodes one search may expand.</summary>
    public int NodeBudget { get; init; } = 512;

    /// <summary>How long a chain may get.</summary>
    public int DepthLimit { get; init; } = 8;

    /// <summary>How much a metre of distance adds to an action's cost.</summary>
    public float DistanceCost { get; init; } = 0.1f;
}

/// <summary>Where an action would be performed.</summary>
/// <param name="Found">Whether a sensor answered at all.</param>
/// <param name="Position">Where, in world space.</param>
/// <param name="Entity">The entity, when the target is one.</param>
public readonly record struct GoapTarget(bool Found, Vector3 Position, Entity Entity) {
    /// <summary>Nowhere. What an action with no target sensor resolves to.</summary>
    public static GoapTarget None => new(false, Vector3.Zero, Entity.Null);
}

/// <summary>What an action costs to reach.</summary>
/// <remarks>
///     doc 37 § Part 4's seam. ⚠ <b>The distance cost is a straight line by default, not a path
///     length.</b> A path query per candidate action per resolve is a nav search per edge of the
///     search graph, which is the cost of the whole system in one line — so the shipped model is
///     arithmetic, and the one that asks the navmesh lives in <c>Vixen.Ai.Nodes</c> where the navmesh
///     does, with the guide saying plainly what it costs.
/// </remarks>
public interface IActionCostModel {
    /// <summary>What one action costs.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="action">The action.</param>
    /// <param name="from">Where the agent is.</param>
    /// <param name="target">Where the action happens.</param>
    /// <returns>The cost. ⚠ Must be positive, or A* has no meaning and the heuristic is not admissible.</returns>
    float Cost(in AgentContext context, GoapAction action, Vector3 from, in GoapTarget target);
}

/// <summary>The cost models that ship.</summary>
public static class ActionCostModels {
    /// <summary>The action's own cost, and nothing else.</summary>
    /// <remarks>What a set whose actions happen where the agent stands wants, and the cheapest.</remarks>
    public static IActionCostModel Flat { get; } = new FlatCostModel();

    /// <summary>The action's cost plus the straight-line distance to its target.</summary>
    /// <param name="perMetre">What a metre adds.</param>
    /// <returns>The model.</returns>
    public static IActionCostModel StraightLine(float perMetre = 0.1f) => new StraightLineCostModel(perMetre);
}

sealed class FlatCostModel : IActionCostModel {
    public float Cost(in AgentContext context, GoapAction action, Vector3 from, in GoapTarget target) {
        ArgumentNullException.ThrowIfNull(action);

        return MathF.Max(1f, action.BaseCost);
    }
}

sealed class StraightLineCostModel(float perMetre) : IActionCostModel {
    public float Cost(in AgentContext context, GoapAction action, Vector3 from, in GoapTarget target) {
        ArgumentNullException.ThrowIfNull(action);

        var distance = target.Found ? (target.Position - from).Length() : 0f;

        // ⚠ Floored at one, not at zero. The heuristic counts unmet conditions, and it is admissible
        // only while every action costs at least as much as it claims to remove.
        return MathF.Max(1f, action.BaseCost + (distance * perMetre));
    }
}

/// <summary>Which of a domain's actions one agent may use.</summary>
/// <param name="Bits">One bit per action index.</param>
/// <remarks>
///     ⚠ <b>A mask on the agent rather than a domain per capability set.</b> A domain describes what a
///     <i>kind</i> of agent can do; whether this particular one has a gun, a key or a broken leg is
///     per agent, and a domain per permutation is a graph rebuild per permutation.
///
///     Sixty-four actions is the limit of a mask, and an action past it is treated as allowed rather
///     than silently forbidden — a domain that large is one nobody is masking.
/// </remarks>
public readonly record struct GoapCapabilities(ulong Bits) {
    /// <summary>Everything the domain has.</summary>
    public static GoapCapabilities All => new(ulong.MaxValue);

    /// <summary>Whether an action is allowed.</summary>
    /// <param name="action">Its index in the domain.</param>
    /// <returns>Whether this agent may use it.</returns>
    public bool Allows(int action) => action >= 64 || (Bits & (1UL << action)) != 0;

    /// <summary>The same mask with one action turned off.</summary>
    /// <param name="action">Its index.</param>
    /// <returns>The mask.</returns>
    public GoapCapabilities Without(int action) => action >= 64 ? this : new(Bits & ~(1UL << action));

    /// <summary>The same mask with one action turned on.</summary>
    /// <param name="action">Its index.</param>
    /// <returns>The mask.</returns>
    public GoapCapabilities With(int action) => action >= 64 ? this : new(Bits | (1UL << action));
}

/// <summary>A sequence of actions, of which only the head is committed.</summary>
/// <remarks>
///     ⚠ <b>The tail is advisory and doc 37 § D11 is explicit about it.</b> An agent that <i>follows</i>
///     a sequence walks into a door that closed after the plan was made; one that re-plans every frame
///     is a search per agent per frame. So the tail is kept — it is what the debugger draws and what a
///     designer reads — and every step re-checks the next action's conditions before starting it
///     rather than trusting the plan that produced it.
/// </remarks>
public sealed class GoapPlan {
    readonly List<int> steps = [];

    /// <summary>Which goal it was made for.</summary>
    public Symbol Goal { get; internal set; }

    /// <summary>Which goal it was made for, by index.</summary>
    public int GoalIndex { get; internal set; } = -1;

    /// <summary>Why there is no plan, or <see cref="PlanFailure.None" />.</summary>
    public PlanFailure Failure { get; internal set; }

    /// <summary>What the whole chain costs.</summary>
    public float Cost { get; internal set; }

    /// <summary>How many nodes the search expanded to find it.</summary>
    public int Expanded { get; internal set; }

    /// <summary>How many steps it has.</summary>
    public int Count => steps.Count;

    /// <summary>The steps, in the order they run. The head is <c>[0]</c>.</summary>
    public ReadOnlySpan<int> Steps => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(steps);

    /// <summary>The action to run now, or <c>-1</c>.</summary>
    public int Head => steps.Count > 0 ? steps[0] : -1;

    /// <summary>Forgets everything.</summary>
    public void Clear() {
        steps.Clear();
        Goal = Symbol.None;
        GoalIndex = -1;
        Failure = PlanFailure.None;
        Cost = 0f;
        Expanded = 0;
    }

    /// <summary>Drops the head, which is what finishing a step does.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Advance() {
        if (steps.Count == 0) {
            return false;
        }

        steps.RemoveAt(0);

        return true;
    }

    /// <summary>Copies another plan over this one.</summary>
    /// <param name="other">The plan to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="other" /> is null.</exception>
    public void Copy(GoapPlan other) {
        ArgumentNullException.ThrowIfNull(other);

        steps.Clear();
        steps.AddRange(other.steps);
        Goal = other.Goal;
        GoalIndex = other.GoalIndex;
        Failure = other.Failure;
        Cost = other.Cost;
        Expanded = other.Expanded;
    }

    internal void Add(int action) => steps.Add(action);
}

/// <summary>The A* over a domain's action graph, bounded and reported.</summary>
/// <remarks>
///     <para>
///         <b>Backwards, from goal to satisfied</b>, which is doc 37 § D10. A node is one action
///         chosen to serve a condition; its children are the actions that can serve <i>its</i> unmet
///         conditions. The search finishes at the first node whose own conditions all hold in the
///         projected world — that action can be run now, and the chain back up to the goal is the
///         plan.
///     </para>
///     <para>
///         ⚠ <b>A plan is a chain, so an action with two unmet conditions is served one at a time —
///         and that is correct rather than a simplification.</b> Only the head is committed
///         (§ D11): the head is by construction runnable now, running it changes the world, and the
///         next resolve plans from what the world then is. A search that instead tried to satisfy
///         every branch of a conjunction at once would be a hyper-graph search whose plans go stale
///         before their second step.
///     </para>
///     <para>
///         ⚠ <b>An action may not appear twice in one chain.</b> Without that, a domain where two
///         actions serve each other's conditions is an infinite descent — and the budget would report
///         exhaustion for a domain with a perfectly good two-step plan in it.
///     </para>
///     <para>
///         <b>It searches a <see cref="GoapSnapshot" /> and never the world</b>, which is what makes a
///         resolve a job. See <see cref="GoapPlanQueue" />.
///     </para>
/// </remarks>
public sealed class GoapPlanner {
    readonly List<Node> nodes = [];
    readonly List<int> open = [];
    readonly int[] candidates;

    /// <summary>Creates a planner over a domain.</summary>
    /// <param name="domain">The domain to search.</param>
    /// <param name="settings">What bounds it, or null for the shipped bounds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="domain" /> is null.</exception>
    public GoapPlanner(GoapDomain domain, GoapSettings? settings = null) {
        ArgumentNullException.ThrowIfNull(domain);

        Domain = domain;
        Settings = settings ?? GoapSettings.Default;
        candidates = new int[Math.Max(1, domain.Count)];
    }

    /// <summary>The domain being searched.</summary>
    public GoapDomain Domain { get; }

    /// <summary>What bounds a search.</summary>
    public GoapSettings Settings { get; }

    /// <summary>How many nodes the last search expanded.</summary>
    public int LastExpanded { get; private set; }

    /// <summary>
    ///     Where the search writes down what it considered and rejected, when a tool asked for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         doc 37 § Part 5's GOAP viewer: <i>"the actions that were considered and rejected with
    ///         why"</i>. A plan says what will happen; a designer staring at an agent that does
    ///         nothing needs the other half — which actions the search looked at, and what it did not
    ///         like about each.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null by default, and the search pays one reference check per rejection.</b> A
    ///         resolve runs on a worker thread inside a per-frame budget; a list every search filled
    ///         would be an allocation and a write per node to serve a panel nobody has open. A tool
    ///         hands one in — and it belongs to whoever is watching rather than to the planner,
    ///         because a queue runs several searches through one planner.
    ///     </para>
    /// </remarks>
    public List<GoapConsidered>? Traced { get; set; }

    /// <summary>Takes a snapshot and searches it, on the caller's thread.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="plan">Where to put the plan.</param>
    /// <param name="goal">Which goal, or <c>-1</c> for the highest-priority unmet one.</param>
    /// <param name="costs">What an action costs, or null for the straight-line model.</param>
    /// <param name="sensors">Where actions happen, or null for nowhere.</param>
    /// <param name="capabilities">Which actions this agent may use.</param>
    /// <returns>Why there is no plan, or <see cref="PlanFailure.None" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    /// <remarks>What a test, a tool or a game that does not want a queue calls.</remarks>
    public PlanFailure Resolve(
        in AgentContext context,
        GoapPlan plan,
        int goal = -1,
        IActionCostModel? costs = null,
        GoapTargetSensors? sensors = null,
        GoapCapabilities capabilities = default
    ) {
        ArgumentNullException.ThrowIfNull(plan);

        var snapshot = new GoapSnapshot(Domain);

        if (!snapshot.Take(in context, goal, costs, sensors, capabilities, Settings.DistanceCost)) {
            plan.Clear();
            plan.Failure = PlanFailure.AlreadyMet;

            return PlanFailure.AlreadyMet;
        }

        return Search(snapshot, plan);
    }

    /// <summary>Searches a snapshot. Touches nothing else, so it may run anywhere.</summary>
    /// <param name="snapshot">What the world looked like.</param>
    /// <param name="plan">Where to put the plan.</param>
    /// <returns>Why there is no plan, or <see cref="PlanFailure.None" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot" /> or <paramref name="plan" /> is null.</exception>
    public PlanFailure Search(GoapSnapshot snapshot, GoapPlan plan) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);

        plan.Clear();
        Traced?.Clear();
        LastExpanded = 0;

        if ((uint)snapshot.Goal >= (uint)Domain.Goals.Length) {
            plan.Failure = PlanFailure.NoGoal;

            return PlanFailure.NoGoal;
        }

        var wanted = Domain.Goals[snapshot.Goal];
        var world = (ReadOnlySpan<int>)snapshot.World;

        plan.Goal = wanted.Name;
        plan.GoalIndex = snapshot.Goal;

        if (wanted.Met(world)) {
            plan.Failure = PlanFailure.AlreadyMet;

            return PlanFailure.AlreadyMet;
        }

        nodes.Clear();
        open.Clear();

        // The goal's own unmet conditions are the roots: one node per action that can serve one of
        // them, which is where a backwards search starts.
        foreach (var condition in wanted.Conditions) {
            if (condition.Holds(world)) {
                continue;
            }

            var found = Domain.Servers(in condition, candidates);

            for (var index = 0; index < found; index++) {
                Push(snapshot, candidates[index], parent: -1);
            }
        }

        var deepest = false;

        while (open.Count > 0) {
            if (LastExpanded >= Settings.NodeBudget) {
                return Give(plan, PlanFailure.BudgetExhausted);
            }

            var index = Take();
            var node = nodes[index];

            LastExpanded++;

            if (Runnable(world, node.Action)) {
                Unwind(plan, index);
                plan.Cost = node.Cost;
                plan.Expanded = LastExpanded;

                return PlanFailure.None;
            }

            if (node.Depth >= Settings.DepthLimit) {
                deepest = true;
                Traced?.Add(new(node.Action, GoapRejection.TooDeep));

                continue;
            }

            Traced?.Add(new(node.Action, GoapRejection.ConditionsUnmet));

            Expand(snapshot, index);
        }

        return Give(plan, deepest ? PlanFailure.DepthExceeded : PlanFailure.Unreachable);
    }

    PlanFailure Give(GoapPlan plan, PlanFailure failure) {
        plan.Failure = failure;
        plan.Expanded = LastExpanded;

        return failure;
    }

    /// <summary>Whether every one of an action's conditions holds right now.</summary>
    bool Runnable(ReadOnlySpan<int> world, int action) {
        foreach (var condition in Domain[action].Conditions) {
            if (!condition.Holds(world)) {
                return false;
            }
        }

        return true;
    }

    void Expand(GoapSnapshot snapshot, int index) {
        var node = nodes[index];
        var action = Domain[node.Action];
        var world = (ReadOnlySpan<int>)snapshot.World;

        for (var slot = 0; slot < action.Conditions.Length; slot++) {
            if (action.Conditions[slot].Holds(world)) {
                continue;
            }

            foreach (var server in Domain.Servers(node.Action, slot)) {
                Push(snapshot, server, index);
            }
        }
    }

    void Push(GoapSnapshot snapshot, int action, int parent) {
        if (!snapshot.Capabilities.Allows(action)) {
            Traced?.Add(new(action, GoapRejection.NotCapable));

            return;
        }

        if (Repeats(parent, action)) {
            Traced?.Add(new(action, GoapRejection.AlreadyInTheChain));

            return;
        }

        var cost = (parent >= 0 ? nodes[parent].Cost : 0f) + snapshot.Costs[action];
        var node = new Node {
            Action = action,
            Parent = parent,
            Depth = (parent >= 0 ? nodes[parent].Depth : 0) + 1,
            Cost = cost,
            Estimate = cost + Unmet(snapshot.World, action)
        };

        nodes.Add(node);
        open.Add(nodes.Count - 1);
    }

    /// <summary>⚠ An action may not appear twice in one chain, or two that serve each other never end.</summary>
    bool Repeats(int parent, int action) {
        for (var index = parent; index >= 0; index = nodes[index].Parent) {
            if (nodes[index].Action == action) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The heuristic: how many of an action's conditions are not true yet.</summary>
    /// <remarks>
    ///     Admissible while every action costs at least one, which <see cref="ActionCostModels" />
    ///     guarantees by flooring the cost — each unmet condition needs at least one more action.
    /// </remarks>
    int Unmet(ReadOnlySpan<int> world, int action) {
        var count = 0;

        foreach (var condition in Domain[action].Conditions) {
            if (!condition.Holds(world)) {
                count++;
            }
        }

        return count;
    }

    /// <summary>The cheapest open node, taken out.</summary>
    /// <remarks>
    ///     A linear scan rather than a heap. The open list is bounded by the node budget, which is
    ///     hundreds — a heap's bookkeeping costs more than the scan at that size, and the scan has no
    ///     state to get wrong.
    /// </remarks>
    int Take() {
        var best = 0;

        for (var index = 1; index < open.Count; index++) {
            if (nodes[open[index]].Estimate < nodes[open[best]].Estimate) {
                best = index;
            }
        }

        var chosen = open[best];

        open.RemoveAt(best);

        return chosen;
    }

    /// <summary>Walks a finished node back to the goal, which is the plan in execution order.</summary>
    void Unwind(GoapPlan plan, int index) {
        for (var node = index; node >= 0; node = nodes[node].Parent) {
            plan.Add(nodes[node].Action);
        }
    }

    struct Node {
        public int Action;
        public int Parent;
        public int Depth;
        public float Cost;
        public float Estimate;
    }
}
