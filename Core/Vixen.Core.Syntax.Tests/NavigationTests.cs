// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Text;
using Xunit;

namespace Vixen.Core.Syntax.Tests;

/// <summary>
///     Navigation over the red tree: descendants, ancestors, and the two position lookups every
///     feature over a caret is built on.
/// </summary>
/// <remarks>
///     These live in the shared assembly rather than in Raven because all three front ends want
///     them and none of them is language-specific — the editor's syntax highlighting, its
///     go-to-definition, and the shader graph's mapping from generated source back to the node
///     that produced it are the same two questions asked of three grammars.
/// </remarks>
public class NavigationTests {
    /// <summary>
    ///     <c>"a b, c "</c> — a phrase of a word and a nested phrase, the inner one built through a
    ///     list so the flattening is exercised rather than assumed.
    /// </summary>
    static ToyPhrase Build() =>
        (ToyPhrase)Toy.Phrase(
                new ToyToken(ToyKind.Word, "a", trailing: Toy.Space()),
                Toy.Phrase(
                    Toy.List(
                        new ToyToken(ToyKind.Word, "b"),
                        new ToyToken(ToyKind.Comma, ",", trailing: Toy.Space())
                    ),
                    new ToyToken(ToyKind.Word, "c", trailing: Toy.Space())
                )
            )
            .CreateRed(null, 0);

    [Fact]
    public void The_fixture_covers_the_text_it_claims_to() {
        Assert.Equal("a b, c ", Build().ToFullString());
    }

    [Fact]
    public void Children_separate_into_nodes_and_tokens_with_lists_flattened() {
        var root = Build();

        Assert.Equal(["a"], root.ChildTokens().Select(t => t.Text));
        var inner = Assert.Single(root.ChildNodes());

        // The list is gone: its two elements are the inner phrase's own children.
        Assert.Equal(["b", ",", "c"], inner.ChildTokens().Select(t => t.Text));
        Assert.Empty(inner.ChildNodes());
    }

    [Fact]
    public void ChildNodesAndTokens_still_walks_raw_slots() {
        // Deliberately unchanged: the tree dumper and the round-trip tests are written against
        // the slot walk, and a list node is a slot.
        var inner = Assert.Single(Build().ChildNodes());
        Assert.True(inner.ChildNodesAndTokens().First().IsList);
    }

    [Fact]
    public void Descendants_are_in_source_order() {
        var root = Build();

        Assert.Equal(["a", "b", ",", "c"], root.DescendantTokens().Select(t => t.Text));
        Assert.Single(root.DescendantNodes());
        Assert.Equal(2, root.DescendantNodesAndSelf().Count());
        Assert.Equal(5, root.DescendantNodesAndTokens().Count());
    }

    [Fact]
    public void The_first_and_last_tokens_bound_the_subtree() {
        var root = Build();

        Assert.Equal("a", root.GetFirstToken()!.Text);
        Assert.Equal("c", root.GetLastToken()!.Text);
    }

    [Fact]
    public void Ancestors_run_innermost_first_and_stop_at_the_root() {
        var root = Build();
        var last = root.DescendantTokens().Last();

        var inner = root.ChildNodes().Single();

        Assert.Equal([inner, root], last.Ancestors());
        Assert.Equal([last, inner, root], last.AncestorsAndSelf());

        // Innermost first: the token's nearest phrase is the inner one, not the root.
        Assert.Same(inner, last.FirstAncestorOrSelf<ToyPhrase>());
        Assert.Same(root, root.FirstAncestorOrSelf<ToyPhrase>());
        Assert.Null(root.FirstAncestorOrSelf<SyntaxToken>());
    }

    [Fact]
    public void Containment_is_asked_of_the_ancestor() {
        var root = Build();
        var inner = root.ChildNodes().Single();
        var first = root.GetFirstToken()!;

        Assert.True(root.Contains(first));
        Assert.True(root.Contains(root));
        Assert.False(inner.Contains(first));
        Assert.False(inner.Contains(null));
    }

