// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph.Nodes;

/// <summary>Four numbers made into a vector.</summary>
/// <remarks>
///     The escape hatch from dynamic typing, and the reason it is not needed often: everything else
///     widens and narrows on its own, and this is for the case where an author means a specific
///     arrangement of specific components.
/// </remarks>
[Node("Vector/Combine", Summary = "Four numbers into a float4.")]
public sealed partial class CombineNode : ShaderNode {
    /// <summary>The first component.</summary>
    [Input(Name = "X")]
    public Scalar X;

    /// <summary>The second.</summary>
    [Input(Name = "Y")]
    public Scalar Y;

    /// <summary>The third.</summary>
    [Input(Name = "Z")]
    public Scalar Z;

    /// <summary>The fourth.</summary>
    [Input(Name = "W")]
    public Scalar W = 1f;

    /// <summary>The vector.</summary>
    [Output(Name = "Out")]
    public Float4 Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Out.Expression, $"float4({X}, {Y}, {Z}, {W})");
}

/// <summary>A vector taken apart.</summary>
/// <remarks>
///     Emits one statement and four swizzles of it rather than four statements, because the input may
///     be an expression with a call in it and evaluating it four times is four calls.
/// </remarks>
[Node("Vector/Split", Summary = "A float4 into its four components.")]
public sealed partial class SplitNode : ShaderNode {
    /// <summary>The vector.</summary>
    [Input(Name = "In")]
    public Float4 In;

    /// <summary>Its first component.</summary>
    [Output(Name = "X")]
    public Scalar X;

    /// <summary>Its second.</summary>
    [Output(Name = "Y")]
    public Scalar Y;

    /// <summary>Its third.</summary>
    [Output(Name = "Z")]
    public Scalar Z;

    /// <summary>Its fourth.</summary>
    [Output(Name = "W")]
    public Scalar W;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        var whole = X.Expression + "_split";

        emitter.Assign(whole, In.Expression);
        emitter.Assign(X.Expression, whole + ".x");
        emitter.Assign(Y.Expression, whole + ".y");
        emitter.Assign(Z.Expression, whole + ".z");
        emitter.Assign(W.Expression, whole + ".w");
    }
}

/// <summary>A coordinate tiled and shifted.</summary>
/// <remarks>
///     The single most-used node in any shader graph, which is why it is one node rather than a
///     multiply and an add an author wires up every time.
/// </remarks>
[Node("Vector/Tiling and Offset", Preview = true, Summary = "UV * Tiling + Offset.")]
public sealed partial class TilingAndOffsetNode : ShaderNode {
    /// <summary>The coordinate.</summary>
    [Input(Name = "UV")]
    public Float2 Uv;

    /// <summary>How many times to repeat.</summary>
    [Input(Name = "Tiling", Default = [1f, 1f])]
    public Float2 Tiling;

    /// <summary>How far to shift.</summary>
    [Input(Name = "Offset", Default = [0f, 0f])]
    public Float2 Offset;

    /// <summary>The transformed coordinate.</summary>
    [Output(Name = "Out")]
    public Float2 Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Out.Expression, $"{Uv} * {Tiling} + {Offset}");
}
