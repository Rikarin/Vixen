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

    /// <summary>
    ///     ⚠ <b>The wrapped handle, which is the likelier shape and was the hole.</b> The walk
    ///     followed arrays and generic arguments and then stopped at a named type's own fields, so a
    ///     <c>List&lt;Entity&gt;</c> was refused and a one-field wrapper around the same handle was
    ///     waved through — and a wrapper is what a codebase reaches for as soon as it has more than
    ///     one kind of link. The handle is exactly as stale on the far side of a save either way.
    /// </summary>
    [Fact]
    public async Task A_handle_one_struct_deep_is_still_a_handle_a_behaviour_holds() {
        var reported = await RunAsync(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            public struct Link {
                public Entity Target;
                public float Weight;
            }

            public sealed class Leash : Behavior {
                Link anchor;
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorStateAnalyzer.HandleDiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("anchor", AnalyzerHarness.Underlined(diagnostic));
    }

    /// <summary>
    ///     The same hole on the other half of the rule: a copy of a component is a copy whether the
    ///     behaviour names the component or names a struct that carries one.
    /// </summary>
    [Fact]
    public async Task A_component_one_struct_deep_is_still_a_copy() {
        var reported = await RunAsync(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            [Component]
            public struct Health {
                public float Value;
            }

            public struct Snapshot {
                public Health Vitals;
                public float TakenAt;
            }

            public sealed class Medic : Behavior {
                Snapshot last;
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorStateAnalyzer.ComponentDiagnosticId, diagnostic.Id);
        Assert.Equal("last", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("Health", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A reference is where the walk stops, and stating the bound is the point.</b> A class
    ///     a behaviour holds is a reference to one object rather than bytes on the behaviour — which
    ///     is what an asset or a service reference is, and those are allowed. Its graph can also be
    ///     arbitrarily large and cyclic, so walking it would make the predicate unpredictable.
    /// </summary>
    [Fact]
    public async Task A_handle_behind_a_class_reference_is_out_of_this_rules_reach() {
        var reported = await RunAsync(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            public sealed class Bookmark {
                public Entity Target;
            }

            public sealed class Journal : Behavior {
                Bookmark latest = new();
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ A struct may name itself through a reference, and a field walk without a bound would
    ///     hang the build rather than fail it. The depth bound is what makes this terminate, and this
    ///     is the fixture that says so — a walk that also followed primitives would need the bound to
    ///     escape <c>System.Single</c>'s own <c>float</c> field instead.
    /// </summary>
    [Fact]
    public async Task A_cyclic_shape_terminates() {
        var reported = await RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Engine.Behaviors;

            public struct Node {
                public List<Node> Children;
                public float Weight;
            }

            public sealed class Tree : Behavior {
                Node root;
            }
            """
        );

        Assert.Empty(reported);
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
