// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph;

/// <summary>Which lines of a generated shader one node wrote.</summary>
/// <param name="Node">
///     The node an author can select. For a node that came out of a sub-graph this is the sub-graph
///     node in their own graph, not the synthetic copy — see <see cref="NodeGraphInlining" />.
/// </param>
/// <param name="Emitted">
///     And the identity the flattened graph gave it, which is the one in the variable names:
///     <c>NodeGraphCompiler.Variable</c> spells an output <c>n{id}_{port}</c>. Kept because "show
///     generated code" is about the text, where that is the name that appears.
/// </param>
/// <param name="Span">Which lines of <see cref="ShaderGraphSource.Source" />, counted from zero.</param>
public readonly record struct ShaderGraphSpan(NodeId Node, NodeId Emitted, NodeSpan Span);

/// <summary>What a shader graph compiles to.</summary>
/// <param name="Name">The shader declaration's name.</param>
/// <param name="Source">The Raven.</param>
/// <param name="Properties">Every uniform the graph asked for, by name, in a stable order.</param>
/// <param name="Spans">
///     Which node is answerable for which lines: the uniform declarations first, then the pixel
///     stage's body in the order it was emitted.
/// </param>
/// <remarks>
///     <para>
///         <b>The uniforms the graph asked for, not the ones every graph has.</b>
///         <c>worldViewProjection</c> and <c>world</c> are declared in every shader and authored in
///         none, so a list an author reads as "what this graph needs from outside" is wrong to
///         include them.
///     </para>
///     <para>
///         ⚠ <b>It does not say which of them a <i>material</i> supplies.</b> A texture node's
///         property is the material's; the clock a <c>Time</c> node reads and the light a PBR master
///         shades with are the engine's, and nothing here can tell them apart because the emitter
///         asks for both the same way. Sorting them is the material compiler's job and doc 08 owns
///         it; until then this is the honest list — every name the shader declares.
///     </para>
/// </remarks>
public sealed record ShaderGraphSource(
    string Name,
    string Source,
    ImmutableArray<ShaderGraphProperty> Properties = default,
    ImmutableArray<ShaderGraphSpan> Spans = default
) {
    /// <inheritdoc cref="ShaderGraphSource" />
    public ImmutableArray<ShaderGraphProperty> Properties { get; } = Properties.IsDefault ? [] : Properties;

    /// <inheritdoc cref="ShaderGraphSource" />
    /// <remarks>
    ///     <para>
    ///         <b>This is the half of doc 07's "diagnostics mapped back to node ports" that the graph
    ///         side owes.</b> Raven's complaints about generated text name a line; an author can act on
    ///         a node. <see cref="NodeAt" /> is the join.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not every line has a node.</b> The preamble, the vertex stage and the master's
    ///         <c>return</c> are the compiler's own text, so a complaint about one of them maps to
    ///         nothing — and reporting the nearest node instead would send an author to a node that is
    ///         fine. <see cref="NodeAt" /> answers false, and a caller says "line N" as it always did.
    ///     </para>
    ///     <para>
    ///         <b>A uniform's declaration is a node's, though it is the compiler that writes it.</b>
    ///         The line is where a property name an author typed is first refused, and the node that
    ///         asked for it is exactly who to send them to — so <c>RavenEmitter.Uniform</c>'s first
    ///         asker owns the declaration the same way an emitting node owns its statements.
    ///     </para>
    /// </remarks>
    public ImmutableArray<ShaderGraphSpan> Spans { get; } = Spans.IsDefault ? [] : Spans;

    /// <summary>Which node wrote a line of the emitted source.</summary>
    /// <param name="line">The line, counted from zero.</param>
    /// <param name="span">What that node wrote, when one did.</param>
    /// <returns><see langword="true" /> if a node wrote that line.</returns>
    public bool NodeAt(int line, out ShaderGraphSpan span) {
        foreach (var candidate in Spans) {
            if (candidate.Span.Contains(line)) {
                span = candidate;

                return true;
            }
        }

        span = default;

        return false;
    }
}

