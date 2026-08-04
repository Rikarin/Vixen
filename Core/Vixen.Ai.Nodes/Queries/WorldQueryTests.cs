// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation;
using Vixen.Physics;
using Vixen.Physics.Queries;

namespace Vixen.Ai.Nodes;

/// <summary>What a trace is drawn between.</summary>
public enum TraceEnds : byte {
    /// <summary>From the point to what the query is about — "can I see the target from here".</summary>
    PointToContext,

    /// <summary>From the agent to the point — "can I get a clear shot at this spot".</summary>
    QuerierToPoint
}

/// <summary>The query generators and tests that need the world.</summary>
/// <remarks>
///     <para>
///         Five of doc 37 § P8's seven tests and one of its seven generators. They are here rather
///         than in <c>Vixen.Ai</c> for the reason the rest of this assembly is: a trace needs
///         <see cref="PhysicsWorld" />, a path needs <see cref="NavMeshQuery" />, and knowing where an
///         entity <i>is</i> needs <see cref="LocalTransform" /> — none of which the planners may see.
///     </para>
///     <para>
///         ⚠ <b>Every one of them is expensive per point, and that is the whole reason
///         <see cref="QueryTestPurpose.Filter" /> exists and why test order is the author's.</b> A
///         four-hundred-point grid with a trace at the top of the list is four hundred raycasts; the
///         same list with a distance filter first is a few dozen. The runtime does not reorder — a
///         query whose cost depended on a heuristic nobody can see would be one nobody could budget.
///     </para>
/// </remarks>
public static class WorldQueryTests {
    /// <summary>Whether a physics ray between two points is clear, as <c>1</c> or <c>0</c>.</summary>
    /// <param name="physics">The world to ask.</param>
    /// <param name="ends">What the ray is drawn between.</param>
    /// <param name="height">How far above each end to start and finish, in metres.</param>
    /// <param name="blockers">Which layers stop it, or null for everything solid.</param>
    /// <returns>The test.</returns>
    /// <remarks>
    ///     ⚠ <b>The eye height is not a detail.</b> A trace between two points on the floor hits the
    ///     floor, so a line-of-sight test without one rejects every point in the level and reads as
    ///     the query being broken.
    /// </remarks>
    public static IQueryTest Trace(
        PhysicsWorld physics,
        TraceEnds ends = TraceEnds.PointToContext,
        float height = 1.7f,
        PhysicsLayerMask? blockers = null
    ) => new TraceQueryTest(physics, ends, height, blockers);

    /// <summary>How many bodies are within a radius of the point.</summary>
    /// <param name="physics">The world to ask.</param>
    /// <param name="radius">How far to look, in metres.</param>
    /// <param name="height">How far above the point to centre the check.</param>
    /// <param name="layers">Which layers count, or null for everything solid.</param>
    /// <returns>The test.</returns>
    /// <remarks>
    ///     Counted rather than answered yes or no, so that "prefer somewhere with cover near it" and
    ///     "reject anywhere inside a wall" are the same test at two purposes.
    /// </remarks>
    public static IQueryTest Overlap(
        PhysicsWorld physics,
        float radius = 1f,
        float height = 0.5f,
        PhysicsLayerMask? layers = null
    ) => new OverlapQueryTest(physics, radius, height, layers);

    /// <summary>How far the agent would actually walk to reach the point, over the navmesh.</summary>
    /// <param name="query">The mesh to ask. ⚠ One search at a time — see <see cref="NavMeshQuery" />.</param>
    /// <param name="budget">How many nodes each search may open.</param>
    /// <returns>The test.</returns>
    /// <remarks>
    ///     ⚠ <b>A path search per point, and the cost is stated rather than hidden.</b> A grid of four
    ///     hundred is four hundred searches, which is not a thing to run on an interval for a crowd —
    ///     it is what a filtering test above it exists to make affordable. A point with no path reads
    ///     <see cref="float.NaN" />, which filters it rather than scoring it badly, because "cannot
    ///     get there" and "a long walk" are different facts.
    /// </remarks>
    public static IQueryTest PathLength(NavMeshQuery query, int budget = 256) => new PathQueryTest(query, budget);

    /// <summary>How far the point is off the navmesh, in metres. Anywhere unreachable filters.</summary>
    /// <param name="query">The mesh to ask.</param>
    /// <returns>The test.</returns>
    /// <remarks>
    ///     ⚠ <b>The cheapest world test there is, and the one that belongs at the top of most
    ///     lists.</b> A grid generated around an agent puts most of its points inside walls and off
    ///     ledges; rejecting those before anything traces or searches is the difference between a
    ///     query that is affordable and one that is not.
    /// </remarks>
    public static IQueryTest OnNavMesh(NavMeshQuery query) => new ProjectQueryTest(query);

    /// <summary>Points at every entity carrying a component.</summary>
    /// <typeparam name="T">The component.</typeparam>
    /// <param name="radius">How far from the origin to look, or zero for the whole world.</param>
    /// <returns>The generator.</returns>
    /// <remarks>
    ///     ⚠ <b>The point carries the entity as well as the position</b>, which is what makes "shoot at
    ///     the best target" the same machine as "stand in the best spot". A generator that produced
    ///     bare positions would have thrown away the only thing the caller wanted.
    /// </remarks>
    public static IQueryGenerator Entities<T>(float radius = 0f) => new EntityGenerator<T>(radius);
}

