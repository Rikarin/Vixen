// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.NodeGraph;

/// <summary>Copying a slice of a graph, and holding it until somebody pastes it.</summary>
/// <remarks>
///     <para>
///         <b>A fragment is a <see cref="NodeGraphAsset" />.</b> A copied selection is a small graph —
///         nodes, the edges between them, the values they carry — which is exactly what the file shape
///         already is. Inventing a second shape for it would mean two things to keep in step and two
///         things to version, and it would mean a fragment could not be written to the system
///         clipboard as text without a converter of its own.
///     </para>
///     <para>
///         ⚠ <b>Only the edges with both ends in the selection travel.</b> A wire whose far end is not
///         copied is not a wire the paste could reproduce — there is nothing for it to arrive at — and
///         carrying it as a dangling reference would produce a paste that silently dropped edges or,
///         worse, connected to whatever node happened to hold that identity in the target graph.
///     </para>
///     <para>
///         <b>An instance rather than a static.</b> Two graph editors open at once should not share a
///         clipboard by accident, and a test should not have to reset a global between cases.
///         <see cref="Default" /> is there for the ordinary single-shell case, which is what the
///         inspector's <c>PropertyClipboard</c> does and for the same reason.
///     </para>
/// </remarks>
public sealed class NodeGraphClipboard {
    /// <summary>How far a paste offsets what it drops, when the caller does not say.</summary>
    /// <remarks>
    ///     Not zero. A paste that landed exactly on top of what was copied looks like nothing
    ///     happened, and the author then drags what they think is the original.
    /// </remarks>
    public static readonly Vector2 DefaultOffset = new(24f, 24f);

    /// <summary>The one every graph editor in a shell shares.</summary>
    public static NodeGraphClipboard Default { get; } = new();

    /// <summary>What was copied, or null when nothing has been.</summary>
    public NodeGraphAsset? Content { get; private set; }

    /// <summary>Whether there is anything to paste.</summary>
    public bool HasContent => Content is { Nodes.Length: > 0 };

    /// <summary>Takes a copy of some of a graph's nodes.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="nodes">Which nodes.</param>
    /// <returns>The fragment, or null when the selection held nothing the graph has.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static NodeGraphAsset? Copy(NodeGraphModel graph, IEnumerable<NodeId> nodes) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodes);

        HashSet<NodeId> wanted = [.. nodes];
        List<GraphNodeAsset> copied = [];

        foreach (var node in graph.Nodes) {
            if (!wanted.Contains(node.Id)) {
                continue;
            }

            copied.Add(new() {
                Id = node.Id.Value,
                Type = node.Type,
                X = node.Position.X,
                Y = node.Position.Y,
                Values = new(node.Values)
            });
        }

        if (copied.Count == 0) {
            return null;
        }

        List<GraphEdgeAsset> edges = [];

        foreach (var edge in graph.Edges) {
            if (!wanted.Contains(edge.From.Node) || !wanted.Contains(edge.To.Node)) {
                continue;
            }

            edges.Add(new() {
                FromNode = edge.From.Node.Value,
                FromPort = edge.From.Port,
                ToNode = edge.To.Node.Value,
                ToPort = edge.To.Port
            });
        }

        // The groups a copied node was in do not travel. A group is a box round a set of nodes, and
        // the pasted nodes are different nodes — carrying it would give the target graph a second box
        // titled the same thing, which is not what "copy these five nodes" means.
        return new() { Version = NodeGraphDocument.Version, Nodes = [.. copied], Edges = [.. edges] };
    }

    /// <summary>Copies some of a graph's nodes into this clipboard.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="nodes">Which nodes.</param>
    /// <returns>Whether anything was copied.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public bool Take(NodeGraphModel graph, IEnumerable<NodeId> nodes) {
        if (Copy(graph, nodes) is not { } fragment) {
            return false;
        }

        Content = fragment;

        return true;
    }

    /// <summary>Forgets what was copied.</summary>
    public void Clear() => Content = null;
}
