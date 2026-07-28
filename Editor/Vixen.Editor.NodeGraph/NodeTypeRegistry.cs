// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.NodeGraph;

/// <summary>
///     Every node type a graph may contain, by path.
/// </summary>
/// <remarks>
///     <para>
///         <b>Filled by generated code, not by reflection.</b> The generator writes one
///         <c>Register</c> per assembly listing that assembly's node types, and a host calls the ones
///         it wants. Scanning assemblies would be the shorter code and would cost a reflection pass at
///         startup, an AOT hazard, and a node library whose contents depend on which assemblies
///         happened to be loaded.
///     </para>
///     <para>
///         <b>An instance rather than a static.</b> Two graphs may want different libraries — a shader
///         graph has no spawner nodes and a VFX graph has no master node — and a test wants a registry
///         with three types in it rather than the whole world. Nothing here is global.
///     </para>
/// </remarks>
public sealed class NodeTypeRegistry {
    readonly Dictionary<string, NodeTypeDefinition> types = new(StringComparer.Ordinal);

    /// <summary>How many types are registered.</summary>
    public int Count => types.Count;

    /// <summary>Every type, in no particular order.</summary>
    public IEnumerable<NodeTypeDefinition> Types => types.Values;

    /// <summary>Adds a type.</summary>
    /// <param name="definition">The type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     A different type is already registered under that path, or the definition is malformed.
    /// </exception>
    /// <remarks>
    ///     Registering the same definition twice is allowed and does nothing, because two libraries
    ///     may both pull in a shared one. Registering a <i>different</i> type under a path that is
    ///     taken is refused: a saved graph names its nodes by path, and two meanings for one path is
    ///     a graph that loads as something else depending on registration order.
    /// </remarks>
    public void Add(NodeTypeDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);

        if (types.TryGetValue(definition.Path, out var existing)) {
            if (!ReferenceEquals(existing, definition) && existing != definition) {
                throw new ArgumentException(
                    $"Two different node types claim the path '{definition.Path}'. A saved graph names its "
                    + "nodes by path, so one path has to mean one thing.",
                    nameof(definition)
                );
            }

            return;
        }

        types.Add(definition.Path, definition);
    }

    /// <summary>The type at a path.</summary>
    /// <param name="path">The path.</param>
    /// <param name="definition">The type, when there is one.</param>
    /// <returns><see langword="true" /> if there is.</returns>
    public bool TryGet(string path, [NotNullWhen(true)] out NodeTypeDefinition? definition) =>
        types.TryGetValue(path, out definition);

    /// <summary>The type at a path, or a stated failure.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The type.</returns>
    /// <exception cref="KeyNotFoundException">Nothing is registered there.</exception>
    public NodeTypeDefinition Get(string path) =>
        types.TryGetValue(path, out var definition)
            ? definition
            : throw new KeyNotFoundException(
                $"No node type is registered at '{path}'. Either its library was not registered, or the "
                + "graph was saved against a plugin that is not loaded."
            );

    /// <summary>Checks a definition is one a graph could actually use.</summary>
    /// <remarks>
    ///     Here rather than in the generator because a definition may also be written by hand — a
    ///     sub-graph's is, and it is the one whose ports come from data rather than from source.
    /// </remarks>
    static void Validate(NodeTypeDefinition definition) {
        if (string.IsNullOrWhiteSpace(definition.Path)) {
            throw new ArgumentException("A node type needs a path; it is what a saved graph stores.", nameof(definition));
        }

        HashSet<string> inputs = new(StringComparer.Ordinal);
        HashSet<string> outputs = new(StringComparer.Ordinal);

        foreach (var port in definition.Ports) {
            if (string.IsNullOrWhiteSpace(port.Name)) {
                throw new ArgumentException(
                    $"A port of '{definition.Path}' has no name, and an edge is stored by port name.",
                    nameof(definition)
                );
            }

            var seen = port.Direction == PortDirection.Input ? inputs : outputs;

            if (!seen.Add(port.Name)) {
                throw new ArgumentException(
                    $"'{definition.Path}' declares two {port.Direction} ports called '{port.Name}'. An edge "
                    + "naming that port would not say which.",
                    nameof(definition)
                );
            }
        }
    }
}