    /// <summary>
    ///     Every position in the file answers, including the ones inside trivia — a caret in the
    ///     indentation is still somewhere, and "what is under the cursor" has to work there.
    /// </summary>
    [Theory]
    [InlineData(0, "a")]
    [InlineData(1, "a")] // the space trailing `a`
    [InlineData(2, "b")]
    [InlineData(3, ",")]
    [InlineData(4, ",")] // the space trailing the comma
    [InlineData(5, "c")]
    [InlineData(6, "c")] // the space trailing `c`
    [InlineData(7, "c")] // one past the end: the caret at end-of-file
    public void FindToken_answers_for_every_position_in_the_full_span(int position, string expected) {
        Assert.Equal(expected, Build().FindToken(position).Text);
    }

    [Fact]
    public void FindToken_refuses_a_position_outside_the_node() {
        var root = Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => root.FindToken(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.FindToken(8));
    }

    /// <summary>
    ///     A zero-width token is what recovery leaves where the source had nothing. It contains no
    ///     position, so a lookup passes over it rather than answering with a token the author
    ///     cannot see.
    /// </summary>
    [Fact]
    public void FindToken_skips_the_missing_tokens_recovery_fabricated() {
        var root = (ToyPhrase)Toy.Phrase(
                new ToyToken(ToyKind.Comma, string.Empty, isMissing: true),
                new ToyToken(ToyKind.Word, "real")
            )
            .CreateRed(null, 0);

        Assert.Equal("real", root.FindToken(0).Text);
        Assert.True(((SyntaxToken)root.GetSlot(0)!).IsMissing);
        Assert.False(((SyntaxToken)root.GetSlot(1)!).IsMissing);
    }

    [Fact]
    public void FindNode_returns_the_innermost_node_covering_a_span() {
        var root = Build();
        var inner = root.ChildNodes().Single();

        // `b, c` is exactly the inner phrase; widening to include `a` can only be the root.
        Assert.Same(inner, root.FindNode(TextSpan.FromBounds(2, 6)));
        Assert.Same(root, root.FindNode(TextSpan.FromBounds(0, 6)));

        // A token is not a node, so a span inside one answers with the node holding it.
        Assert.Same(inner, root.FindNode(new TextSpan(2, 1)));

        // Nothing beneath the root covers the trailing trivia, which Span excludes.
        Assert.Null(root.FindNode(new TextSpan(6, 1)));
    }

    [Fact]
    public void Trivia_is_reachable_from_the_token_that_carries_it() {
        var root = (ToyPhrase)Toy.Phrase(
                new ToyToken(ToyKind.Word, "x", Toy.List(Toy.Space("  "), Toy.Comment("/* why */")), Toy.Space()),
                null
            )
            .CreateRed(null, 0);

        var token = (SyntaxToken)root.GetSlot(0)!;

        Assert.Equal(["  ", "/* why */"], token.LeadingTrivia.Select(t => t.Text));
        Assert.Equal([(int)ToyKind.Space, (int)ToyKind.Comment], token.LeadingTrivia.Select(t => t.RawKind));

        // Positions are absolute, so a highlighter can colour the comment where it actually is.
        Assert.Equal(new TextSpan(2, 9), token.LeadingTrivia[1].Span);
        Assert.Equal(new TextSpan(12, 1), Assert.Single(token.TrailingTrivia).Span);
    }

    [Fact]
    public void Equivalence_ignores_trivia_but_not_structure_or_text() {
        var spaced = (ToyPhrase)Toy.Phrase(
                new ToyToken(ToyKind.Word, "a", Toy.Space("   "), Toy.Space()),
                new ToyToken(ToyKind.Word, "b")
            )
            .CreateRed(null, 0);

        var tight = (ToyPhrase)Toy.Phrase(
                new ToyToken(ToyKind.Word, "a"),
                new ToyToken(ToyKind.Word, "b")
            )
            .CreateRed(null, 0);

        var different = (ToyPhrase)Toy.Phrase(
                new ToyToken(ToyKind.Word, "a"),
                new ToyToken(ToyKind.Word, "c")
            )
            .CreateRed(null, 0);

        Assert.True(spaced.IsEquivalentTo(tight));
        Assert.NotEqual(spaced.ToFullString(), tight.ToFullString());

        Assert.False(spaced.IsEquivalentTo(different));
        Assert.False(spaced.IsEquivalentTo(null));
        Assert.False(spaced.IsEquivalentTo(spaced.GetFirstToken()));
    }
}
