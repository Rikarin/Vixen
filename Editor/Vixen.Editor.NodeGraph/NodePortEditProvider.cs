// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.NodeGraph;

/// <summary>One of a node's inline port values, as a member the editing pipeline can bind.</summary>
/// <remarks>
///     <para>
///         <b>The case <c>IEditProvider</c>'s own remarks name: "a graph node's ports are described
///         by the node".</b> A port is not a field of anything — its value lives in
///         <see cref="GraphNode.Values" />, keyed by the port's name, because that is what survives a
///         save and an undo. No generator describes it and no reflection pass can find it, so before
///         this the only way to edit one was a panel that knew about ports, which is what
///         <see cref="NodeInspector" /> used to be.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="InspectorMember" /> rather than a bare <c>IEditMember</c>, and that is
///         the whole point.</b> The narrow contract would have been enough to <i>write</i> a port; it
///         would not have got the port a drawer, a reset button, a tooltip or a row. Deriving from
///         the inspector's descriptor type is what <c>ReflectedMember</c> does for the same reason,
///         and it is what puts a node's ports into the ordinary inspector panel instead of a second
///         one written by hand.
///     </para>
///     <para>
///         ⚠ <b>The lanes are the storage and the value is what a person edits.</b> A port is one to
///         four floats on the node; a row wants a <c>float</c>, a <c>bool</c>, an <c>int</c> or a
///         <see cref="Vector3" />. <see cref="MemberType" /> is the second of those, and the
///         conversion in both directions is here so that every consumer — a drawer, a
///         <c>binding-path</c>, the copy-property command — sees the shape it expects.
///     </para>
/// </remarks>
public sealed class NodePortMember : InspectorMember {
    readonly NodeGraphModel graph;

    /// <summary>The port this stands for.</summary>
    public PortDefinition Port { get; }

    /// <inheritdoc />
    public override Type MemberType { get; }

    /// <inheritdoc />
    public override Type OwnerType => typeof(GraphNode);

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <summary>Describes one input port of a node type.</summary>
    /// <param name="graph">The graph the nodes belong to, which is what an edit is recorded against.</param>
    /// <param name="port">The port.</param>
    /// <exception cref="ArgumentException">The port carries no value a person can type.</exception>
    /// <remarks>
    ///     A texture, a sampler and a flow port take no typed value at all — see
    ///     <see cref="PortKinds.Fields" /> — so there is no member to be made of one.
    ///     <see cref="NodePortEditProvider" /> skips them rather than asking.
    /// </remarks>
    public NodePortMember(NodeGraphModel graph, PortDefinition port)
        : base(Named(port), Named(port)) {
        ArgumentNullException.ThrowIfNull(graph);

        this.graph = graph;
        Port = port;
        MemberType = TypeOf(port.Kind);

        if (port.Summary.Length > 0) {
            Tooltip = port.Summary;
        }
    }

