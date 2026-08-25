// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph;

/// <summary>
///     One node's sub-expression, as a shader that draws it on a quad.
/// </summary>
/// <remarks>
///     <para>
///         <b>A graph transformation, not a second compiler.</b> What a preview needs is the
///         expression one node produces, and the graph already says what that is: the node, everything
///         upstream of it, and a master to end at. So this copies that closure into a graph of its
///         own, hangs an <c>Master/Unlit</c> on the node's output and hands the result to the
///         ordinary <see cref="ShaderGraphCompiler" />. Nothing about emission, typing, conversion or
///         diagnostics is duplicated, and a preview is by construction compiled the same way the
///         shader is — which is the only arrangement in which a preview can be trusted to be showing
///         the graph rather than a second opinion about it.
///     </para>
///     <para>
///         <b>The vertex stage needs no special case either.</b> Every graph emits
///         <c>worldViewProjection * float4(position, 1f)</c> and exactly the varyings it asked for, so
///         a preview is that stage over a quad with identity transforms: a renderer supplies clip-space
///         corners as <c>position</c> and the unit square as <c>texcoord</c>, and the shader is
///         unmodified. See <see cref="ShaderGraphPreviewRenderer" />.
///     </para>
///     <para>
///         ⚠ <b>Unlit, deliberately, and not because it is the simplest master.</b> A preview shaded
///         by <c>Master/PBR</c> would answer a question nobody asked — what this value looks like as a
///         base colour under one directional light — and it would put the answer in a lit frame's
///         units, where an authored 0–1 tint and a pass that never ran are the same picture. Unlit
///         writes the value straight out, so what a preview shows is the number the node computed.
///     </para>
///     <para>
///         ⚠ <b>A node that is itself a master previews as itself.</b> Wiring a master's output into
///         another master is not a thing the graph can express — a master has no output port — so the
///         closure ends there and the shader is the one that graph would emit anyway.
///     </para>
/// </remarks>
public static class ShaderGraphPreview {
    /// <summary>The master a preview ends at, when the node is not one itself.</summary>
    public const string Master = "Master/Unlit";

    /// <summary>Which of the master's inputs the previewed value is wired into.</summary>
    public const string MasterInput = "Colour";

    /// <summary>What the emitted shader is called, so a preview never collides with the graph's own.</summary>
    public const string Name = "Preview";

    /// <summary>Compiles the sub-expression one node produces.</summary>
    /// <param name="graph">The graph the node is in.</param>
    /// <param name="node">Which node.</param>
    /// <param name="registry">The node types the graph is edited against.</param>
    /// <param name="port">
    ///     Which of the node's outputs to show, or <see langword="null" /> for its first.
    /// </param>
    /// <returns>
    ///     The shader and everything the compiler had to say. The artefact is null when the node is
    ///     not in the graph, has no output a colour can be made of, or the closure does not compile.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> or <paramref name="registry" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Cheap, and meant to be called often.</b> This is string work over a handful of nodes;
    ///     what is expensive is what a renderer does with the text afterwards. That is the whole
    ///     reason the two halves are separate — a preview is invalidated by comparing this output with
    ///     the last one, so an edit that does not change the expression costs no compilation, no
    ///     pipeline and no draw. See <c>ShaderGraphPreviewRenderer</c>.
    /// </remarks>
    public static NodeGraphCompilation<ShaderGraphSource> Compile(
        NodeGraphModel graph,
        NodeId node,
        NodeTypeRegistry registry,
        string? port = null
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(registry);

        if (!graph.TryGet(node, out var subject)) {
            return new(null, [new("SGP0001", $"{node} is not in this graph.", node)]);
        }

        if (!registry.TryGet(subject.Type, out var definition)) {
            return new(
                null,
                [new("SGP0002", $"No node type is registered at '{subject.Type}', so it has no expression.", node)]
            );
        }

        var isMaster = definition.Create() is ShaderMasterNode;
        var output = isMaster ? null : Output(definition, port);

        if (!isMaster && output is null) {
            return new(
                null,
                [
                    new(
                        "SGP0003",
                        $"'{definition.Path}' has no output that could be shown as a colour"
                        + (port is null ? "." : $", so '{port}' is not one."),
                        node
                    )
                ]
            );
        }

        var preview = Closure(graph, node);

        preview.Name = Name;

        if (!isMaster) {
            // The master is added last and takes an identity above every copied one, so no node the
            // author made can collide with it and the variable names stay the ones the graph's own
            // compile would have used.
            var end = preview.Add(Master, new Vector2(subject.Position.X + 240f, subject.Position.Y));

            preview.Connect(new(node, output!.Name), new(end.Id, MasterInput));
        }

        return new ShaderGraphCompiler(registry) { DefaultName = Name }.Compile(preview);
    }

