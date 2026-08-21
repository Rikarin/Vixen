// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.NodeGraph;
using Vixen.Vfx;

namespace Vixen.Editor.VfxGraph;

/// <summary>
///     What a VFX graph compiles to: one effect, for both processors.
/// </summary>
/// <param name="Graph">The compiled graph the CPU simulation runs.</param>
/// <param name="Shader">The same graph as a Raven compute shader, for the GPU.</param>
/// <remarks>
///     <b>The second one is nearly free, and that is the return on a decision made much earlier.</b>
///     Doc 06 asked for dual-target compilation to be designed in rather than retrofitted, so
///     <see cref="VfxCompiledGraph" /> was made an array of fixed-size operations and
///     <see cref="VfxShaderEmitter" /> was written against it. A node graph that produces that array
///     therefore produces the shader too, by calling one method — there is no second lowering, no
///     second node library, and no way for the two to disagree about what the graph meant.
/// </remarks>
public sealed record VfxGraphArtefact(VfxCompiledGraph Graph, VfxShader Shader);

/// <summary>
///     A VFX graph, compiled to a runtime effect and the shader that runs it on a device.
/// </summary>
/// <remarks>
///     <para>
///         <b>Blocks in a chain, not expressions in a tree.</b> A shader graph's edges carry values; a
///         VFX graph's carry <i>order</i> — see <see cref="PortKind.Flow" />. The framework's
///         topological sort turns the chain into the list <see cref="VfxCompiledGraph.Compile" />
///         wants, and its cycle refusal means an author cannot draw a loop of blocks.
///     </para>
///     <para>
///         <b>An unwired block still runs.</b> Order among blocks nothing wires is the order they were
///         added to the graph, because that is what the framework's sort falls back to — so a graph
///         built by dropping blocks in and never connecting them compiles to what an author would
///         expect, and connecting them is how to say otherwise.
///     </para>
/// </remarks>
public sealed class VfxGraphCompiler : NodeGraphCompiler<VfxGraphArtefact> {
    VfxGraphBuilder builder = new();

    /// <summary>Starts a compiler over a node library.</summary>
    /// <param name="registry">The node types the graph may contain.</param>
    public VfxGraphCompiler(NodeTypeRegistry registry) : base(registry) { }

    /// <summary>What the emitted shader is called, when the graph does not say.</summary>
    public string DefaultName { get; set; } = "Effect";

    /// <inheritdoc />
    protected override void Begin(NodeGraphModel graph) => builder = new();

    /// <inheritdoc />
    protected override void Visit(GraphNode node, NodeTypeDefinition definition, Node instance, NodeBinding binding) {
        if (instance is not VfxNode vfx) {
            Report(new(
                "VG0001",
                $"'{definition.Path}' is in this graph's library but is not a VFX node, so there is nothing "
                + "it could contribute.",
                node.Id
            ));

            return;
        }

        vfx.Contribute(builder);
    }

    /// <inheritdoc />
    protected override VfxGraphArtefact? Finish(NodeGraphModel graph) {
        if (builder.Spawners.Count == 0) {
            Report(new(
                "VG0002",
                "This graph has no spawner, so it would produce no particles at all. Add one from the "
                + "Spawn category.",
                NodeId.None
            ));

            return null;
        }

        // ⚠ Checked before the ribbon is resolved as well as after, because a name that could not be
        // declared is also a name the ribbon will not find — and a graph told about both would be
        // told twice about one mistake.
        if (Complained()) {
            return null;
        }

        // A ribbon names its strip attribute and every block that writes one has now been walked, so
        // this is where the name becomes a slot — see VfxGraphBuilder.RibbonAttribute for why it
        // could not have been done as the node contributed.
        if (builder.Renderer is { Kind: VfxRendererKind.Ribbon }) {
            var slot = builder.SlotOf(builder.RibbonAttribute);

            if (Complained()) {
                return null;
            }

            builder.Renderer = VfxRenderer.Ribbon(slot);
        }

        // Compile is what refuses a graph that reads an attribute nothing writes, and it says so in a
        // sentence. Turning that into a diagnostic against the graph is better than letting an
        // exception out of a compiler whose whole job is to report problems.
        try {
            var compiled = VfxCompiledGraph.Compile(
                [.. builder.Spawners],
                [.. builder.Initializers],
                [.. builder.Updaters],
                builder.Capacity,
                builder.Renderer,
                [.. builder.Customs]
            );

            return new(compiled, VfxShaderEmitter.Emit(compiled, Identifier(graph.Name.Length > 0 ? graph.Name : DefaultName)));
        } catch (ArgumentException exception) {
            Report(new("VG0003", exception.Message, NodeId.None));

            return null;
        }
    }

    /// <summary>Reports whatever the nodes found wrong, and says whether there was any of it.</summary>
    /// <returns><see langword="true" /> if the graph cannot be compiled.</returns>
    /// <remarks>
    ///     <c>Contribute</c> is handed a builder and not a diagnostic sink, so a node leaves what it
    ///     found in <see cref="VfxGraphBuilder.Problems" /> rather than throwing — the walk reports
    ///     everything it can see, which is the whole reason <c>NodeGraphCompiler</c> keeps going after
    ///     an error. This is where that list becomes diagnostics.
    /// </remarks>
    bool Complained() {
        if (builder.Problems.Count == 0) {
            return false;
        }

        foreach (var problem in builder.Problems) {
            Report(new("VG0004", problem, NodeId.None));
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A VFX graph's parameters are numbers rather than source, so nothing here ever spells one
    ///     into a program — the nodes read <see cref="NodeBinding.Value" /> instead. This exists
    ///     because the base class needs it for a diagnostic's sake, and it produces something
    ///     readable for exactly that.
    /// </remarks>
    protected override string Constant(ReadOnlySpan<float> value, PortKind kind) {
        var lanes = new string[Math.Max(1, value.Length)];

        for (var index = 0; index < value.Length; index++) {
            lanes[index] = value[index].ToString("R", CultureInfo.InvariantCulture);
        }

        return value.Length == 0 ? "0" : string.Join(", ", lanes);
    }

    /// <inheritdoc />
    protected override string Convert(string expression, PortKind from, PortKind target) => expression;

    /// <summary>A graph's name, as something Raven would accept as one.</summary>
    static string Identifier(string name) {
        var text = new System.Text.StringBuilder();

        foreach (var character in name) {
            if (char.IsLetterOrDigit(character) || character == '_') {
                text.Append(character);
            }
        }

        return text.Length == 0 || char.IsDigit(text[0]) ? "Effect" + text : text.ToString();
    }
}