    /// <inheritdoc />
    public override object? GetBoxed(object owner) {
        ArgumentNullException.ThrowIfNull(owner);

        var lanes = Lanes((GraphNode) owner);

        return Port.Kind switch {
            PortKind.Bool => lanes[0] != 0f,
            PortKind.Int => (int) lanes[0],
            PortKind.Float2 => new Vector2(lanes[0], lanes[1]),
            PortKind.Float3 => new Vector3(lanes[0], lanes[1], lanes[2]),
            PortKind.Float4 => new Vector4(lanes[0], lanes[1], lanes[2], lanes[3]),
            _ => lanes[0]
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The un-undoable path, and the pipeline only takes it for a target with no
    ///     document.</b> Everything else goes through <see cref="CreateSetCommand" />, which is a
    ///     <see cref="SetPortValueCommand" /> — the command the graph already had, so a port edited
    ///     from the inspector and a port edited on the canvas produce the same entry and merge with
    ///     each other.
    /// </remarks>
    public override void SetBoxed(object owner, object? value) {
        ArgumentNullException.ThrowIfNull(owner);

        ((GraphNode) owner).SetValue(Port.Name, Encode(value));
        graph.Touch();
    }

    /// <inheritdoc />
    public override IEditorCommand CreateSetCommand(
        IReadOnlyList<object> targets,
        object? value,
        EditorDocument? document
    ) {
        ArgumentNullException.ThrowIfNull(targets);

        var nodes = new NodeId[targets.Count];

        for (var index = 0; index < targets.Count; index++) {
            nodes[index] = ((GraphNode) targets[index]).Id;
        }

        return new SetPortValueCommand(graph, nodes, Port.Name, Encode(value), document);
    }

    /// <summary>What one port's value is worth as a member.</summary>
    /// <param name="kind">What the port carries.</param>
    /// <returns>The type a row edits.</returns>
    /// <exception cref="ArgumentException">The kind carries no typed value.</exception>
    /// <remarks>
    ///     ⚠ <b>A dynamic port is a <c>float</c> however wide it turned out to be</b>, which is
    ///     <see cref="PortKinds.Fields" />' rule and not a simplification of it: the compiler splats a
    ///     short constant, so <c>0.25</c> typed into a port that resolved to a colour compiles as a
    ///     grey. Offering three boxes because a <i>different</i> port was wired to a <c>float3</c>
    ///     would make the same graph edit differently depending on what is beside it.
    /// </remarks>
    public static Type TypeOf(PortKind kind) => kind switch {
        PortKind.Bool => typeof(bool),
        PortKind.Int => typeof(int),
        PortKind.Float or PortKind.Dynamic => typeof(float),
        PortKind.Float2 => typeof(Vector2),
        PortKind.Float3 => typeof(Vector3),
        PortKind.Float4 => typeof(Vector4),
        _ => throw new ArgumentException(
            $"A {kind} port takes no typed value, so there is no member to make of it.",
            nameof(kind)
        )
    };

    /// <summary>The lanes a value is stored as.</summary>
    float[] Encode(object? value) {
        var lanes = new float[PortKinds.Fields(Port.Kind)];

        switch (value) {
            case bool flag:
                lanes[0] = flag ? 1f : 0f;
                break;

            case Vector2 vector:
                Fill(lanes, vector.X, vector.Y);
                break;

            case Vector3 vector:
                Fill(lanes, vector.X, vector.Y, vector.Z);
                break;

            case Vector4 vector:
                Fill(lanes, vector.X, vector.Y, vector.Z, vector.W);
                break;

            // ⚠ `IConvertible` rather than a `float` pattern, and the reason is the same one
            // `MarkupBinding` names: a numeric input is a `double` and a slider is a `float`, so a
            // row can hand back either for a port that is neither.
            case IConvertible number:
                lanes[0] = number.ToSingle(System.Globalization.CultureInfo.InvariantCulture);
                break;
        }

        return lanes;

        static void Fill(float[] lanes, params ReadOnlySpan<float> values) {
            for (var index = 0; index < lanes.Length && index < values.Length; index++) {
                lanes[index] = values[index];
            }
        }
    }

    /// <summary>What the port holds on a node, falling back to the type's default a lane at a time.</summary>
    /// <remarks>
    ///     ⚠ <b>A short default fills the lanes it has and no more</b> — the rule
    ///     <see cref="NodeInspector" /> had first. A one-number default on a three-lane port leaves
    ///     the other two at zero here, where a person is being shown three boxes; the compiler's
    ///     splat is what the same default means when it reaches a <c>float3</c> as one value.
    /// </remarks>
    float[] Lanes(GraphNode node) {
        var fields = PortKinds.Fields(Port.Kind);
        var lanes = new float[fields];

        var stored = node.Values.TryGetValue(Port.Name, out var written) ? written : [];
        var fallback = Port.Default;

        for (var index = 0; index < fields; index++) {
            lanes[index] = index < stored.Length ? stored[index]
                : index < fallback.Length ? fallback[index]
                : 0f;
        }

        return lanes;
    }

    static string Named(PortDefinition port) {
        ArgumentNullException.ThrowIfNull(port);

        return port.Name;
    }
}

/// <summary>How the editing pipeline reaches a node's inline port values.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P1's second <c>IEditProvider</c>, and the first one outside the
///         inspector.</b> Terrain, foliage and blockout edit ordinary C# objects, which
///         <c>InspectorEditProvider</c> already describes; a graph node is the case that genuinely
///         cannot be described that way, because its editable members are decided by the node
///         <i>type</i> a saved graph names by string and not by the CLR type every node shares.
///     </para>
///     <para>
///         ⚠ <b>One provider per node type, not one per process.</b> <c>EditTarget</c> resolves
///         members by CLR type, and every node is a <see cref="GraphNode" /> — so a provider that
///         answered for <c>GraphNode</c> in general would have to answer with some node type's ports
///         and would be wrong for all the others. <see cref="For" /> builds one against the
///         definition of the type actually selected, which also means a selection of several nodes
///         is only legitimate when they are all that type: see <see cref="Describes" />.
///     </para>
///     <para>
///         ⚠ <b>A connected input is not a member.</b> A port fed by an edge takes its value from
///         that edge, and a row showing a number the compiler ignores is how somebody comes to spend
///         an afternoon changing a field that does nothing. The wiring is therefore part of what a
///         provider is built against, and a graph whose edges changed needs a new one.
///     </para>
/// </remarks>
public sealed class NodePortEditProvider : IEditProvider {
    /// <summary>The node type whose ports this describes.</summary>
    public NodeTypeDefinition Definition { get; }

    /// <summary>The description an inspector draws rows from.</summary>
    /// <remarks>
    ///     Its <c>Type</c> is <see cref="GraphNode" /> and its factory makes a detached one, so a
    ///     member's default is the port's declared default and the reset button appears exactly when
    ///     a node has been given an inline value that differs from it.
    /// </remarks>
    public InspectorDescriptor Descriptor { get; }

    /// <summary>The input ports that are fed by an edge, in declaration order.</summary>
    /// <remarks>
    ///     What a panel says instead of a row. They are not members — see the type's remarks — and a
    ///     panel that simply left them out would make a wired port look like a port that vanished.
    /// </remarks>
    public IReadOnlyList<PortDefinition> Connected { get; }

    NodePortEditProvider(
        NodeTypeDefinition definition,
        InspectorDescriptor descriptor,
        IReadOnlyList<PortDefinition> connected
    ) {
        Definition = definition;
        Descriptor = descriptor;
        Connected = connected;
    }

    /// <summary>Describes one node type's editable ports, as they stand in one graph.</summary>
    /// <param name="graph">The graph the nodes belong to.</param>
    /// <param name="definition">The node type.</param>
    /// <param name="node">
    ///     The node whose wiring decides which inputs are connected, or <see cref="NodeId.None" /> to
    ///     describe every input as editable.
    /// </param>
    /// <param name="readOnly">
    ///     Whether the ports are shown rather than edited, which is what a graph with no undo stack
    ///     gets — see <c>NodeGraphView.IsReadOnly</c>. Rows that accepted an edit nothing recorded
    ///     would be worse than rows that refuse.
    /// </param>
    /// <returns>The provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> or <paramref name="definition" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>One node's wiring, not the selection's.</b> Two nodes of one type can be wired
    ///     differently, and there is no honest set of rows for that — which is why a multi-node
    ///     selection is only offered where the nodes agree, and why the caller that assembles one
    ///     passes the node it checked.
    /// </remarks>
    public static NodePortEditProvider For(
        NodeGraphModel graph,
        NodeTypeDefinition definition,
        NodeId node,
        bool readOnly = false
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(definition);

        List<InspectorMember> members = [];
        List<PortDefinition> connected = [];

        foreach (var port in definition.Ports) {
            if (port.Direction != PortDirection.Input || PortKinds.Fields(port.Kind) <= 0) {
                continue;
            }

            if (node != NodeId.None && graph.Source(new(node, port.Name)) is not null) {
                connected.Add(port);
                continue;
            }

            members.Add(new NodePortMember(graph, port) { IsReadOnly = readOnly });
        }

        var descriptor = new InspectorDescriptor(
            typeof(GraphNode),
            members,

            // ⚠ A detached node, deliberately never added to the graph. It is read once, for the
            // members' defaults, and a node that were added would be a node in the document that
            // nobody put there.
            () => new GraphNode(NodeId.None, definition.Path, default)
        );

        return new(definition, descriptor, connected);
    }

    /// <summary>Whether a selection is one this provider can honestly describe.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="nodes">The selected nodes.</param>
    /// <returns>Whether they are all of <see cref="Definition" />'s type and wired the same way.</returns>
    /// <remarks>
    ///     ⚠ <b>The guard <c>EditTarget</c> cannot perform.</b> Its <c>CommonType</c> is the CLR type
    ///     and every node shares one, so a mixed selection of a Add and a Multiply looks uniform to
    ///     it and would be given whichever type's rows this provider was built for. Nothing below the
    ///     graph knows the difference, so the check belongs here.
    /// </remarks>
    public bool Describes(NodeGraphModel graph, IReadOnlyList<GraphNode> nodes) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodes);

        foreach (var node in nodes) {
            if (!string.Equals(node.Type, Definition.Path, StringComparison.Ordinal)) {
                return false;
            }

            foreach (var port in Definition.Ports) {
                if (port.Direction != PortDirection.Input || PortKinds.Fields(port.Kind) <= 0) {
                    continue;
                }

                var wired = graph.Source(new(node.Id, port.Name)) is not null;

                if (wired != Connected.Contains(port)) {
                    return false;
                }
            }
        }

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<IEditMember> MembersOf(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        return type == typeof(GraphNode) ? Descriptor.Members : [];
    }

    /// <inheritdoc />
    public bool TryResolve(Type type, string path, [NotNullWhen(true)] out IEditMember? member) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (type == typeof(GraphNode) && Descriptor.TryGetMember(path, out var found)) {
            member = found;
            return true;
        }

        member = null;
        return false;
    }
}