    /// <summary>The output a preview shows: the one asked for, or the first that is a vector.</summary>
    /// <remarks>
    ///     ⚠ <b>A vector, because that is what a colour can be made of.</b>
    ///     <c>PortKinds.Accepts</c> refuses a boolean or a texture where a <c>float3</c> is wanted, so
    ///     a preview of one would be a diagnostic rather than a picture — and reporting "this node has
    ///     no output" is a better answer than reporting a type error the author did not make. A
    ///     <see cref="PortKind.Dynamic" /> output is taken: what it resolves to is decided by what is
    ///     wired to the node, and by the time the closure is compiled that is a vector or a
    ///     diagnostic.
    /// </remarks>
    static PortDefinition? Output(NodeTypeDefinition definition, string? port) {
        foreach (var candidate in definition.Ports) {
            if (candidate.Direction != PortDirection.Output) {
                continue;
            }

            if (port is not null && !string.Equals(candidate.Name, port, StringComparison.Ordinal)) {
                continue;
            }

            if (candidate.Kind == PortKind.Dynamic || PortKinds.IsVector(candidate.Kind)) {
                return candidate;
            }

            if (port is not null) {
                return null;
            }
        }

        return null;
    }

    /// <summary>A graph holding one node and everything that feeds it, under the identities it had.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The identities are preserved, and that is what makes a preview comparable.</b>
    ///         <c>NodeGraphCompiler</c> names an output's variable after the node's identity, so a
    ///         closure that renumbered would emit different text for the same expression every time a
    ///         node upstream of it was deleted — and a renderer that invalidates by comparing the text
    ///         would rebuild every preview in the graph.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Upstream only.</b> A preview is what the node <i>produces</i>; the nodes it feeds
    ///         are downstream of the answer and copying them would put a second master in the graph,
    ///         which the compiler refuses — with a message about the author's graph, which is not
    ///         wrong about anything.
    ///     </para>
    /// </remarks>
    static NodeGraphModel Closure(NodeGraphModel graph, NodeId node) {
        HashSet<NodeId> reached = [];
        Stack<NodeId> pending = new();

        pending.Push(node);

        while (pending.Count > 0) {
            var current = pending.Pop();

            if (!reached.Add(current)) {
                continue;
            }

            foreach (var edge in graph.Edges) {
                if (edge.To.Node == current && !reached.Contains(edge.From.Node)) {
                    pending.Push(edge.From.Node);
                }
            }
        }

        var preview = new NodeGraphModel();

        // In the original's insertion order rather than the set's, so the emitted text is stable: a
        // HashSet enumerates in whatever order it was filled, and the filling is a depth-first walk
        // whose order depends on the edge list.
        foreach (var original in graph.Nodes) {
            if (!reached.Contains(original.Id)) {
                continue;
            }

            var copy = preview.Add(original.Id, original.Type, original.Position);

            foreach (var (name, lanes) in original.Values) {
                copy.SetValue(name, [.. lanes]);
            }

            foreach (var (name, text) in original.Texts) {
                copy.SetText(name, text);
            }
        }

        foreach (var edge in graph.Edges) {
            if (reached.Contains(edge.From.Node) && reached.Contains(edge.To.Node)) {
                preview.Connect(edge.From, edge.To);
            }
        }

        return preview;
    }
}
