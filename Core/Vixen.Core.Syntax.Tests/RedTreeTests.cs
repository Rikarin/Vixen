using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Text;
using Xunit;

namespace Vixen.Core.Syntax.Tests;

/// <summary>
///     The lazy red overlay: positions, parents, identity caching and locations.
/// </summary>
public class RedTreeTests {
    static ToyPhrase Build() {
        var green = Toy.Phrase(
            new ToyToken(ToyKind.Word, "left", trailing: Toy.Space()),
            new ToyToken(ToyKind.Word, "right")
        );

        return (ToyPhrase)green.CreateRed(null, 0);
    }

    [Fact]
    public void Children_get_absolute_positions_from_preceding_widths() {
        var root = Build();

        var left = root.GetSlot(0)!;
        var right = root.GetSlot(1)!;

        Assert.Equal(new TextSpan(0, 10), root.FullSpan);
        Assert.Equal(new TextSpan(0, 5), left.FullSpan);

        // "left" plus its trailing space, so the second token starts at 5.
        Assert.Equal(new TextSpan(5, 5), right.FullSpan);
    }

    [Fact]
    public void Span_excludes_trivia_while_FullSpan_includes_it() {
        var root = (ToyPhrase)Toy.Phrase(
            new ToyToken(ToyKind.Word, "a", Toy.Space("  ")),
            new ToyToken(ToyKind.Word, "b", trailing: Toy.Space(" "))
        ).CreateRed(null, 0);

        Assert.Equal(new TextSpan(0, 5), root.FullSpan);
        Assert.Equal(TextSpan.FromBounds(2, 4), root.Span);
    }

    [Fact]
    public void Realized_children_are_cached_so_navigation_is_identity_stable() {
        var root = Build();

        Assert.Same(root.GetSlot(0), root.GetSlot(0));
        Assert.Same(root, root.GetSlot(0)!.Parent);
        Assert.Null(root.Parent);
    }

    [Fact]
    public void Tokens_are_leaves_and_expose_their_text() {
        var root = Build();
        var token = Assert.IsType<SyntaxToken>(root.GetSlot(0));

        Assert.Equal("left", token.Text);
        Assert.Null(token.GetSlot(0));
        Assert.Equal(0, token.SlotCount);
    }

    [Fact]
    public void ChildNodesAndTokens_skips_empty_slots() {
        var root = (ToyPhrase)Toy.Phrase(new ToyToken(ToyKind.Word, "only"), null).CreateRed(null, 0);

        Assert.Equal(2, root.SlotCount);
        Assert.Single(root.ChildNodesAndTokens());
    }

    [Fact]
    public void ToFullString_round_trips_and_ToString_trims_outer_trivia() {
        var root = (ToyPhrase)Toy.Phrase(
            new ToyToken(ToyKind.Word, "a", Toy.Space("  ")),
            new ToyToken(ToyKind.Word, "b", trailing: Toy.Space(" "))
        ).CreateRed(null, 0);

        Assert.Equal("  ab ", root.ToFullString());
        Assert.Equal("ab", root.ToString());
    }

    [Fact]
    public void The_kind_survives_the_round_trip_through_the_raw_value() {
        Assert.Equal(ToyKind.Phrase, Build().Kind);
    }

    /// <summary>
    ///     A node knows its span but not its file. It reaches both through the tree, which
    ///     is why <see cref="ISyntaxTree" /> exists at all.
    /// </summary>
    [Fact]
    public void GetLocation_resolves_through_the_tree_and_is_None_without_one() {
        var root = Build();
        Assert.True(root.GetLocation().IsNone);

        root.SyntaxTree = new ToyTree("toy.txt", SourceText.From("left right"));

        var location = root.GetLocation();
        Assert.False(location.IsNone);
        Assert.Equal("toy.txt", location.FilePath);
        Assert.Equal(new LinePosition(0, 0), location.GetLineSpan().Start);
    }

    [Fact]
    public void A_child_finds_the_tree_by_walking_up_to_the_root() {
        var root = Build();
        root.SyntaxTree = new ToyTree("toy.txt", SourceText.From("left right"));

        Assert.Same(root.SyntaxTree, root.GetSlot(1)!.SyntaxTree);
    }
}
