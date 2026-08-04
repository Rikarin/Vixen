// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Nodes.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Navigation;

namespace Vixen.Ai.Nodes;

/// <summary>What a GOAP action costs to reach, asked of the navmesh.</summary>
/// <param name="query">The mesh to ask. ⚠ One search at a time — see the remarks.</param>
/// <param name="perMetre">What a metre of travel adds to an action's cost.</param>
/// <param name="budget">How many nodes one query may open.</param>
/// <remarks>
///     <para>
///         doc 37 § D12's second implementation of <see cref="IActionCostModel" />, and the guide is
///         supposed to say plainly what it costs, so: <b>this is a navigation query per action per
///         resolve.</b> A domain of forty actions asks the mesh forty times every time an agent
///         thinks. That is affordable at doc 28's stated scale — <i>"the few dozen agents where
///         emergent behaviour is the point"</i> — and is not affordable for a crowd.
///     </para>
///     <para>
///         ⚠ <b>Budgeted rather than exact, for P4's reason.</b> Vixen bakes no coarse graph, so there
///         is no hierarchical query to ask; a search stopped at a node budget answers the same
///         question with the same shape of cost. A destination the budget could not reach is costed as
///         its straight line times <see cref="UnreachablePenalty" /> rather than refused, because a
///         cost model that vetoed would turn "I could not afford to check" into "there is no way
///         there".
///     </para>
///     <para>
///         ⚠ <b><see cref="NavMeshQuery" /> holds one node pool and runs one search at a time.</b> A
///         cost model is shared by every agent of its domain, so a queue configured with a
///         <c>JobScheduler</c> needs a model per parallel search or none at all.
///     </para>
/// </remarks>
public sealed class NavigationCostModel(NavMeshQuery query, float perMetre = 0.1f, int budget = 128)
    : IActionCostModel {
    static readonly Vector3 Extents = new(4f, 4f, 4f);

    readonly NavMeshQuery query = query ?? throw new ArgumentNullException(nameof(query));
    readonly NavPolyRef[] corridor = new NavPolyRef[128];
    readonly NavPathPoint[] points = new NavPathPoint[64];

    /// <summary>What an unreachable target's straight-line distance is multiplied by.</summary>
    public float UnreachablePenalty { get; init; } = 4f;

    /// <inheritdoc />
    public float Cost(in AgentContext context, GoapAction action, Vector3 from, in GoapTarget target) {
        ArgumentNullException.ThrowIfNull(action);

        if (!target.Found) {
            return MathF.Max(1f, action.BaseCost);
        }

        var straight = (target.Position - from).Length();

        return MathF.Max(1f, action.BaseCost + (Travel(from, target.Position, straight) * perMetre));
    }

    /// <summary>How far the agent would actually walk, or a penalised straight line.</summary>
    float Travel(Vector3 from, Vector3 to, float straight) {
        var filter = NavQueryFilter.Default;

        if (!query.FindNearestPoly(from, Extents, filter, out var start, out var origin)
            || !query.FindNearestPoly(to, Extents, filter, out var end, out var destination)) {
            return straight * UnreachablePenalty;
        }

        if (query.InitSlicedFindPath(start, end, origin, destination, filter) == NavPathStatus.Failed) {
            return straight * UnreachablePenalty;
        }

        query.UpdateSlicedFindPath(Math.Max(1, budget), out _);

        if (query.FinalizeSlicedFindPath(corridor, out var count) != NavPathStatus.Complete) {
            return straight * UnreachablePenalty;
        }

        var found = query.FindStraightPath(origin, destination, corridor.AsSpan(0, count), points);
        var travelled = 0f;

        for (var index = 1; index < found; index++) {
            travelled += (points[index].Position - points[index - 1].Position).Length();
        }

        // A one-point path is the agent already standing on the destination polygon, which is a
        // straight line rather than nothing.
        return found > 1 ? travelled : straight;
    }
}

/// <summary>Wiring the GOAP planner to the engine's idea of where things are.</summary>
/// <remarks>
///     ⚠ <b><c>Vixen.Ai</c> cannot see a transform</b>, which is doc 37's whole argument for putting
///     the planners in <c>Core/</c> — so the planner asks for a position through a delegate and this
///     is the one that reads <c>LocalTransform</c>. A game with no transforms hands over nothing, every
///     distance reads as zero, and the search plans by action cost alone.
/// </remarks>
public static class GoapWiring {
    /// <summary>A sensor table that knows how to find where an agent is.</summary>
    /// <returns>The table, ready for target sensors to be added to it.</returns>
    public static GoapTargetSensors Sensors() =>
        new() {
            AgentPosition = (in AgentContext context, out Vector3 position) =>
                AgentTarget.TryPositionOf(context.World, context.Entity, out position)
        };

    /// <summary>A target sensor that reads a blackboard key, the way a movement task does.</summary>
    /// <param name="key">The key holding a <c>Vector3</c> or an <c>Entity</c>.</param>
    /// <returns>The sensor.</returns>
    /// <remarks>
    ///     The bridge between the three planners: perception writes a key, a tree's <c>MoveTo</c>
    ///     reads it, and a GOAP action targets it — one fact, three consumers.
    /// </remarks>
    public static GoapTargetLookup FromKey(BlackboardKey key) =>
        (in AgentContext context, out Vector3 position, out Vixen.Core.Entity target) =>
            AgentTarget.TryResolve(in context, key, out position, out target);
}
