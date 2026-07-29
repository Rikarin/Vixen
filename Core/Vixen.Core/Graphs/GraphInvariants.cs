// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>Why a connection was refused, or that it was not.</summary>
/// <remarks>
///     ⚠ <b>One set of reasons for every graph in the repository.</b> A graph that is drawn and a
///     graph that is saved refuse the same three things; they differ in what they can name, not in
///     what they allow. Whichever holds a direction reports <see cref="WrongDirection" /> and
///     whichever does not never produces it, but neither invents a reason of its own — so a rule
///     that changes changes here, and a caller that switches over this is told by the compiler.
/// </remarks>
public enum GraphConnectionError : byte {
    /// <summary>It may be made.</summary>
    None,

    /// <summary>The output end names something the graph does not have.</summary>
    FromNotInGraph,

    /// <summary>The input end names something the graph does not have.</summary>
    ToNotInGraph,

    /// <summary>The two ends are not an output and an input.</summary>
    WrongDirection,

    /// <summary>Both ends are on the same node.</summary>
    SameNode,

    /// <summary>The input's node already feeds the output's, so the edge would close a loop.</summary>
    Cycle
}

/// <summary>
///     The rules every directed graph in the repository enforces, in the one place they are written.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists at all.</b> There are two node graphs — one a canvas draws and one a
///         document is saved from — and they are deliberately separate types, because a model that
///         could only be read out of a live element tree would make saving, compiling and diffing a
///         graph depend on a font. What is <i>not</i> deliberate is enforcing the same three
///         invariants twice, by two algorithms, and that is what these methods are: the cycle
///         refusal, the cascade, and the one-edge-per-input rule, each written once.
///     </para>
///     <para>
///         ⚠ <b>A rule change lands here or it lands nowhere.</b> Allowing an input to take more than
///         one edge, say, is a change to <see cref="Arriving" />'s contract — every graph that calls
///         it moves together, and one that had quietly grown its own copy would be the thing this was
///         written to prevent.
///     </para>
///     <para>
///         Generic over the node and the edge rather than over an interface the graphs implement:
///         one holds object references and the other holds numbered identities, and neither should
///         have to give that up. The selectors are static lambdas at every call site, so the
///         delegates are cached and none of this allocates per call beyond the walk itself.
///     </para>
/// </remarks>
public static class GraphInvariants {
    /// <summary>Whether one node feeds another, directly or through any number of others.</summary>
    /// <typeparam name="TNode">How a node is named.</typeparam>
    /// <typeparam name="TEdge">What an edge is.</typeparam>
    /// <param name="edges">Every edge in the graph.</param>
    /// <param name="from">The node an edge leaves.</param>
    /// <param name="to">The node an edge arrives at.</param>
    /// <param name="start">Where the walk begins.</param>
    /// <param name="target">What it is looking for.</param>
    /// <returns><see langword="true" /> if <paramref name="target" /> is reachable.</returns>
    /// <remarks>
    ///     <para>
    ///         The cycle check. A wire from one node to another closes a loop exactly when the
    ///         destination already reaches the source, so a caller asks
    ///         <c>Reaches(edges, …, start: to, target: from)</c> before making one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Breadth-first with an explicit queue rather than recursion.</b> A chain of ten
    ///         thousand nodes is a legal graph and a stack overflow is not a diagnostic. A node is
    ///         reachable from itself, so <paramref name="start" /> equal to <paramref name="target" />
    ///         is true — which is what makes a self-connection fall out of the same question rather
    ///         than needing a rule of its own.
    ///     </para>
    /// </remarks>
    public static bool Reaches<TNode, TEdge>(
        IReadOnlyList<TEdge> edges,
        Func<TEdge, TNode> from,
        Func<TEdge, TNode> to,
        TNode start,
        TNode target
    ) where TNode : notnull {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var comparer = EqualityComparer<TNode>.Default;

        if (comparer.Equals(start, target)) {
            return true;
        }

        Queue<TNode> pending = new();
        HashSet<TNode> seen = new(comparer);

        pending.Enqueue(start);
        seen.Add(start);

        while (pending.Count > 0) {
            var current = pending.Dequeue();

            for (var index = 0; index < edges.Count; index++) {
                var edge = edges[index];

                if (!comparer.Equals(from(edge), current)) {
                    continue;
                }

                var next = to(edge);

                if (comparer.Equals(next, target)) {
                    return true;
                }

                if (seen.Add(next)) {
                    pending.Enqueue(next);
                }
            }
        }

        return false;
    }

