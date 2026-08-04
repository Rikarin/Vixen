// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai;

/// <summary>What a test is for.</summary>
/// <remarks>
///     ⚠ <b>Unreal's three, and the distinction earns its keep the first time somebody writes a
///     query.</b> "Must have line of sight" and "prefer more cover" are the same reading used two
///     ways, and a pipeline with only one of them makes the first into a score of zero that any
///     other test can outvote — which is how an agent ends up standing in the open because the spot
///     was otherwise excellent.
/// </remarks>
public enum QueryTestPurpose : byte {
    /// <summary>Reject a point that fails, and contribute nothing to the score.</summary>
    Filter,

    /// <summary>Contribute to the score, and reject nothing.</summary>
    Score,

    /// <summary>Both: reject a point outside the bounds, and score the ones that survive.</summary>
    Both
}

/// <summary>Reads one number about one candidate point.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 4's other query seam. It answers about a <i>point</i> where an
///         <see cref="IUtilityInput" /> answers about an <i>agent</i>, and that is the only difference
///         between the two halves of § D14's claim — everything after the reading, the curve, the
///         clamp, the mean and the veto, is shared code.
///     </para>
///     <para>
///         ⚠ <b>The reading is raw and unnormalised.</b> Distance comes back in metres and a dot
///         product in <c>[-1,1]</c>; <see cref="QueryTest" /> normalises against its own bounds before
///         the curve sees anything, for the reason <see cref="IUtilityInput" />'s remarks give at
///         length — a curve whose domain were "0 to whatever this level's size is" could not be drawn
///         and could not be shared.
///     </para>
/// </remarks>
public interface IQueryTest {
    /// <summary>What it is called.</summary>
    Symbol Name { get; }

    /// <summary>Reads the point.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="origin">Where the query is measured from.</param>
    /// <param name="point">The candidate.</param>
    /// <returns>The raw reading. <see cref="float.NaN" /> means "cannot answer", which filters.</returns>
    float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point);
}

/// <summary>A reading written as a lambda.</summary>
/// <param name="context">The agent.</param>
/// <param name="origin">Where the query is measured from.</param>
/// <param name="point">The candidate.</param>
/// <returns>The raw reading.</returns>
public delegate float QueryReading(in AgentContext context, in QueryOrigin origin, in QueryPoint point);

/// <summary>One reading, normalised, clamped, curved, and used to filter or to score or to both.</summary>
/// <remarks>
///     <para>
///         <b>This is <see cref="UtilityConsideration" /> with a point substituted for an agent</b>,
///         which is doc 37 § D14 in one sentence. The curve is the same <see cref="IResponseCurve" />
///         the utility editor draws, and the combination is the same
///         <see cref="CandidateScoring.Score{TSource}" /> — so an author who has learned one of the
///         two has learned both.
///     </para>
///     <para>
///         ⚠ <b>A test that cannot answer filters the point rather than scoring it zero.</b> "There is
///         no path to here" and "the path to here is long" are different facts, and a reading of
///         <see cref="float.NaN" /> says the first. Scoring it zero would be a veto that a
///         <see cref="QueryTestPurpose.Score" /> test has no business casting.
///     </para>
/// </remarks>
public sealed class QueryTest {
    /// <summary>Creates a test.</summary>
    /// <param name="test">What it reads.</param>
    /// <param name="curve">The shape the normalised reading goes through.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public QueryTest(IQueryTest test, IResponseCurve? curve = null) {
        ArgumentNullException.ThrowIfNull(test);

        Test = test;
        Curve = curve ?? ResponseCurve.Identity;
    }

    /// <summary>What it reads.</summary>
    public IQueryTest Test { get; }

    /// <summary>The shape the normalised reading goes through.</summary>
    public IResponseCurve Curve { get; }

    /// <summary>What it is called.</summary>
    public Symbol Name => Test.Name;

    /// <summary>What it is for.</summary>
    public QueryTestPurpose Purpose { get; init; } = QueryTestPurpose.Score;

    /// <summary>The reading that normalises to zero.</summary>
    public float Minimum { get; init; }

    /// <summary>The reading that normalises to one.</summary>
    public float Maximum { get; init; } = 1f;

    /// <summary>A reading below this filters the point. Ignored by a pure scoring test.</summary>
    public float Floor { get; init; } = float.NegativeInfinity;

    /// <summary>A reading above this filters the point. Ignored by a pure scoring test.</summary>
    public float Ceiling { get; init; } = float.PositiveInfinity;

    /// <summary>How much this test counts for, against the others.</summary>
    /// <remarks>
    ///     ⚠ Applied to the <i>curved</i> value as a pull toward one, not as a multiplier on the
    ///     score. A weight that multiplied would break the geometric mean's whole property — that the
    ///     count of factors is irrelevant — because a factor of 2 is not in <c>[0,1]</c> and a factor
    ///     of 0.5 would be a permanent half-veto on an otherwise perfect point.
    /// </remarks>
    public float Weight { get; init; } = 1f;

    /// <summary>Whether it rejects points.</summary>
    public bool Filters => Purpose is QueryTestPurpose.Filter or QueryTestPurpose.Both;

    /// <summary>Whether it contributes to the score.</summary>
    public bool Scores => Purpose is QueryTestPurpose.Score or QueryTestPurpose.Both;

    /// <summary>Runs it against one point.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="origin">Where the query is measured from.</param>
    /// <param name="point">The candidate.</param>
    /// <param name="score">Its factor, in <c>[0,1]</c>. One when this test does not score.</param>
    /// <returns>Whether the point survives.</returns>
    public bool Run(in AgentContext context, in QueryOrigin origin, in QueryPoint point, out float score) {
        var reading = Test.Read(in context, in origin, in point);

        score = 1f;

        if (float.IsNaN(reading)) {
            return false;
        }

        if (Filters && (reading < Floor || reading > Ceiling)) {
            return false;
        }

        if (!Scores) {
            return true;
        }

        var span = MathF.Abs(Maximum - Minimum) < 1e-6f ? 1f : Maximum - Minimum;
        var normalised = Math.Clamp((reading - Minimum) / span, 0f, 1f);
        var curved = Math.Clamp(Curve.Evaluate(normalised), 0f, 1f);
        var weight = Math.Clamp(Weight, 0f, 1f);

        score = 1f - (weight * (1f - curved));

        return true;
    }
}

