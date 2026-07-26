// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Xunit;
using Green = Vixen.Core.Syntax.InternalSyntax;

namespace Vixen.Core.Syntax.Tests;

/// <summary>
///     Width accounting, trivia separation and round-tripping — over a language that is
///     not Raven, so nothing here can quietly depend on Raven's kind enum.
/// </summary>
public class GreenTreeTests {
    [Fact]
    public void Token_width_separates_trivia_from_text() {
        var token = new ToyToken(ToyKind.Word, "hello", Toy.Space("  "), Toy.Space());

        Assert.Equal(8, token.FullWidth);
        Assert.Equal(5, token.Width);
        Assert.Equal(2, token.GetLeadingTriviaWidth());
        Assert.Equal(1, token.GetTrailingTriviaWidth());
        Assert.Equal("  hello ", token.ToString());
    }

    [Fact]
    public void A_node_sums_its_children_and_round_trips_byte_for_byte() {
        var phrase = Toy.Phrase(
            new ToyToken(ToyKind.Word, "left", trailing: Toy.Space()),
            new ToyToken(ToyKind.Word, "right")
        );

        Assert.Equal("left right", phrase.ToString());
        Assert.Equal(10, phrase.FullWidth);

        // Outer trivia is excluded from Width; the inner space is interior text.
        Assert.Equal(10, phrase.Width);
    }

    [Fact]
    public void Outer_trivia_is_excluded_from_width_at_every_level() {
        var phrase = Toy.Phrase(
            new ToyToken(ToyKind.Word, "a", Toy.Space("   ")),
            new ToyToken(ToyKind.Word, "b", trailing: Toy.Space("  "))
        );

        Assert.Equal("   ab  ", phrase.ToString());
        Assert.Equal(7, phrase.FullWidth);
        Assert.Equal(2, phrase.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void A_list_collapses_to_its_single_element_and_otherwise_holds_all_of_them(int count) {
        var children = new Green.GreenNode?[count];
        for (var i = 0; i < count; i++) {
            children[i] = new ToyToken(ToyKind.Word, i.ToString());
        }

        var list = Green.SyntaxList.List(children);

        switch (count) {
            case 0:
                Assert.Null(list);
                break;
            case 1:
                // A one-element list is the element: no wrapper is allocated.
                Assert.Same(children[0], list);
                Assert.False(list!.IsList);
                break;
            default:
                Assert.True(list!.IsList);
                Assert.Equal(count, list.SlotCount);
                break;
        }
    }

    /// <summary>
    ///     List-ness is answered by <c>IsList</c>, not by comparing kinds — that is what
    ///     lets the shared tree build lists without knowing any language's enum. The
    ///     reserved value still has to line up, or projecting a list node's kind would
    ///     name the wrong member.
    /// </summary>
    [Fact]
    public void A_list_node_carries_the_reserved_kind() {
        var list = Green.SyntaxList.List([
            new ToyToken(ToyKind.Word, "a"),
            new ToyToken(ToyKind.Word, "b")
        ]);

        Assert.NotNull(list);
        Assert.True(list.IsList);
        Assert.Equal(SyntaxKinds.List, list.RawKind);
        Assert.Equal(ToyKind.List, (ToyKind)list.RawKind);
    }

    [Fact]
    public void Terminals_report_themselves_as_first_and_last() {
        var first = new ToyToken(ToyKind.Word, "a");
        var last = new ToyToken(ToyKind.Word, "b");
        var phrase = Toy.Phrase(first, last);

        Assert.Same(first, phrase.GetFirstTerminal());
        Assert.Same(last, phrase.GetLastTerminal());
    }
}
