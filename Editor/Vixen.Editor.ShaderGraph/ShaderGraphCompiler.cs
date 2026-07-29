// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph;

/// <summary>What a shader graph compiles to.</summary>
/// <param name="Name">The shader declaration's name.</param>
/// <param name="Source">The Raven.</param>
public sealed record ShaderGraphSource(string Name, string Source);

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

        shader.Emit(new(body, uniforms, stage));
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

        foreach (var (uniform, type) in uniforms.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            text.AppendLine().AppendLine($"    var {uniform}: {type}");
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
            .AppendLine("    func Fragment(): float4 {")
            .Append(body)
            .AppendLine($"        return {master.Result}")
            .AppendLine("    }")
            .AppendLine("}");

        return new(name, text.ToString());
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