/// <summary>A physics ray between two points.</summary>
sealed class TraceQueryTest(PhysicsWorld physics, TraceEnds ends, float height, PhysicsLayerMask? blockers)
    : IQueryTest {
    readonly PhysicsWorld physics = physics ?? throw new ArgumentNullException(nameof(physics));

    public Symbol Name { get; } = Symbol.Intern("Trace");

    public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) {
        if (ends == TraceEnds.PointToContext && !origin.HasContext) {
            return float.NaN;
        }

        var lift = new Vector3(0f, height, 0f);

        var (from, to) = ends == TraceEnds.PointToContext
            ? (point.Position + lift, origin.Context + lift)
            : (origin.Querier + lift, point.Position + lift);

        var ray = to - from;
        var distance = ray.Length();

        if (distance <= MathUtil.ZeroTolerance) {
            return 1f;
        }

        var filter = blockers is { } mask ? QueryFilter.On(mask) : QueryFilter.Default;

        return physics.Raycast(from, ray / distance, distance, out _, filter) ? 0f : 1f;
    }
}

/// <summary>How many bodies are near the point.</summary>
sealed class OverlapQueryTest(PhysicsWorld physics, float radius, float height, PhysicsLayerMask? layers)
    : IQueryTest {
    readonly PhysicsWorld physics = physics ?? throw new ArgumentNullException(nameof(physics));
    readonly float radius = MathF.Max(0.01f, radius);

    public Symbol Name { get; } = Symbol.Intern("Overlap");

    public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) {
        var centre = point.Position + new Vector3(0f, height, 0f);
        var filter = layers is { } mask ? QueryFilter.On(mask) : QueryFilter.Default;

        // ⚠ Four rays rather than a sphere cast, and it is a deliberate trade: what a cover query
        // wants to know is "is there something solid beside me", which four short horizontal probes
        // answer well enough to rank points by — and the alternative is a shape query per point of a
        // grid, which is the cost this whole pipeline is arranged to avoid.
        var count = 0;

        ReadOnlySpan<Vector3> directions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ];

        foreach (var direction in directions) {
            if (physics.Raycast(centre, direction, radius, out _, filter)) {
                count++;
            }
        }

        return count;
    }
}

/// <summary>How far the agent would walk to the point.</summary>
sealed class PathQueryTest(NavMeshQuery query, int budget) : IQueryTest {
    static readonly Vector3 Extents = new(4f, 4f, 4f);

    readonly NavMeshQuery query = query ?? throw new ArgumentNullException(nameof(query));
    readonly NavPolyRef[] corridor = new NavPolyRef[256];

    public Symbol Name { get; } = Symbol.Intern("PathLength");

    public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) {
        var filter = NavQueryFilter.Default;

        if (!query.FindNearestPoly(origin.Querier, Extents, filter, out var start, out var from)
            || !query.FindNearestPoly(point.Position, Extents, filter, out var end, out var to)
            || query.InitSlicedFindPath(start, end, from, to, filter) == NavPathStatus.Failed) {
            return float.NaN;
        }

        query.UpdateSlicedFindPath(Math.Max(1, budget), out _);

        // Complete and not Partial: "as far as I got" is what an agent should walk and is not an
        // answer to "how far away is this".
        if (query.FinalizeSlicedFindPath(corridor, out var count) != NavPathStatus.Complete || count == 0) {
            return float.NaN;
        }

        // The corridor's length is polygons rather than metres, so the straight line between the ends
        // scaled by how far the corridor wandered is what stands in for a funnelled length. A funnel
        // per point would be a second search per point, which the budget is here to refuse.
        return (to - from).Length() * MathF.Max(1f, count / 4f);
    }
}

/// <summary>How far the point is off the navmesh.</summary>
sealed class ProjectQueryTest(NavMeshQuery query) : IQueryTest {
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    readonly NavMeshQuery query = query ?? throw new ArgumentNullException(nameof(query));

    public Symbol Name { get; } = Symbol.Intern("OnNavMesh");

    public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) =>
        query.FindNearestPoly(point.Position, Extents, NavQueryFilter.Default, out _, out var on)
            ? (on - point.Position).Length()
            : float.NaN;
}

/// <summary>A point at every entity carrying a component.</summary>
sealed class EntityGenerator<T> : IQueryGenerator {
    readonly QueryDescription description = new QueryDescription().WithAll<T, LocalTransform>();
    readonly float radius;

    public EntityGenerator(float radius) => this.radius = MathF.Max(0f, radius);

    public Symbol Name { get; } = Symbol.Intern($"Entities<{typeof(T).Name}>");

    public int Estimate => 32;

    public void Generate(in AgentContext context, in QueryOrigin origin, List<QueryPoint> points) {
        ArgumentNullException.ThrowIfNull(points);

        var around = origin.Around;
        var limit = radius * radius;

        foreach (var chunk in context.World.Chunks(description)) {
            var transforms = chunk.ReadValues<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!QueryGenerators.Room(points)) {
                    return;
                }

                var position = transforms[index].Position;

                if (radius > 0f && (position - around).LengthSquared() > limit) {
                    continue;
                }

                points.Add(new(position, entities[index]));
            }
        }
    }
}
