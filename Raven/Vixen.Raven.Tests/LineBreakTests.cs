// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Newlines inside a parenthesized list — a parameter list and an argument list —
///     are layout rather than terminators, so a wide signature can be broken over lines.
/// </summary>
/// <remarks>
///     Everywhere else in this language a newline ends something, which is why the three
///     positions are named rather than "anywhere inside the parens": after the <c>(</c>,
///     after each <c>,</c>, and before the <c>)</c>. Nothing in the grammar can start with
///     a <c>,</c> or a <c>)</c>, so a newline in front of one cannot be the end of a
///     statement — and a newline anywhere else in the list still is. The library's own
///     signatures are what wanted this: several of them ran past 120 columns with nowhere
///     to break.
/// </remarks>
public class LineBreakTests {
    const string Broken = """
                          package A

                          shader S {
                              var world: mat4

                              func Blend(
                                  a: float3,
                                  b: float3,
                                  t: float
                              ): float3 {
                                  return a * (1f - t) + b * t
                              }

                              [VertexShader]
                              [Semantic("SV_Position")]
                              func Vertex(position: float3): float4 {
                                  val mixed = Blend(
                                      float3(1f, 0f, 0f),
                                      float3(0f, 1f, 0f),
                                      position.x
                                  )

                                  return world * float4(mixed, 1f)
                              }
                          }

                          """;

    [Fact]
    public void A_broken_parameter_list_and_call_parse() {
        var tree = SyntaxTree.ParseText(Broken, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>
    ///     The newlines are trivia, so the tree still reproduces the file — which is what
    ///     makes the layout the author's rather than the parser's.
    /// </summary>
    [Fact]
    public void The_newlines_survive_as_trivia() {
        var tree = SyntaxTree.ParseText(Broken, path: "Test.rvn");
        Assert.Equal(Broken, tree.GetRoot().ToFullString());
    }

    /// <summary>
    ///     Breaking a signature changes nothing a caller or the binder can see: the same
    ///     three parameters, in order, on the same symbol.
    /// </summary>
    [Fact]
    public void Breaking_a_signature_does_not_change_it() {
        var tree = SyntaxTree.ParseText(Broken, path: "Test.rvn");
        var compilation = Compilation.Create("Broken", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var method = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Blend");

        Assert.Equal(["a", "b", "t"], method.ParameterList.Parameters.Select(p => p.Identifier.Text));
    }

    /// <summary>
    ///     Empty lists, blank lines between the entries, and the one-per-line form all
    ///     land in the same three positions.
    /// </summary>
    [Theory]
    [InlineData("func M(\n) {\n}\n")]
    [InlineData("func M(\n    a: float\n) {\n}\n")]
    [InlineData("func M(a: float,\n       b: float) {\n}\n")]
    [InlineData("func M(\n\n    a: float,\n\n    b: float\n\n) {\n}\n")]
    [InlineData("func M() {\n    f(\n    )\n}\n")]
    [InlineData("func M() {\n    f(\n        1f,\n        2f\n    )\n}\n")]
    [InlineData("func M() {\n    f(1f,\n      2f)\n}\n")]
    [InlineData("func M() {\n    f(\n        g(\n            1f\n        )\n    )\n}\n")]
    public void Line_breaks_land_in_the_positions_a_list_is_broken_at(string member) {
        var source = $"package A\n\nshader S {{\n{member}}}\n";
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(source, tree.GetRoot().ToFullString());
    }

    /// <summary>
    ///     A newline is layout only between the entries. One in the middle of a parameter
    ///     or an argument is still a terminator, and still an error.
    /// </summary>
    [Theory]
    [InlineData("func M(a:\n float) {\n}\n")]
    [InlineData("func M() {\n    f(1f\n     2f)\n}\n")]
    public void A_newline_inside_an_entry_is_still_a_terminator(string member) {
        var tree = SyntaxTree.ParseText($"package A\n\nshader S {{\n{member}}}\n", path: "Test.rvn");
        Assert.NotEmpty(tree.Diagnostics);
    }
}
