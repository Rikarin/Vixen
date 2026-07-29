// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     The rules every directed graph in the repository shares, on their own — no ports, no canvas,
///     no document.
/// </summary>
public class GraphInvariantTests {
    static readonly Func<(int From, int To), int> From = edge => edge.From;
    static readonly Func<(int From, int To), int> To = edge => edge.To;

    [Fact]
    public void A_node_reaches_itself() {
        Assert.True(GraphInvariants.Reaches(Array.Empty<(int, int)>(), From, To, 1, 1));
    }

    [Fact]
    public void Reachability_follows_a_chain_the_whole_way() {
        (int, int)[] edges = [(1, 2), (2, 3), (3, 4)];

        Assert.True(GraphInvariants.Reaches(edges, From, To, 1, 4));
        Assert.False(GraphInvariants.Reaches(edges, From, To, 4, 1));
    }

    /// <summary>The direction is the whole answer: a diamond reaches forwards and not back.</summary>
    [Fact]
    public void Reachability_does_not_run_backwards_along_an_edge() {
        (int, int)[] edges = [(1, 2), (1, 3), (2, 4), (3, 4)];

        Assert.True(GraphInvariants.Reaches(edges, From, To, 1, 4));
        Assert.False(GraphInvariants.Reaches(edges, From, To, 2, 3));
    }

    /// <summary>
    ///     ⚠ The one that matters: a graph that already has a loop in it must not hang the walk.
    /// </summary>
    /// <remarks>
    ///     No graph in the repository can get into this state — they all refuse a cycle as it is made
    ///     — but this is the function they refuse it <i>with</i>, and a check that only terminates on
    ///     input it has already vetted is not a check.
    /// </remarks>
    [Fact]
    public void A_graph_that_already_has_a_loop_does_not_hang_the_walk() {
        (int, int)[] edges = [(1, 2), (2, 3), (3, 1)];

        Assert.True(GraphInvariants.Reaches(edges, From, To, 1, 3));
        Assert.False(GraphInvariants.Reaches(edges, From, To, 1, 9));
    }

    /// <summary>A chain far longer than a stack would survive, walked iteratively.</summary>
    [Fact]
    public void A_chain_of_ten_thousand_is_walked_without_recursion() {
        var edges = new (int From, int To)[10_000];

        for (var index = 0; index < edges.Length; index++) {
            edges[index] = (index, index + 1);
        }

        Assert.True(GraphInvariants.Reaches(edges, From, To, 0, 10_000));
    }

    [Fact]
    public void The_edge_arriving_at_an_input_is_found_by_index() {
        (int, int)[] edges = [(1, 2), (3, 4), (5, 6)];

        Assert.Equal(1, GraphInvariants.Arriving(edges, To, 4));
        Assert.Equal(-1, GraphInvariants.Arriving(edges, To, 7));
    }

    /// <summary>Both ends, and the survivors keep the order they were in.</summary>
    [Fact]
    public void Detaching_takes_every_edge_with_an_end_on_the_node() {
        List<(int From, int To)> edges = [(1, 2), (2, 3), (3, 4), (4, 2), (1, 4)];
        List<(int From, int To)> detached = [];

        Assert.Equal(3, GraphInvariants.Detach(edges, From, To, 2, detached));
        Assert.Equal([(1, 2), (2, 3), (4, 2)], detached);
        Assert.Equal([(3, 4), (1, 4)], edges);
    }

    /// <summary>
    ///     A deletion that touches nothing changes nothing — including the list's order.
    /// </summary>
    [Fact]
    public void Detaching_a_node_with_no_edges_leaves_the_list_alone() {
        List<(int From, int To)> edges = [(1, 2), (2, 3)];

        Assert.Equal(0, GraphInvariants.Detach(edges, From, To, 9));
        Assert.Equal([(1, 2), (2, 3)], edges);
    }

    [Fact]
    public void Every_refusal_says_something_and_an_allowed_one_says_nothing() {
        Assert.Empty(GraphInvariants.Describe(GraphConnectionError.None, "a", "b"));

        foreach (var error in Enum.GetValues<GraphConnectionError>()) {
            if (error == GraphConnectionError.None) {
                continue;
            }

            var sentence = GraphInvariants.Describe(error, "a", "b");

            Assert.NotEmpty(sentence);
            Assert.EndsWith(".", sentence, StringComparison.Ordinal);
        }
    }
}
