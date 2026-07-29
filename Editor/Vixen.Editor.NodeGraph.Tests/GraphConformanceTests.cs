// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.NodeGraph;
using Xunit;
using CanvasGraph = Vixen.Ui.Controls.Advanced.NodeGraph;
using CanvasNode = Vixen.Ui.Controls.Advanced.GraphNode;
using CanvasPort = Vixen.Ui.Controls.Advanced.GraphPort;

namespace Tests;

/// <summary>
///     The two graph models, asked the same questions and required to give the same answers.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this file exists.</b> There are two node graphs — <see cref="NodeGraphModel" />,
///         which is the document, and <c>Vixen.Ui.Controls.Advanced.NodeGraph</c>, which is what a
///         canvas draws — and keeping them apart is argued for at length in both. What was never
///         argued for is the two of them enforcing the same three invariants independently. They now
///         share <see cref="GraphInvariants" />, and this is what says so out loud: a rule that grows
///         a second copy fails here rather than in whichever of the two nobody was looking at.
///     </para>
///     <para>
///         ⚠ <b>It is a conformance test, not a unit test of either.</b> Each model has its own file
///         of those. Nothing here asserts what the rules <i>are</i>; it asserts that both models have
///         the same ones.
///     </para>
/// </remarks>
public class GraphConformanceTests {
    [Fact]
    public void A_cycle_is_refused_by_both() {
        var pair = new GraphPair();

        var a = pair.Add();
        var b = pair.Add();
        var c = pair.Add();

        Assert.Equal(GraphConnectionError.None, pair.Connect(a, b));
        Assert.Equal(GraphConnectionError.None, pair.Connect(b, c));
        Assert.Equal(GraphConnectionError.Cycle, pair.Connect(c, a));
    }

    [Fact]
    public void A_node_wired_to_itself_is_refused_by_both() {
        var pair = new GraphPair();
        var node = pair.Add();

        Assert.Equal(GraphConnectionError.SameNode, pair.Connect(node, node));
    }

    [Fact]
    public void An_end_that_is_not_in_the_graph_is_refused_by_both() {
        var pair = new GraphPair();

        var known = pair.Add();
        var stranger = pair.Stranger();

        Assert.Equal(GraphConnectionError.FromNotInGraph, pair.Connect(stranger, known));
        Assert.Equal(GraphConnectionError.ToNotInGraph, pair.Connect(known, stranger));
    }

    [Fact]
    public void A_second_edge_into_an_input_displaces_the_first_in_both() {
        var pair = new GraphPair();

        var first = pair.Add();
        var second = pair.Add();
        var sink = pair.Add();

        pair.Connect(first, sink, "A");
        pair.Connect(second, sink, "A");

        // One each, and it is the second in both: replace, not refuse, not two.
        pair.AssertAgreed();
        Assert.Equal(1, pair.Edges);
    }

    [Fact]
    public void An_output_feeds_as_many_inputs_as_it_likes_in_both() {
        var pair = new GraphPair();

        var source = pair.Add();

        pair.Connect(source, pair.Add());
        pair.Connect(source, pair.Add());

        pair.AssertAgreed();
        Assert.Equal(2, pair.Edges);
    }

    [Fact]
    public void Removing_a_node_takes_the_same_edges_with_it_in_both() {
        var pair = new GraphPair();

        var source = pair.Add();
        var middle = pair.Add();
        var sink = pair.Add();

        pair.Connect(source, middle);
        pair.Connect(middle, sink);
        pair.Remove(middle);

        pair.AssertAgreed();
        Assert.Equal(0, pair.Edges);
    }

