// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>What a <c>Behavior</c> is allowed to hold, and what it is not.</summary>
/// <remarks>
///     <para>
///         The rule is doc 04 § The rule that keeps this coherent, as narrowed: data on a behaviour
///         is fine and is serialised on purpose; a second copy of something the world is the
///         authority on is not. So every negative here is a behaviour with real state in it, because
///         a rule that fired on a <c>float</c> would be turned off within a day.
///     </para>
/// </remarks>
public class BehaviorStateTests {
    static Task<ImmutableArray<Diagnostic>> RunAsync(string source) =>
        AnalyzerHarness.RunAsync(source, new BehaviorStateAnalyzer());

    [Fact]
    public async Task An_entity_handle_on_a_behaviour_is_refused() {
        var reported = await RunAsync(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            public sealed class Follower : Behavior {
                public Entity Target { get; init; }
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorStateAnalyzer.HandleDiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Target", AnalyzerHarness.Underlined(diagnostic));
    }

    [Fact]
    public async Task A_list_of_children_is_the_case_the_document_names() {
        var reported = await RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            public sealed class Squad : Behavior {
                readonly List<Entity> members = new();

                public int Count => members.Count;
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorStateAnalyzer.HandleDiagnosticId, diagnostic.Id);
        Assert.Equal("members", AnalyzerHarness.Underlined(diagnostic));
    }

    [Fact]
    public async Task An_array_of_handles_is_the_same_thing_said_differently() {
        var reported = await RunAsync(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            public sealed class Waypoints : Behavior {
                Entity[] points = [];
            }
            """
        );

        Assert.Equal(BehaviorStateAnalyzer.HandleDiagnosticId, Assert.Single(reported).Id);
    }

    [Fact]
    public async Task A_component_struct_on_a_behaviour_is_refused() {
        var reported = await RunAsync(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            [Component]
            public struct Health {
                public float Value;
            }

            public sealed class Damage : Behavior {
                Health cached;
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorStateAnalyzer.ComponentDiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Health", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tag_component_counts_as_one() {
        var reported = await RunAsync(
            """
            using Vixen.Ecs;
            using Vixen.Engine.Behaviors;

            public struct Frozen : ITagComponent;

            public sealed class Thaw : Behavior {
                Frozen? remembered;
            }
            """
        );

        Assert.Equal(BehaviorStateAnalyzer.ComponentDiagnosticId, Assert.Single(reported).Id);
    }

    /// <summary>
    ///     ⚠ The case a rule reading only <c>[Component]</c> would miss, and it is the one doc 04
    ///     spells out — "a cached transform". A transform component carries no <c>[Component]</c>,
    ///     because that attribute is what makes a component scene-placeable rather than what makes it
    ///     a component. What gives it away is the read.
    /// </summary>
    [Fact]
    public async Task Caching_what_Get_returns_is_refused_even_for_an_unannotated_component() {
        var reported = await RunAsync(
            """
            using Vixen.Engine.Behaviors;

            public struct LocalTransform {
                public float X;
            }

            public sealed class Cacher : Behavior {
                LocalTransform cached;

                public void Awake() => cached = Get<LocalTransform>();
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorStateAnalyzer.ComponentDiagnosticId, diagnostic.Id);
        Assert.Equal("Get<LocalTransform>()", AnalyzerHarness.Underlined(diagnostic));
    }

    [Fact]
    public async Task A_behaviours_own_data_is_left_alone() {
        var reported = await RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Engine.Behaviors;

            public struct Vector3 {
                public float X;
                public float Y;
                public float Z;
            }

            public sealed class Patrol : Behavior {
                public float Speed { get; init; } = 5f;
                public string Route { get; init; } = "loop";
                public List<float> Dwell { get; } = new();
                public Vector3 Home { get; init; }

                float phase;
                bool wasAirborne;
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ The base class holds the one handle the design does hand out — the entity the behaviour
    ///     is on — so a rule that walked inherited members would fire on every behaviour ever
    ///     written, which is the shape of a rule that gets deleted rather than obeyed.
    /// </summary>
    [Fact]
    public async Task The_handle_a_behaviour_inherits_is_not_one_it_holds() {
        var reported = await RunAsync(
            """
            using Vixen.Engine.Behaviors;

            public sealed class Ordinary : Behavior {
                public float Speed { get; init; }
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ <b>The false positive the tree found, and the reason the rule reads storage.</b>
    ///     <c>NetworkBehaviour.NetworkId</c> is a computed property that reaches through to the world
    ///     on every call — which is precisely what this rule asks an author to write. A version that
    ///     read the property's declared type reported it, and would have taught everyone that the
    ///     right answer is a violation.
    /// </summary>
    [Fact]
    public async Task Reaching_through_to_the_world_in_a_computed_property_is_the_answer_not_the_defect() {
        var reported = await RunAsync(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            public struct NetworkId {
                public int Value;
            }

            public sealed class Replicated : Behavior {
                public NetworkId Id => Read<NetworkId>();
                public Entity Owner => Entity;
            }
            """
        );

        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_type_that_is_not_a_behaviour_may_hold_whatever_it_likes() {
        var reported = await RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Core;

            public sealed class Rig {
                public Entity Controller { get; init; }
                public List<Entity> Parts { get; } = new();
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     A registry keyed on the type rather than on an instance is not this behaviour's state, and
    ///     a constant cannot be a handle at all.
    /// </summary>
    [Fact]
    public async Task Static_state_is_not_the_instances() {
        var reported = await RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            public sealed class Spawner : Behavior {
                static readonly List<Entity> Spawned = new();

                public int Total => Spawned.Count;
            }
            """
        );

        Assert.Empty(reported);
    }
}
