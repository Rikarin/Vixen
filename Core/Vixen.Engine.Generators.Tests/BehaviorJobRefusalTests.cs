// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>What a <c>[BehaviorJob]</c> batch may not do, and the far larger set it may.</summary>
/// <remarks>
///     ⚠ <b>Every negative here is a call that is genuinely correct where it stands</b> — the same
///     call in an unmarked type, or in <c>Awake</c>, which runs on the lifecycle drain — because the
///     cost of this rule firing wrongly is an author taking <c>[BehaviorJob]</c> off a type that was
///     safe, and nothing tells them that happened.
/// </remarks>
public class BehaviorJobRefusalTests {
    static Task<ImmutableArray<Diagnostic>> RunAsync(string source) =>
        AnalyzerHarness.RunAsync(source, new BehaviorJobAnalyzer());

    const string Managed = """
        [Vixen.Core.Component]
        public sealed class Inventory {
            public int Slots;
        }
        """;

    const string Position = """
        public struct Position {
            public float X;
        }
        """;

    [Fact]
    public async Task Disabling_a_behaviour_inside_a_dispatched_update_is_refused() {
        var reported = await RunAsync(
            """
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public sealed class Blinker : Behavior {
                protected override void Update() {
                    Enabled = false;
                }
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorJobAnalyzer.RaceDiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Enabled = false", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("List<Behavior?>", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Destroying_from_inside_a_dispatched_update_is_refused() {
        var reported = await RunAsync(
            """
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public sealed class Bomb : Behavior {
                protected override void Update() {
                    Destroy();
                }
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorJobAnalyzer.RaceDiagnosticId, diagnostic.Id);
        Assert.Equal("Destroy()", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("Bomb", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_a_coroutine_from_a_dispatched_late_update_is_refused() {
        var reported = await RunAsync(
            """
            using Vixen.Engine.Behaviors;
            using Vixen.Engine.Coroutines;

            [BehaviorJob]
            public sealed class Patroller : Behavior {
                protected override void LateUpdate() {
                    Run(new Coroutine());
                }
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorJobAnalyzer.RaceDiagnosticId, diagnostic.Id);
        Assert.Equal("Run(new Coroutine())", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("coroutine scheduler", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The member of the set nothing at the call site betrays: reaching a managed component
    ///     through <c>Get&lt;T&gt;</c> grows a table the whole world shares.
    /// </summary>
    [Fact]
    public async Task Getting_a_managed_component_from_a_dispatched_update_is_refused() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public sealed class Looter : Behavior {
                protected override void Update() {
                    Get<Inventory>().Slots++;
                }
            }

            {{Managed}}
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorJobAnalyzer.RaceDiagnosticId, diagnostic.Id);
        Assert.Equal("Get<Inventory>()", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("managed component", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structural_change_from_a_dispatched_update_is_refused() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public sealed class Spawner : Behavior {
                protected override void Update() {
                    World.Create();
                }
            }

            {{Position}}
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorJobAnalyzer.RaceDiagnosticId, diagnostic.Id);
        Assert.Equal("World.Create()", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("archetypes", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    /// <summary>A lambda made inside the pass still runs inside the batch.</summary>
    [Fact]
    public async Task A_lambda_inside_a_dispatched_update_is_read_as_part_of_it() {
        var reported = await RunAsync(
            """
            using System;
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public sealed class Deferred : Behavior {
                protected override void Update() {
                    Action action = () => Destroy();
                    action();
                }
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorJobAnalyzer.RaceDiagnosticId, diagnostic.Id);
        Assert.Equal("Destroy()", AnalyzerHarness.Underlined(diagnostic));
    }

    [Fact]
    public async Task VXS0417_says_nothing_about_an_unmarked_behaviour() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Engine.Behaviors;
            using Vixen.Engine.Coroutines;

            public sealed class Ordinary : Behavior {
                protected override void Update() {
                    Enabled = false;
                    Destroy();
                    Run(new Coroutine());
                    Get<Inventory>().Slots++;
                    World.Create();
                }
            }

            {{Managed}}
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ The lifecycle callbacks of a marked type are drained on one thread, so every call the
    ///     rule refuses above is correct in <c>Awake</c> — and a rule that reported them would push
    ///     authors to un-mark types that were safe.
    /// </summary>
    [Fact]
    public async Task VXS0417_says_nothing_about_a_marked_type_s_lifecycle_callbacks() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Engine.Behaviors;
            using Vixen.Engine.Coroutines;

            [BehaviorJob]
            public sealed class Waking : Behavior {
                protected override void Awake() {
                    Enabled = true;
                    Run(new Coroutine());
                    Get<Inventory>().Slots++;
                }
            }

            {{Managed}}
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>The read half is safe and the rule has to know it, or it bans reading a component.</summary>
    [Fact]
    public async Task VXS0417_says_nothing_about_reading_a_component_or_an_unmanaged_one() {
        var reported = await RunAsync(
            $$"""
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public sealed class Mover : Behavior {
                protected override void Update() {
                    Get<Position>().X += Read<Inventory>().Slots;
                }
            }

            {{Position}}

            {{Managed}}
            """
        );

        Assert.Empty(reported);
    }

    [Fact]
    public async Task The_attribute_on_something_that_is_not_a_behaviour_is_reported() {
        var reported = await RunAsync(
            """
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public sealed class NotABehavior {
                public void Update() { }
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(BehaviorJobAnalyzer.TargetDiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("NotABehavior", diagnostic.GetMessage(null), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The mark is not inherited, and the rule follows the bucket rather than the type
    ///     hierarchy: a subclass that has not asked for dispatch is not dispatched, so its
    ///     <c>Update</c> is ordinary code.
    /// </summary>
    [Fact]
    public async Task A_subclass_of_a_marked_type_is_not_itself_dispatched() {
        var reported = await RunAsync(
            """
            using Vixen.Engine.Behaviors;

            [BehaviorJob]
            public class Parent : Behavior {
                protected override void Update() { }
            }

            public sealed class Child : Parent {
                protected override void Update() {
                    Destroy();
                }
            }
            """
        );

        Assert.Empty(reported);
    }
}
