// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     P8's exit criterion: the same scorer object serves an environment query and a utility set,
///     asserted by construction rather than by comment.
/// </summary>
/// <remarks>
///     ⚠ <b>doc 37 § D14 claims that "where should I stand" and "what should I do" are the same
///     machine.</b> That is either checkable or it is a remark somebody will contradict in six months
///     by writing a second mean. These tests are the check: both hosts implement one interface, both
///     go through one combining routine, and one curve object literally scores in both.
/// </remarks>
public class QueryScorerExitCriteriaTests {
    [Fact]
    public void AQueryAndAUtilitySetAreBothScoredCandidateSets() {
        var set = new UtilitySet(Symbol.Intern("villager"), Action("wander", 0, 0.4f));
        var query = new EnvironmentQuery(
            Symbol.Intern("cover"),
            [QueryGenerators.CurrentLocation()],
            new QueryTest(QueryTests.Distance())
        );

        Assert.IsAssignableFrom<IScoredCandidateSet<UtilityAction>>(set);
        Assert.IsAssignableFrom<IScoredCandidateSet<QueryPoint>>(query);
    }

    /// <summary>
    ///     ⚠ One curve <i>object</i>, held by a consideration and by a test at the same time. Not two
    ///     curves with the same parameters — the same instance, which is what "the same scorer object
    ///     serves it and a utility set" says.
    /// </summary>
    [Fact]
    public void OneCurveObjectScoresAnActionAndAPointAtOnce() {
        var curve = ResponseCurve.Threshold(0.5f);

        var action = new UtilityAction(
            Symbol.Intern("flee"),
            0,
            new UtilityConsideration(Symbol.Intern("danger"), UtilityInputs.Constant(0.8f), curve)
        );

        var test = new QueryTest(QueryTests.Distance(), curve) { Maximum = 10f };

        Assert.Same(curve, action.Considerations[0].Curve);
        Assert.Same(curve, test.Curve);

        var context = Context(out var world);

        using (world) {
            // The action reads 0.8; the point is 8 m away, which normalises to 0.8. One curve, one
            // number, two hosts.
            Assert.True(test.Run(in context, new(Vector3.Zero, Vector3.Zero), new(new(8f, 0f, 0f)), out var factor));
            Assert.Equal(action.Score(in context), factor, 4);
        }
    }

    /// <summary>
    ///     ⚠ And the combination is one routine, not two that agree today. Every path — a utility
    ///     action, a query point, the public helper the guide names — ends in
    ///     <see cref="CandidateScoring.Combine" />.
    /// </summary>
    [Fact]
    public void TheMeanAndItsVetoAreOneImplementation() {
        Assert.Equal(UtilityScoring.Combine([0.6f, 0.6f]), CandidateScoring.Combine([0.6f, 0.6f]), 6);
        Assert.Equal(0.6f, CandidateScoring.Combine([0.6f, 0.6f, 0.6f, 0.6f]), 4);
        Assert.Equal(0f, CandidateScoring.Combine([0.9f, 0f, 0.9f]));

        var context = Context(out var world);

        using (world) {
            // A query point with a zero factor is vetoed exactly as an action with one is, because it
            // is the same code doing it.
            var query = new EnvironmentQuery(
                Symbol.Intern("vetoed"),
                [QueryGenerators.CurrentLocation()],
                new QueryTest(QueryTests.From("nothing", static (in AgentContext c, in QueryOrigin o, in QueryPoint p) => 0f)),
                new QueryTest(QueryTests.From("everything", static (in AgentContext c, in QueryOrigin o, in QueryPoint p) => 1f))
            );

            var results = new QueryResults();

            query.Run(in context, new(Vector3.Zero, Vector3.Zero), results);

            Assert.Equal(1, results.Count);
            Assert.Equal(0f, results.Points[0].Score);
        }
    }

