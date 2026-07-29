// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.NodeGraph;

/// <summary>One node, as a file holds it.</summary>
[DataContract("GraphNode")]
public sealed record GraphNodeAsset {
    /// <summary>Its identity within the graph.</summary>
    public int Id { get; init; }

    /// <summary>Which node type, by path.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Where it sits on the canvas, horizontally.</summary>
    /// <remarks>
    ///     Two floats rather than a <see cref="Vector2" />, because this is a file a person reads and
    ///     merges. A nested mapping for two numbers is two more lines per node in every diff, and the
    ///     model keeps the vector — this is the shape written down, not the shape worked with.
    /// </remarks>
    public float X { get; init; }

    /// <summary>And vertically.</summary>
    public float Y { get; init; }

    /// <summary>The inline values its unconnected inputs were given, port name to lanes.</summary>
    public Dictionary<string, float[]> Values { get; init; } = [];
}

/// <summary>One connection, as a file holds it.</summary>
/// <remarks>
///     Four fields rather than two nested pairs, because this is a line in a diff. A reviewer looking
///     at a changed graph should be able to read which wire moved without unfolding two levels of
///     mapping, and a merge conflict in a flat record is one a person can resolve.
/// </remarks>
[DataContract("GraphEdge")]
public sealed record GraphEdgeAsset {
    /// <summary>The node the value leaves.</summary>
    public int FromNode { get; init; }

    /// <summary>Which of its outputs.</summary>
    public string FromPort { get; init; } = string.Empty;

    /// <summary>The node it arrives at.</summary>
    public int ToNode { get; init; }

    /// <summary>Which of its inputs.</summary>
    public string ToPort { get; init; } = string.Empty;
}

/// <summary>A group box, as a file holds it.</summary>
[DataContract("GraphGroup")]
public sealed record GraphGroupAsset {
    /// <summary>What the group is called.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Which nodes are in it.</summary>
    public int[] Nodes { get; init; } = [];
}

/// <summary>A sticky note, as a file holds it.</summary>
[DataContract("GraphComment")]
public sealed record GraphCommentAsset {
    /// <summary>What it says.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Where it sits, horizontally.</summary>
    public float X { get; init; }

    /// <summary>And vertically.</summary>
    public float Y { get; init; }

    /// <summary>How wide it is.</summary>
    public float Width { get; init; }

    /// <summary>How tall.</summary>
    public float Height { get; init; }
}

/// <summary>One port of a graph's sub-graph interface, as a file holds it.</summary>
/// <remarks>
///     Written down rather than derived from the boundary nodes, for the reason
///     <see cref="NodeGraphModel.Interface" /> gives: a signature that came and went with a node
///     would change under every containing graph when somebody deleted one.
/// </remarks>
[DataContract("GraphPort")]
public sealed record GraphPortAsset {
    /// <summary>What the port is called. An edge in a containing graph names it by this.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Which way it faces, as seen from a graph containing this one.</summary>
    public PortDirection Direction { get; init; }

    /// <summary>What it carries.</summary>
    public PortKind Kind { get; init; }

    /// <summary>What an unconnected input takes, one float per lane.</summary>
    public float[] Default { get; init; } = [];