    /// <summary>
    ///     A long scripted run, because the cases above are the ones somebody thought of.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Deterministic, from a fixed seed and a hand-rolled generator.</b> A flaky conformance
    ///     test is one people delete. The script is the same on every machine and on every run, so a
    ///     failure is a bug in one of the two models and never in the weather; the generator is
    ///     written out rather than taken from <c>Random</c> because that class does not promise the
    ///     same sequence across runtime versions, which is the one property this needs.
    /// </remarks>
    [Fact]
    public void The_two_models_stay_in_step_over_a_long_scripted_run() {
        var pair = new GraphPair();
        var made = new List<NodeId>();

        for (var index = 0; index < 12; index++) {
            made.Add(pair.Add());
        }

        var state = 0x5eedu;

        for (var step = 0; step < 600; step++) {
            // xorshift32: three shifts, no allocation, and the same numbers everywhere forever.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            var roll = state;
            var from = made[(int)(roll % (uint)made.Count)];
            var to = made[(int)(roll / 7 % (uint)made.Count)];
            var port = $"P{roll / 13 % 3}";

            if (roll % 23 == 0) {
                // Occasionally delete one and put a fresh one in its place, so the run covers the
                // cascade and the identities do not all outlive the whole script.
                pair.Remove(from);
                made.Remove(from);
                made.Add(pair.Add());

                continue;
            }

            pair.Connect(from, to, port);
        }

        // Every Connect and every Remove above asserted agreement as it went; this is the tally.
        pair.AssertAgreed();
        Assert.True(pair.Edges > 0, "the script never made an edge, so it proved nothing");
    }

    /// <summary>One document graph and one canvas graph, kept as the same graph by construction.</summary>
    /// <remarks>
    ///     The projection <see cref="NodeGraphView" /> does, reduced to what a rule can see: an
    ///     identity per node, a port object per name, and no view, canvas, registry or font anywhere
    ///     near it.
    /// </remarks>
    sealed class GraphPair {
        readonly NodeGraphModel document = new();
        readonly CanvasGraph canvas = new();

        readonly Dictionary<NodeId, CanvasNode> shown = [];
        readonly Dictionary<(NodeId Node, string Port, bool Input), CanvasPort> ports = [];

        /// <summary>How many edges the two agree that they have.</summary>
        public int Edges => document.Edges.Count;

        /// <summary>Adds a node to both.</summary>
        public NodeId Add() {
            var node = document.Add("Test/Node");

            shown[node.Id] = canvas.AddNode(node.Id.ToString());

            return node.Id;
        }

        /// <summary>
        ///     A node in neither graph, under an identity neither will hand out.
        /// </summary>
        /// <remarks>
        ///     ⚠ Made in both, added to neither. "An end that is not in this graph" has to be
        ///     representable on both sides for the refusal to be comparable at all — on the document
        ///     side that is an identity nothing was added under, and on the canvas side it is a node
        ///     object that was never handed to <c>AddNode</c>.
        /// </remarks>
        public NodeId Stranger() {
            var id = new NodeId(int.MaxValue);

            shown[id] = new(id.ToString());

            return id;
        }

        /// <summary>Connects in both, and requires the same verdict from each.</summary>
        /// <returns>The verdict they agreed on.</returns>
        public GraphConnectionError Connect(NodeId from, NodeId to, string port = "A") {
            document.TryConnect(new(from, "Out"), new(to, port), out _, out var documentError);
            canvas.TryConnect(Port(from, "Out", false), Port(to, port, true), out var canvasError);

            Assert.Equal(documentError, canvasError);
            AssertAgreed();

            return documentError;
        }

        /// <summary>Removes from both.</summary>
        public void Remove(NodeId id) {
            document.Remove(id, out _);

            if (shown.Remove(id, out var node)) {
                canvas.Remove(node);
            }

            AssertAgreed();
        }

        /// <summary>
        ///     That the two hold the same set of edges — not merely the same number of them.
        /// </summary>
        public void AssertAgreed() {
            var expected = document.Edges
                .Select(edge => (edge.From.Node, edge.From.Port, edge.To.Node, edge.To.Port))
                .ToHashSet();

            var actual = canvas.Wires
                .Select(wire => (Owner(wire.From), wire.From.Name, Owner(wire.To), wire.To.Name))
                .ToHashSet();

            Assert.Equal(expected, actual);
        }

        NodeId Owner(CanvasPort port) {
            foreach (var (id, node) in shown) {
                if (ReferenceEquals(node, port.Node)) {
                    return id;
                }
            }

            return NodeId.None;
        }

        CanvasPort Port(NodeId node, string name, bool input) {
            var key = (node, name, input);

            if (ports.TryGetValue(key, out var found)) {
                return found;
            }

            var view = shown[node];
            var made = input ? view.AddInput(name) : view.AddOutput(name);

            ports[key] = made;

            return made;
        }
    }
}
