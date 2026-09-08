// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>Structural change inside a query body, in the three shapes the ECS offers.</summary>
/// <remarks>
///     Every negative here is a structural change that is genuinely fine — outside iteration, or a
///     value write that moves nothing — because the whole risk of this rule is that it fires on the
///     ordinary case and gets disabled at the project level.
/// </remarks>
public class QueryMutationTests {
    static Task<ImmutableArray<Diagnostic>> RunAsync(string source) =>
        AnalyzerHarness.RunAsync(source, new QueryMutationAnalyzer());

    const string Position = """
        public struct Position {
            public float X;
        }
        """;

    [Fact]
    public async Task Destroying_inside_a_query_lambda_is_reported() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Core;
            using Vixen.Ecs;

            public static class Reaper {
                public static void Sweep(World world, QueryDescription description, Entity entity) {
                    world.Query<Position>(description, (ref Position position) => world.Destroy(entity));
                }
            }

            {{Position}}
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(QueryMutationAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Equal("world.Destroy(entity)", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("the query body", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_component_during_a_chunk_walk_is_reported() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Core;
            using Vixen.Ecs;

            public static class Tagger {
                public static void Sweep(World world, QueryDescription description, Entity entity) {
                    foreach (var chunk in world.Chunks(description)) {
                        world.Add(entity, new Position());
                    }
                }
            }

            {{Position}}
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(QueryMutationAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("a chunk walk", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_walk_over_a_querys_own_chunks_is_the_same_walk() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Core;
            using Vixen.Ecs;

            public static class Tagger {
                public static void Sweep(World world, QueryDescription description, Entity entity) {
                    foreach (var chunk in world.Query(description).Chunks()) {
                        world.Remove<Position>(entity);
                    }
                }
            }

            {{Position}}
            """
        );

        Assert.Equal(QueryMutationAnalyzer.DiagnosticId, Assert.Single(reported).Id);
    }

    /// <summary>
    ///     ⚠ The form a reader cannot see from the call site: <c>ForEach</c> takes a
    ///     <c>ref TVisitor</c> and the body is a method on another type, in another file, that looks
    ///     like an ordinary method until you know what implements it.
    /// </summary>
    [Fact]
    public async Task A_struct_visitors_Update_is_a_query_body() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Core;
            using Vixen.Ecs;

            public struct Cleaner : IForEach<Position> {
                public World World;
                public Entity Doomed;

                public void Update(ref Position position) => World.Destroy(Doomed);
            }

            {{Position}}
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(QueryMutationAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("a struct visitor", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structural_change_outside_iteration_is_allowed_and_fast() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Core;
            using Vixen.Ecs;

            public static class Spawner {
                public static void Spawn(World world) {
                    var entity = world.Create();

                    world.Add(entity, new Position());
                    world.Remove<Position>(entity);
                    world.Destroy(entity);
                }
            }

            {{Position}}
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     Writing a component's value is what a query body is for. It moves no entity between
    ///     archetypes, so it invalidates nothing — and a rule that flagged it would flag every
    ///     system in the engine.
    /// </summary>
    [Fact]
    public async Task Writing_a_value_inside_a_query_body_is_not_a_structural_change() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Core;
            using Vixen.Ecs;

            public static class Mover {
                public static void Sweep(World world, QueryDescription description, Entity entity) {
                    world.Query<Position>(description, (ref Position position) => position.X += 1f);
                    world.Query<Position>(description, (ref Position position) => world.Set(entity, position));
                }
            }

            {{Position}}
            """
        );

        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_foreach_over_something_that_is_not_a_chunk_walk_is_left_alone() {
        var reported = await RunAsync(
            $$"""
            using System.Collections.Generic;
            using Vixen.Core;
            using Vixen.Ecs;

            public static class Loader {
                public static void Spawn(World world, List<Position> authored) {
                    foreach (var position in authored) {
                        world.Add(world.Create(), position);
                    }
                }
            }

            {{Position}}
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ Mutating the entity the loop is about to leave is sometimes exactly right, and a rule
    ///     with no way out is one that gets turned off at the project level rather than at the line.
    /// </summary>
    [Fact]
    public async Task A_pragma_is_how_an_author_says_they_meant_it() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Core;
            using Vixen.Ecs;

            public static class Reaper {
                public static void Sweep(World world, QueryDescription description, Entity entity) {
            #pragma warning disable VXS0415 // One entity, removed as the walk leaves it.
                    world.Query<Position>(description, (ref Position position) => world.Destroy(entity));
            #pragma warning restore VXS0415
                }
            }

            {{Position}}
            """
        );

        Assert.Empty(reported);
    }
}
