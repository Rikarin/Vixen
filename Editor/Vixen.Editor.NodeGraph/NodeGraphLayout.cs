// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.NodeGraph;

/// <summary>How much room an automatic layout leaves.</summary>
/// <param name="Origin">Where the leftmost column's left edge goes.</param>
/// <param name="ColumnGap">How much clear space is left between one column and the next.</param>
/// <param name="RowGap">And between one node and the one under it.</param>
/// <param name="NodeWidth">How wide a node is taken to be.</param>
/// <param name="HeaderHeight">How tall its title bar is.</param>
/// <param name="PortPitch">How far apart its ports are.</param>
/// <param name="Padding">How much room is left under its last port.</param>
/// <remarks>
///     ⚠ <b>The four size numbers are the view's, restated.</b> A layout that assumed every node was
///     the same height puts a two-port node and a twelve-port one on the same pitch, so the columns
///     either overlap or are mostly air. They are parameters rather than read from a canvas because
///     this runs against a model with no view attached — which is what makes it testable against
///     coordinates rather than against a screenshot — and <c>NodeGraphView</c> passes its own.
/// </remarks>
public readonly record struct NodeLayoutOptions(
    Vector2 Origin = default,
    float ColumnGap = 80f,
    float RowGap = 24f,
    float NodeWidth = 160f,
    float HeaderHeight = 22f,
    float PortPitch = 18f,
    float Padding = 6f
) {
    /// <summary>What a view with the stock theme would use.</summary>
    /// <remarks>
    ///     ⚠ <b>Spelled out rather than <c>new()</c>.</b> A struct's implicit parameterless constructor
    ///     wins overload resolution against a primary constructor whose parameters are all optional,
    ///     so <c>new NodeLayoutOptions()</c> is seven zeros — a layout where every column is at x = 0
    ///     and every node is the same height. The defaults in the signature are for a caller naming
    ///     one argument; this is the value.
    /// </remarks>
    public static NodeLayoutOptions Default { get; } = new(default, 80f, 24f, 160f, 22f, 18f, 6f);

    /// <summary>How tall a node with a given number of port rows is.</summary>
    /// <param name="rows">The taller of its two sides.</param>
    /// <returns>Its height, in graph units.</returns>
    public float HeightOf(int rows) => HeaderHeight + (Math.Max(1, rows) * PortPitch) + Padding;
}

/// <summary>
///     Laying a graph out left to right, in columns of things that depend on each other.
/// </summary>
/// <remarks>
///     <para>
///         <b>Layered, because a data-flow graph is already layered.</b> Every wire runs from an output
///         on a node's right to an input on another's left, so "how far along is this node" is a
///         well-defined number — the longest chain of nodes feeding it — and putting every node at its
///         own number means no wire ever runs backwards. A force-directed layout would produce
///         something prettier in the abstract and would put a texture sample to the right of the thing
///         that reads it, which is the one arrangement an author cannot read.
///     </para>
///     <para>
///         <b>Longest path rather than shortest.</b> Both give a legal layering; the longest one pushes
///         each node as far right as its dependencies allow, so a node feeding the master node sits
///         beside the other things feeding it rather than three columns to the left with a long wire.
///     </para>
///     <para>
///         <b>Crossings are reduced by the median heuristic, both ways, a fixed number of times.</b>
///         Minimising crossings exactly is NP-hard and the heuristic gets most of the way there; a
///         fixed pass count rather than "until it stops improving" is what makes the result a function
///         of the graph rather than of floating-point luck, which a golden test needs.
///     </para>
///     <para>
///         ⚠ <b>It answers with positions and does not apply them.</b> Moving nodes is an undoable edit
///         and <see cref="LayoutCommand" /> is what records it; a method that did both could only be
///         tested through a command stack, and "lay this out but let me see it first" would have no
///         way to exist.
///     </para>
/// </remarks>
public static class NodeGraphLayout {
    /// <summary>How many times the ordering sweep runs in each direction.</summary>
    /// <remarks>
    ///     Four is where the heuristic stops paying on the graphs this was measured against, and a
    ///     constant is what makes the answer reproducible.
    /// </remarks>
    public const int Sweeps = 4;

