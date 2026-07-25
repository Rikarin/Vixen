using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
/// Characterization tests for a <em>known defect</em> in the expression grammar,
/// found while building the Phase 2 binder.
/// </summary>
/// <remarks>
/// <para>
/// ANTLR gives a left-recursive rule's alternatives <em>decreasing</em>
/// precedence in the order they are written, but <c>RavenParser2.g4</c>'s
/// <c>expression</c> rule lists them roughly the other way round: assignment
/// first (so it binds tightest) and invocation, indexing and member access last
/// (so they bind loosest). Every binary operator also shares a single
/// alternative, which makes them all one precedence level.
/// </para>
/// <para>
/// The consequences are visible below: <c>1 + f(x)</c> parses as
/// <c>(1 + f)(x)</c> and <c>1 + 2 * 3</c> as <c>(1 + 2) * 3</c>. These tests
/// pin the current behaviour so the fix — reordering and splitting the
/// <c>expression</c> alternatives — has something to flip, and so nobody mistakes
/// the binder for the thing that is wrong.
/// </para>
/// </remarks>
public class GrammarPrecedenceCharacterizationTests {
    static ExpressionSyntax ParseExpression(string expression) {
        var tree = SyntaxTree.ParseText(
            $"package A\n\nshader S {{\n    func M() {{\n        var probe = {expression}\n    }}\n}}\n");

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

    [Fact]
    public void Defect_invocation_binds_looser_than_arithmetic() {
        // Should be `1 + f(x)`; is `(1 + f)(x)`.
        var invocation = Assert.IsType<InvocationExpressionSyntax>(ParseExpression("1 + f(x)"));
        Assert.IsType<BinaryExpressionSyntax>(invocation.Expression);
    }

    [Fact]
    public void Defect_all_binary_operators_share_one_precedence_level() {
        // Should be `1 + (2 * 3)`; is `(1 + 2) * 3`.
        var binary = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("1 + 2 * 3"));
        Assert.Equal("*", binary.OperatorToken.Text);
        Assert.IsType<BinaryExpressionSyntax>(binary.Left);
    }

    [Fact]
    public void Defect_assignment_binds_tighter_than_arithmetic() {
        // Should be `x = (a + b)`; is `(x = a) + b`.
        var binary = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("x = a + b"));
        Assert.IsType<AssignmentExpressionSyntax>(binary.Left);
    }

    [Fact]
    public void Dotted_names_still_parse_correctly_on_both_sides_of_an_operator() {
        // `a.b` reaches the tree as a qualified name rather than through the
        // member-access alternative, so this common shape is unaffected.
        var binary = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a.b * c.d"));
        Assert.IsType<QualifiedNameSyntax>(binary.Left);
        Assert.IsType<QualifiedNameSyntax>(binary.Right);
    }
}
