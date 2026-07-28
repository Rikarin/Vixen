// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph.Nodes;

/// <summary>Two values added.</summary>
/// <remarks>
///     <b>The shape every binary maths node has.</b> Two dynamic inputs and a dynamic output, so one
///     node works on floats, on colours and on positions — which is what
///     <see cref="PortKind.Dynamic" /> exists for, and the reason there is not an <c>AddFloat3</c>
///     next to this.
/// </remarks>
[Node("Math/Add", Preview = true, Summary = "A + B.")]
public sealed partial class AddNode : ShaderNode {
    /// <summary>The first.</summary>
    [Input]
    public DynamicVector A;

    /// <summary>The second.</summary>
    [Input]
    public DynamicVector B;

    /// <summary>Their sum.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"{A} + {B}");
}

/// <summary>One value taken from another.</summary>
[Node("Math/Subtract", Preview = true, Summary = "A - B.")]
public sealed partial class SubtractNode : ShaderNode {
    /// <summary>The first.</summary>
    [Input]
    public DynamicVector A;

    /// <summary>The second.</summary>
    [Input]
    public DynamicVector B;

    /// <summary>Their difference.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"{A} - {B}");
}

/// <summary>Two values multiplied, component by component.</summary>
[Node("Math/Multiply", Preview = true, Summary = "A * B, component by component.")]
public sealed partial class MultiplyNode : ShaderNode {
    /// <summary>The first.</summary>
    [Input]
    public DynamicVector A = 1f;

    /// <summary>The second.</summary>
    [Input]
    public DynamicVector B = 1f;

    /// <summary>Their product.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"{A} * {B}");
}

/// <summary>One value divided by another.</summary>
[Node("Math/Divide", Summary = "A / B.")]
public sealed partial class DivideNode : ShaderNode {
    /// <summary>The numerator.</summary>
    [Input]
    public DynamicVector A;

    /// <summary>The denominator.</summary>
    [Input]
    public DynamicVector B = 1f;

    /// <summary>The quotient.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"{A} / {B}");
}

/// <summary>A blend between two values.</summary>
[Node("Math/Lerp", Preview = true, Summary = "A at T = 0, B at T = 1.")]
public sealed partial class LerpNode : ShaderNode {
    /// <summary>The value at zero.</summary>
    [Input]
    public DynamicVector A;

    /// <summary>The value at one.</summary>
    [Input]
    public DynamicVector B = 1f;

    /// <summary>How far between them.</summary>
    [Input]
    public Scalar T = 0.5f;

    /// <summary>The blend.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"lerp({A}, {B}, {T})");
}

/// <summary>A value held between zero and one.</summary>
[Node("Math/Saturate", Summary = "Clamped to 0..1.")]
public sealed partial class SaturateNode : ShaderNode {
    /// <summary>The value.</summary>
    [Input(Name = "In")]
    public DynamicVector In;

    /// <summary>The clamped value.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"saturate({In})");
}

/// <summary>One minus a value.</summary>
[Node("Math/One Minus", Summary = "1 - In. What an author reaches for to invert a mask.")]
public sealed partial class OneMinusNode : ShaderNode {
    /// <summary>The value.</summary>
    [Input(Name = "In")]
    public DynamicVector In;

    /// <summary>Its complement.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"1f - {In}");
}

/// <summary>A value raised to a power.</summary>
[Node("Math/Power", Summary = "A raised to B.")]
public sealed partial class PowerNode : ShaderNode {
    /// <summary>The base.</summary>
    [Input]
    public DynamicVector A;

    /// <summary>The exponent.</summary>
    [Input]
    public Scalar B = 2f;

    /// <summary>The power.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"pow({A}, {B})");
}

/// <summary>The absolute value.</summary>
[Node("Math/Absolute", Summary = "Its magnitude, component by component.")]
public sealed partial class AbsoluteNode : ShaderNode {
    /// <summary>The value.</summary>
    [Input(Name = "In")]
    public DynamicVector In;

    /// <summary>Its magnitude.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"abs({In})");
}

/// <summary>The fractional part.</summary>
[Node("Math/Fraction", Summary = "What is left after the whole part. What tiles a coordinate.")]
public sealed partial class FractionNode : ShaderNode {
    /// <summary>The value.</summary>
    [Input(Name = "In")]
    public DynamicVector In;

    /// <summary>Its fractional part.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"frac({In})");
}

/// <summary>A sine.</summary>
[Node("Math/Sine", Summary = "sin(In), in radians.")]
public sealed partial class SineNode : ShaderNode {
    /// <summary>The angle.</summary>
    [Input(Name = "In")]
    public DynamicVector In;

    /// <summary>Its sine.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"sin({In})");
}

/// <summary>A smooth ramp between two edges.</summary>
[Node("Math/Smoothstep", Summary = "0 below Edge0, 1 above Edge1, an S-curve between.")]
public sealed partial class SmoothstepNode : ShaderNode {
    /// <summary>Where the ramp starts.</summary>
    [Input(Name = "Edge0")]
    public Scalar Edge0;

    /// <summary>Where it ends.</summary>
    [Input(Name = "Edge1")]
    public Scalar Edge1 = 1f;

    /// <summary>The value.</summary>
    [Input(Name = "In")]
    public Scalar In;

    /// <summary>The ramp.</summary>
    [Output(Name = "Out")]
    public Scalar Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) =>
        emitter.Assign(Out.Expression, $"smoothstep({Edge0}, {Edge1}, {In})");
}

/// <summary>The dot product.</summary>
[Node("Math/Dot", Summary = "A · B, which is a scalar however wide they are.")]
public sealed partial class DotNode : ShaderNode {
    /// <summary>The first.</summary>
    [Input]
    public DynamicVector A;

    /// <summary>The second.</summary>
    [Input]
    public DynamicVector B;

    /// <summary>Their dot product.</summary>
    [Output(Name = "Out")]
    public Scalar Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"dot({A}, {B})");
}

/// <summary>A vector of unit length.</summary>
[Node("Math/Normalize", Summary = "The same direction, one unit long.")]
public sealed partial class NormalizeNode : ShaderNode {
    /// <summary>The vector.</summary>
    [Input(Name = "In")]
    public DynamicVector In;

    /// <summary>The unit vector.</summary>
    [Output(Name = "Out")]
    public DynamicVector Out;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) => emitter.Assign(Out.Expression, $"normalize({In})");
}