    /// <summary>Works out where every node should go.</summary>
    /// <param name="graph">The graph. Not modified.</param>
    /// <param name="registry">The node library, for how many ports a node has.</param>
    /// <param name="options">How much room to leave.</param>
    /// <returns>A position per node, for every node in the graph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public static IReadOnlyDictionary<NodeId, Vector2> Arrange(
        NodeGraphModel graph,
        NodeTypeRegistry? registry = null,
        NodeLayoutOptions options = default
    ) {
        ArgumentNullException.ThrowIfNull(graph);

        if (options == default) {
            options = NodeLayoutOptions.Default;
        }

        var order = graph.Ordered();

        if (order.Count == 0) {
            return new Dictionary<NodeId, Vector2>();
        }

        var columns = Layer(graph, order);
        var rows = Order(graph, order, columns);

        return Place(graph, registry, options, rows);
    }

    /// <summary>Which column each node is in: the longest chain of nodes feeding it.</summary>
    /// <remarks>
    ///     One pass, because <see cref="NodeGraphModel.Ordered" /> already hands nodes over after
    ///     everything feeding them — so the maximum over a node's sources is final by the time it is
    ///     read. That is the second thing refusing cycles at connection time buys.
    /// </remarks>
    static Dictionary<NodeId, int> Layer(NodeGraphModel graph, IReadOnlyList<GraphNode> order) {
        Dictionary<NodeId, int> columns = [];

        foreach (var node in order) {
            columns[node.Id] = 0;
        }

        foreach (var node in order) {
            foreach (var edge in graph.Edges) {
                if (edge.From.Node != node.Id) {
                    continue;
                }

                var wanted = columns[node.Id] + 1;

                if (columns.TryGetValue(edge.To.Node, out var current) && current < wanted) {
                    columns[edge.To.Node] = wanted;
                }
            }
        }

        return columns;
    }

    /// <summary>Which row each node is in, within its column.</summary>
    static List<List<NodeId>> Order(
        NodeGraphModel graph,
        IReadOnlyList<GraphNode> order,
        Dictionary<NodeId, int> columns
    ) {
        var width = 0;

        foreach (var column in columns.Values) {
            width = Math.Max(width, column + 1);
        }

        List<List<NodeId>> rows = new(width);

        for (var index = 0; index < width; index++) {
            rows.Add([]);
        }

        // Seeded in topological order rather than in identity order, so a graph laid out twice comes
        // out the same and a graph built left to right starts close to what its author drew.
        foreach (var node in order) {
            rows[columns[node.Id]].Add(node.Id);
        }

        Dictionary<NodeId, int> places = [];

        for (var sweep = 0; sweep < Sweeps; sweep++) {
            Reindex(rows, places);
            Sweep(graph, rows, places, forwards: true);

            Reindex(rows, places);
            Sweep(graph, rows, places, forwards: false);
        }

        return rows;
    }

    static void Reindex(List<List<NodeId>> rows, Dictionary<NodeId, int> places) {
        places.Clear();

        foreach (var column in rows) {
            for (var index = 0; index < column.Count; index++) {
                places[column[index]] = index;
            }
        }
    }

    /// <summary>One ordering pass: every node moves to the median of what it is joined to.</summary>
    /// <remarks>
    ///     ⚠ <b>A node with nothing on the side being swept keeps its place.</b> Giving it a median of
    ///     zero would drag every unconnected node to the top of its column and make the sweep in the
    ///     other direction undo the one before it, which is a layout that oscillates rather than
    ///     settles. The sentinel is its current index, which is also what makes the sort stable.
    /// </remarks>
    static void Sweep(NodeGraphModel graph, List<List<NodeId>> rows, Dictionary<NodeId, int> places, bool forwards) {
        var first = forwards ? 1 : rows.Count - 2;
        var step = forwards ? 1 : -1;

        for (var index = first; index >= 0 && index < rows.Count; index += step) {
            var column = rows[index];
            Dictionary<NodeId, float> keys = [];

            foreach (var id in column) {
                keys[id] = Median(graph, id, places, forwards) ?? places[id];
            }

            // Stable, so nodes with equal keys — every node whose neighbours are all in the other
            // direction — keep the order they were already in rather than being shuffled by a sort
            // that is free to do so.
            var sorted = column
                .Select((id, position) => (Id: id, Key: keys[id], Position: position))
                .OrderBy(entry => entry.Key)
                .ThenBy(entry => entry.Position)
                .Select(entry => entry.Id)
                .ToList();

            column.Clear();
            column.AddRange(sorted);

            // ⚠ Written back before the next column is read. A sweep whose medians all came from the
            // positions the columns had *before* the sweep started is a single pass repeated four
            // times rather than four passes, and it stops improving after the first one.
            for (var place = 0; place < column.Count; place++) {
                places[column[place]] = place;
            }
        }
    }

    /// <summary>The middle of the rows a node's neighbours on one side occupy.</summary>
    static float? Median(NodeGraphModel graph, NodeId id, Dictionary<NodeId, int> places, bool upstream) {
        List<int> neighbours = [];

        foreach (var edge in graph.Edges) {
            var mine = upstream ? edge.To.Node : edge.From.Node;
            var theirs = upstream ? edge.From.Node : edge.To.Node;

            if (mine == id && places.TryGetValue(theirs, out var place)) {
                neighbours.Add(place);
            }
        }

        if (neighbours.Count == 0) {
            return null;
        }

        neighbours.Sort();

        var middle = neighbours.Count / 2;

        return neighbours.Count % 2 == 1
            ? neighbours[middle]
            : (neighbours[middle - 1] + neighbours[middle]) * 0.5f;
    }

    /// <summary>Turns columns and rows into coordinates.</summary>
    /// <remarks>
    ///     ⚠ <b>Every column is centred on the tallest one.</b> Stacking each from the top means a
    ///     column of one node sits level with the top of a column of nine, so the one wire between
    ///     them runs the height of the picture. Centring costs one extra pass over the heights and
    ///     makes a chain of nodes come out as a line.
    /// </remarks>
    static Dictionary<NodeId, Vector2> Place(
        NodeGraphModel graph,
        NodeTypeRegistry? registry,
        NodeLayoutOptions options,
        List<List<NodeId>> rows
    ) {
        Dictionary<NodeId, Vector2> placed = [];
        List<float> extents = new(rows.Count);
        var tallest = 0f;

        foreach (var column in rows) {
            var height = 0f;

            foreach (var id in column) {
                height += HeightOf(graph, registry, options, id) + options.RowGap;
            }

            height = Math.Max(0f, height - options.RowGap);

            extents.Add(height);
            tallest = Math.Max(tallest, height);
        }

        for (var index = 0; index < rows.Count; index++) {
            var x = options.Origin.X + (index * (options.NodeWidth + options.ColumnGap));
            var y = options.Origin.Y + ((tallest - extents[index]) * 0.5f);

            foreach (var id in rows[index]) {
                placed[id] = new(x, y);
                y += HeightOf(graph, registry, options, id) + options.RowGap;
            }
        }

        return placed;
    }

    static float HeightOf(NodeGraphModel graph, NodeTypeRegistry? registry, NodeLayoutOptions options, NodeId id) {
        if (registry is null || !graph.TryGet(id, out var node) || !registry.TryGet(node.Type, out var definition)) {
            return options.HeightOf(1);
        }

        var inputs = 0;
        var outputs = 0;

        foreach (var port in definition.Ports) {
            if (port.Direction == PortDirection.Input) {
                inputs++;
            } else {
                outputs++;
            }
        }

        return options.HeightOf(Math.Max(inputs, outputs));
    }
}
