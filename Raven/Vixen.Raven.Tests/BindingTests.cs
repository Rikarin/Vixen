// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Binding;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.SemanticTestBase;
using Vixen.Core.Syntax;

namespace Tests;

/// <summary>
///     Phase 2b: expressions bind to typed bound nodes with their symbols resolved
///     and their conversions made explicit.
/// </summary>
public class BindingTests {
    [Theory]
    [InlineData("42", "int")]
    [InlineData("42u", "uint")]
    [InlineData("4.5", "double")]
    [InlineData("4.5f", "float")]
    [InlineData("0x1F", "int")]
    [InlineData("0b1010", "int")]
    [InlineData("true", "bool")]
    public void Literals_get_their_type(string literal, string expected) =>
        Assert.Equal(expected, TypeOfExpression(literal));

    [Theory]
    [InlineData("1 + 2", "int")]
    [InlineData("1 + 2.0", "double")]
    [InlineData("1 / 2f", "float")]
    [InlineData("1 < 2", "bool")]
    [InlineData("true && false", "bool")]
    [InlineData("1 == 2", "bool")]
    [InlineData("-1", "int")]
    [InlineData("!true", "bool")]
    [InlineData("1 ..  4", "int..")]
    [InlineData("true ? 1 : 2f", "float")]
    public void Operators_produce_the_expected_type(string expression, string expected) =>
        Assert.Equal(expected, TypeOfExpression(expression));

    [Theory]
    [InlineData("float3(1, 2, 3)", "float3")]
    [InlineData("float3(0)", "float3")]
    [InlineData("float4(float3(1, 2, 3), 1)", "float4")]
    [InlineData("mat3(1)", "mat3")]
    [InlineData("int(1.5)", "int")]
    public void Built_in_types_are_constructible(string expression, string expected) =>
        Assert.Equal(expected, TypeOfExpression(expression));

    [Theory]
    [InlineData("v.x", "float")]
    [InlineData("v.xy", "float2")]
    [InlineData("v.xyz", "float3")]
    [InlineData("v.rgb", "float3")]
    [InlineData("v.wzyx", "float4")]
    public void Vector_swizzles_bind_to_synthesized_members(string expression, string expected) =>
        Assert.Equal(expected, TypeOfExpression(expression, "    val v: float4\n"));

    [Theory]
    [InlineData("v * 2", "float3")]
    [InlineData("2 * v", "float3")]
    [InlineData("v + v", "float3")]
    [InlineData("m * v", "float3")]
    [InlineData("m * m", "mat3")]
    [InlineData("v < v", "bool3")]
    public void Vector_and_matrix_arithmetic_resolves(string expression, string expected) =>
        Assert.Equal(expected, TypeOfExpression(expression, "    val v: float3\n    val m: mat3\n"));

    [Theory]
    [InlineData("dot(v, v)", "float")]
    [InlineData("normalize(v)", "float3")]
    [InlineData("cross(v, v)", "float3")]
    [InlineData("length(v)", "float")]
    [InlineData("clamp(v, v, v)", "float3")]
    [InlineData("lerp(v, v, f)", "float3")]
    [InlineData("mul(m, v)", "float3")]
    [InlineData("abs(1)", "int")]
    [InlineData("max(1, 2)", "int")]
    [InlineData("max(1f, 2f)", "float")]
    public void Intrinsics_resolve_through_overload_resolution(string expression, string expected) =>
        Assert.Equal(expected, TypeOfExpression(expression, "    val v: float3\n    val m: mat3\n    val f: float\n"));

    [Theory]
    [InlineData("numbers[0]", "int")]
    [InlineData("numbers[1 .. 2]", "int[]")]
    [InlineData("v[0]", "float")]
    [InlineData("m[0]", "float3")]
    public void Indexing_yields_the_element_type(string expression, string expected) =>
        Assert.Equal(
            expected,
            TypeOfExpression(expression, "    val numbers: int[]\n    val v: float3\n    val m: mat3\n")
        );

    [Fact]
    public void Tuples_and_collections_infer_a_structural_type() {
        Assert.Equal("(int, float)", TypeOfExpression("(1, 2f)"));
        Assert.Equal("(code: int, scale: float)", TypeOfExpression("(code: 1, scale: 2f)"));
        Assert.Equal("int[]", TypeOfExpression("[1, 2, 3]"));
        Assert.Equal("float[]", TypeOfExpression("[1, 2f, 3]"));
    }


