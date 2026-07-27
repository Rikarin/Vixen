// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     A type argument that is itself generic — docs/plan/07 § I, "nested generics do not parse".
/// </summary>
/// <remarks>
///     <para>
///         <c>Box&lt;Box&lt;float&gt;&gt;</c> ends on <c>&gt;&gt;</c>, which the lexer takes as one
///         right-shift token because nothing at that level tells it otherwise. The parser splits it
///         back into two, but only when an enclosing type-argument list will take the second half —
///         so <c>a &lt; b &gt;&gt; c</c> stays the comparison it always was.
///     </para>
///     <para>
///         Deliberately not in <c>all_constructs.rvn</c>: the grammar oracle is broken here too
///         (its <c>type_argument_list</c> matches a single <c>'&gt;'</c>), so a shared corpus entry
///         would fail the differential rather than pin the fix.
///     </para>
/// </remarks>
public class NestedGenericTests {
    [Theory]
    [InlineData("Box<Box<float>>")]
    [InlineData("Box<Box<Box<float>>>")]
    [InlineData("Box<Box<float>, int>")]
    [InlineData("Box<int, Box<float>>")]
    [InlineData("Box< Box<float> >")]
    [InlineData("Box<Box<float> >")]
    [InlineData("A.B<A.B<float>>")]
    [InlineData("Box<Box<float>>[4]")]
    public void A_nested_type_argument_parses_with_no_diagnostics(string type) {
        var source = $"package A\n\nshader S {{\n    var value: {type}\n}}\n";
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(source, tree.GetRoot().ToFullString());
    }

    [Fact]
    public void The_split_produces_two_ordinary_close_angle_tokens() {
        var tree = SyntaxTree.ParseText("package A\n\nshader S {\n    var value: Box<Box<float>>\n}\n");
        Assert.Empty(tree.Diagnostics);

        var outer = Assert.IsType<GenericNameSyntax>(FindGeneric(tree.GetRoot()));
        var inner = Assert.IsType<GenericNameSyntax>(
            Assert.Single(outer.TypeArgumentList.Arguments)
        );

        Assert.Equal("Box", outer.Identifier.Text);
        Assert.Equal("Box", inner.Identifier.Text);

        // One `>` each, adjacent, together spelling the source's `>>`.
        Assert.Equal(">", inner.TypeArgumentList.GreaterThanToken.Text);
        Assert.Equal(">", outer.TypeArgumentList.GreaterThanToken.Text);
        Assert.Equal(
            inner.TypeArgumentList.GreaterThanToken.Span.End,
            outer.TypeArgumentList.GreaterThanToken.Span.Start
        );
    }

    /// <summary>
    ///     The ambiguity the split must not resolve the other way. Pinned as a tree shape rather
    ///     than as "no diagnostics", because the wrong reading also parses cleanly.
    /// </summary>
    [Theory]
    [InlineData("val x = a < b >> c")]
    [InlineData("val x = a < b>>c")]
    [InlineData("val x = a >> b < c")]
    public void A_right_shift_after_a_comparison_is_still_a_shift(string statement) {
        var source = $"package A\n\nshader S {{\n    func M() {{\n        {statement}\n    }}\n}}\n";
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(source, tree.GetRoot().ToFullString());
        Assert.Null(FindGeneric(tree.GetRoot()));

        // One token, not two: a binary operator reaches the tree as its own text.
        Assert.Contains("Token(OperatorToken) \">>\"", SyntaxDumper.Dump(tree.GetRoot()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_generic_in_expression_position_parses_as_one_name() {
        var tree = SyntaxTree.ParseText(
            "package A\n\nshader S {\n    func M() {\n        val x = Make<Box<float>>()\n    }\n}\n"
        );

        Assert.Empty(tree.Diagnostics);
        Assert.NotNull(FindGeneric(tree.GetRoot()));
    }

    static GenericNameSyntax? FindGeneric(SyntaxNode node) {
        if (node is GenericNameSyntax generic) {
            return generic;
        }

        foreach (var child in node.ChildNodesAndTokens()) {
            if (FindGeneric(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
