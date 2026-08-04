// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Turns the data in a <c>.vxquery</c> into a query the runtime runs.</summary>
/// <remarks>
///     <para>
///         The same shape <see cref="UtilitySetContentCompiler" /> and
///         <see cref="GoapDomainContentCompiler" /> have, and against the same
///         <see cref="BehaviorTreeResolver" /> — which by now holds five tables and is plainly the
///         game's resolution table for AI content rather than a tree's.
///     </para>
///     <para>
///         ⚠ <b>Everything it cannot resolve is a diagnostic and a placeholder, never a refusal.</b>
///         Authoring a query before its trace test exists is the ordinary order of work. A generator
///         that does not resolve makes no points; a test that does not resolve <b>filters
///         everything</b>, which is the safe direction — an unfinished query answers "nowhere" rather
///         than confidently sending an agent to a spot nothing checked.
///     </para>
/// </remarks>
public static class QueryContentCompiler {
    /// <summary>Builds a query from a file.</summary>
    /// <param name="content">The file.</param>
    /// <param name="resolver">Where registered generators and tests are looked up.</param>
    /// <param name="diagnostics">Everything that could not be resolved.</param>
    /// <param name="query">The query.</param>
    /// <returns>Whether it compiled with nothing wrong.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> or <paramref name="resolver" /> is null.</exception>
    public static bool TryCompile(
        QueryContent content,
        BehaviorTreeResolver resolver,
        out IReadOnlyList<BehaviorTreeDiagnostic> diagnostics,
        out EnvironmentQuery? query
    ) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolver);

        var problems = new List<BehaviorTreeDiagnostic>();
        var generators = new List<IQueryGenerator>(content.Generators.Count);
        var tests = new List<QueryTest>(content.Tests.Count);

        foreach (var authored in content.Generators) {
            generators.Add(Generator(authored, resolver, problems));
        }

        foreach (var authored in content.Tests) {
            tests.Add(Test(authored, resolver, problems));
        }

        if (generators.Count == 0) {
            problems.Add(new(Symbol.None, "A query with no generators has nothing to score."));
        }

        query = new EnvironmentQuery(Symbol.Intern(content.Name), [.. generators], [.. tests]);
        diagnostics = problems;

        return problems.Count == 0;
    }

    static IQueryGenerator Generator(
        QueryGeneratorContent content,
        BehaviorTreeResolver resolver,
        List<BehaviorTreeDiagnostic> problems
    ) {
        switch (content.Kind) {
            case QueryGeneratorKind.Grid:
                return QueryGenerators.Grid(content.Extent, content.Inner, content.AroundQuerier);

            case QueryGeneratorKind.Circle:
                return QueryGenerators.Circle(content.Extent, content.Points, content.AroundQuerier);

            case QueryGeneratorKind.Donut:
                return QueryGenerators.Donut(
                    content.Inner,
                    content.Extent,
                    content.Rings,
                    content.Points,
                    content.AroundQuerier
                );

            case QueryGeneratorKind.Cone:
                return QueryGenerators.Cone(content.Degrees, content.Extent, content.Rings, content.Points);

            case QueryGeneratorKind.CurrentLocation:
                return QueryGenerators.CurrentLocation();

            default:
                if (resolver.TryGetGenerator(content.Source, out var registered)) {
                    return registered!;
                }

                problems.Add(
                    new(Symbol.Intern(content.Source), $"No query generator called '{content.Source}' is registered.")
                );

                // ⚠ Not null. A query whose generator is missing must still be a query the editor can
                // open, list and diff — a compiler that returned nothing would make an unfinished file
                // unopenable, which is the state every file is in while it is being written.
                return QueryGenerators.Composite();
        }
    }

    static QueryTest Test(
        QueryTestContent content,
        BehaviorTreeResolver resolver,
        List<BehaviorTreeDiagnostic> problems
    ) {
        IQueryTest reading;

        switch (content.Kind) {
            case QueryTestKind.Distance:
                reading = QueryTests.Distance(
                    content.FromContext ? QueryDistanceFrom.Context : QueryDistanceFrom.Querier
                );

                break;

            case QueryTestKind.Dot:
                reading = QueryTests.Dot(content.FromContext);

                break;

            default:
                if (resolver.TryGetTest(content.Source, out var registered)) {
                    reading = registered!;

                    break;
                }

                problems.Add(
                    new(Symbol.Intern(content.Source), $"No query test called '{content.Source}' is registered.")
                );

                // ⚠ Filters everything, which is the safe direction. An unfinished query that answered
                // "nowhere" is an agent that falls through to its next branch; one that answered with
                // a point nothing had checked is an agent walking into a wall with confidence.
                reading = Unresolved.Instance;

                break;
        }

        return new(reading, content.BuildCurve()) {
            Purpose = content.Purpose,
            Minimum = content.Minimum,
            Maximum = content.Maximum,
            Floor = content.Floor,
            Ceiling = content.Ceiling,
            Weight = content.Weight
        };
    }

    /// <summary>A test that never answers, so its query filters everything.</summary>
    sealed class Unresolved : IQueryTest {
        public static Unresolved Instance { get; } = new();

        public Symbol Name { get; } = Symbol.Intern("unresolved");

        public float Read(in AgentContext context, in QueryOrigin origin, in QueryPoint point) => float.NaN;
    }
}