    /// <summary>
    ///     P8's headline: "the best cover point with line of sight to the target" as one authored
    ///     query.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The trace is a lambda here and a physics raycast in a game, and that is the seam
    ///     working.</b> <c>IQueryTest</c> is the thing a project replaces; the wall in this test is
    ///     three lines of arithmetic, and swapping it for <c>WorldQueryTests.Trace</c> changes the
    ///     query's behaviour not at all.
    /// </remarks>
    [Fact]
    public void TheBestCoverPointWithLineOfSightIsOneAuthoredQuery() {
        var context = Context(out var world);

        using (world) {
            var target = new Vector3(0f, 0f, 20f);

            // A wall along x = 4, from z = 0 to z = 20: anything east of it cannot see the target.
            var query = new EnvironmentQuery(
                Symbol.Intern("cover"),
                [QueryGenerators.Grid(8f, 2f)],
                // Must be able to see the target at all.
                new QueryTest(QueryTests.From("sight", Sight)) {
                    Purpose = QueryTestPurpose.Filter,
                    Floor = 0.5f
                },
                // Prefer somewhere close to the agent: a falling line, which is what "less is better"
                // is in every one of the six shapes.
                new QueryTest(QueryTests.Distance(), new ResponseCurve { Slope = -1f, Shift = 1f }) {
                    Purpose = QueryTestPurpose.Both,
                    Maximum = 8f,
                    Ceiling = 8f
                },
                // And prefer somewhere far from the target.
                new QueryTest(QueryTests.Distance(QueryDistanceFrom.Context)) { Minimum = 0f, Maximum = 30f }
            );

            var results = new QueryResults { Detailed = true };

            Assert.True(query.Run(in context, new(Vector3.Zero, target, true), results), Report(results));
            Assert.True(results.TryBest(out var best), Report(results));

            // Everything east of the wall was filtered, and nothing that survived is east of it.
            Assert.True(best.Position.X <= 4f, $"the winner was at {best.Position}.");
            Assert.True(results.Surviving < results.Generated, Report(results));

            foreach (var point in results.Points) {
                Assert.True(point.Filtered || point.Position.X <= 4f, $"{point} survived behind the wall.");
            }

            // ⚠ And the criterion's other half: the winner is what the *scorer* chose, which is the
            // same routine a utility set's selector is handed.
            Span<float> scores = new float[results.Count];

            for (var index = 0; index < results.Count; index++) {
                scores[index] = results.Points[index].Score;
            }

            Assert.Equal(results.Best, CandidateScoring.Best(scores));
        }

        static float Sight(in AgentContext context, in QueryOrigin origin, in QueryPoint point) =>
            point.Position.X > 4f && origin.Context.X <= 4f ? 0f : 1f;
    }

    static string Report(QueryResults results) {
        var lines = new List<string> { $"{results.Generated} generated, {results.Surviving} survived." };

        foreach (var point in results.Points) {
            lines.Add(point.ToString());
        }

        return string.Join('\n', lines);
    }

    static UtilityAction Action(string name, ushort index, float reading) =>
        new(
            Symbol.Intern(name),
            index,
            new UtilityConsideration(
                Symbol.Intern("axis"),
                UtilityInputs.Constant(reading),
                ResponseCurve.Identity
            )
        );

    internal static AgentContext Context(out World world) {
        world = new World("queries");

        return new(world, new(5, 1, world.Id), new(BlackboardLayout.Empty), null, GameTime.Zero, 0);
    }
}

/// <summary>The generators: what each shape makes, and the ceiling that stops a typo hanging a frame.</summary>
public class QueryGeneratorTests {
    [Fact]
    public void AGridIsSquareAndCentredOnWhereItWasTold() {
        var points = Generate(QueryGenerators.Grid(2f, 1f), new(new(10f, 0f, 10f), Vector3.Zero));

        // Five along each side: −2, −1, 0, 1, 2.
        Assert.Equal(25, points.Count);
        Assert.Contains(points, point => point.Position == new Vector3(10f, 0f, 10f));
        Assert.All(points, point => Assert.True(MathF.Abs(point.Position.X - 10f) <= 2.001f));
    }

    [Fact]
    public void ADonutKeepsItsPointsBetweenItsRadii() {
        var points = Generate(QueryGenerators.Donut(3f, 6f, 2, 8), new(Vector3.Zero, Vector3.Zero));

        Assert.Equal(16, points.Count);

        foreach (var point in points) {
            var radius = point.Position.Length();

            Assert.InRange(radius, 2.999f, 6.001f);
        }
    }

