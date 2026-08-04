// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Ai;

/// <summary>Where a query's candidates are measured from.</summary>
/// <remarks>
///     ⚠ <b>The querier and the context are two things, and Unreal calls them that for a reason.</b>
///     "Points around <i>me</i>, scored by distance to <i>the enemy</i>" needs both, and a generator
///     that only knew the agent could not express the commonest cover query there is.
/// </remarks>
/// <param name="Querier">Where the agent is.</param>
/// <param name="Context">What the query is about — the target, the objective, the noise.</param>
/// <param name="HasContext">Whether <paramref name="Context" /> means anything.</param>
/// <param name="ContextEntity">The entity the context came from, if it came from one.</param>
public readonly record struct QueryOrigin(
    Vector3 Querier,
    Vector3 Context,
    bool HasContext = false,
    Entity ContextEntity = default
) {
    /// <summary>The context if there is one, and the querier otherwise.</summary>
    /// <remarks>
    ///     A query authored around a target and run with none generates around the agent rather than
    ///     around the origin of the world, which is what an author meant every time it happens.
    /// </remarks>
    public Vector3 Around => HasContext ? Context : Querier;
}

/// <summary>Makes the candidate points a query scores.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 4's seam. A generator is deliberately dumb: it produces positions and does
///         not know what they are for, because every question about whether a position is any
///         <i>good</i> is a test's. That split is what lets one grid generator serve "where do I take
///         cover", "where do I throw this" and "where do I flank from".
///     </para>
///     <para>
///         ⚠ <b>A generator may not read the world.</b> The shipped ones are arithmetic over an origin,
///         and the two that are not — points from entities, points on a navmesh — take what they need
///         through a seam of their own, because <c>Vixen.Ai</c> can see neither a transform nor a
///         mesh. Doc 37's whole argument for putting the planners in <c>Core/</c> is that they do not
///         reach for an engine.
///     </para>
/// </remarks>
public interface IQueryGenerator {
    /// <summary>What it is called.</summary>
    Symbol Name { get; }

    /// <summary>Roughly how many points it will make, so a run can size its list once.</summary>
    int Estimate { get; }

    /// <summary>Generates.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="origin">Where to generate around.</param>
    /// <param name="points">Where to put them.</param>
    void Generate(in AgentContext context, in QueryOrigin origin, List<QueryPoint> points);
}

/// <summary>The generators that ship.</summary>
/// <remarks>
///     Six of doc 37 § P8's seven. The seventh — entities with a component — needs to know where an
///     entity <i>is</i>, which is <c>Vixen.Engine</c>'s, so it lives in <c>Vixen.Ai.Nodes</c> beside
///     the other nodes that touch the world.
/// </remarks>
public static class QueryGenerators {
    /// <summary>How many points a generator may make, whatever its parameters say.</summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling rather than a warning.</b> A grid is <c>(2·extent/spacing + 1)²</c> points and
    ///     a designer who types <c>0.1</c> into a spacing field gets four hundred thousand of them —
    ///     each of which may be traced. The bound is what turns that from a hung frame into a query
    ///     that returns a coarse answer and a number an author can see.
    /// </remarks>
    public const int MaximumPoints = 4096;

    /// <summary>A square grid of points on the ground, centred on the origin.</summary>
    /// <param name="extent">How far from the centre it reaches, in metres.</param>
    /// <param name="spacing">How far apart the points are.</param>
    /// <param name="aroundQuerier">Whether to centre on the agent rather than on the context.</param>
    /// <returns>The generator.</returns>
    public static IQueryGenerator Grid(float extent = 10f, float spacing = 1f, bool aroundQuerier = true) =>
        new GridGenerator(extent, spacing, aroundQuerier);

    /// <summary>A ring of points at a fixed radius.</summary>
    /// <param name="radius">How far out.</param>
    /// <param name="count">How many.</param>
    /// <param name="aroundQuerier">Whether to centre on the agent rather than on the context.</param>
    /// <returns>The generator.</returns>
    public static IQueryGenerator Circle(float radius = 5f, int count = 16, bool aroundQuerier = false) =>
        new DonutGenerator(radius, radius, 1, count, aroundQuerier);

