// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Gameplay;
using Vixen.Gameplay.Quests;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Gameplay.Quests;

/// <summary>Turns an event chain into the graph the canvas draws.</summary>
/// <remarks>
///     <para>
///         <b>A projection rather than the document, for the reason <see cref="EventChain" />
///         records:</b> the canvas refuses a cycle and gives an input one wire, and an event chain has
///         both. So the spanning walk's edges become wires and everything else becomes a badge on the
///         box it left — visible, labelled, and not silently missing.
///     </para>
///     <para>
///         Each box carries its <see cref="DynamicEventTemplate" /> in <see cref="GraphNode.Tag" />,
///         which is how a click on a box becomes a selection in the catalog.
///     </para>
/// </remarks>
public sealed class EventChainProjection {
    readonly Dictionary<uint, GraphNode> boxes = [];

    /// <summary>How far apart the columns are.</summary>
    public float ColumnWidth { get; set; } = 260f;

    /// <summary>How far apart the rows are.</summary>
    public float RowHeight { get; set; } = 120f;

    /// <summary>What the canvas shows.</summary>
    public NodeGraph Graph { get; private set; } = new();

    /// <summary>The chain the last <see cref="Project" /> drew.</summary>
    public EventChain? Chain { get; private set; }

    /// <summary>How many edges were drawn as badges rather than as wires.</summary>
    public int Deferred { get; private set; }

    /// <summary>Rebuilds the picture.</summary>
    /// <param name="chain">The chain.</param>
    /// <returns>The graph, which is a fresh one every time.</returns>
    /// <remarks>
    ///     Rebuilt rather than patched, as <c>Vixen.Editor.Ai</c>'s is: an edit to a chain adds or
    ///     removes an edge far more often than it changes a field, and either of those moves every
    ///     depth badge after it.
    /// </remarks>
    public NodeGraph Project(EventChain chain) {
        ArgumentNullException.ThrowIfNull(chain);

        Graph = new();
        Chain = chain;
        Deferred = 0;
        boxes.Clear();

        var depths = Depths(chain);
        var rows = new Dictionary<int, int>();

        foreach (var id in chain.Order) {
            if (chain.Find(id) is not { } template) {
                continue;
            }

            var depth = depths.GetValueOrDefault(id.Value);
            var row = rows.GetValueOrDefault(depth);

            rows[depth] = row + 1;

            // Accented by being an *entry* rather than a root, because a looping chain has no roots
            // and the box a designer needs to find is the one the picture starts from.
            boxes[id.Value] = Box(template, depth, row, chain.Entries.Contains(id));
        }

        foreach (var edge in chain.TreeEdges) {
            if (!boxes.TryGetValue(edge.From.Value, out var from) || !boxes.TryGetValue(edge.To.Value, out var to)) {
                continue;
            }

            Graph.Connect(Port(from, edge.Branch), to.Inputs[0]);
        }

        // ⚠ Everything the walk could not wire is drawn on the box it left rather than dropped. A
        // canvas that silently omitted the edge closing a loop would show a chain that ends where the
        // content says it begins again.
        foreach (var edge in chain.BackEdges.Concat(chain.Dangling)) {
            if (!boxes.TryGetValue(edge.From.Value, out var from)) {
                continue;
            }

            var target = edge.To.IsSome && chain.Find(edge.To) is { } found
                ? found.DisplayName.Length > 0 ? found.DisplayName : found.Definition.Address
                : edge.Address;

            from.Attachments.Add(
                new(
                    edge.Branch == EventBranch.Success ? $"↻ on success → {target}" : $"↻ on failure → {target}",
                    edge.To.IsSome ? string.Empty : "not in this build",
                    edge.To.IsSome ? "loop" : "missing",
                    Above: false
                )
            );

            Deferred++;
        }

        return Graph;
    }

    /// <summary>The box showing an event, if it is in the picture.</summary>
    /// <param name="id">Which event.</param>
    /// <returns>The box, or null.</returns>
    public GraphNode? BoxOf(DefId id) => boxes.GetValueOrDefault(id.Value);

    /// <summary>The event a box is showing.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The event, or null.</returns>
    public static DynamicEventTemplate? EventOf(GraphNode box) => box?.Tag as DynamicEventTemplate;

    /// <summary>Tints the picture by what is actually running.</summary>
    /// <param name="director">The director, or null to clear the tinting.</param>
    /// <returns>How many boxes were tinted.</returns>
    /// <remarks>
    ///     The live overlay <c>Vixen.Editor.Ai</c> has for a behaviour tree, for a chain: a designer
    ///     watching a camp fall wants to see which box is lit, not read a log.
    /// </remarks>
    public int Live(DynamicEventDirector? director) {
        foreach (var box in boxes.Values) {
            box.Accent = string.Empty;
            box.Badge = string.Empty;
        }

        if (director is null) {
            return 0;
        }

        var tinted = 0;

        foreach (var instance in director.Running) {
            if (BoxOf(instance.Id) is not { } box) {
                continue;
            }

            box.Accent = "active";
            box.Badge = instance.Participants.ToString(CultureInfo.InvariantCulture);
            tinted++;
        }

        return tinted;
    }

    static GraphPort Port(GraphNode box, EventBranch branch) =>
        branch == EventBranch.Success ? box.Outputs[0] : box.Outputs[1];

    static Dictionary<uint, int> Depths(EventChain chain) {
        var depths = new Dictionary<uint, int>();

        foreach (var id in chain.Entries) {
            depths.TryAdd(id.Value, 0);
        }

        // Over the tree edges in walk order, which is a topological order of the spanning tree — so
        // one pass is enough and a cycle cannot make this loop.
        foreach (var edge in chain.TreeEdges) {
            depths[edge.To.Value] = depths.GetValueOrDefault(edge.From.Value) + 1;
        }

        return depths;
    }

    GraphNode Box(DynamicEventTemplate template, int depth, int row, bool isRoot) {
        var box = new GraphNode(template.DisplayName.Length > 0 ? template.DisplayName : template.Definition.Address) {
            Position = new(depth * ColumnWidth, row * RowHeight),
            Width = 200f,
            Tag = template,
            Accent = isRoot ? "root" : string.Empty
        };

        foreach (var objective in template.Objectives) {
            box.Attachments.Add(
                new(
                    objective.DisplayName.Length > 0 ? objective.DisplayName : objective.Definition.Type,
                    string.Create(CultureInfo.InvariantCulture, $"×{objective.Count}"),
                    "objective"
                )
            );
        }

        if (template.Duration > 0f) {
            box.Attachments.Add(
                new("timer", string.Create(CultureInfo.InvariantCulture, $"{template.Duration:0.#} s"), "timer", Above: false)
            );
        }

        box.AddInput("from");

        // Two outputs, always, because a chain's whole point is that both of them lead somewhere —
        // and a box that grew a failure port only when it had one would move its success wire every
        // time a designer added a failure branch.
        box.AddOutput("success");
        box.AddOutput("failure");

        Graph.AddNode(box);

        return box;
    }
}