    /// <summary>⚠ A cone aimed at nothing must not produce NaN, or every test below it reads garbage.</summary>
    [Fact]
    public void AConeWithNoContextStillMakesRealPoints() {
        var points = Generate(QueryGenerators.Cone(90f, 5f, 2, 3), new(Vector3.Zero, Vector3.Zero));

        Assert.Equal(6, points.Count);
        Assert.All(points, point => Assert.False(float.IsNaN(point.Position.X) || float.IsNaN(point.Position.Z)));
    }

    /// <summary>
    ///     ⚠ The ceiling is what turns "somebody typed 0.05 into a spacing field" from a hung frame
    ///     into a coarse answer and a number they can see.
    /// </summary>
    [Fact]
    public void AGeneratorAskedForHalfAMillionPointsStopsAtTheCeiling() {
        var points = Generate(QueryGenerators.Grid(100f, 0.05f), new(Vector3.Zero, Vector3.Zero));

        Assert.Equal(QueryGenerators.MaximumPoints, points.Count);
    }

    [Fact]
    public void ACompositeIsEveryGeneratorInOrder() {
        var points = Generate(
            QueryGenerators.Composite(QueryGenerators.CurrentLocation(), QueryGenerators.Circle(4f, 6)),
            new(new(1f, 0f, 1f), Vector3.Zero)
        );

        Assert.Equal(7, points.Count);
        Assert.Equal(new Vector3(1f, 0f, 1f), points[0].Position);
    }

    static List<QueryPoint> Generate(IQueryGenerator generator, QueryOrigin origin) {
        var context = QueryScorerExitCriteriaTests.Context(out var world);

        using (world) {
            var points = new List<QueryPoint>();

            generator.Generate(in context, in origin, points);

            return points;
        }
    }
}

/// <summary>The tests: the three purposes, the normalisation, and what "cannot answer" means.</summary>
public class QueryTestPurposeTests {
    [Fact]
    public void AFilterRejectsOutsideItsBoundsAndScoresNothing() {
        var context = QueryScorerExitCriteriaTests.Context(out var world);

        using (world) {
            var test = new QueryTest(QueryTests.Distance()) { Purpose = QueryTestPurpose.Filter, Ceiling = 5f };
            var origin = new QueryOrigin(Vector3.Zero, Vector3.Zero);

            Assert.True(test.Run(in context, in origin, new(new(3f, 0f, 0f)), out var near));
            Assert.Equal(1f, near);
            Assert.False(test.Run(in context, in origin, new(new(9f, 0f, 0f)), out _));
        }
    }

    /// <summary>
    ///     ⚠ "Cannot answer" filters rather than scoring zero. "There is no path to here" and "the
    ///     path to here is long" are different facts, and only the first is a rejection a scoring
    ///     test is entitled to make.
    /// </summary>
    [Fact]
    public void AReadingOfNotANumberFiltersThePointEvenForAScoringTest() {
        var context = QueryScorerExitCriteriaTests.Context(out var world);

        using (world) {
            var test = new QueryTest(QueryTests.Distance(QueryDistanceFrom.Context)) {
                Purpose = QueryTestPurpose.Score
            };

            // No context, so the distance to it cannot be answered.
            Assert.False(test.Run(in context, new(Vector3.Zero, Vector3.Zero), new(new(1f, 0f, 0f)), out _));
        }
    }

    [Fact]
    public void AWeightPullsAFactorTowardOneRatherThanMultiplyingIt() {
        var context = QueryScorerExitCriteriaTests.Context(out var world);

        using (world) {
            var origin = new QueryOrigin(Vector3.Zero, Vector3.Zero);
            var point = new QueryPoint(new(10f, 0f, 0f));

            var full = new QueryTest(QueryTests.Distance()) { Maximum = 10f };
            var half = new QueryTest(QueryTests.Distance()) { Maximum = 10f, Weight = 0.5f };

            full.Run(in context, in origin, in point, out var strong);
            half.Run(in context, in origin, in point, out var weak);

            // Both read 1.0; a weight of a half pulls the factor halfway to one, which for a factor
            // that is already one changes nothing — and for a factor of zero would give 0.5 rather
            // than a veto.
            Assert.Equal(1f, strong, 4);
            Assert.Equal(1f, weak, 4);

            var zero = new QueryTest(QueryTests.Distance()) { Maximum = 10f, Weight = 0.5f };

            zero.Run(in context, in origin, new(Vector3.Zero), out var none);
            Assert.Equal(0.5f, none, 4);
        }
    }

