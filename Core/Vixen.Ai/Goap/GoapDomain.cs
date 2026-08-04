// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Ai;

/// <summary>Whether an action has to be at its target before it starts.</summary>
/// <remarks>
///     doc 37 § D12. An action is performed <b>at a position</b>, and movement is not modelled as
///     actions in the graph — the alternative, a <c>MoveTo(x)</c> action per destination, makes the
///     graph a function of the world's contents.
/// </remarks>
public enum GoapMoveMode : byte {
    /// <summary>Walk there, then do it. The usual one.</summary>
    MoveThenPerform,

    /// <summary>Do it on the way. For anything that works at range.</summary>
    PerformWhileMoving,

    /// <summary>It happens wherever the agent is.</summary>
    Anywhere
}

/// <summary>Where an action is performed.</summary>
/// <remarks>
///     doc 37 § D12's target sensor. It answers "the nearest pear" rather than "pear number four",
///     which is what keeps the graph a function of the <i>action set</i> rather than of the world's
///     contents.
/// </remarks>
public interface IGoapTargetSensor {
    /// <summary>Finds where an action would be performed.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="position">Where to put the place.</param>
    /// <param name="target">The entity, when the target is one.</param>
    /// <returns>Whether there is anywhere to go.</returns>
    bool TryResolve(in AgentContext context, out Vector3 position, out Entity target);
}

/// <summary>One thing an agent can do, and what it needs and changes.</summary>
/// <remarks>
///     <para>
///         doc 37 § D2 again: <see cref="Action" /> is an index into the world's
///         <c>AgentActionRegistry</c>, the same thing a behaviour-tree task and a utility action name.
///     </para>
///     <para>
///         ⚠ <b><see cref="BaseCost" /> is what a designer tunes and the distance is what the world
///         adds.</b> An action set whose costs are all one plans by depth, which is the default and is
///         usually right; a set that wants "shooting is cheaper than punching" says so here rather
///         than in a heuristic.
///     </para>
/// </remarks>
public sealed class GoapAction {
    /// <summary>Creates an action.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="action">Its index in the world's <c>AgentActionRegistry</c>.</param>
    /// <param name="conditions">What has to be true before it can run.</param>
    /// <param name="effects">What it changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="conditions" /> or <paramref name="effects" /> is null.</exception>
    public GoapAction(Symbol name, ushort action, GoapCondition[] conditions, params GoapEffect[] effects) {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(effects);

        Name = name;
        Action = action;
        Conditions = conditions;
        Effects = effects;
    }

    /// <summary>What it is called.</summary>
    public Symbol Name { get; }

    /// <summary>Which action it runs.</summary>
    public ushort Action { get; }

    /// <summary>What has to be true before it can run.</summary>
    public GoapCondition[] Conditions { get; }

    /// <summary>What it changes.</summary>
    public GoapEffect[] Effects { get; }

    /// <summary>What it costs before the world has its say.</summary>
    public float BaseCost { get; init; } = 1f;

    /// <summary>Which target sensor says where it happens, or none.</summary>
    public Symbol Target { get; init; }

    /// <summary>How close is close enough, in metres.</summary>
    public float StoppingDistance { get; init; } = 1.5f;

    /// <summary>Whether the agent has to be there first.</summary>
    public GoapMoveMode Move { get; init; } = GoapMoveMode.MoveThenPerform;

