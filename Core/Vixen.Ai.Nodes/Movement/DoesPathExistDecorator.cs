// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;
using Vixen.Navigation;

namespace Vixen.Ai.Nodes;

/// <summary>How hard to look for a path before answering.</summary>
/// <remarks>
///     ⚠ <b>Unreal offers raycast, hierarchical and full; Vixen has no hierarchical graph, and
///     <see cref="Budgeted" /> is what stands in for the middle one.</b> A hierarchical query answers
///     "are these two places in the same connected region" off a coarse graph built beside the mesh.
///     Vixen bakes no such graph, and inventing one for a decorator would be a second navigation
///     structure to keep in step with the first. A search with a node budget answers the same
///     question with the same shape of cost — cheap, conservative, and wrong only in the direction
///     that makes an agent give up rather than walk into a dead end.
/// </remarks>
public enum PathTest : byte {
    /// <summary>A straight walk across the surface. Cheapest; says no to anything round a corner.</summary>
    Raycast,

    /// <summary>A search stopped after a node budget. Says no when it runs out.</summary>
    Budgeted,

    /// <summary>The whole search. Exact, and the most expensive thing a decorator can do.</summary>
    Full
}

/// <summary>Whether the agent could actually get to what a key names.</summary>
/// <param name="query">The mesh to ask. ⚠ One search at a time — see the remarks.</param>
/// <param name="key">The key holding a <c>Vector3</c> or an <c>Entity</c>.</param>
/// <param name="test">How hard to look.</param>
/// <param name="budget">How many nodes <see cref="PathTest.Budgeted" /> may open.</param>
/// <param name="aborts">What it may interrupt.</param>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>DoesPathExist</c>, and the reason it is a decorator rather than a task
///         is that "can I get there" gates a branch: the whole point is to fall through to the next
///         one without the agent taking a step first.
///     </para>
///     <para>
///         ⚠ <b>It observes the key, so it re-tests when the key changes and not when the world
///         does.</b> A door closing does not write a blackboard key, so a branch already running
///         under this decorator keeps running until its <c>MoveTo</c> reports that the crowd failed.
///         That is the right division: the decorator answers "is it worth starting", and the task
///         answers "did it work".
///     </para>
///     <para>
///         ⚠ <b><see cref="NavMeshQuery" /> holds one node pool and runs one search at a time.</b>
///         The decorator object is shared by every agent running the tree, so the query is too, and
///         it is safe only because a tree step is single-threaded. A project that parallelises agent
///         steps hands each thread its own query.
///     </para>
/// </remarks>
public sealed class DoesPathExistDecorator(
    NavMeshQuery query,
    BlackboardKey key,
    PathTest test = PathTest.Budgeted,
    int budget = 256,
    ObserverAborts aborts = ObserverAborts.None
) : BehaviorDecorator {
    static readonly Vector3 Extents = new(4f, 4f, 4f);

    readonly NavMeshQuery query = query ?? throw new ArgumentNullException(nameof(query));
    readonly BlackboardKey[] observed = key.IsValid ? [key] : [];
    readonly NavPolyRef[] corridor = new NavPolyRef[256];

    /// <inheritdoc />
    public override ObserverAborts Aborts => aborts;

    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => observed;

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var agent = context.Agent;

        if (!AgentTarget.TryResolve(in agent, key, out var goal, out _)
            || !agent.World.Has<LocalTransform>(agent.Entity)) {
            return false;
        }

        var here = agent.World.Get<LocalTransform>(agent.Entity).Position;
        var filter = NavQueryFilter.Default;

        // Both ends have to be on the mesh before anything else is worth asking. An agent standing
        // off it — knocked into a hole, spawned badly — is the case that otherwise reads as "there is
        // no path to anywhere", which is true and unhelpful.
        if (!query.FindNearestPoly(here, Extents, filter, out var start, out var from)
            || !query.FindNearestPoly(goal, Extents, filter, out var end, out var to)) {
            return false;
        }

        if (test == PathTest.Raycast) {
            return query.Raycast(start, from, to, filter, out var hit) && !hit.Hit;
        }

        if (query.InitSlicedFindPath(start, end, from, to, filter) == NavPathStatus.Failed) {
            return false;
        }

        query.UpdateSlicedFindPath(test == PathTest.Full ? int.MaxValue : Math.Max(1, budget), out _);

        // Complete and not Partial: Partial is "as far as I got", which is what an agent should
        // *walk* and is not an answer to "can I get there".
        return query.FinalizeSlicedFindPath(corridor, out _) == NavPathStatus.Complete;
    }
}
