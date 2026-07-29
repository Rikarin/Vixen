// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.AssetEditors.Compositor;

/// <summary>Turns a compositor graph into the document a renderer builds a frame from.</summary>
/// <remarks>
///     <para>
///         <b>The framework's walk collects; this class assembles.</b>
///         <see cref="NodeGraphCompiler{TArtefact}" /> visits every node in dependency order with its
///         ports resolved, which is exactly what is needed to instantiate them and let the
///         declarations land. What it cannot do is produce a <i>tree</i>, because it has no notion of
///         one — so <see cref="Finish" /> walks the flow edges itself, from the frame node outwards.
///     </para>
///     <para>
///         ⚠ <b>A flow output takes one edge.</b> The model lets an output fan out, which is right
///         for a value — two nodes can read one number — and meaningless for an ordering: two nodes
///         cannot both be next. The second edge is reported against the node rather than silently
///         picked between, because the alternative is a frame whose pass order depends on which wire
///         was drawn first.
///     </para>
///     <para>
///         ⚠ <b>A node nothing reaches is reported and left out.</b> An author unhooking a pass while
///         debugging has done something deliberate, and a compiler that quietly dropped it would
///         leave them wondering why the frame is unchanged; one that quietly included it would make
///         unhooking do nothing.
///     </para>
/// </remarks>
public sealed class CompositorGraphCompiler : NodeGraphCompiler<GraphicsCompositorAsset> {
    readonly Dictionary<NodeId, CompositorNode> instances = [];
    readonly CompositorDeclarations declarations = new();

    NodeId frame;

    /// <summary>Compiles against a node library.</summary>
    /// <param name="registry">The node types this build has.</param>
    public CompositorGraphCompiler(NodeTypeRegistry registry) : base(registry) {
    }

    /// <summary>A registry holding this assembly's compositor nodes.</summary>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     The generated <c>NodeTypes.Register</c> is per assembly and a host picks — so this is a
    ///     convenience for the one host that wants exactly these, and not a global.
    /// </remarks>
    public static NodeTypeRegistry CreateRegistry() {
        var registry = new NodeTypeRegistry();
        Vixen.Editor.AssetEditors.NodeTypes.Register(registry);

        return registry;
    }

    /// <inheritdoc />
    protected override void Begin(NodeGraphModel graph) {
        instances.Clear();
        declarations.Resources.Clear();
        declarations.Buffers.Clear();
        declarations.Stages.Clear();
        declarations.ViewBlock = null;

        frame = NodeId.None;
    }

    /// <inheritdoc />
    protected override void Visit(
        GraphNode node,
        NodeTypeDefinition definition,
        Node instance,
        NodeBinding binding
    ) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(instance);

        if (instance is not CompositorNode compositor) {
            Report(new(
                "CO0001",
                $"'{node.Type}' is not a compositor node. A graph mixing node libraries has one of them "
                + "registered by mistake.",
                node.Id
            ));

            return;
        }

        instances[node.Id] = compositor;
        compositor.Contribute(declarations);

        if (compositor is not FrameNode) {
            return;
        }

        if (frame.IsValid) {
            Report(new(
                "CO0002",
                "This graph has two frame nodes, so there are two answers to what it renders. Delete one.",
                node.Id
            ));

            return;
        }

        frame = node.Id;
    }

    /// <inheritdoc />
    protected override GraphicsCompositorAsset? Finish(NodeGraphModel graph) {
        ArgumentNullException.ThrowIfNull(graph);

        if (!frame.IsValid) {
            Report(new(
                "CO0003",
                "This graph has no frame node, so nothing says what it renders. Add one from Frame/Frame.",
                NodeId.None
            ));

            return null;
        }

        HashSet<NodeId> reached = [frame];
        var game = FrameNode.Sequence("Frame", Chain(graph, frame, reached));

        foreach (var node in graph.Nodes) {
            // A declaration is reached by being in the graph at all, which is what makes it a
            // declaration; only the nodes with flow ports have to be on a chain.
            if (reached.Contains(node.Id)
                || !instances.TryGetValue(node.Id, out var instance)
                || !HasFlow(instance)) {
                continue;
            }

            Report(new(
                "CO0004",
                $"'{node.Type}' is not on the frame's chain, so it does not run. Wire it in, or delete it.",
                node.Id
            ));
        }

        return new() {
            Stages = [.. declarations.Stages],
            Resources = [.. declarations.Resources],
            Buffers = [.. declarations.Buffers],
            ViewBlock = declarations.ViewBlock,
            Game = game
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Unreachable in practice: a compositor node's ports are all <see cref="PortKind.Flow" />,
    ///     which carries no value, so the framework never asks this to spell one. It answers rather
    ///     than throws because a node library somebody adds later may not be all flow.
    /// </remarks>
    protected override string Constant(ReadOnlySpan<float> value, PortKind kind) =>
        value.Length == 0 ? "0" : value[0].ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    /// <remarks>Nothing to convert between: see <see cref="Constant" />.</remarks>
    protected override string Convert(string expression, PortKind from, PortKind target) => expression;

    /// <summary>The chain hanging off one node's <c>Body</c> port, each node already assembled.</summary>
    List<ISceneRendererAsset> Chain(NodeGraphModel graph, NodeId node, HashSet<NodeId> reached) {
        List<ISceneRendererAsset> children = [];

        for (var current = Next(graph, node, "Body"); current.IsValid; current = Next(graph, current, "Out")) {
            if (!reached.Add(current)) {
                // The model refuses cycles as they are made, so reaching a node twice means one node
                // is on two chains — a wire from a pass's body back into the outer sequence. Stopping
                // is what keeps the walk finite.
                Report(new("CO0005", "This node is on two chains, so it would run twice. Disconnect one.", current));

                break;
            }

            if (!instances.TryGetValue(current, out var instance)) {
                continue;
            }

            // The recursion happens before the node is emitted, because a container is built from
            // what is inside it — which is also why the walk is depth first rather than two passes.
            var emitted = instance.Emit(HasBody(instance) ? Chain(graph, current, reached) : []);

            if (emitted is not null) {
                children.Add(emitted);
            }
        }

        return children;
    }

    /// <summary>What one flow output connects to, or nothing.</summary>
    NodeId Next(NodeGraphModel graph, NodeId node, string port) {
        var found = NodeId.None;

        foreach (var edge in graph.Edges) {
            if (edge.From.Node != node || !string.Equals(edge.From.Port, port, StringComparison.Ordinal)) {
                continue;
            }

            if (found.IsValid) {
                Report(new(
                    "CO0006",
                    $"Two nodes are wired to '{port}', and two nodes cannot both be next. Chain them instead.",
                    node
                ));

                break;
            }

            found = edge.To.Node;
        }

        return found;
    }

    static bool HasBody(CompositorNode node) => node is FrameNode or SequenceNode or RenderPassNode;

    static bool HasFlow(CompositorNode node) => node is not (ResourceNode or BufferNode or StageNode);
}