/// <summary>The tests that need nothing but arithmetic.</summary>
/// <remarks>
///     Two of doc 37 § P8's seven. The other five — trace, pathfinding, overlap, project and tag —
///     need a physics world, a navmesh or a gameplay tag, none of which <c>Vixen.Ai</c> may see, so
///     they live in <c>Vixen.Ai.Nodes</c> beside the other nodes that touch the world.
/// </remarks>
public static class QueryTests {
    /// <summary>A test from a lambda.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="reading">What it reads.</param>
    /// <returns>The test.</returns>
    public static IQueryTest From(string name, QueryReading reading) => new DelegateQueryTest(Symbol.Intern(name), reading);

    /// <summary>How far the point is from something, in metres.</summary>
    /// <param name="from">What to measure from.</param>
    /// <returns>The test.</returns>
    public static IQueryTest Distance(QueryDistanceFrom from = QueryDistanceFrom.Querier) =>
        new DistanceQueryTest(from);

    /// <summary>
    ///     How far in front of the agent the point is: the cosine of the angle between the agent's
    ///     look direction and the direction to the point, in <c>[-1,1]</c>.
    /// </summary>
    /// <param name="towardContext">
    ///     Whether the agent's direction is taken as "toward the context" rather than "toward the
    ///     point". True answers "is this point on the same side as my target".
    /// </param>
    /// <returns>The test.</returns>
    public static IQueryTest Dot(bool towardContext = true) => new DotQueryTest(towardContext);
}

/// <summary>What a distance test measures from.</summary>
public enum QueryDistanceFrom : byte {
    /// <summary>The agent.</summary>
    Querier,

    /// <summary>What the query is about.</summary>
    Context
}

/// <summary>Distance, in metres.</summary>
sealed class DistanceQueryTest(QueryDistanceFrom from) : IQueryTest {
    public Symbol Name { get; } = Symbol.Intern(from == QueryDistanceFrom.Context ? "DistanceToContext" : "Distance");

    public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) {
        if (from == QueryDistanceFrom.Context && !origin.HasContext) {
            // ⚠ Not zero. A query authored around a target and run without one must reject its points
            // rather than score every one of them as "right on top of it".
            return float.NaN;
        }

        var anchor = from == QueryDistanceFrom.Context ? origin.Context : origin.Querier;

        return (point.Position - anchor).Length();
    }
}

/// <summary>The cosine of the angle between a direction and the direction to the point.</summary>
sealed class DotQueryTest(bool towardContext) : IQueryTest {
    public Symbol Name { get; } = Symbol.Intern("Dot");

    public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) {
        var offset = point.Position - origin.Querier;
        var to = new Vector3(offset.X, 0f, offset.Z);

        if (to.LengthSquared() <= MathUtil.ZeroTolerance) {
            // The agent's own position: it is exactly in front of itself by convention, because the
            // alternative is a NaN that filters the current location out of every query that scores
            // direction.
            return 1f;
        }

        var aim = towardContext && origin.HasContext ? origin.Context - origin.Querier : to;
        var facing = new Vector3(aim.X, 0f, aim.Z);

        return facing.LengthSquared() <= MathUtil.ZeroTolerance
            ? 1f
            : Vector3.Dot(Vector3.Normalize(facing), Vector3.Normalize(to));
    }
}

/// <summary>A test that is a lambda.</summary>
sealed class DelegateQueryTest(Symbol name, QueryReading reading) : IQueryTest {
    readonly QueryReading reading = reading ?? throw new ArgumentNullException(nameof(reading));

    public Symbol Name => name;

    public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) =>
        reading(in context, in origin, in point);
}
