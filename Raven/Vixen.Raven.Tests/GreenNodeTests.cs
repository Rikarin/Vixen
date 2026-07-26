// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Syntax;
using Xunit;
using Green = Vixen.Core.Syntax.InternalSyntax;

namespace Tests;

/// <summary>
///     Foundation tests for the internal green tree: width accounting, trivia
///     separation, and byte-for-byte full-text round-tripping.
/// </summary>
public class GreenNodeTests {
    static Green.SyntaxTrivia Space => new((int)SyntaxKind.WhitespaceTrivia, " ");

    [Fact]
    public void Token_full_width_includes_trivia() {
        // "  foo " -> 2 leading + 3 text + 1 trailing
        var token = new Green.SyntaxIdentifier((int)SyntaxKind.IdentifierToken, 
            "foo",
            new Green.SyntaxTrivia((int)SyntaxKind.WhitespaceTrivia, "  "),
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
        Green.GreenNode a = new Green.SyntaxIdentifier((int)SyntaxKind.IdentifierToken, "a", trailing: Space);
        Green.GreenNode b = new Green.SyntaxIdentifier((int)SyntaxKind.IdentifierToken, "b");
        var list = Green.SyntaxList.List(a, b);

        Assert.Equal(2, list.SlotCount);
        Assert.Equal(a.FullWidth + b.FullWidth, list.FullWidth);
        Assert.Equal("a b", list.ToString());
    }

    [Fact]
    public void Node_span_excludes_outer_trivia_but_full_width_keeps_it() {
        // Leading trivia on the first terminal and trailing on the last must be
        // excluded from Width but retained in FullWidth.
        Green.GreenNode first = new Green.SyntaxIdentifier((int)SyntaxKind.IdentifierToken, "x", new Green.SyntaxTrivia((int)SyntaxKind.WhitespaceTrivia, "  "));
        Green.GreenNode last = new Green.SyntaxIdentifier((int)SyntaxKind.IdentifierToken, "y", trailing: new Green.SyntaxTrivia((int)SyntaxKind.WhitespaceTrivia, "   "));
        var list = Green.SyntaxList.List(first, last);

        Assert.Equal(2, list.GetLeadingTriviaWidth());
        Assert.Equal(3, list.GetTrailingTriviaWidth());
        Assert.Equal(list.FullWidth - 5, list.Width);
        Assert.Equal("  xy   ", list.ToString());
    }
}
