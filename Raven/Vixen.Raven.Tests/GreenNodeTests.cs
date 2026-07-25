using Vixen.Raven.Syntax;
using Vixen.Raven.Syntax.InternalSyntax;
using Xunit;
using SyntaxTrivia = Vixen.Raven.Syntax.InternalSyntax.SyntaxTrivia;
using SyntaxList = Vixen.Raven.Syntax.InternalSyntax.SyntaxList;

namespace Tests;

/// <summary>
///     Foundation tests for the internal green tree: width accounting, trivia
///     separation, and byte-for-byte full-text round-tripping.
/// </summary>
public class GreenNodeTests {
    static SyntaxTrivia Space => new(SyntaxKind.WhitespaceTrivia, " ");

    [Fact]
    public void Token_full_width_includes_trivia() {
        // "  foo " -> 2 leading + 3 text + 1 trailing
        var token = new SyntaxIdentifier(
            "foo",
            new SyntaxTrivia(SyntaxKind.WhitespaceTrivia, "  "),
            Space
        );

        Assert.Equal(6, token.FullWidth);
        Assert.Equal(3, token.Width);
        Assert.Equal(2, token.GetLeadingTriviaWidth());
        Assert.Equal(1, token.GetTrailingTriviaWidth());
        Assert.Equal("  foo ", token.ToString());
    }

    [Fact]
    public void List_sums_child_widths_and_roundtrips() {
        GreenNode a = new SyntaxIdentifier("a", trailing: Space);
        GreenNode b = new SyntaxIdentifier("b");
        var list = SyntaxList.List(a, b);

        Assert.Equal(2, list.SlotCount);
        Assert.Equal(a.FullWidth + b.FullWidth, list.FullWidth);
        Assert.Equal("a b", list.ToString());
    }

    [Fact]
    public void Node_span_excludes_outer_trivia_but_full_width_keeps_it() {
        // Leading trivia on the first terminal and trailing on the last must be
        // excluded from Width but retained in FullWidth.
        GreenNode first = new SyntaxIdentifier("x", new SyntaxTrivia(SyntaxKind.WhitespaceTrivia, "  "));
        GreenNode last = new SyntaxIdentifier("y", trailing: new SyntaxTrivia(SyntaxKind.WhitespaceTrivia, "   "));
        var list = SyntaxList.List(first, last);

        Assert.Equal(2, list.GetLeadingTriviaWidth());
        Assert.Equal(3, list.GetTrailingTriviaWidth());
        Assert.Equal(list.FullWidth - 5, list.Width);
        Assert.Equal("  xy   ", list.ToString());
    }
}