    /// <summary>One line saying what it means.</summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
///     A whole graph, as a file holds it.
/// </summary>
/// <remarks>
///     <para>
///         <b>A separate shape from <see cref="NodeGraphModel" />, on purpose.</b> The model is a
///         thing with invariants — an input takes one edge, a cycle cannot be made — and enforcing
///         those while loading half a file is not possible. The document has no invariants at all: it
///         is what was written down, including whatever a hand edit or a bad merge left. Turning one
///         into the other is where the checking happens, and where a file that has gone wrong
///         produces a diagnostic rather than a half-built model.
///     </para>
///     <para>
///         <b>This assembly does not choose a file format.</b> These are <c>[DataContract]</c>
///         records described by the reflection generator, so a caller writes them as YAML with
///         <c>Vixen.Core.Yaml</c> or bakes them with the binary serializer, and neither parser is
///         linked by anything that only wants to compile a graph. The same arrangement the compositor
///         asset uses, for the same reason.
///     </para>
/// </remarks>
[DataContract("NodeGraph")]
public sealed record NodeGraphAsset {
    /// <summary>The file shape's version.</summary>
    /// <remarks>
    ///     Written now so that a reader can refuse a file from the future with a sentence rather than
    ///     a stack trace. There is one version and nothing yet reads it, which is the cheapest moment
    ///     to add it.
    /// </remarks>
    public int Version { get; init; } = 1;

    /// <summary>What the graph is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its nodes.</summary>
    public GraphNodeAsset[] Nodes { get; init; } = [];

    /// <summary>Its edges.</summary>
    public GraphEdgeAsset[] Edges { get; init; } = [];

    /// <summary>Its group boxes.</summary>
    public GraphGroupAsset[] Groups { get; init; } = [];

    /// <summary>Its sticky notes.</summary>
    public GraphCommentAsset[] Comments { get; init; } = [];

    /// <summary>The ports it has when another graph contains it as a node.</summary>
    public GraphPortAsset[] Interface { get; init; } = [];
}

/// <summary>Between the model and the file shape.</summary>
public static class NodeGraphDocument {
    /// <summary>The current file shape's version.</summary>
    public const int Version = 1;

    /// <summary>Writes a model out.</summary>
    /// <param name="graph">The model.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public static NodeGraphAsset Save(NodeGraphModel graph) {
        ArgumentNullException.ThrowIfNull(graph);

        List<GraphNodeAsset> nodes = [];

        foreach (var node in graph.Nodes) {
            nodes.Add(new() {
                Id = node.Id.Value,
                Type = node.Type,
                X = node.Position.X,
                Y = node.Position.Y,
                Values = new(node.Values)
            });
        }

        List<GraphEdgeAsset> edges = [];

        foreach (var edge in graph.Edges) {
            edges.Add(new() {
                FromNode = edge.From.Node.Value,
                FromPort = edge.From.Port,
                ToNode = edge.To.Node.Value,
                ToPort = edge.To.Port
            });
        }

        List<GraphGroupAsset> groups = [];

        foreach (var group in graph.Groups) {
            groups.Add(new() { Title = group.Title, Nodes = [.. group.Nodes.Select(id => id.Value)] });
        }

        List<GraphCommentAsset> comments = [];

        foreach (var comment in graph.Comments) {
            comments.Add(new() {
                Text = comment.Text,
                X = comment.Position.X,
                Y = comment.Position.Y,
                Width = comment.Size.X,
                Height = comment.Size.Y
            });
        }

        List<GraphPortAsset> ports = [];

        foreach (var port in graph.Interface) {
            ports.Add(new() {
                Name = port.Name,
                Direction = port.Direction,
                Kind = port.Kind,
                Default = [.. port.Default],
                Summary = port.Summary
            });
        }

        return new() {
            Version = Version,
            Name = graph.Name,
            Nodes = [.. nodes],
            Edges = [.. edges],
            Groups = [.. groups],
            Comments = [.. comments],
            Interface = [.. ports]
        };
    }

