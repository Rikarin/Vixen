// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai.Diagnostics;

/// <summary>One breakpoint: a tree, and a node in it.</summary>
/// <param name="Tree">Which tree, by name, so a breakpoint survives a recompile of it.</param>
/// <param name="Node">Which node, by execution index.</param>
/// <remarks>
///     ⚠ <b>Named by the tree rather than by the agent, and that is the useful direction.</b> "Stop
///     when <i>anything</i> reaches this node" is the question an author has — they are debugging the
///     tree, not the guard — and a breakpoint bound to one entity would have to be set again for the
///     next one that misbehaves.
/// </remarks>
public readonly record struct AiBreakpoint(Symbol Tree, int Node) {
    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Tree}#{Node}");
}

/// <summary>Where a breakpoint stopped, and which agent it stopped.</summary>
/// <param name="Breakpoint">Which one.</param>
/// <param name="Entity">Which agent hit it.</param>
/// <param name="Node">Which node it actually stopped on, which is at or below the breakpoint's.</param>
/// <param name="Tick">When.</param>
public readonly record struct AiBreakpointHit(AiBreakpoint Breakpoint, Entity Entity, int Node, long Tick) {
    /// <summary>Whether anything is recorded here.</summary>
    public bool IsSome => !Entity.IsNull;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Tick}] {Entity} stopped at {Node} on {Breakpoint}");
}

/// <summary>
///     The nodes a running tree stops at, shared by every agent, off unless somebody sets one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Unreal has these and they are the difference between reading a tree and debugging
///         one</b> — doc 37 § Part 5 says so in as many words. A tree that gets to the wrong branch
///         once every few minutes cannot be caught by watching; stopping the agent <i>at</i> the node
///         leaves the blackboard, the active path and every decorator's last answer exactly as they
///         were when the decision was made.
///     </para>
///     <para>
///         ⚠ <b>A breakpoint stops the agent, not the game.</b> There is no world to freeze from here
///         and freezing one would be the wrong tool: the rest of the level carries on, other agents
///         carry on, and the one being debugged holds its position with its state intact.
///         <see cref="Resume" /> lets it go.
///     </para>
///     <para>
///         ⚠ <b>The scope rule is the abort rule.</b> A breakpoint on a composite stops when anything
///         <i>inside</i> it becomes the active node, which is the same containment test
///         <c>ObserverAborts</c> uses and the same one the editor's abort-scope overlay shades. One
///         rule an author can see is worth more than two they have to remember apart.
///     </para>
/// </remarks>
public sealed class AiBreakpoints {
    readonly HashSet<AiBreakpoint> breakpoints = [];

    /// <summary>Whether any of them are consulted at all. Off is a single branch per node entry.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many are set.</summary>
    public int Count => breakpoints.Count;

    /// <summary>Every breakpoint set.</summary>
    public IReadOnlyCollection<AiBreakpoint> All => breakpoints;

    /// <summary>The last one that fired.</summary>
    public AiBreakpointHit LastHit { get; private set; }

    /// <summary>How many times anything has stopped.</summary>
    public int Hits { get; private set; }

    /// <summary>Sets one.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="node">Which node.</param>
    /// <returns>Whether it was not already there.</returns>
    public bool Add(Symbol tree, int node) => breakpoints.Add(new(tree, node));

    /// <summary>Clears one.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="node">Which node.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(Symbol tree, int node) => breakpoints.Remove(new(tree, node));

    /// <summary>Sets one if it is not set and clears it if it is.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="node">Which node.</param>
    /// <returns>Whether it is now set.</returns>
    public bool Toggle(Symbol tree, int node) => Add(tree, node) || !Remove(tree, node);

    /// <summary>Whether a node has one on it.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="node">Which node.</param>
    /// <returns>Whether it has.</returns>
    public bool Contains(Symbol tree, int node) => breakpoints.Contains(new(tree, node));

    /// <summary>Clears the lot.</summary>
    public void Clear() {
        breakpoints.Clear();
        LastHit = default;
        Hits = 0;
    }

    /// <summary>
    ///     Whether entering a node stops the tree, and records the stop when it does.
    /// </summary>
    /// <param name="template">The tree.</param>
    /// <param name="node">The node that has just become active.</param>
    /// <param name="entity">Which agent.</param>
    /// <param name="tick">When.</param>
    /// <returns>Whether the agent should stop.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template" /> is null.</exception>
    public bool Halts(BehaviorTreeTemplate template, int node, Entity entity, long tick) {
        ArgumentNullException.ThrowIfNull(template);

        if (!Enabled || breakpoints.Count == 0) {
            return false;
        }

        // Up the parents rather than over the set, because a path is a handful of nodes and the set
        // is however many somebody has clicked. Nearest first, so a breakpoint on a task wins over
        // one on the composite that contains it and the hit names the closer of the two.
        for (var walk = node; walk >= 0; walk = template[walk].Parent) {
            var candidate = new AiBreakpoint(template.Name, walk);

            if (!breakpoints.Contains(candidate)) {
                continue;
            }

            LastHit = new(candidate, entity, node, tick);
            Hits++;

            return true;
        }

        return false;
    }

    /// <summary>Lets a stopped agent go.</summary>
    /// <param name="instance">The tree that stopped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Resuming does not clear the breakpoint</b>, and the agent will stop there again the
    ///     next time it enters — which is what "run to here" means and is why stepping a loop is
    ///     resume, resume, resume rather than one resume and a puzzled look.
    /// </remarks>
    public static void Resume(BehaviorTreeInstance instance) {
        ArgumentNullException.ThrowIfNull(instance);

        instance.Resume();
    }
}