    /// <summary>⚠ The order of the tests is the file's, and a filter above stops the reads below it.</summary>
    [Fact]
    public void AFilteredPointDoesNotPayForTheTestsBelowIt() {
        var context = QueryScorerExitCriteriaTests.Context(out var world);

        using (world) {
            var reads = 0;

            var query = new EnvironmentQuery(
                Symbol.Intern("ordered"),
                [QueryGenerators.Grid(2f, 1f)],
                new QueryTest(QueryTests.Distance()) { Purpose = QueryTestPurpose.Filter, Ceiling = 0.5f },
                new QueryTest(
                    QueryTests.From(
                        "expensive",
                        (in AgentContext c, in QueryOrigin o, in QueryPoint p) => {
                            reads++;

                            return 1f;
                        }
                    )
                )
            );

            var results = new QueryResults();

            query.Run(in context, new(Vector3.Zero, Vector3.Zero), results);

            Assert.Equal(25, results.Generated);
            Assert.Equal(1, results.Surviving);
            Assert.Equal(1, reads);
        }
    }
}

/// <summary>The file, and what the compiler does with what it cannot resolve.</summary>
public class QueryContentTests {
    [Fact]
    public void AQueryCompilesOutOfItsFile() {
        var content = new QueryContent { Name = "Cover" };

        content.Generators.Add(new() { Kind = QueryGeneratorKind.Donut, Inner = 2f, Extent = 6f, Rings = 2, Points = 8 });
        content.Tests.Add(
            new() {
                Kind = QueryTestKind.Distance,
                Purpose = QueryTestPurpose.Both,
                Maximum = 6f,
                Ceiling = 6f
            }
        );

        Assert.True(QueryContentCompiler.TryCompile(content, new BehaviorTreeResolver(), out var problems, out var query));
        Assert.Empty(problems);
        Assert.NotNull(query);
        Assert.Equal(16, query.Estimate);
    }

    /// <summary>
    ///     ⚠ An unresolved test filters everything, which is the safe direction: an unfinished query
    ///     answers "nowhere" rather than confidently sending an agent somewhere nothing checked.
    /// </summary>
    [Fact]
    public void AnUnresolvedTestIsADiagnosticAndFiltersEverything() {
        var content = new QueryContent { Name = "Unfinished" };

        content.Generators.Add(new() { Kind = QueryGeneratorKind.Grid, Extent = 2f, Inner = 1f });
        content.Tests.Add(new() { Kind = QueryTestKind.Registered, Source = "cover-value" });

        Assert.False(QueryContentCompiler.TryCompile(content, new BehaviorTreeResolver(), out var problems, out var query));
        Assert.Contains(problems, problem => problem.Message.Contains("No query test called", StringComparison.Ordinal));

        var context = QueryScorerExitCriteriaTests.Context(out var world);

        using (world) {
            var results = new QueryResults();

            Assert.False(query!.Run(in context, new(Vector3.Zero, Vector3.Zero), results));
            Assert.Equal(25, results.Generated);
            Assert.Equal(0, results.Surviving);
        }
    }

    [Fact]
    public void ARegisteredGeneratorAndTestAreResolvedOffTheResolver() {
        var resolver = new BehaviorTreeResolver();

        resolver.AddGenerator("perch", QueryGenerators.Circle(3f, 4));
        resolver.AddTest("height", QueryTests.From("height", static (in AgentContext c, in QueryOrigin o, in QueryPoint p) => p.Position.Y));

        var content = new QueryContent { Name = "Perches" };

        content.Generators.Add(new() { Kind = QueryGeneratorKind.Registered, Source = "perch" });
        content.Tests.Add(new() { Kind = QueryTestKind.Registered, Source = "height", Maximum = 5f });

        Assert.True(QueryContentCompiler.TryCompile(content, resolver, out var problems, out var query));
        Assert.Empty(problems);
        Assert.Equal(Symbol.Intern("height"), query!.Tests[0].Name);
    }
}