    /// <summary>Reads a document into a model.</summary>
    /// <param name="asset">The document.</param>
    /// <param name="diagnostics">Everything that had to be dropped or repaired, in file order.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset" /> is null.</exception>
    /// <exception cref="NotSupportedException">The file is from a later version than this reader.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>It repairs rather than refuses, and says what it repaired.</b> An edge naming a node
    ///         that is not in the file is dropped; a duplicated identity is dropped; an edge arriving
    ///         at an input that already has one replaces it. Every one of those is what a bad merge
    ///         produces, and a graph editor that will not open the file is an editor that cannot be
    ///         used to fix it. What it does <i>not</i> repair is a file from a version it does not
    ///         know, because there it cannot tell what the bytes mean.
    ///     </para>
    ///     <para>
    ///         An edge that would close a cycle is dropped for the same reason: the model refuses
    ///         cycles by construction, so a file containing one has to lose an edge somewhere, and
    ///         losing the later one with a diagnostic is the version an author can act on.
    ///     </para>
    /// </remarks>
    public static NodeGraphModel Load(NodeGraphAsset asset, out IReadOnlyList<NodeDiagnostic> diagnostics) {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Version > Version) {
            throw new NotSupportedException(
                $"This graph was written by version {asset.Version} and this reader understands {Version}. "
                + "Opening it would silently drop whatever the newer version added."
            );
        }

        List<NodeDiagnostic> found = [];
        var graph = new NodeGraphModel { Name = asset.Name };

        // Before the nodes, because the boundary nodes a sub-graph's entry and exit are drawn from
        // read it — see SubGraphs. A duplicate is dropped rather than kept: two ports of one name is
        // a signature where a containing graph's edge does not say which it arrives at.
        HashSet<(string Name, PortDirection Direction)> declared = [];

        foreach (var entry in asset.Interface) {
            if (string.IsNullOrWhiteSpace(entry.Name)) {
                found.Add(new("NG0103", "A port of the graph's interface has no name, and an edge is stored by port name.", NodeId.None));

                continue;
            }

            if (!declared.Add((entry.Name, entry.Direction))) {
                found.Add(new(
                    "NG0103",
                    $"The graph's interface declares two {entry.Direction} ports called '{entry.Name}'. The second was dropped.",
                    NodeId.None
                ));

                continue;
            }

            graph.Interface.Add(new(entry.Name, entry.Direction, entry.Kind, [.. entry.Default], entry.Summary));
        }

        // The identities are the file's, not fresh ones. See NodeGraphModel.Add(NodeId, ...).
        foreach (var entry in asset.Nodes) {
            var id = new NodeId(entry.Id);

            if (!id.IsValid) {
                found.Add(new("NG0100", $"A node in the file has identity {entry.Id}, which is not one.", id));

                continue;
            }

            if (graph.TryGet(id, out _)) {
                found.Add(new("NG0100", $"Two nodes in the file claim {id}. The second was dropped.", id));

                continue;
            }

            var node = graph.Add(id, entry.Type, new(entry.X, entry.Y));

            foreach (var (port, value) in entry.Values) {
                node.SetValue(port, value);
            }
        }

        foreach (var entry in asset.Edges) {
            var from = new NodeId(entry.FromNode);
            var to = new NodeId(entry.ToNode);

            if (!graph.TryGet(from, out _)) {
                found.Add(new("NG0101", $"An edge leaves {from}, which is not in the file.", from));

                continue;
            }

            if (!graph.TryGet(to, out _)) {
                found.Add(new("NG0101", $"An edge arrives at {to}, which is not in the file.", to));

                continue;
            }

            try {
                graph.Connect(new(from, entry.FromPort), new(to, entry.ToPort));
            } catch (ArgumentException exception) {
                found.Add(new("NG0102", $"An edge was dropped: {exception.Message}", to, entry.ToPort));
            }
        }

        foreach (var entry in asset.Groups) {
            var group = new GraphGroup { Title = entry.Title };

            foreach (var member in entry.Nodes) {
                var id = new NodeId(member);

                if (graph.TryGet(id, out _)) {
                    group.Nodes.Add(id);
                }
            }

            graph.Groups.Add(group);
        }

        foreach (var entry in asset.Comments) {
            graph.Comments.Add(new() {
                Text = entry.Text,
                Position = new(entry.X, entry.Y),
                Size = new(entry.Width, entry.Height)
            });
        }

        diagnostics = found;

        return graph;
    }
}