    /// <summary>Whether this action can serve a condition.</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>Whether one of its effects pushes that key the right way.</returns>
    /// <remarks>
    ///     The whole of doc 37 § D10's matching rule, and it is worth stating that this is all of it.
    ///     The alternative — full symbolic world states with arbitrary predicates — is what makes
    ///     classic GOAP implementations both slow and impossible to author.
    /// </remarks>
    public bool Serves(in GoapCondition condition) {
        foreach (var effect in Effects) {
            if (effect.Key == condition.Key && effect.Increases == condition.WantsIncrease) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Something an agent wants to be true.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Conditions">What has to hold for it to be met.</param>
/// <param name="Priority">Which goal wins when more than one is unmet. Higher first.</param>
public readonly record struct GoapGoal(Symbol Name, GoapCondition[] Conditions, int Priority = 0) {
    /// <summary>Whether the world already satisfies it.</summary>
    /// <param name="state">The projected world.</param>
    /// <returns>Whether nothing needs doing.</returns>
    public bool Met(ReadOnlySpan<int> state) {
        foreach (var condition in Conditions) {
            if (!condition.Holds(state)) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Everything one kind of agent can want and can do, with the graph between them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The graph is built once, here, and not per resolve.</b> Which action's effect can
///         serve which action's condition is a fact about the <i>action set</i>, so working it out
///         inside the search would be the same nested loop over every partial plan — doc 37 § D10.
///         What is per agent is the condition evaluations and the costs, and nothing else.
///     </para>
///     <para>
///         <b>Immutable and shared by every agent of its kind</b>, the way a
///         <see cref="BehaviorTreeTemplate" /> and a <see cref="UtilitySet" /> are.
///     </para>
///     <para>
///         ⚠ <b>An action may be excluded per agent, and that is what "capabilities" means.</b> A
///         domain describes what the kind can do; an agent carries a mask of which of them <i>it</i>
///         can, so a wounded guard and a healthy one share one graph and plan differently. A domain
///         per capability set would be a domain per permutation.
///     </para>
/// </remarks>
public sealed class GoapDomain {
    readonly GoapAction[] actions;
    readonly GoapGoal[] goals;

    // For every action, which other actions can serve each of its conditions — flattened into one
    // array with an offset per (action, condition) pair, because a jagged array of small arrays is a
    // pointer chase per expansion in the innermost loop of the search.
    readonly int[] servers;
    readonly int[] offsets;

    /// <summary>Creates a domain and builds its graph.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="keys">The world keys it reasons about.</param>
    /// <param name="actions">What its agents can do.</param>
    /// <param name="goals">What they might want.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public GoapDomain(Symbol name, GoapWorldKeys keys, GoapAction[] actions, GoapGoal[] goals) {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(goals);

        Name = name;
        Keys = keys;
        this.actions = actions;
        this.goals = goals;

        var pairs = 0;

        foreach (var action in actions) {
            pairs += action.Conditions.Length;
        }

        offsets = new int[pairs + 1];

        var edges = new List<int>();
        var pair = 0;

        foreach (var action in actions) {
            foreach (var condition in action.Conditions) {
                offsets[pair] = edges.Count;

                for (var candidate = 0; candidate < actions.Length; candidate++) {
                    if (actions[candidate].Serves(in condition)) {
                        edges.Add(candidate);
                    }
                }

                pair++;
            }
        }

        offsets[pairs] = edges.Count;
        servers = [.. edges];

        // Where each action's conditions start in the pair table, so a condition's servers are one
        // addition away rather than a scan.
        ConditionOffsets = new int[actions.Length + 1];

        var running = 0;

        for (var index = 0; index < actions.Length; index++) {
            ConditionOffsets[index] = running;
            running += actions[index].Conditions.Length;
        }

        ConditionOffsets[actions.Length] = running;
    }

    /// <summary>What it is called.</summary>
    public Symbol Name { get; }

    /// <summary>The world keys it reasons about.</summary>
    public GoapWorldKeys Keys { get; }

    /// <summary>How many actions it holds.</summary>
    public int Count => actions.Length;

    /// <summary>The action at an index.</summary>
    /// <param name="index">Its index.</param>
    public GoapAction this[int index] => actions[index];

    /// <summary>Everything its agents can do, in order.</summary>
    public ReadOnlySpan<GoapAction> Actions => actions;

    /// <summary>Everything they might want, in order.</summary>
    public ReadOnlySpan<GoapGoal> Goals => goals;

    /// <summary>How many edges the graph has, which is what an editor's viewer draws.</summary>
    public int EdgeCount => servers.Length;

    internal int[] ConditionOffsets { get; }

    /// <summary>Which actions can serve one of an action's conditions.</summary>
    /// <param name="action">The action's index.</param>
    /// <param name="condition">Which of its conditions.</param>
    /// <returns>The indices of the actions whose effects push that key the right way.</returns>
    public ReadOnlySpan<int> Servers(int action, int condition) {
        var pair = ConditionOffsets[action] + condition;

        return servers.AsSpan(offsets[pair], offsets[pair + 1] - offsets[pair]);
    }

    /// <summary>Which actions can serve a goal's condition.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="found">Where to put the indices.</param>
    /// <returns>How many were written.</returns>
    /// <remarks>
    ///     A goal's conditions are not in the pair table, because a goal is per agent-kind data that a
    ///     domain may have many of and the table is over actions. There are a handful of goals and a
    ///     resolve looks at one of them, so a scan here costs nothing the search would notice.
    /// </remarks>
    public int Servers(in GoapCondition condition, Span<int> found) {
        var count = 0;

        for (var index = 0; index < actions.Length && count < found.Length; index++) {
            if (actions[index].Serves(in condition)) {
                found[count++] = index;
            }
        }

        return count;
    }

    /// <summary>Looks a goal up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index, or <c>-1</c>.</returns>
    public int IndexOfGoal(Symbol name) {
        for (var index = 0; index < goals.Length; index++) {
            if (goals[index].Name == name) {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>The domains a world's agents may name, by index.</summary>
/// <remarks>The same arrangement <c>BehaviorTreeLibrary</c> and <c>UtilitySetLibrary</c> have.</remarks>
public sealed class GoapDomainLibrary {
    readonly Dictionary<Symbol, GoapDomain> byName = [];
    readonly List<GoapDomain> ordered = [];

    /// <summary>How many domains it holds.</summary>
    public int Count => ordered.Count;

    /// <summary>The domain at an index, which is what an <c>AiAgent</c> names.</summary>
    /// <param name="index">Its index.</param>
    public GoapDomain this[int index] => ordered[index];

    /// <summary>Adds a domain.</summary>
    /// <param name="domain">The domain.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domain" /> is null.</exception>
    /// <exception cref="InvalidOperationException">One of that name is already in it.</exception>
    public int Add(GoapDomain domain) {
        ArgumentNullException.ThrowIfNull(domain);

        if (domain.Name != Symbol.None && !byName.TryAdd(domain.Name, domain)) {
            throw new InvalidOperationException($"'{domain.Name}' is already in this library.");
        }

        ordered.Add(domain);

        return ordered.Count - 1;
    }

    /// <summary>Looks a domain up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="domain">Where to put it.</param>
    /// <returns>Whether the library has it.</returns>
    public bool TryGet(Symbol name, out GoapDomain? domain) => byName.TryGetValue(name, out domain);
}