    /// <summary>Where the edge arriving at an input is, if there is one.</summary>
    /// <typeparam name="TPort">How an input is named.</typeparam>
    /// <typeparam name="TEdge">What an edge is.</typeparam>
    /// <param name="edges">Every edge in the graph.</param>
    /// <param name="to">The input an edge arrives at.</param>
    /// <param name="input">The input being asked about.</param>
    /// <returns>Its index in <paramref name="edges" />, or <c>-1</c> when the input is empty.</returns>
    /// <remarks>
    ///     ⚠ <b>An input takes one edge and an output takes many</b>, which is the asymmetry every
    ///     data-flow graph has: a value can feed anything, and a slot that expected one value cannot
    ///     be handed two. A caller connecting to an occupied input removes what this returns rather
    ///     than refusing, because a user who drags a second wire into a full slot means to change what
    ///     feeds it — a version that refused would make the gesture "delete the old wire, then drag
    ///     the new one", two steps for one intention, and the first has no obvious affordance.
    ///     <para>
    ///         This is also the seam a multi-input port would come through. Nothing else knows the
    ///         rule.
    ///     </para>
    /// </remarks>
    public static int Arriving<TPort, TEdge>(IReadOnlyList<TEdge> edges, Func<TEdge, TPort> to, TPort input)
        where TPort : notnull {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(to);

        var comparer = EqualityComparer<TPort>.Default;

        for (var index = 0; index < edges.Count; index++) {
            if (comparer.Equals(to(edges[index]), input)) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Takes out every edge with an end on a node.</summary>
    /// <typeparam name="TNode">How a node is named.</typeparam>
    /// <typeparam name="TEdge">What an edge is.</typeparam>
    /// <param name="edges">Every edge in the graph. Rewritten in place.</param>
    /// <param name="from">The node an edge leaves.</param>
    /// <param name="to">The node an edge arrives at.</param>
    /// <param name="node">The node being removed.</param>
    /// <param name="detached">Told what went, in the order it was in, for an undo to put back.</param>
    /// <returns>How many went.</returns>
    /// <remarks>
    ///     ⚠ <b>The edges go with the node.</b> An edge whose node is gone is drawn from an anchor
    ///     computed off a rectangle nobody owns — and it would still be there to be saved, so the
    ///     graph would reload with a connection to something that does not exist.
    ///     <para>
    ///         The survivors keep their order and so does <paramref name="detached" />, because a
    ///         graph that reordered its edges on a deletion would diff as though every one of them
    ///         had changed.
    ///     </para>
    /// </remarks>
    public static int Detach<TNode, TEdge>(
        List<TEdge> edges,
        Func<TEdge, TNode> from,
        Func<TEdge, TNode> to,
        TNode node,
        ICollection<TEdge>? detached = null
    ) where TNode : notnull {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var comparer = EqualityComparer<TNode>.Default;
        var kept = 0;

        for (var index = 0; index < edges.Count; index++) {
            var edge = edges[index];

            if (comparer.Equals(from(edge), node) || comparer.Equals(to(edge), node)) {
                detached?.Add(edge);

                continue;
            }

            edges[kept++] = edge;
        }

        var gone = edges.Count - kept;
        edges.RemoveRange(kept, gone);

        return gone;
    }

    /// <summary>What to tell somebody about a refusal.</summary>
    /// <typeparam name="TPort">How an end of an edge is named.</typeparam>
    /// <param name="error">Why it was refused.</param>
    /// <param name="from">The output end.</param>
    /// <param name="to">The input end.</param>
    /// <returns>A sentence, or an empty string for <see cref="GraphConnectionError.None" />.</returns>
    /// <remarks>
    ///     Here rather than at each graph so that the two say the same thing about the same refusal.
    ///     A graph that throws and a graph that returns null still differ in <i>how</i> they refuse —
    ///     one is a document API and the other is a gesture — but no longer in what they refuse or in
    ///     what an author is told about it.
    /// </remarks>
    public static string Describe<TPort>(GraphConnectionError error, TPort from, TPort to) =>
        error switch {
            GraphConnectionError.None => "",
            GraphConnectionError.FromNotInGraph => $"{from} is not in this graph.",
            GraphConnectionError.ToNotInGraph => $"{to} is not in this graph.",
            GraphConnectionError.WrongDirection =>
                $"{from} and {to} are not an output and an input, so no wire runs between them.",
            GraphConnectionError.SameNode => $"{from} and {to} are on the same node, which cannot feed itself.",
            GraphConnectionError.Cycle =>
                $"Connecting {from} to {to} would close a cycle: {to} already feeds {from}.",
            _ => $"{from} cannot be connected to {to}."
        };
}
