// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.NodeGraph;

/// <summary>
///     A graph, walked in order, with every node's ports resolved and bound. What is done with each
///     one is the subclass's.
/// </summary>
/// <typeparam name="TArtefact">What the graph compiles to.</typeparam>
/// <remarks>
///     <para>
///         <b>What is shared and what is not.</b> Walking a graph in dependency order, deciding what a
///         dynamic port resolved to, naming the variable an output writes, converting a value that
///         arrives at a port of a different width, and reporting all of it against the node it came
///         from — none of that differs between a shader graph and a VFX graph. What differs is the
///         language the literals and conversions are spelled in, and what a bound node <i>does</i>.
///         Those three are abstract and nothing else is.
///     </para>
///     <para>
///         <b>Names are derived from identity, not from a counter.</b> An output's variable is
///         <c>n{id}_{port}</c>, so compiling the same graph twice produces the same source — which is
///         what a golden test needs, and what makes a diff between two saved versions of a graph mean
///         something.
///     </para>
///     <para>
///         <b>It keeps going after an error.</b> A graph with a missing node type and a badly typed
///         edge should report both, because an author fixing one at a time is an author compiling five
///         times. The artefact is withheld at the end rather than the walk abandoned at the first
///         problem.
///     </para>
/// </remarks>
public abstract class NodeGraphCompiler<TArtefact> where TArtefact : class {
    readonly List<NodeDiagnostic> diagnostics = [];

    /// <summary>Starts a compiler over a node library.</summary>
    /// <param name="registry">The node types the graph may contain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry" /> is null.</exception>
    protected NodeGraphCompiler(NodeTypeRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
    }

    /// <summary>The node types the graph may contain.</summary>
    protected NodeTypeRegistry Registry { get; }

    /// <summary>Where sub-graphs are found, or null when this compiler does not support them.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Set and the walk changes shape; unset and it does not.</b> With a source in place
    ///         <see cref="Compile" /> inlines every sub-graph node first — see <see cref="SubGraphs" />
    ///         — and everything below this line then walks a graph that has none. Without one, a
    ///         sub-graph node is a node type that is not registered, which is already a diagnostic
    ///         naming the node.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the subclass is handed is the flattened graph, not the author's.</b>
    ///         <see cref="Finish" /> and <see cref="Visit" /> see nodes that were never in the file,
    ///         which is right — they are compiling the program, not the document — and it is why a
    ///         subclass must not use a node's identity to look anything up in the original.
    ///     </para>
    /// </remarks>
    public ISubGraphSource? SubGraphSource { get; set; }

    /// <summary>Compiles a graph.</summary>
    /// <param name="graph">The graph.</param>
    /// <returns>The artefact and everything the compiler had to say.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public NodeGraphCompilation<TArtefact> Compile(NodeGraphModel graph) {
        ArgumentNullException.ThrowIfNull(graph);

        diagnostics.Clear();

        if (SubGraphSource is { } source && SubGraphs.ContainsSubGraph(graph, source)) {
            graph = SubGraphs.Flatten(graph, source, out var inlining);

            foreach (var diagnostic in inlining) {
                Report(diagnostic);
            }
        }

        Begin(graph);

        foreach (var node in graph.Ordered()) {
            if (!Registry.TryGet(node.Type, out var definition)) {
                Report(new(
                    "NG0001",
                    $"No node type is registered at '{node.Type}'. The graph was saved against a library "
                    + "this process has not loaded.",
                    node.Id
                ));

                continue;
            }

            var resolved = Resolve(graph, node, definition);
            var binding = Bind(graph, node, definition, resolved);
            var instance = definition.Create();

            instance.Binding = binding;
            instance.Bind(binding);
            Visit(node, definition, instance, binding);
        }

        var artefact = HasErrors ? null : Finish(graph);