/// <summary>
///     A shader graph, as Raven source.
/// </summary>
/// <remarks>
///     <para>
///         <b>Source, not IR.</b> Doc 07 settled this: the generated shader is inspectable through
///         "show generated code", it is type-checked by the same compiler a hand-written shader is,
///         and its diagnostics come back with spans that map to the nodes that emitted them. A graph
///         that lowered straight to IR would need its own type checker and would produce shaders
///         nobody could read.
///     </para>
///     <para>
///         <b>The vertex stage is fixed and the pixel stage is the graph.</b> A shader graph is about
///         what a surface looks like; the transform and the interpolators are the same in every one.
///         The stage is emitted with exactly the varyings the graph asked for, so a graph that never
///         reads a normal does not interpolate one — which is a real cost on a dense mesh and a
///         varying slot on every mesh.
///     </para>
///     <para>
///         <b>Exactly one master.</b> A graph with none produces nothing to write; a graph with two
///         would produce two shaders under one name. Both are reported against the graph rather than
///         guessed at.
///     </para>
/// </remarks>
public sealed class ShaderGraphCompiler : NodeGraphCompiler<ShaderGraphSource> {
    readonly StringBuilder body = new();
    readonly Dictionary<string, string> uniforms = new(StringComparer.Ordinal);
    readonly HashSet<ShaderStageInput> stage = [];
    readonly List<ShaderGraphSpan> spans = [];
    readonly Dictionary<string, NodeId> declaredBy = new(StringComparer.Ordinal);

    RavenEmitter emitter = null!;
    ShaderMasterNode? master;
    NodeId masterId;

    /// <summary>Starts a compiler over a node library.</summary>
    /// <param name="registry">The node types the graph may contain.</param>
    public ShaderGraphCompiler(NodeTypeRegistry registry) : base(registry) { }

    /// <summary>What the emitted shader declaration is called, when the graph does not say.</summary>
    public string DefaultName { get; set; } = "Generated";

    /// <inheritdoc />
    protected override void Begin(NodeGraphModel graph) {
        body.Clear();
        uniforms.Clear();
        stage.Clear();
        spans.Clear();
        declaredBy.Clear();

        // ⚠ One emitter for the whole walk, where there used to be one per node. It is what counts
        // the body's lines, and a fresh one per node would count each node's from zero.
        emitter = new(body, uniforms, stage);
        master = null;
        masterId = NodeId.None;
    }

    /// <inheritdoc />
    protected override void Visit(GraphNode node, NodeTypeDefinition definition, Node instance, NodeBinding binding) {
        if (instance is not ShaderNode shader) {
            Report(new(
                "SG0001",
                $"'{definition.Path}' is in this graph's library but is not a shader node, so there is "
                + "nothing it could emit.",
                node.Id
            ));

            return;
        }

        if (shader is ShaderMasterNode found) {
            if (master is not null) {
                Report(new(
                    "SG0002",
                    $"This graph has two master nodes, {masterId} and {node.Id}. A shader has one output, "
                    + "so one of them is the one that matters and the graph does not say which.",
                    node.Id
                ));

                return;
            }

            master = found;
            masterId = node.Id;
        }

        var first = emitter.Lines;

        shader.Emit(emitter);

        // ⚠ `Inlining.Resolve`, so the span names a node the author has. A node inlined out of a
        // sub-graph wrote these lines under a synthetic identity, and a squiggle in the generated pane
        // that reported one would be a diagnostic pointing at nothing selectable — which is the whole
        // failure this map exists to close. The synthetic identity is kept beside it because it is the
        // one in the variable names on those very lines.
        if (emitter.Lines > first) {
            spans.Add(new(Inlining.Resolve(node.Id), node.Id, new(first, emitter.Lines - first)));
        }

        // ⚠ And the declarations, which are the compiler's text and the node's doing. A property node
        // whose name an author typed a space into is refused twice — once where it is declared and
        // once where it is read — and the declaration is the more useful of the two to be sent to.
        // `TryAdd` because a name is declared once however many nodes ask for it, which is
        // `RavenEmitter.Uniform`'s bargain: the first asker owns the line.
        foreach (var name in uniforms.Keys) {
            declaredBy.TryAdd(name, node.Id);
        }
    }