    /// <summary>Rings of points between two radii — the shape a "stand near but not on top of" wants.</summary>
    /// <param name="inner">The nearest radius.</param>
    /// <param name="outer">The furthest.</param>
    /// <param name="rings">How many rings.</param>
    /// <param name="perRing">How many points on each.</param>
    /// <param name="aroundQuerier">Whether to centre on the agent rather than on the context.</param>
    /// <returns>The generator.</returns>
    public static IQueryGenerator Donut(
        float inner = 3f,
        float outer = 8f,
        int rings = 3,
        int perRing = 12,
        bool aroundQuerier = false
    ) => new DonutGenerator(inner, outer, rings, perRing, aroundQuerier);

    /// <summary>A fan of points in front of the agent, aimed at the context.</summary>
    /// <param name="degrees">How wide the fan is.</param>
    /// <param name="radius">How far it reaches.</param>
    /// <param name="arcs">How many rings of it.</param>
    /// <param name="perArc">How many points on each.</param>
    /// <returns>The generator.</returns>
    public static IQueryGenerator Cone(float degrees = 90f, float radius = 8f, int arcs = 3, int perArc = 7) =>
        new ConeGenerator(degrees, radius, arcs, perArc);

    /// <summary>The one point the agent is already standing on.</summary>
    /// <returns>The generator.</returns>
    /// <remarks>
    ///     ⚠ <b>Not a degenerate case — it is how "should I move at all" is asked.</b> A query whose
    ///     candidates are the grid <i>and</i> the current position lets the tests decide, and without
    ///     it an agent re-picks a spot a centimetre away every interval and shuffles for ever.
    /// </remarks>
    public static IQueryGenerator CurrentLocation() => new CurrentLocationGenerator();

    /// <summary>Every point of several generators, in order.</summary>
    /// <param name="generators">Them.</param>
    /// <returns>The generator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generators" /> is null.</exception>
    public static IQueryGenerator Composite(params IQueryGenerator[] generators) => new CompositeGenerator(generators);

    /// <summary>Whether a generator may add another point.</summary>
    /// <param name="points">The list being filled.</param>
    /// <returns>Whether there is room under <see cref="MaximumPoints" />.</returns>
    /// <remarks>
    ///     Public because <see cref="IQueryGenerator" /> is a seam: a project's own generator, and
    ///     the two in <c>Vixen.Ai.Nodes</c>, have to honour the same ceiling as the shipped ones or
    ///     the bound is one only half the generators respect.
    /// </remarks>
    public static bool Room(List<QueryPoint> points) {
        ArgumentNullException.ThrowIfNull(points);

        return points.Count < MaximumPoints;
    }
}

/// <summary>A square grid on the ground.</summary>
sealed class GridGenerator(float extent, float spacing, bool aroundQuerier) : IQueryGenerator {
    readonly float extent = MathF.Max(0f, extent);
    readonly float spacing = MathF.Max(0.05f, spacing);

    public Symbol Name { get; } = Symbol.Intern("Grid");

    public int Estimate {
        get {
            var side = (int)(this.extent / this.spacing * 2f) + 1;

            return Math.Min(side * side, QueryGenerators.MaximumPoints);
        }
    }

    public void Generate(in AgentContext context, in QueryOrigin origin, List<QueryPoint> points) {
        ArgumentNullException.ThrowIfNull(points);

        var centre = aroundQuerier ? origin.Querier : origin.Around;
        var steps = (int)(extent / spacing);

        for (var z = -steps; z <= steps; z++) {
            for (var x = -steps; x <= steps; x++) {
                if (!QueryGenerators.Room(points)) {
                    return;
                }

                points.Add(new(centre + new Vector3(x * spacing, 0f, z * spacing)));
            }
        }
    }
}

/// <summary>Rings between two radii. A circle is the degenerate one with a single ring.</summary>
sealed class DonutGenerator(float inner, float outer, int rings, int perRing, bool aroundQuerier) : IQueryGenerator {
    readonly float inner = MathF.Max(0f, MathF.Min(inner, outer));
    readonly float outer = MathF.Max(inner, outer);
    readonly int rings = Math.Max(1, rings);
    readonly int perRing = Math.Max(1, perRing);

