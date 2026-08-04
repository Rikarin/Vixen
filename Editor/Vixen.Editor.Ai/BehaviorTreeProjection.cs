// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Core.Mathematics;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Ai;

/// <summary>Turns a behaviour tree into the graph the canvas draws, and back again.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A projection rather than the document itself, and that is doc 37 § D19's decision.</b>
///         The canvas draws a <see cref="NodeGraph" /> — boxes, ports and wires — and a behaviour tree
///         is a tree with attachments and an ordered child list. Making the document be the canvas's
///         model would mean either teaching the framework about ordered edges and attachments, which
///         neither the shader graph nor the VFX graph nor the compositor has any use for, or teaching
///         the tree to be a dataflow graph, which it is not. So the canvas gets a picture and every
///         edit goes back through <see cref="BehaviorTreeModel" />.
///     </para>
///     <para>
///         Each projected node carries its <see cref="BehaviorNodeContent" /> in
///         <see cref="GraphNode.Tag" />, which is how a click on a box becomes a selection in the
///         document.
///     </para>
/// </remarks>
public sealed class BehaviorTreeProjection {
    readonly Dictionary<BehaviorNodeContent, GraphNode> boxes = [];

    /// <summary>What the canvas shows.</summary>
    public NodeGraph Graph { get; private set; } = new();

    /// <summary>Rebuilds the picture from the document.</summary>
    /// <param name="model">The tree.</param>
    /// <returns>The graph, which is a fresh one every time.</returns>
    /// <remarks>
    ///     ⚠ <b>Rebuilt rather than patched.</b> A tree edit is a reparent, a reorder or a delete far
    ///     more often than it is a field change, and every one of those moves a badge on every node
    ///     after it — because the badge is the pre-order index. Patching would be a diff of the whole
    ///     picture to save building thirty boxes.
    /// </remarks>
    public NodeGraph Project(BehaviorTreeModel model) {
        ArgumentNullException.ThrowIfNull(model);

        Graph = new();
        boxes.Clear();

        if (model.Content.Root is null) {
            return Graph;
        }

        var index = 0;

        foreach (var node in model.Walk()) {
            boxes[node] = Box(model, node, index++);
        }

        foreach (var node in model.Walk()) {
            foreach (var child in node.Children) {
                Graph.Connect(boxes[node].Outputs[0], boxes[child].Inputs[0]);
            }
        }

        return Graph;
    }

    /// <summary>The box showing a node, if it is in the picture.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The box, or null.</returns>
    public GraphNode? BoxOf(BehaviorNodeContent node) => boxes.GetValueOrDefault(node!);

    /// <summary>The node a box is showing.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The node, or null.</returns>
    public static BehaviorNodeContent? NodeOf(GraphNode box) => box?.Tag as BehaviorNodeContent;

    /// <summary>Where a node's box is on the canvas, in graph units.</summary>
    /// <param name="node">The node.</param>
    /// <param name="canvas">The canvas, for its metrics.</param>
    /// <returns>The rectangle, or an empty one.</returns>
    public Rectangle RectOf(BehaviorNodeContent node, NodeCanvas canvas) {
        ArgumentNullException.ThrowIfNull(canvas);

        return BoxOf(node) is { } box ? canvas.RectOf(box) : Rectangle.Empty;
    }

    /// <summary>The rectangle covering a set of nodes, for the abort-scope overlay.</summary>
    /// <param name="nodes">The nodes.</param>
    /// <param name="canvas">The canvas.</param>
    /// <param name="padding">How much room to leave around them.</param>
    /// <returns>The region, or an empty rectangle.</returns>
    public Rectangle RegionOf(IEnumerable<BehaviorNodeContent> nodes, NodeCanvas canvas, float padding = 12f) {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(canvas);

        var bounds = Rectangle.Empty;
        var any = false;

        foreach (var node in nodes) {
            if (BoxOf(node) is not { } box) {
                continue;
            }

            var rectangle = canvas.RectOf(box);

            bounds = any ? Rectangle.Union(bounds, rectangle) : rectangle;
            any = true;
        }

        return any
            ? new Rectangle(
                bounds.X - padding,
                bounds.Y - padding,
                bounds.Width + (padding * 2f),
                bounds.Height + (padding * 2f)
            )
            : Rectangle.Empty;
    }

    GraphNode Box(BehaviorTreeModel model, BehaviorNodeContent node, int index) {
        var type = model.TypeOf(node);
        var box = new GraphNode(node.Name.Length > 0 ? node.Name : type?.Label ?? node.Type) {
            Position = new(node.X, node.Y),
            Width = 180f,
            Tag = node,

            // The execution index, which *is* the priority — doc 37 § D5. Unreal draws the same badge
            // from its derived order; here it is drawn from the order the file stores, so the number
            // and the behaviour cannot disagree.
            Badge = index.ToString(CultureInfo.InvariantCulture),
            Accent = type is null ? "unknown" : string.Empty
        };

        foreach (var decorator in node.Decorators) {
            box.Attachments.Add(
                new(
                    model.TypeOf(decorator)?.Label ?? decorator.Type,
                    model.Summarise(decorator),
                    "decorator"
                )
            );
        }

        foreach (var service in node.Services) {
            box.Attachments.Add(
                new(
                    model.TypeOf(service)?.Label ?? service.Type,
                    Interval(service),
                    "service",
                    Above: false
                )
            );
        }

        // One way in and one way out. A tree's edges carry nothing — `PortKind.Flow` already means
        // "an edge that means *after*" — so the ports exist to give the wire two ends, and in
        // vertical orientation the canvas anchors them at the top and bottom edges.
        box.AddInput("in");
        box.AddOutput("out");

        Graph.AddNode(box);

        return box;
    }

    static string Interval(BehaviorAttachmentContent service) =>
        service.RandomDeviation > 0f
            ? string.Create(CultureInfo.InvariantCulture, $"every {service.Interval:0.##} s ± {service.RandomDeviation:0.##}")
            : string.Create(CultureInfo.InvariantCulture, $"every {service.Interval:0.##} s");
}
