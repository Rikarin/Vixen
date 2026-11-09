// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph;

/// <summary>What one image input of a node type asks of what feeds it: one channel, or anything at all.</summary>
/// <remarks>
///     <para>
///         <b><see cref="TextureMeasuredInputs" />, per port</b> (Patina #113). A host that feeds a node's inputs from
///         pictures it chose — a filter layer's second image, a mask step's field — has to know two things no port
///         definition carries: whether the port refuses a colour, so it says which grey it means, and whether the node
///         does without the port, so an empty slot is left empty rather than refused.
///     </para>
///     <para>
///         ⚠ <b>Asked of the compiler, for that class's reason.</b> Only a node's <c>Compile</c> says either, and a
///         compound inherits both from whichever node inside reads its interface, so a list beside the nodes would be
///         a second answer that drifts from the first. Each answer is a compile of a graph of three or four nodes at
///         eight texels, cached per registry once compounds can be resolved.
///     </para>
/// </remarks>
public static class TextureImageInputs {
    static readonly ConditionalWeakTable<NodeTypeRegistry, ConcurrentDictionary<(string, string, bool), bool>> Answers =
        new();

    /// <summary>Whether a colour wired into one image input of a type is refused as a colour.</summary>
    /// <param name="registry">The node types, compounds included.</param>
    /// <param name="subGraphs">Where compounds come from, or null, in which case a compound says no.</param>
    /// <param name="type">The node type's path.</param>
    /// <param name="port">The image input's name.</param>
    /// <returns>Whether that input wants one channel and refuses four.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="subGraphs" /> is null.</exception>
    public static bool Measures(NodeTypeRegistry registry, ISubGraphSource? subGraphs, string type, string port) =>
        Answer(registry, subGraphs, type, port, true);

    /// <summary>Whether a type compiles with one of its image inputs left unwired.</summary>
    /// <param name="registry">The node types, compounds included.</param>
    /// <param name="subGraphs">Where compounds come from, or null, in which case a compound says no.</param>
    /// <param name="type">The node type's path.</param>
    /// <param name="port">The image input's name.</param>
    /// <returns>Whether the node does without it, every other image input being fed.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="subGraphs" /> is null.</exception>
    public static bool Optional(NodeTypeRegistry registry, ISubGraphSource? subGraphs, string type, string port) =>
        Answer(registry, subGraphs, type, port, false);

    static bool Answer(NodeTypeRegistry registry, ISubGraphSource? subGraphs, string type, string port, bool colour) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(port);

        // ⚠ Not cached without a source, for `TextureMeasuredInputs.Measures`' reason.
        if (subGraphs is null) {
            return Probe(registry, null, type, port, colour);
        }

        return Answers.GetOrCreateValue(registry)
            .GetOrAdd((type, port, colour), key => Probe(registry, subGraphs, key.Item1, key.Item2, key.Item3));
    }

    /// <summary>
    ///     Compiles the type with every image input but <paramref name="port" /> fed a grey, and that one fed a colour
    ///     or nothing.
    /// </summary>
    static bool Probe(NodeTypeRegistry registry, ISubGraphSource? subGraphs, string type, string port, bool colour) {
        if (!registry.TryGet(type, out var definition)
            || definition.Port(port, PortDirection.Input) is not { Kind: PortKind.Image }) {
            return false;
        }

        PortDefinition? output = null;

        foreach (var candidate in definition.Ports) {
            if (candidate is { Direction: PortDirection.Output, Kind: PortKind.Image }) {
                output = candidate;

                break;
            }
        }

        if (output is null) {
            return false;
        }

        NodeGraphModel graph = new();
        var node = graph.Add(type);
        var end = graph.Add("Output/Output");
        var picture = graph.Add("Source/Uniform");
        var grey = graph.Add("Colour/Grayscale");

        picture.SetValue("Colour", 0.5f, 0.4f, 0.3f, 1f);
        graph.Connect(new(picture.Id, "Out"), new(grey.Id, "Input"));
        end.SetText("Usage", "baseColor");
        graph.Connect(new(node.Id, output.Name), new(end.Id, "Input"));

        foreach (var input in definition.Ports) {
            if (input is not { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                continue;
            }

            if (string.Equals(input.Name, port, StringComparison.Ordinal)) {
                if (colour) {
                    graph.Connect(new(picture.Id, "Out"), new(node.Id, input.Name));
                }

                continue;
            }

            graph.Connect(new(grey.Id, "Out"), new(node.Id, input.Name));
        }

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 8, BaseHeight = 8, SubGraphSource = subGraphs };
        var diagnostics = compiler.Compile(graph).Diagnostics;

        if (colour) {
            foreach (var diagnostic in diagnostics) {
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

        // ⚠ Any error and not only `NoImage`: a compound reports an unwired interface input against an inner port,
        // re-addressed onto this node, and a node that refuses in some other way is not one to leave unwired either.
        foreach (var diagnostic in diagnostics) {
            if (diagnostic.Severity == NodeSeverity.Error) {
                return false;
            }
        }

        return true;
    }
}
