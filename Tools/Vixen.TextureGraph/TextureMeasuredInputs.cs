// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph;

/// <summary>Which node types measure the image they read rather than composite it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Asked of the compiler rather than declared, because only a node's <c>Compile</c> says it.</b>
///         A distance field, a height to normal or a gradient map reads its input with
///         <see cref="TextureEmitter.ReadGrey" />, and a colour arriving there is
///         <see cref="TextureDiagnostics.ColourWhereOneChannelIsWanted" />. No port definition carries
///         that, and a compound inherits it from whichever node inside reads its interface, so a list
///         written down beside the nodes would be a second answer that drifts from the first.
///     </para>
///     <para>
///         <b>A host that feeds such a node from a colour chain</b> — a layer stack's filter layer, a
///         mask's effect — has to say which grey it means, and this is how it learns that it has to.
///         The answer is per registry, which is per project once its compounds are published.
///     </para>
/// </remarks>
public static class TextureMeasuredInputs {
    static readonly ConditionalWeakTable<NodeTypeRegistry, ConcurrentDictionary<string, bool>> Answers = new();

    /// <summary>Whether a colour wired into the type's single image input is refused as a colour.</summary>
    /// <param name="registry">The node types, compounds included.</param>
    /// <param name="subGraphs">Where compounds come from, or null, in which case a compound says no.</param>
    /// <param name="type">The node type's path.</param>
    /// <returns>Whether that input wants one channel and refuses four.</returns>
    /// <exception cref="ArgumentNullException">The registry or the type is null.</exception>
    public static bool Measures(NodeTypeRegistry registry, ISubGraphSource? subGraphs, string type) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(type);

        // ⚠ Not cached without a source: a compound probed without one says no for want of its body, and the
        // same registry asked again with the source must not be handed that answer.
        if (subGraphs is null) {
            return Probe(registry, null, type);
        }

        return Answers.GetOrCreateValue(registry).GetOrAdd(type, path => Probe(registry, subGraphs, path));
    }

    static bool Probe(NodeTypeRegistry registry, ISubGraphSource? subGraphs, string type) {
        if (!registry.TryGet(type, out var definition)) {
            return false;
        }

        PortDefinition? input = null;
        PortDefinition? output = null;

        foreach (var port in definition.Ports) {
            if (port.Kind != PortKind.Image) {
                continue;
            }

            if (port.Direction == PortDirection.Input) {
                if (input is not null) {
                    return false;
                }

                input = port;
            } else {
                if (output is not null) {
                    return false;
                }

                output = port;
            }
        }

        if (input is null || output is null) {
            return false;
        }

        NodeGraphModel graph = new();
        var colour = graph.Add("Source/Uniform");
        var node = graph.Add(type);
        var end = graph.Add("Output/Output");

        colour.SetValue("Colour", 0.5f, 0.5f, 0.5f, 1f);
        end.SetText("Usage", "baseColor");
        graph.Connect(new(colour.Id, "Out"), new(node.Id, input.Name));
        graph.Connect(new(node.Id, output.Name), new(end.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 8, BaseHeight = 8, SubGraphSource = subGraphs };

        foreach (var diagnostic in compiler.Compile(graph).Diagnostics) {
            if (string.Equals(
                    diagnostic.Id,
                    TextureDiagnostics.ColourWhereOneChannelIsWanted,
                    StringComparison.Ordinal
                )) {
                return true;
            }
        }

        return false;
    }
}
