// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax;

namespace Tests;

/// <summary>
///     The <c>expression</c> rule's precedence ladder. ANTLR gives a left-recursive
///     rule's alternatives <em>decreasing</em> precedence in the order they are
///     written, so <c>RavenParser.g4</c> lists them tightest-first: postfix, prefix,
///     the arithmetic ladder, conditional, then assignment.
/// </summary>
/// <remarks>
///     These started life as characterization tests for the inverted ordering the
///     grammar shipped with — <c>1 + f(x)</c> parsed as <c>(1 + f)(x)</c>,
///     <c>1 + 2 * 3</c> as <c>(1 + 2) * 3</c>, and <c>x = a + b</c> as
///     <c>(x = a) + b</c>. They now pin the corrected shapes.
/// </remarks>
public class ExpressionPrecedenceTests {
    [Fact]
    public void Invocation_binds_tighter_than_arithmetic() {
        // `1 + f(x)`, not `(1 + f)(x)`.
        var binary = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("1 + f(x)"));
        Assert.Equal("+", binary.OperatorToken.Text);
        Assert.IsType<InvocationExpressionSyntax>(binary.Right);
    }

    [Fact]
    public void Element_access_and_member_access_bind_tighter_than_arithmetic() {
        var indexed = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("1 + xs[0]"));
        Assert.IsType<ElementAccessExpressionSyntax>(indexed.Right);

        // A dotted name reaches the tree as a qualified name primary, so it is
        // already a single operand.
        var dotted = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a.b * c.d"));
        Assert.IsType<QualifiedNameSyntax>(dotted.Left);
        Assert.IsType<QualifiedNameSyntax>(dotted.Right);
    }

    [Fact]
    public void Multiplication_binds_tighter_than_addition() {
        // `1 + (2 * 3)`, not `(1 + 2) * 3`.
        var binary = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("1 + 2 * 3"));
        Assert.Equal("+", binary.OperatorToken.Text);

        var right = Assert.IsType<BinaryExpressionSyntax>(binary.Right);
        Assert.Equal("*", right.OperatorToken.Text);
    }

    [Fact]
    public void The_arithmetic_ladder_runs_from_multiplicative_to_logical_or() {
        // `(a * b) + c` — same level associates left.
        var additive = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a * b + c"));
        Assert.Equal("+", additive.OperatorToken.Text);
        Assert.Equal("*", Assert.IsType<BinaryExpressionSyntax>(additive.Left).OperatorToken.Text);

        // `(a + b) < c`
        var relational = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a + b < c"));
        Assert.Equal("<", relational.OperatorToken.Text);

        // `(a < b) == c`
        var equality = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a < b == c"));
        Assert.Equal("==", equality.OperatorToken.Text);

        // `(a == b) && c`
        var logical = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a == b && c"));
        Assert.Equal("&&", logical.OperatorToken.Text);

        // `(a && b) || c`
        var or = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a && b || c"));
        Assert.Equal("||", or.OperatorToken.Text);
    }

    [Fact]
    public void Assignment_binds_loosest_and_associates_right() {
        // `x = (a + b)`, not `(x = a) + b`.
        var assignment = Assert.IsType<AssignmentExpressionSyntax>(ParseExpression("x = a + b"));
        Assert.Equal("+", Assert.IsType<BinaryExpressionSyntax>(assignment.Right).OperatorToken.Text);

        // `x = (y = 1)`
        var chained = Assert.IsType<AssignmentExpressionSyntax>(ParseExpression("x = y = 1"));
        Assert.IsType<AssignmentExpressionSyntax>(chained.Right);
    }

    [Fact]
    public void Unary_operators_bind_tighter_than_arithmetic() {
        // `(-a) * b`
        var binary = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("-a * b"));
        Assert.Equal("*", binary.OperatorToken.Text);
        Assert.IsType<PrefixUnaryExpressionSyntax>(binary.Left);

        // `(a++) + b`
        var postfix = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a++ + b"));
        Assert.IsType<PostfixUnaryExpressionSyntax>(postfix.Left);
    }

    [Fact]
    public void A_cast_applies_to_the_operand_not_the_whole_expression() {
        // `((int)a) + b`
        var binary = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("(int)a + b"));
        Assert.Equal("+", binary.OperatorToken.Text);
        Assert.IsType<CastExpressionSyntax>(binary.Left);
    }

    [Fact]
    public void The_conditional_operator_sits_between_logic_and_assignment() {
        // `(a || b) ? c : d`
        var conditional = Assert.IsType<ConditionalExpressionSyntax>(ParseExpression("a || b ? c : d"));
        Assert.Equal("||", Assert.IsType<BinaryExpressionSyntax>(conditional.Condition).OperatorToken.Text);

        // `x = (a ? b : c)`
        var assignment = Assert.IsType<AssignmentExpressionSyntax>(ParseExpression("x = a ? b : c"));
        Assert.IsType<ConditionalExpressionSyntax>(assignment.Right);
    }


    [Fact]
    public void A_range_sits_between_shifts_and_comparisons() {
        // `a .. (b + c)`
        var range = Assert.IsType<RangeExpressionSyntax>(ParseExpression("a .. b + c"));
        Assert.Equal("+", Assert.IsType<BinaryExpressionSyntax>(range.Right).OperatorToken.Text);
    }

    static ExpressionSyntax ParseExpression(string expression) {
        var tree = SyntaxTree.ParseText(
            $"package A\n\nshader S {{\n    func M() {{\n        var probe = {expression}\n    }}\n}}\n"
        );

        Assert.Empty(tree.Diagnostics);

        return Find(tree.GetRoot())
            ?? throw new InvalidOperationException("No initializer found.");

        static ExpressionSyntax? Find(SyntaxNode node) {
            if (node is EqualsValueClauseSyntax clause) {
                return clause.Value;
            }

            foreach (var child in node.ChildNodesAndTokens()) {
                if (Find(child) is { } found) {
                    return found;
                }
            }

            return null;
        }
    }
}
