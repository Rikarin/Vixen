// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.NodeGraph;

/// <summary>Which way a port faces.</summary>
public enum PortDirection {
    /// <summary>Into the node. At most one edge may arrive at it.</summary>
    Input,

    /// <summary>Out of it. Any number of edges may leave.</summary>
    Output
}

/// <summary>One port of a node type.</summary>
/// <param name="Name">What it is called. A saved graph names its edges by this.</param>
/// <param name="Direction">Which way it faces.</param>
/// <param name="Kind">What it carries.</param>
/// <param name="Default">
///     The value an unconnected input takes, one float per lane, or empty when it has none.
/// </param>
/// <param name="Summary">One line saying what it means.</param>
/// <remarks>
///     <b>Named rather than numbered, and that is a compatibility decision.</b> A node type that gains
///     a port must not renumber the ones a saved graph is already connected to, and an author who
///     renames a field would otherwise silently move every edge that pointed at it. Ports are matched
///     by name on load; a name that is gone is an edge that is dropped, with a diagnostic, which is
///     the failure that can be understood.
/// </remarks>
public sealed record PortDefinition(
    string Name,
    PortDirection Direction,
    PortKind Kind,
    ImmutableArray<float> Default = default,
    string Summary = ""
) {
    /// <summary>The default, or an empty span when the port has none.</summary>
    public ImmutableArray<float> Default { get; } = Default.IsDefault ? [] : Default;
}

/// <summary>One node type: what a graph can contain an instance of.</summary>
/// <param name="Path">The menu path, and the key a saved graph stores.</param>
/// <param name="Ports">Its ports, inputs first, in declaration order.</param>
/// <param name="Create">Makes an instance. Generated, so there is no reflection anywhere in this.</param>
/// <param name="Summary">One line saying what the node is for.</param>
/// <param name="Preview">Whether a view should draw a preview thumbnail for it.</param>
public sealed record NodeTypeDefinition(
    string Path,
    ImmutableArray<PortDefinition> Ports,
    Func<Node> Create,
    string Summary = "",
    bool Preview = false
) {
    /// <summary>The last segment of <see cref="Path" />: what the node is called.</summary>
    public string Title => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>Everything before that: where it sits in the create menu.</summary>
    public string Category => Path.LastIndexOf('/') is var slash && slash < 0 ? "" : Path[..slash];

    /// <summary>One port by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="direction">Which way it faces.</param>
    /// <returns>The port, or null when the type has no such port.</returns>
    public PortDefinition? Port(string name, PortDirection direction) {
        foreach (var port in Ports) {
            if (port.Direction == direction && string.Equals(port.Name, name, StringComparison.Ordinal)) {
                return port;
            }
        }

        return null;
    }

    /// <summary>Whether any of its ports is dynamically typed.</summary>
    public bool IsDynamic {
        get {
            foreach (var port in Ports) {
                if (port.Kind == PortKind.Dynamic) {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
///     The base every node type derives from, and the half of it the generator writes.
/// </summary>
/// <remarks>
///     A node's ports are its <i>fields</i>, which is what makes a node declaration readable — the
///     alternative is a dictionary the node looks itself up in, and an <c>Emit</c> full of string
///     keys. <see cref="Bind" /> is what fills them, and it is generated from the same marked fields
///     the port metadata comes from, so the two cannot disagree.
/// </remarks>
public abstract class Node {
    /// <summary>What each port carries this time round.</summary>
    /// <remarks>
    ///     Set by the compiler just before the node is asked to do anything, and available so a node
    ///     can ask the one question its fields cannot answer — whether a port is <i>connected</i>.
    ///     An unconnected input still carries an expression, because it carries its default, and a
    ///     texture node that wants the mesh's own coordinate when nobody wired one has to be able to
    ///     tell the two apart.
    /// </remarks>
    public NodeBinding Binding { get; internal set; } = NodeBinding.Empty;

    /// <summary>Fills the port fields from what the compiler resolved. Generated.</summary>
    /// <param name="binding">The expressions the ports carry this time round.</param>
    public abstract void Bind(NodeBinding binding);
}

/// <summary>What each of a node's ports carries, for one compilation of one instance.</summary>
/// <remarks>
///     Handed to <see cref="Node.Bind" /> rather than reached out for, so a node cannot read a port it
///     did not declare and cannot read the graph at all. A node is a function from its inputs to its
///     outputs, and this is the argument list.
/// </remarks>
public sealed class NodeBinding {
    readonly Dictionary<string, string> inputs;
    readonly Dictionary<string, string> outputs;
    readonly Dictionary<string, float[]> values;
    readonly HashSet<string> connected;

    internal NodeBinding(
        Dictionary<string, string> inputs,
        Dictionary<string, string> outputs,
        Dictionary<string, float[]> values,
        HashSet<string> connected,
        PortKind resolved
    ) {
        this.inputs = inputs;
        this.outputs = outputs;
        this.values = values;
        this.connected = connected;
        Resolved = resolved;
    }

    /// <summary>A binding with nothing in it, for a node nobody has compiled yet.</summary>
    public static NodeBinding Empty { get; } = new([], [], [], [], PortKind.Float);

    /// <summary>The constant an unconnected input carries, as lanes.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns>Its lanes, padded to the resolved width; empty when the port is connected.</returns>
    /// <remarks>
    ///     <b>For a target that is not source text.</b> A shader graph reads
    ///     <see cref="Input" /> and interpolates it into a line of Raven; a VFX graph is compiling to
    ///     an array of operations whose parameters are <i>numbers</i>, and parsing them back out of a
    ///     literal it just formatted would be absurd. So the resolution happens once and both forms
    ///     are handed over.
    /// </remarks>
    public ReadOnlySpan<float> Value(string name) => values.TryGetValue(name, out var lanes) ? lanes : [];

    /// <summary>Whether an input has an edge arriving at it.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns><see langword="true" /> if something is wired to it.</returns>
    /// <remarks>
    ///     Not the same as "carries an expression": an unconnected input carries its default, which
    ///     is a perfectly good expression and the wrong thing for a node that wanted to substitute
    ///     something of its own.
    /// </remarks>
    public bool IsConnected(string name) => connected.Contains(name);

    /// <summary>What this instance's dynamic ports resolved to.</summary>
    /// <remarks>
    ///     <see cref="PortKind.Float" /> for a node that has none, which is harmless: a node with no
    ///     dynamic ports has nothing to ask this about.
    /// </remarks>
    public PortKind Resolved { get; }

    /// <summary>The expression arriving at one input.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns>The expression.</returns>
    /// <exception cref="KeyNotFoundException">
    ///     The node has no such input — which, since the caller is generated from the same fields the
    ///     definition is, means the generated code and the definition have come apart.
    /// </exception>
    public string Input(string name) => inputs[name];

    /// <summary>The variable one output writes to.</summary>
    /// <param name="name">The port's name.</param>
    /// <returns>The variable's name.</returns>
    /// <exception cref="KeyNotFoundException">The node has no such output.</exception>
    public string Output(string name) => outputs[name];
}
