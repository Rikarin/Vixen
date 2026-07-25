// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;
using static Tests.LoweringTestBase;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     <c>val</c> type parameters — <c>shader Blur&lt;val TapCount: int&gt;</c> — which
///     parameterise a shader by a compile-time constant rather than by a type.
/// </summary>
public class ValueParameterTests {
    const string Blur = """
                        package A

                        shader Blur<val TapCount: int> {
                            var source: float4

                            func Filter(): float4 {
                                var total = source
                                for (i in 0..TapCount) {
                                    total = total + source
                                }

                                return total
                            }
                        }

                        """;

    static (Compilation Compilation, IrModule Module) LowerWith(string source, PermutationValues values) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", values, [tree]);
        var semantic = compilation.GetDiagnostics();
        Assert.True(
            semantic.Count == 0,
            "Expected no semantic diagnostics, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return (compilation, module);
    }

    static IReadOnlyList<Diagnostic> DiagnosticsWith(string source, PermutationValues values) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        return Compilation.Create("Test", values, [tree]).GetDiagnostics();
    }

    static FieldSymbol Parameter(Compilation compilation, string shader, string name) =>
        Assert.Single(FindType(compilation, shader).GetMembers(name).OfType<FieldSymbol>());

    // --- The parameter is a constant member --------------------------------

    [Fact]
    public void A_value_parameter_is_a_constant_member_not_a_type_parameter() {
        var (compilation, _) = LowerWith(Blur, PermutationValues.Parse(["TapCount=4"]));
        var shader = FindType(compilation, "Blur");

        // Arity is unchanged: `Blur` does not take a type argument.
        Assert.Empty(shader.TypeParameters);

        var parameter = Parameter(compilation, "Blur", "TapCount");
        Assert.True(parameter.IsConst);
        Assert.Equal(4, parameter.ConstantValue);

        // And it is not data on the target.
        Assert.Equal(ResourceKind.None, parameter.ResourceKind);
    }

    [Fact]
    public void A_qualified_value_wins_over_a_bare_one() {
        var (compilation, _) = LowerWith(Blur, PermutationValues.Parse(["TapCount=4", "Blur.TapCount=9"]));

        Assert.Equal(9, Parameter(compilation, "Blur", "TapCount").ConstantValue);
    }

    [Fact]
    public void The_value_folds_into_the_body() {
        var (_, four) = LowerWith(Blur, PermutationValues.Parse(["TapCount=4"]));
        var (_, eight) = LowerWith(Blur, PermutationValues.Parse(["TapCount=8"]));

        Assert.Contains("4", PrintFunction(four, "Filter"), StringComparison.Ordinal);
        Assert.Contains("8", PrintFunction(eight, "Filter"), StringComparison.Ordinal);
        Assert.NotEqual(PrintFunction(four, "Filter"), PrintFunction(eight, "Filter"));
    }

    /// <summary>
    ///     A value parameter changes codegen, so it belongs in the cache key for the same
    ///     reason a permutation key does.
    /// </summary>
    [Fact]
    public void A_read_value_parameter_is_reported_as_used() {
        var (compilation, _) = LowerWith(Blur, PermutationValues.Parse(["TapCount=4"]));

        Assert.Contains("TapCount", compilation.UsedPermutationKeys);
    }

    [Fact]
    public void A_value_parameter_folds_a_branch_away_like_a_permutation() {
        var (_, module) = LowerWith(
            """
            package A

            shader S<val Fancy: bool> {
                var tint: float4

                func Shade(): float4 {
                    if (Fancy) {
                        return tint * 2.0f
                    }

                    return tint
                }
            }

            """,
            PermutationValues.Parse(["Fancy=false"])
        );

        var body = PrintFunction(module, "Shade");
        Assert.DoesNotContain("if", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mul", body, StringComparison.Ordinal);
    }

    // --- Validation --------------------------------------------------------

    /// <summary>
    ///     The difference from a <c>[Permutation]</c> field: there is no default, so compiling
    ///     without a value is an error rather than a fallback.
    /// </summary>
    [Fact]
    public void A_value_parameter_with_no_value_is_rejected() {
        var diagnostic = Assert.Single(DiagnosticsWith(Blur, PermutationValues.Empty));

        Assert.Equal("RVN2082", diagnostic.Id);
        Assert.Contains("TapCount", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_parameter_outside_a_shader_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            struct S<val N: int> {
            }

            """,
            "RVN2080"
        );

    [Theory]
    [InlineData("float")]
    [InlineData("float4")]
    public void A_value_parameter_of_an_unsupported_type_is_rejected(string type) =>
        AssertDiagnostics(
            $$"""
              package A

              shader S<val Scale: {{type}}> {
              }

              """,
            "RVN2081"
        );

    [Fact]
    public void A_value_of_the_wrong_type_is_rejected() {
        var diagnostic = Assert.Single(DiagnosticsWith(Blur, PermutationValues.Parse(["TapCount=true"])));

        Assert.Equal("RVN2083", diagnostic.Id);
    }

    [Fact]
    public void Assigning_to_a_value_parameter_is_rejected_with_a_reason() {
        var ids = DiagnosticsWith(
                """
                package A

                shader S<val N: int> {
                    func Probe() {
                        N = 1
                    }
                }

                """,
                PermutationValues.Parse(["N=1"])
            )
            .Select(d => d.Id);

        Assert.Contains("RVN2084", ids);
    }

    /// <summary>
    ///     Semantic only, deliberately: an uninstantiated <c>Box&lt;T&gt;</c> has no target
    ///     representation and does not lower, which is correct and unrelated to this claim.
    /// </summary>
    [Fact]
    public void An_ordinary_type_parameter_still_works_beside_a_value_one() {
        var tree = SyntaxTree.ParseText(
            """
            package A

            struct Box<T> {
                var value: T
            }

            shader S<val N: int> {
                var tint: float4
            }

            """,
            path: "Test.rvn"
        );

        var compilation = Compilation.Create("Test", PermutationValues.Parse(["N=1"]), [tree]);
        Assert.Empty(compilation.GetDiagnostics());

        Assert.Single(FindType(compilation, "Box").TypeParameters);
        Assert.Empty(FindType(compilation, "S").TypeParameters);
    }
}
