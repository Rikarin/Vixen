// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     Constructs deliberately removed from the language because a GPU has no way to
///     represent them. These are pinned so nobody reintroduces one by accident, and
///     so the reason is written down next to the evidence.
/// </summary>
public class RemovedConstructsTests {
    [Theory]
    // No function pointers or closures on a GPU.
    [InlineData("        val f = x => x")]
    [InlineData("        val f = (x: int) => x")]
    // No null, so nothing that tests for or coalesces it.
    [InlineData("        val x = a ?? b")]
    [InlineData("        x ??= 1")]
    [InlineData("        val x = a!")]
    // No boxing or dynamic dispatch, so no anonymous aggregates.
    [InlineData("        val o = { A = 1 }")]
    // No character type.
    [InlineData("        val c = 'a'")]
    public void The_construct_no_longer_parses(string body) => AssertDoesNotParse(body);

    [Fact]
    public void Null_is_no_longer_a_keyword_so_it_reads_as_an_undefined_name() =>
        AssertDiagnostics("package A\n\nshader S {\n    func M() {\n        val x = null\n    }\n}\n", "RVN2010");

    [Fact]
    public void Nullable_type_annotations_no_longer_parse() {
        var tree = SyntaxTree.ParseText("package A\n\nshader S {\n    val x: int?\n}\n");
        Assert.NotEmpty(tree.Diagnostics);
    }

    [Theory]
    // `string`, `char`, `long` and `object` are gone from the type table, so a
    // reference to one is an ordinary "type not found".
    [InlineData("string")]
    [InlineData("char")]
    [InlineData("long")]
    [InlineData("object")]
    public void The_type_no_longer_exists(string name) =>
        AssertDiagnostics($"package A\n\nshader S {{\n    val x: {name}\n}}\n", "RVN2002");

    [Fact]
    public void A_string_literal_still_parses_because_attributes_need_it() {
        var tree = SyntaxTree.ParseText(
            """
            package A

            shader S {
                [Semantic("SV_Target")]
                var tint: float4
            }

            """
        );

        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void An_attribute_keeps_reading_its_string_argument() {
        var compilation = AssertNoDiagnostics(
            """
            package A

            shader S {
                [Semantic("SV_Target")]
                var tint: float4
            }

            """
        );

        Assert.Equal(
            "SV_Target",
            GetMember<FieldSymbol>(FindType(compilation, "S"), "tint")
                .SemanticName
        );
    }

    [Fact]
    public void A_string_literal_in_expression_position_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                func M() {
                    val s = "text"
                }
            }

            """,
            "RVN2025"
        );

    [Fact]
    public void An_integer_literal_too_large_for_int_takes_the_unsigned_shape() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func M() {
                    val big = 3000000000
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var declaration = FindNode<VariableDeclarationSyntax>(tree, d => d.Identifier.ValueText == "big");
        var local = Assert.IsType<LocalSymbol>(model.GetDeclaredSymbol(declaration));
        Assert.Equal("uint", local.Type.ToDisplayString());
    }

    static void AssertDoesNotParse(string body) {
        var tree = SyntaxTree.ParseText($"package A\n\nshader S {{\n    func M() {{\n{body}\n    }}\n}}\n");
        Assert.NotEmpty(tree.Diagnostics);
    }
}