    public Symbol Name { get; } = Symbol.Intern("Donut");

    public int Estimate => Math.Min(rings * perRing, QueryGenerators.MaximumPoints);

    public void Generate(in AgentContext context, in QueryOrigin origin, List<QueryPoint> points) {
        ArgumentNullException.ThrowIfNull(points);

        var centre = aroundQuerier ? origin.Querier : origin.Around;
        var step = rings == 1 ? 0f : (outer - inner) / (rings - 1);

        for (var ring = 0; ring < rings; ring++) {
            var radius = inner + (step * ring);

            for (var index = 0; index < perRing; index++) {
                if (!QueryGenerators.Room(points)) {
                    return;
                }

                var angle = index / (float)perRing * MathUtil.TwoPi;

                points.Add(new(centre + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius)));
            }
        }
    }
}

/// <summary>A fan in front of the agent, aimed at the context.</summary>
sealed class ConeGenerator(float degrees, float radius, int arcs, int perArc) : IQueryGenerator {
    readonly float half = float.DegreesToRadians(Math.Clamp(degrees, 0f, 360f)) * 0.5f;
    readonly float radius = MathF.Max(0.01f, radius);
    readonly int arcs = Math.Max(1, arcs);
    readonly int perArc = Math.Max(1, perArc);

    public Symbol Name { get; } = Symbol.Intern("Cone");

    public int Estimate => Math.Min(arcs * perArc, QueryGenerators.MaximumPoints);

    public void Generate(in AgentContext context, in QueryOrigin origin, List<QueryPoint> points) {
        ArgumentNullException.ThrowIfNull(points);

        var aim = origin.HasContext ? origin.Context - origin.Querier : Vector3.UnitZ;
        var flat = new Vector3(aim.X, 0f, aim.Z);

        // A cone with no direction is a cone aimed at nothing, and normalising a zero vector produces
        // NaN positions that poison every test downstream — so it falls back to +Z rather than
        // generating a fan of quiet corruption.
        var forward = flat.LengthSquared() > MathUtil.ZeroTolerance ? Vector3.Normalize(flat) : Vector3.UnitZ;

        var facing = MathF.Atan2(forward.X, forward.Z);
        var step = arcs == 1 ? radius : radius / arcs;

        for (var arc = 1; arc <= arcs; arc++) {
            var distance = step * arc;

            for (var index = 0; index < perArc; index++) {
                if (!QueryGenerators.Room(points)) {
                    return;
                }

                var fraction = perArc == 1 ? 0.5f : index / (float)(perArc - 1);
                var angle = facing - half + (fraction * half * 2f);

                points.Add(
                    new(origin.Querier + new Vector3(MathF.Sin(angle) * distance, 0f, MathF.Cos(angle) * distance))
                );
            }
        }
    }
}

/// <summary>The point the agent is standing on.</summary>
sealed class CurrentLocationGenerator : IQueryGenerator {
    public Symbol Name { get; } = Symbol.Intern("CurrentLocation");

    public int Estimate => 1;

    public void Generate(in AgentContext context, in QueryOrigin origin, List<QueryPoint> points) {
        ArgumentNullException.ThrowIfNull(points);

        if (QueryGenerators.Room(points)) {
            points.Add(new(origin.Querier, context.Entity));
        }
    }
}

/// <summary>Several generators, one after another.</summary>
sealed class CompositeGenerator : IQueryGenerator {
    readonly IQueryGenerator[] generators;

    public CompositeGenerator(IQueryGenerator[] generators) {
        ArgumentNullException.ThrowIfNull(generators);

        this.generators = generators;
    }

    public Symbol Name { get; } = Symbol.Intern("Composite");

    public int Estimate {
        get {
            var total = 0;

            foreach (var generator in generators) {
                total += generator.Estimate;
            }

            return Math.Min(total, QueryGenerators.MaximumPoints);
        }
    }

    public void Generate(in AgentContext context, in QueryOrigin origin, List<QueryPoint> points) {
        foreach (var generator in generators) {
            generator.Generate(in context, in origin, points);
        }
    }
}