    [Fact]
    public void Member_access_resolves_through_the_base_chain() {
        var (compilation, tree, model) = Compile(
            """
            package A

            class Base {
                val count: int
            }

            shader S : Base {
                func Read(): int {
                    return count
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var name = FindNode<IdentifierNameSyntax>(
            tree,
            n => n.Identifier.ValueText == "count"
                && n.Parent is ReturnStatementSyntax
        );

        var symbol = model.GetSymbolInfo(name).Symbol;
        var field = Assert.IsAssignableFrom<FieldSymbol>(symbol);
        Assert.Equal("Base", field.ContainingType?.Name);
    }

    [Fact]
    public void An_unqualified_instance_member_gets_an_implicit_self_receiver() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                val scale: float

                func Read(): float {
                    return scale
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var name = FindNode<IdentifierNameSyntax>(
            tree,
            n => n.Identifier.ValueText == "scale"
                && n.Parent is ReturnStatementSyntax
        );

        var bound = Assert.IsType<BoundFieldExpression>(model.GetBoundNode(name));
        Assert.IsType<BoundSelfExpression>(bound.Receiver);
    }

    [Fact]
    public void Conversions_are_materialized_in_the_bound_tree() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func Widen(): float {
                    val narrow: int = 1
                    return narrow
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var name = FindNode<IdentifierNameSyntax>(
            tree,
            n => n.Identifier.ValueText == "narrow"
                && n.Parent is ReturnStatementSyntax
        );

        var info = model.GetTypeInfo(name);
        Assert.Equal("int", info.Type?.ToDisplayString());
        Assert.Equal("float", info.ConvertedType?.ToDisplayString());
    }

    [Fact]
    public void Overload_resolution_prefers_the_closer_match() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func Take(value: int): int {
                    return value
                }

                func Take(value: float): float {
                    return value
                }

                func Use() {
                    var whole = Take(1)
                    var real = Take(1.5f)
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var calls = FindAll<InvocationExpressionSyntax>(tree).ToArray();
        Assert.Equal(2, calls.Length);

        Assert.Equal("int", ReturnTypeOfCall(model, calls[0]));
        Assert.Equal("float", ReturnTypeOfCall(model, calls[1]));
    }

    [Fact]
    public void Named_arguments_are_matched_by_parameter_name() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func Blend(a: float, b: float): float {
                    return a
                }

                func Use() {
                    var result = Blend(b: 1f, a: 2f)
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var call = FindNode<InvocationExpressionSyntax>(tree);
        var bound = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(call));

        // Reordered into parameter order: `a` receives the literal 2.
        Assert.Equal(2f, bound.Arguments[0].ConstantValue);
        Assert.Equal(1f, bound.Arguments[1].ConstantValue);
    }

    [Fact]
    public void Default_arguments_fill_the_missing_parameters() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func Scale(value: float, factor: float = 2): float {
                    return value * factor
                }

                func Use() {
                    var scaled = Scale(1f)
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var call = FindNode<InvocationExpressionSyntax>(tree);
        var bound = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(call));
        Assert.Equal(2, bound.Arguments.Count);
    }

    [Fact]
    public void For_over_a_range_binds_an_int_iteration_variable() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func Loop() {
                    var total = 0
                    for (i in 0 .. 10) {
                        total = total + i
                    }
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var statement = FindNode<ForStatementSyntax>(tree);
        var bound = Assert.IsType<BoundForStatement>(model.GetBoundNode(statement));
        Assert.Equal("int", bound.IterationVariable.Type.ToDisplayString());
    }

    [Fact]
    public void Generic_method_type_arguments_substitute_into_the_signature() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func Identity<T>(value: T): T {
                    return value
                }

                func Use() {
                    var result = Identity<int>(1)
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var call = FindNode<InvocationExpressionSyntax>(tree);
        var bound = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(call));
        Assert.Equal("int", bound.Method.ReturnType.ToDisplayString());
        Assert.Equal("int", bound.Type.ToDisplayString());
    }

    /// <summary>Wraps an expression in a shader method and reports the type it binds to.</summary>
    static string TypeOfExpression(string expression, string members = "") {
        var source = $$"""
                       package A

                       shader S {
                       {{members}}
                           func Probe() {
                               var probe = {{expression}}
                           }
                       }

                       """;

        var (compilation, tree, model) = Compile(source);
        Assert.Empty(compilation.GetDiagnostics());

        var declaration = FindNode<VariableDeclarationSyntax>(tree, d => d.Identifier.ValueText == "probe");
        var local = Assert.IsType<LocalSymbol>(model.GetDeclaredSymbol(declaration));
        return local.Type.ToDisplayString();
    }

    static string ReturnTypeOfCall(SemanticModel model, InvocationExpressionSyntax call) =>
        Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(call)).Type.ToDisplayString();

    static IEnumerable<T> FindAll<T>(SyntaxTree tree) where T : SyntaxNode {
        IEnumerable<SyntaxNode> Descend(SyntaxNode node) {
            yield return node;
            foreach (var child in node.ChildNodesAndTokens()) {
                foreach (var descendant in Descend(child)) {
                    yield return descendant;
                }
            }
        }

        return Descend(tree.GetRoot()).OfType<T>();
    }
}