        return new(artefact, [.. diagnostics]);
    }

    /// <summary>Called before the walk, to reset whatever the subclass accumulates.</summary>
    /// <param name="graph">The graph about to be walked.</param>
    protected virtual void Begin(NodeGraphModel graph) { }

    /// <summary>Called once per node, in dependency order, with its ports already filled.</summary>
    /// <param name="node">The node as the graph holds it.</param>
    /// <param name="definition">Its type.</param>
    /// <param name="instance">The bound instance, whose port fields are filled.</param>
    /// <param name="binding">What each port carries, for a node that wants to ask.</param>
    protected abstract void Visit(GraphNode node, NodeTypeDefinition definition, Node instance, NodeBinding binding);

    /// <summary>Called after the walk, when nothing went wrong, to produce the artefact.</summary>
    /// <param name="graph">The graph that was walked.</param>
    /// <returns>The artefact, or null to fail without a further diagnostic.</returns>
    protected abstract TArtefact? Finish(NodeGraphModel graph);

    /// <summary>How a constant is spelled in the target language.</summary>
    /// <param name="value">Its lanes.</param>
    /// <param name="kind">What it is.</param>
    /// <returns>The literal.</returns>
    protected abstract string Constant(ReadOnlySpan<float> value, PortKind kind);

    /// <summary>How a value of one kind is read as another.</summary>
    /// <param name="expression">The value.</param>
    /// <param name="from">What it is.</param>
    /// <param name="target">What the port wants.</param>
    /// <returns>The expression that reads it as <paramref name="target" />.</returns>
    /// <remarks>
    ///     Only called where <see cref="PortKinds.Accepts" /> said yes and the two kinds differ, so an
    ///     implementation only has to handle vector widths.
    /// </remarks>
    protected abstract string Convert(string expression, PortKind from, PortKind target);

    /// <summary>Says something about a node.</summary>
    /// <param name="diagnostic">What to say.</param>
    protected void Report(NodeDiagnostic diagnostic) => diagnostics.Add(diagnostic);

    /// <summary>Whether anything has gone wrong so far.</summary>
    protected bool HasErrors {
        get {
            foreach (var diagnostic in diagnostics) {
                if (diagnostic.Severity == NodeSeverity.Error) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The variable one output writes to.</summary>
    /// <param name="node">The node it belongs to.</param>
    /// <param name="port">The port's name.</param>
    /// <returns>The variable's name.</returns>
    /// <remarks>
    ///     Public shape rather than a private detail because a subclass emitting a declaration needs
    ///     the same name the next node will read, and computing it twice in two places is how they
    ///     come to differ.
    /// </remarks>
    protected static string Variable(GraphNode node, string port) => $"n{node.Id.Value}_{port}";

    /// <summary>What a node's dynamic ports resolve to, from what arrives at them.</summary>
    PortKind Resolve(NodeGraphModel graph, GraphNode node, NodeTypeDefinition definition) {
        if (!definition.IsDynamic) {
            return PortKind.Float;
        }

        List<PortKind> arriving = [];

        foreach (var port in definition.Ports) {
            if (port is not { Direction: PortDirection.Input, Kind: PortKind.Dynamic }) {
                continue;
            }

            if (graph.Source(new(node.Id, port.Name)) is { } source && KindOf(graph, source) is { } kind) {
                arriving.Add(kind);
            }
        }

        return PortKinds.Resolve([.. arriving]);
    }

    /// <summary>What an output port carries, following its node's own resolution.</summary>
    PortKind? KindOf(NodeGraphModel graph, PortRef output) {
        if (!graph.TryGet(output.Node, out var node) || !Registry.TryGet(node.Type, out var definition)) {
            return null;
        }

        if (definition.Port(output.Port, PortDirection.Output) is not { } port) {
            return null;
        }

        // A dynamic output is as wide as its own node resolved to, and that node was resolved before
        // this one because the walk is in dependency order and Resolve only ever looks upstream.
        return port.Kind == PortKind.Dynamic ? Resolve(graph, node, definition) : port.Kind;
    }

    /// <summary>Assembles what every port of one node carries.</summary>
    NodeBinding Bind(NodeGraphModel graph, GraphNode node, NodeTypeDefinition definition, PortKind resolved) {
        Dictionary<string, string> inputs = new(StringComparer.Ordinal);
        Dictionary<string, string> outputs = new(StringComparer.Ordinal);
        Dictionary<string, float[]> values = new(StringComparer.Ordinal);
        HashSet<string> connected = new(StringComparer.Ordinal);

        foreach (var port in definition.Ports) {
            var kind = port.Kind == PortKind.Dynamic ? resolved : port.Kind;

            if (port.Direction == PortDirection.Output) {
                outputs[port.Name] = Variable(node, port.Name);

                continue;
            }

            var wired = graph.Source(new(node.Id, port.Name)) is not null;

            if (wired) {
                connected.Add(port.Name);
            }

            // A flow port carries no value, so there is nothing to spell and nothing to default. An
            // ordering that had a literal would be a number nobody could act on.
            if (kind == PortKind.Flow) {
                inputs[port.Name] = "";

                continue;
            }

            if (!wired) {
                values[port.Name] = [.. Value(node, port, kind)];
            }

            inputs[port.Name] = Incoming(graph, node, port, kind);
        }

        return new(inputs, outputs, values, connected, resolved);
    }

    /// <summary>What arrives at one input: an upstream value, an inline one, or a default.</summary>
    string Incoming(NodeGraphModel graph, GraphNode node, PortDefinition port, PortKind kind) {
        if (graph.Source(new(node.Id, port.Name)) is { } source) {
            var from = KindOf(graph, source);

            if (from is null) {
                Report(new(
                    "NG0002",
                    $"'{port.Name}' is connected to {source}, which is not an output this library has.",
                    node.Id,
                    port.Name
                ));

                return Constant(Value(node, port, kind), kind);
            }

            if (!PortKinds.Accepts(from.Value, kind)) {
                Report(new(
                    "NG0003",
                    $"'{port.Name}' wants a {kind} and {source} carries a {from}. There is no conversion "
                    + "between those two that would mean anything.",
                    node.Id,
                    port.Name
                ));

                return Constant(Value(node, port, kind), kind);
            }

            var expression = Variable(GraphNodeOf(graph, source), source.Port);

            return from.Value == kind ? expression : Convert(expression, from.Value, kind);
        }

        return Constant(Value(node, port, kind), kind);
    }

    /// <summary>The lanes an unconnected input takes: the author's, or the type's.</summary>
    static ReadOnlySpan<float> Value(GraphNode node, PortDefinition port, PortKind kind) {
        if (node.Values.TryGetValue(port.Name, out var inline)) {
            return inline;
        }

        // A dynamic port's default was declared as one number and may be being read as four. Padding
        // with the same number rather than with zero is what makes an unconnected dynamic input read
        // as the scalar its author wrote, splatted, which is what every shader language does.
        if (port.Default.Length == 1 && PortKinds.Lanes(kind) > 1) {
            var splat = new float[PortKinds.Lanes(kind)];

            Array.Fill(splat, port.Default[0]);

            return splat;
        }

        return port.Default.AsSpan();
    }

    static GraphNode GraphNodeOf(NodeGraphModel graph, PortRef port) =>
        graph.TryGet(port.Node, out var node)
            ? node
            : throw new InvalidOperationException($"{port.Node} left the graph while it was being compiled.");
}
