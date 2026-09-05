// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Ui.Generators.Tests;

/// <summary>Which of a component's public properties a binding can keep current.</summary>
/// <remarks>
///     ⚠ <b>What every test here checks is the <i>set</i> reported, not merely that something was.</b>
///     A rule of this shape fails by being too broad — a component has a dozen public members and
///     only some of them are parameters — so a test that asserted "one diagnostic somewhere" would be
///     green against a rule that fired on the wrong one.
/// </remarks>
public class ComponentParameterTests {
    /// <summary>The framework types the analyzer resolves by metadata name.</summary>
    /// <remarks>
    ///     Enough of <c>Component</c> and of the reactive namespace to bind against, on
    ///     <see cref="AnalyzerHarness.StringId" />'s terms.
    /// </remarks>
    const string Framework = """
        namespace Vixen.Ui.Composition {
            public sealed class BuildContext { }

            public abstract class Component {
                protected abstract void Build(BuildContext ctx);
            }
        }

        namespace Vixen.Ui.Reactive {
            public interface IReadOnlySignal<out T> { T Value { get; } }

            public sealed class Signal<T> : IReadOnlySignal<T> {
                public Signal(T initial) { Value = initial; }

                public T Value { get; set; }
            }
        }
        """;

    static async Task<string[]> Reported(string source) {
        var reported = await AnalyzerHarness.RunAsync(source, new ComponentParameterAnalyzer(), Framework);

        return [.. reported.Select(AnalyzerHarness.Underlined)];
    }

    /// <summary>A plain settable property is a parameter a binding cannot keep current.</summary>
    [Fact]
    public async Task A_plain_settable_property_is_reported() {
        var reported = await Reported(
            """
            using Vixen.Ui.Composition;

            public sealed class Panel : Component {
                public string Title { get; set; } = "";

                protected override void Build(BuildContext ctx) { }
            }
            """
        );

        Assert.Equal(["Title"], reported);
    }

    /// <summary>The diagnostic names the type, the property and what to wrap.</summary>
    [Fact]
    public async Task The_diagnostic_says_which_signal_to_reach_for() {
        var reported = await AnalyzerHarness.RunAsync(
            """
            using Vixen.Ui.Composition;

            public sealed class Panel : Component {
                public int Count { get; set; }

                protected override void Build(BuildContext ctx) { }
            }
            """,
            new ComponentParameterAnalyzer(),
            Framework
        );

        var one = Assert.Single(reported);

        Assert.Equal(ComponentParameterAnalyzer.PlainParameterId, one.Id);
        Assert.Equal(DiagnosticSeverity.Info, one.Severity);
        Assert.Contains("Signal<int>", one.GetMessage(null), StringComparison.Ordinal);
    }

    /// <summary>Signal-backed is the shape the rule is asking for, so it says nothing about it.</summary>
    /// <remarks>
    ///     Both spellings: the concrete <c>Signal&lt;T&gt;</c> a component owns, and the
    ///     <c>IReadOnlySignal&lt;T&gt;</c> a component that only reads a value should ask for.
    /// </remarks>
    [Fact]
    public async Task A_signal_backed_parameter_is_not_reported() {
        var reported = await Reported(
            """
            using Vixen.Ui.Composition;
            using Vixen.Ui.Reactive;

            public sealed class Panel : Component {
                public Signal<string> Title { get; set; } = new("");

                public IReadOnlySignal<int> Count { get; set; } = new Signal<int>(0);

                protected override void Build(BuildContext ctx) { }
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ Nothing a caller cannot assign is a parameter, and this is the half a broad rule gets
    ///     wrong: a component's computed reads and its <c>ref</c> targets are public and are not
    ///     parameters.
    /// </summary>
    [Fact]
    public async Task A_property_a_caller_cannot_assign_is_not_a_parameter() {
        var reported = await Reported(
            """
            using Vixen.Ui.Composition;

            public sealed class Panel : Component {
                public string Computed => "read only";

                public string Part { get; private set; } = "";

                public static string Shared { get; set; } = "";

                protected override void Build(BuildContext ctx) { }
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>A callback is invoked rather than read, so tracking is not a sentence about it.</summary>
    [Fact]
    public async Task A_delegate_parameter_is_not_reported() {
        var reported = await Reported(
            """
            using System;
            using Vixen.Ui.Composition;

            public sealed class Panel : Component {
                public Action? Chosen { get; set; }

                public Func<int, string>? Format { get; set; }

                protected override void Build(BuildContext ctx) { }
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>Nothing that is not a component is looked at.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument check.</b> Public settable properties are most of the C# ever
    ///     written; a rule that ran anywhere but on a <c>Component</c> subclass would report on
    ///     every DTO in the tree and would be turned off within a day.
    /// </remarks>
    [Fact]
    public async Task A_type_that_is_not_a_component_is_left_alone() {
        var reported = await Reported(
            """
            public sealed class Settings {
                public string Theme { get; set; } = "";
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>A component several levels down is still a component.</summary>
    [Fact]
    public async Task A_parameter_on_a_derived_component_is_reported() {
        var reported = await Reported(
            """
            using Vixen.Ui.Composition;

            public abstract class PanelBase : Component { }

            public sealed class Panel : PanelBase {
                public string Title { get; set; } = "";

                protected override void Build(BuildContext ctx) { }
            }
            """
        );

        Assert.Equal(["Title"], reported);
    }
}
