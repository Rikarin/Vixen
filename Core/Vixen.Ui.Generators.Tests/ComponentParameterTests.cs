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

    /// <summary>
    ///     ⚠ Generated code with no <c>#line</c> is still never reported, and that is the promise
    ///     the <c>@code</c> change had to keep.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The objection to a diagnostic in generated C# is that the author cannot edit
    ///         it</b>, and for a file no directive maps that is still true. The analyzer now sets
    ///         <c>GeneratedCodeAnalysisFlags.Analyze</c> — so such a declaration <i>is</i> looked at
    ///         — and withholds <c>ReportDiagnostics</c>, so a diagnostic whose location is still
    ///         inside the generated tree is dropped by Roslyn rather than by the rule.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The partial arrangement is the one worth testing, and it is a third answer
    ///         again.</b> A class that is half hand-written — which is every <c>.vxml</c> with a
    ///         code-behind — was analyzed even before the change, with the diagnostic on the
    ///         generated half dropped afterwards. Both halves are asserted here at once:
    ///         <c>Rank</c> is hand-written and reported, <c>Title</c> is emitted under no mapping
    ///         and is not, in one type.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task A_parameter_in_unmapped_generated_code_is_not_reported() {
        var reported = await AnalyzerHarness.RunAsync(
            """
            using Vixen.Ui.Composition;

            public sealed partial class Panel {
                public int Rank { get; set; }
            }
            """,
            new ComponentParameterAnalyzer(),
            Framework,
            """
            // <auto-generated />
            partial class Panel : global::Vixen.Ui.Composition.Component {
                public string Title { get; set; } = "";

                protected override void Build(global::Vixen.Ui.Composition.BuildContext ctx) { }
            }
            """
        );

        Assert.Equal(["Rank"], reported.Select(AnalyzerHarness.Underlined));
    }

    /// <summary>
    ///     ⚠ A parameter declared in a <c>.vxml</c>'s <c>@code</c> block is reported, against the
    ///     <c>.vxml</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the case the rule exists for and the case it could not see.</b> Every
    ///         markup component in this repository declares its parameters in <c>@code</c>, so
    ///         "sweep the tree and count the hits" used to read zero for a reason that had nothing
    ///         to do with how much code was at fault.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What makes it safe is the directive, not a decision to tolerate a diagnostic on
    ///         generated code.</b> <c>ComponentEmitter</c> copies each block under a <c>#line</c>
    ///         span, so the location resolves to characters in the <c>.vxml</c> that the author
    ///         wrote — which is what this asserts, rather than merely that something was reported.
    ///         Nothing had tested that a <c>#line</c> survives as far as a diagnostic's location;
    ///         it does.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task A_parameter_mapped_back_to_a_vxml_is_reported_there() {
        var reported = await AnalyzerHarness.RunAsync(
            "// A .vxml with no code-behind hand-writes nothing at all.",
            new ComponentParameterAnalyzer(),
            Framework,
            """
            // <auto-generated />
            sealed class Panel : global::Vixen.Ui.Composition.Component {
            #line (9,5)-(9,43) 4 "Panels/Inspector.vxml"
                public string Title { get; set; } = "";
            #line default

                protected override void Build(global::Vixen.Ui.Composition.BuildContext ctx) { }
            }
            """
        );

        var one = Assert.Single(reported);
        var where = one.Location.GetLineSpan();

        Assert.Equal(ComponentParameterAnalyzer.PlainParameterId, one.Id);
        Assert.Contains("'Panel.Title'", one.GetMessage(null), StringComparison.Ordinal);
        Assert.Equal("Panels/Inspector.vxml", where.Path);
        Assert.Equal(8, where.StartLinePosition.Line);
    }

    /// <summary>
    ///     ⚠ A property backed by a signal field is not reported, and the premise that said
    ///     otherwise was the rule's rather than the code's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>"Back it with a <c>Signal&lt;T&gt;</c>" describes exactly this property.</b>
    ///         Reporting it made the rule complain about the shape it recommends, which under
    ///         <c>TreatWarningsAsErrors</c> would have made that shape a build error —
    ///         <c>Samples/02-HelloUi/Panels/Inspector.vxml</c>'s <c>Model</c> is written this way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What decides it is the getter, and this test is the reason the analyzer reads
    ///         accessor bodies at all.</b> An effect subscribes to what it reads; reading this
    ///         property reads <c>title.Value</c>, so it takes the dependency however plain
    ///         <c>string</c> looks.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task A_property_backed_by_a_signal_field_is_not_reported() {
        var reported = await Reported(
            """
            using Vixen.Ui.Composition;
            using Vixen.Ui.Reactive;

            public sealed class Panel : Component {
                readonly Signal<string> title = new("");

                public string Title {
                    get => title.Value;
                    set => title.Value = value;
                }

                protected override void Build(BuildContext ctx) { }
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ A getter with a body that reads nothing reactive is still reported, which is what
    ///     stops the accessor rule from being a way to opt out.
    /// </summary>
    /// <remarks>
    ///     <b>The instrument check for the accessor half of the rule.</b> A rule that read "the getter
    ///     has a body" as evidence of tracking would silence every hand-written accessor in the
    ///     tree, which is a far larger set than the signal-backed one. The question is what the
    ///     getter reads.
    /// </remarks>
    [Fact]
    public async Task A_property_over_a_plain_field_is_reported() {
        var reported = await Reported(
            """
            using Vixen.Ui.Composition;

            public sealed class Panel : Component {
                string title = "";

                public string Title {
                    get => title;
                    set => title = value;
                }

                protected override void Build(BuildContext ctx) { }
            }
            """
        );

        Assert.Equal(["Title"], reported);
    }
}
