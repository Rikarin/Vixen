// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph.Nodes;

/// <summary>The interpolated texture coordinate.</summary>
[Node("Input/UV", Summary = "The interpolated texture coordinate.")]
public sealed partial class UvNode : ShaderNode {
    /// <summary>The coordinate.</summary>
    [Output(Name = "UV")]
    public Float2 Uv;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Uv.Expression, emitter.Stage(ShaderStageInput.Uv));
}

/// <summary>The world-space position of the fragment.</summary>
[Node("Input/World Position", Summary = "Where the fragment is, in world space.")]
public sealed partial class WorldPositionNode : ShaderNode {
    /// <summary>The position.</summary>
    [Output(Name = "Position")]
    public Float3 Position;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Position.Expression, emitter.Stage(ShaderStageInput.WorldPosition));
}

/// <summary>The interpolated world-space normal.</summary>
[Node("Input/World Normal", Summary = "The interpolated surface normal, in world space.")]
public sealed partial class WorldNormalNode : ShaderNode {
    /// <summary>The normal.</summary>
    [Output(Name = "Normal")]
    public Float3 Normal;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Normal.Expression, $"normalize({emitter.Stage(ShaderStageInput.WorldNormal)})");
}

/// <summary>The interpolated vertex colour.</summary>
[Node("Input/Vertex Colour", Summary = "The mesh's own per-vertex colour.")]
public sealed partial class VertexColourNode : ShaderNode {
    /// <summary>The colour.</summary>
    [Output(Name = "Colour")]
    public Float4 Colour;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Colour.Expression, emitter.Stage(ShaderStageInput.VertexColour));
}

/// <summary>How long the effect has been running.</summary>
[Node("Input/Time", Summary = "Seconds since the shader's clock started.")]
public sealed partial class TimeNode : ShaderNode {
    /// <summary>The time, in seconds.</summary>
    [Output(Name = "Time")]
    public Scalar Time;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Time.Expression, emitter.Uniform("time", "float"));
}

/// <summary>A constant the author types in.</summary>
/// <remarks>
///     Its value lives on the port rather than on the node, which is what lets an inspector edit it
///     with the same code that edits any other unconnected input — and what lets it be replaced by a
///     wire without the node knowing.
/// </remarks>
[Node("Input/Constant", Summary = "A number typed into the graph.")]
public sealed partial class ConstantNode : ShaderNode {
    /// <summary>The value.</summary>
    [Input(Name = "Value")]
    public DynamicVector Value = 0f;

    /// <summary>The same value, as an output.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, Value.Expression);
}

/// <summary>A material property: a colour the material sets rather than the graph.</summary>
[Node("Input/Colour Property", Summary = "A colour the material supplies.")]
public sealed partial class ColourPropertyNode : ShaderNode, IShaderPropertyNode {
    /// <summary>The colour.</summary>
    [Output(Name = "Colour")]
    public Float4 Colour;

    /// <inheritdoc />
    public string PropertyType => "float4";

    /// <inheritdoc />
    public string DefaultProperty => "tint";

    /// <summary>What the property is called. Authored on the node, not wired.</summary>
    /// <remarks>
    ///     Not a port because it is not a value that flows: it names a binding, and a name arriving
    ///     down a wire would be a name that changed per fragment. It is a graph <i>text</i> rather
    ///     than a C# field for <see cref="IShaderPropertyNode" />'s reason — a field would make every
    ///     colour property in every graph the same one.
    /// </remarks>
    public string PropertyName => ShaderProperties.NameOf(this, DefaultProperty);

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Colour.Expression, emitter.Uniform(PropertyName, "float4"));
}

/// <summary>A material property: a number the material sets.</summary>
[Node("Input/Float Property", Summary = "A number the material supplies.")]
public sealed partial class FloatPropertyNode : ShaderNode, IShaderPropertyNode {
    /// <summary>The value.</summary>
    [Output(Name = "Out")]
    public Scalar Out;

    /// <inheritdoc />
    public string PropertyType => "float";

    /// <inheritdoc />
    public string DefaultProperty => "value";

    /// <inheritdoc cref="ColourPropertyNode.PropertyName" />
    public string PropertyName => ShaderProperties.NameOf(this, DefaultProperty);

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Out.Expression, emitter.Uniform(PropertyName, "float"));
}
