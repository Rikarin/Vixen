// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>
///     A list of generators and a list of tests: "where should I stand", answered by scoring
///     candidate points the way a utility set scores candidate actions.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 37 § D14, built.</b> Unreal's EQS generates candidate points, runs scored tests over
///         them and takes the best; utility scoring generates candidate actions, runs scored
///         considerations over them and takes the best. Those are the same machine, so this is a
///         <see cref="IScoredCandidateSet{T}" /> and its scoring goes through
///         <see cref="CandidateScoring" /> — the same weighted geometric mean, with the same zero
///         rule, as <see cref="UtilitySet" />.
///     </para>
///     <para>
///         ⚠ <b>A list asset, not a second node graph.</b> Generators, then tests in order, and no
///         wiring decisions anywhere — which is what Unreal's EQS editor also is, once you look past
///         the fact that it is drawn on a graph canvas: a root with a fixed list of children.
///     </para>
///     <para>
///         ⚠ <b>Cheap tests first is the author's decision and the query honours the order.</b> A
///         filtering test rejects a point and the run stops reading it, so putting a trace above a
///         distance check costs a raycast per point that a subtraction would have thrown away. The
///         editor says so; the runtime does not reorder, because a runtime that reordered would make
///         a query's cost unpredictable and its behaviour depend on a heuristic nobody can see.
///     </para>
/// </remarks>
public sealed class EnvironmentQuery : IScoredCandidateSet<QueryPoint> {
    readonly IQueryGenerator[] generators;
    readonly QueryTest[] tests;
    readonly List<QueryPoint> candidates = [];

    /// <summary>Creates a query.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="generators">What makes the candidates.</param>
    /// <param name="tests">What filters and scores them, in order.</param>
    /// <exception cref="ArgumentNullException">Either list is null.</exception>
    public EnvironmentQuery(Symbol name, IQueryGenerator[] generators, params QueryTest[] tests) {
        ArgumentNullException.ThrowIfNull(generators);
        ArgumentNullException.ThrowIfNull(tests);

        Name = name;
        this.generators = generators;
        this.tests = tests;
    }

    /// <summary>What it is called.</summary>
    public Symbol Name { get; }

    /// <summary>What makes the candidates.</summary>
    public ReadOnlySpan<IQueryGenerator> Generators => generators;

    /// <summary>What filters and scores them, in order.</summary>
    public ReadOnlySpan<QueryTest> Tests => tests;

    /// <summary>How many points the generators expect to make.</summary>
    public int Estimate {
        get {
            var total = 0;

            foreach (var generator in generators) {
                total += generator.Estimate;
            }

            return Math.Min(total, QueryGenerators.MaximumPoints);
        }
    }

    /// <summary>Where the last <see cref="Run" /> was measured from.</summary>
    /// <remarks>
    ///     Kept so that <see cref="IScoredCandidateSet{T}" />'s members can answer about the points a
    ///     run produced — an interface that took an origin would be one a utility set could not
    ///     implement, and the whole value of the shared abstraction is that both do.
    /// </remarks>
    public QueryOrigin LastOrigin { get; private set; }