    /// <inheritdoc />
    protected override ShaderGraphSource? Finish(NodeGraphModel graph) {
        if (master is null) {
            Report(new(
                "SG0003",
                "This graph has no master node, so there is nothing for the shader to write. Add one from "
                + "the Master category.",
                NodeId.None
            ));

            return null;
        }

        var name = Identifier(graph.Name.Length > 0 ? graph.Name : DefaultName);
        var text = new StringBuilder();

        text.AppendLine("// Generated from a node graph by Vixen.Editor.ShaderGraph. The graph is the source; this is not.")
            .AppendLine()
            .AppendLine("package Vixen.ShaderGraph.Generated")
            .AppendLine()
            .AppendLine($"shader {name} {{")
            .AppendLine("    /// The object-to-clip transform. Every graph has one; none of them author it.")
            .AppendLine("    var worldViewProjection: mat4")
            .AppendLine()
            .AppendLine("    /// The object-to-world transform, for the world-space interpolators.")
            .AppendLine("    var world: mat4");

        List<ShaderGraphSpan> declarations = [];

        // Where the next line will land, counted once and then carried. Every `AppendLine` adds
        // exactly one line whatever the platform spells a newline as, so the cursor is arithmetic
        // rather than a rescan of a builder that is getting longer.
        var cursor = Lines(text) - 1;

        foreach (var (uniform, type) in uniforms.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            text.AppendLine().AppendLine($"    var {uniform}: {type}");

            // A blank line and then the declaration, so the declaration is the second of the two.
            var declaration = cursor + 1;

            cursor += 2;

            if (declaredBy.TryGetValue(uniform, out var owner)) {
                declarations.Add(new(Inlining.Resolve(owner), owner, new(declaration, 1)));
            }
        }

        // Only the varyings the graph asked for. Ordered, so the same graph emits the same source.
        foreach (var input in stage.Order()) {
            text.AppendLine().AppendLine($"    stream var {Stream(input)}: {StreamType(input)}");
        }

        text.AppendLine()
            .AppendLine("    [VertexShader]")
            .AppendLine("    [Semantic(\"SV_Position\")]")
            .AppendLine($"    func Vertex({string.Join(", ", VertexParameters())}): float4 {{");

        foreach (var input in stage.Order()) {
            text.AppendLine($"        {Stream(input)} = {VertexAssignment(input)}");
        }

        text.AppendLine("        return worldViewProjection * float4(position, 1f)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [FragmentShader]")
            .AppendLine("    [Semantic(\"SV_Target\")]")
            .AppendLine("    func Fragment(): float4 {");

        // ⚠ Read here, between the header and the body, because this is the one moment the offset is
        // knowable: the emitter counted the body's lines from zero and everything above is the
        // compiler's own text, whose length depends on how many uniforms and varyings the graph asked
        // for. Computing it anywhere else means counting a string twice and having the two disagree.
        var offset = Lines(text) - 1;

        text.Append(body)
            .AppendLine($"        return {master.Result}")
            .AppendLine("    }")
            .AppendLine("}");

        return new(
            name,
            text.ToString(),
            [
                .. uniforms.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new ShaderGraphProperty(entry.Key, entry.Value))
            ],
            [
                .. declarations,
                .. spans.Select(span => span with { Span = new(span.Span.Line + offset, span.Span.Lines) })
            ]
        );
    }

    /// <summary>How many lines a builder holds, counting a trailing newline as ending one.</summary>
    static int Lines(StringBuilder text) {
        var count = 1;

        for (var index = 0; index < text.Length; index++) {
            if (text[index] == '\n') {
                count++;
            }
        }

        return count;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Raven spells a constructed vector <c>float3(a, b, c)</c> and a scalar with an <c>f</c>
    ///     suffix, and a machine whose decimal separator is a comma must not emit source no compiler
    ///     will parse — which is why this goes through <see cref="Literal" /> rather than
    ///     <c>ToString</c>.
    /// </remarks>
    protected override string Constant(ReadOnlySpan<float> value, PortKind kind) {
        var lanes = PortKinds.Lanes(kind);

        if (kind == PortKind.Bool) {
            return value.Length > 0 && value[0] != 0f ? "true" : "false";
        }

        if (kind == PortKind.Int) {
            return ((int)(value.Length > 0 ? value[0] : 0f)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (lanes <= 1) {
            return Literal.Of(value.Length > 0 ? value[0] : 0f) + "f";
        }

        var components = new string[lanes];

        for (var index = 0; index < lanes; index++) {
            // A default shorter than the port is padded with its last lane rather than with zero: a
            // two-lane default read as a float3 means "and the same again", not "and then black".
            var lane = value.Length == 0 ? 0f : value[Math.Min(index, value.Length - 1)];

            components[index] = Literal.Of(lane) + "f";
        }

        return $"float{lanes}({string.Join(", ", components)})";
    }

    /// <inheritdoc />
    protected override string Convert(string expression, PortKind from, PortKind target) {
        var source = PortKinds.Lanes(from);
        var wanted = PortKinds.Lanes(target);

        if (source == wanted) {
            return expression;
        }

        if (source == 1) {
            // A scalar widens by splatting, which is what every shader language does for `v * s` and
            // what an author wiring a mask into a colour means.
            return $"float{wanted}({string.Join(", ", Enumerable.Repeat(expression, wanted))})";
        }

        if (wanted == 1) {
            return $"{expression}.x";
        }

        if (wanted < source) {
            return expression + "." + "xyzw"[..wanted];
        }

        // Widening a vector pads with zeroes and then a one, which is the homogeneous convention: a
        // float3 read as a float4 is a point, and reading it as a direction is what a Combine node is
        // for.
        var parts = new List<string>();

        for (var index = 0; index < source; index++) {
            parts.Add($"{expression}.{"xyzw"[index]}");
        }

        while (parts.Count < wanted) {
            parts.Add(parts.Count == wanted - 1 ? "1f" : "0f");
        }

        return $"float{wanted}({string.Join(", ", parts)})";
    }

    IEnumerable<string> VertexParameters() {
        yield return "position: float3";

        if (stage.Contains(ShaderStageInput.Uv)) {
            yield return "texcoord: float2";
        }

        if (stage.Contains(ShaderStageInput.WorldNormal)) {
            yield return "normal: float3";
        }

        if (stage.Contains(ShaderStageInput.VertexColour)) {
            yield return "colour: float4";
        }
    }

    static string VertexAssignment(ShaderStageInput input) => input switch {
        ShaderStageInput.Uv => "texcoord",
        ShaderStageInput.WorldPosition => "(world * float4(position, 1f)).xyz",
        ShaderStageInput.WorldNormal => "(world * float4(normal, 0f)).xyz",
        _ => "colour"
    };

    static string Stream(ShaderStageInput input) => input switch {
        ShaderStageInput.Uv => "uv",
        ShaderStageInput.WorldPosition => "worldPosition",
        ShaderStageInput.WorldNormal => "worldNormal",
        _ => "vertexColour"
    };

    static string StreamType(ShaderStageInput input) => input switch {
        ShaderStageInput.Uv => "float2",
        ShaderStageInput.VertexColour => "float4",
        _ => "float3"
    };

    /// <summary>A graph's name, as something Raven would accept as one.</summary>
    static string Identifier(string name) {
        var text = new StringBuilder();

        foreach (var character in name) {
            if (char.IsLetterOrDigit(character) || character == '_') {
                text.Append(character);
            }
        }

        return text.Length == 0 || char.IsDigit(text[0]) ? "Shader" + text : text.ToString();
    }
}
