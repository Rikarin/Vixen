// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph.Nodes;

/// <summary>A texture read at a coordinate.</summary>
/// <remarks>
///     <para>
///         <b>The texture and its sampler are one property, not two ports.</b> A graph where an author
///         wires a sampler is a graph where an author can wire the wrong one, and every real material
///         wants the sampler that belongs to the texture. The declaration is <c>{name}</c> and
///         <c>{name}Sampler</c>, which is the convention the hand-written library shaders already use.
///     </para>
///     <para>
///         The coordinate defaults to the mesh's own, because that is what an author dropping a
///         texture node into an empty graph means — and it is the reason
///         <see cref="RavenEmitter.Stage" /> exists rather than a UV node being compulsory.
///     </para>
/// </remarks>
[Node("Texture/Sample 2D", Preview = true, Summary = "Reads a texture at a coordinate.")]
public sealed partial class SampleTexture2DNode : ShaderNode, IShaderPropertyNode {
    /// <summary>Where to read. Defaults to the mesh's own coordinate.</summary>
    [Input(Name = "UV")]
    public Float2 Uv;

    /// <summary>What was read.</summary>
    [Output(Name = "RGBA")]
    public Float4 Rgba;

    /// <inheritdoc />
    public string PropertyType => "Texture2D";

    /// <inheritdoc />
    public string DefaultProperty => "albedo";

    /// <inheritdoc />
    /// <remarks>
    ///     Read off the graph rather than set on the node, so that a graph with a base map and a
    ///     normal map in it is two textures rather than one sampled twice — see
    ///     <see cref="IShaderPropertyNode" />.
    /// </remarks>
    public string PropertyName => ShaderProperties.NameOf(this, DefaultProperty);

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        var texture = emitter.Uniform(PropertyName, "Texture2D");
        var sampler = emitter.Uniform(PropertyName + "Sampler", "Sampler");

        // An unconnected UV port carries the literal its default made, which is not a coordinate. The
        // node asks for the stage's own instead, which is what an author who did not wire one means.
        var coordinate = Binding.IsConnected("UV") ? Uv.Expression : emitter.Stage(ShaderStageInput.Uv);

        emitter.Assign(Rgba.Expression, $"{texture}.Sample({sampler}, {coordinate})");
    }
}