    /// <summary>Runs it.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="origin">Where to measure from.</param>
    /// <param name="results">Where the points go. Cleared first.</param>
    /// <returns>Whether anything survived.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results" /> is null.</exception>
    public bool Run(in AgentContext context, in QueryOrigin origin, QueryResults results) {
        ArgumentNullException.ThrowIfNull(results);

        results.Begin(Name, tests.Length);
        LastOrigin = origin;

        candidates.Clear();
        candidates.EnsureCapacity(Estimate);

        foreach (var generator in generators) {
            generator.Generate(in context, in origin, candidates);
        }

        results.Generated = candidates.Count;

        Span<float> factors = tests.Length <= 32 ? stackalloc float[32] : new float[tests.Length];

        foreach (var point in candidates) {
            factors.Clear();

            var filtered = false;
            var scoring = 0;

            for (var index = 0; index < tests.Length; index++) {
                var test = tests[index];

                if (!test.Run(in context, in origin, in point, out var factor)) {
                    filtered = true;

                    break;
                }

                if (test.Scores) {
                    factors[scoring++] = factor;
                }
            }

            // ⚠ Through the shared scorer and not through a mean of its own. A query cannot *stream*
            // its factors — a test may filter the point, so filtering and scoring are interleaved
            // down one list — so it collects what survived and hands it to the same routine a utility
            // action's considerations go through.
            var survived = new FactorSpan(factors[..scoring]);
            var score = filtered ? 0f : CandidateScoring.Score(in survived, 1f);

            results.Add(in point, score, filtered, factors[..tests.Length]);
        }

        return results.Best >= 0;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The candidates of the last run, not a fresh generation.</b> The interface exists so
    ///     that a table, an overlay or a preview can drive a query and a utility set with one piece of
    ///     code, and re-generating on every read would make scrolling a list of two hundred points
    ///     re-run two hundred queries.
    /// </remarks>
    public int CandidateCount => candidates.Count;

    /// <inheritdoc />
    public QueryPoint CandidateAt(int index) => candidates[index];

    /// <inheritdoc />
    public Symbol CandidateName(int index) => Name;

    /// <inheritdoc />
    public int FactorsOf(int index) => tests.Length;

    /// <inheritdoc />
    public Symbol FactorName(int index, int factor) => tests[factor].Name;

    /// <inheritdoc />
    public float ScoreOf(in AgentContext context, int index, Span<float> detail = default) {
        var point = candidates[index];
        var origin = LastOrigin;

        Span<float> factors = tests.Length <= 32 ? stackalloc float[32] : new float[tests.Length];
        var scoring = 0;

        for (var test = 0; test < tests.Length; test++) {
            if (!tests[test].Run(in context, in origin, in point, out var factor)) {
                if (detail.Length >= tests.Length) {
                    detail[test] = 0f;
                }

                return 0f;
            }

            if (detail.Length >= tests.Length) {
                detail[test] = factor;
            }

            if (tests[test].Scores) {
                factors[scoring++] = factor;
            }
        }

        var survived = new FactorSpan(factors[..scoring]);

        return CandidateScoring.Score(in survived, 1f);
    }
}

/// <summary>Every query a game's agents may run, by index.</summary>
/// <remarks>
///     Filled the way <c>BehaviorTreeLibrary</c> and <c>UtilitySetLibrary</c> are, and named the same
///     way: a node holds an index, because a component is a handle and a few numbers.
/// </remarks>
public sealed class EnvironmentQueryLibrary {
    readonly Dictionary<Symbol, EnvironmentQuery> byName = [];
    readonly List<EnvironmentQuery> ordered = [];

    /// <summary>How many there are.</summary>
    public int Count => ordered.Count;

    /// <summary>The query at an index.</summary>
    /// <param name="index">Its index.</param>
    public EnvironmentQuery this[int index] => ordered[index];

    /// <summary>Adds one.</summary>
    /// <param name="query">The query.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query" /> is null.</exception>
    /// <exception cref="InvalidOperationException">One of that name is already in it.</exception>
    public int Add(EnvironmentQuery query) {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Name.IsSome && !byName.TryAdd(query.Name, query)) {
            throw new InvalidOperationException($"A query called '{query.Name}' is already registered.");
        }

        ordered.Add(query);

        return ordered.Count - 1;
    }

    /// <summary>Finds one by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="query">Where to put it.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryGet(Symbol name, out EnvironmentQuery? query) => byName.TryGetValue(name, out query);

    /// <summary>The index of a query by name, or <c>-1</c>.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index.</returns>
    public int IndexOf(Symbol name) => byName.TryGetValue(name, out var query) ? ordered.IndexOf(query) : -1;
}
