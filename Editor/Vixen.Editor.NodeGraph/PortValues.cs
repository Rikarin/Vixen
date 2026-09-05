// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Editor.NodeGraph;

/// <summary>
///     One port's value, during compilation: the text that reads it in the emitted source.
/// </summary>
/// <remarks>
///     <para>
///         <b>A port field holds an expression, not a number.</b> A node's <c>Emit</c> is a line of
///         target source with its ports interpolated into it — <c>$"{Result} = mix({A}, {B}, {T})"</c>
///         — so what a port has to be is something that stringifies to whatever names it in that
///         source. For a connected input that is the variable the upstream node wrote; for an
///         unconnected one it is a literal; for an output it is a fresh variable name.
///     </para>
///     <para>
///         <b>The declared type is the port's kind.</b> That is the whole reason there are several of
///         these rather than one: the generator reads the field's type to know what the port carries,
///         and a node author reads it to know what they are holding. They are otherwise identical,
///         which is what <see cref="IPortValue" /> says.
///     </para>
/// </remarks>
public interface IPortValue {
    /// <summary>The text that reads this port in the emitted source.</summary>
    string Expression { get; }
}

/// <summary>One float.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Scalar(string Expression) : IPortValue {
    /// <summary>A literal, so that a node's field initializer compiles.</summary>
    /// <remarks>
    ///     <b>The value here is not what becomes the port's default.</b> The generator reads the
    ///     initializer from the source text and records the constant in the port's definition, because
    ///     that is the only way a default can reach a create-menu or an inspector that never
    ///     instantiates the node. This conversion exists so <c>[Input] public Scalar T = 0.5f;</c> is
    ///     legal C#, and the expression it produces is overwritten before anything reads it.
    /// </remarks>
    public static implicit operator Scalar(float value) => new(Literal.Of(value));

    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>Two floats.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Float2(string Expression) : IPortValue {
    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>Three floats.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Float3(string Expression) : IPortValue {
    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>Four floats.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Float4(string Expression) : IPortValue {
    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>A vector whose width is whatever it is connected to.</summary>
/// <param name="Expression">The text that reads it.</param>
/// <remarks>
///     Its width is not knowable from the field, which is the point of it — see
///     <see cref="PortKind.Dynamic" />. A node that needs to know asks the binding it was given.
/// </remarks>
public readonly record struct DynamicVector(string Expression) : IPortValue {
    /// <summary>A literal, so that a node's field initializer compiles. See <see cref="Scalar" />.</summary>
    public static implicit operator DynamicVector(float value) => new(Literal.Of(value));

    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>A boolean.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Bool(string Expression) : IPortValue {
    /// <summary>A literal, so that a node's field initializer compiles. See <see cref="Scalar" />.</summary>
    public static implicit operator Bool(bool value) => new(value ? "true" : "false");

    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>A 32-bit signed integer.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Int(string Expression) : IPortValue {
    /// <summary>A literal, so that a node's field initializer compiles. See <see cref="Scalar" />.</summary>
    public static implicit operator Int(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>A 2D texture.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Texture(string Expression) : IPortValue {
    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>A sampler.</summary>
/// <param name="Expression">The text that reads it.</param>
public readonly record struct Sampler(string Expression) : IPortValue {
    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>A whole raster. See <see cref="PortKind.Image" />.</summary>
/// <param name="Expression">The text that reads it.</param>
/// <remarks>
///     ⚠ <b>Still an expression, even though nothing samples it in a line of shader source.</b> A
///     texture graph's compiler emits a dispatch per node rather than an expression per node, so what
///     this names is the intermediate the upstream kernel wrote — which is exactly what the pool
///     allocator hands out and frees. Making it something other than a string would make it the one
///     port field with a different shape, for a target that has not been written yet.
/// </remarks>
public readonly record struct Image(string Expression) : IPortValue {
    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>No value: an edge that means "after". See <see cref="PortKind.Flow" />.</summary>
/// <param name="Expression">Always empty. A flow port carries nothing to read.</param>
public readonly record struct Flow(string Expression) : IPortValue {
    /// <inheritdoc />
    public override string ToString() => Expression;
}

/// <summary>How a constant is spelled when a compiler has to write one.</summary>
/// <remarks>
///     <para>
///         Round-trip format and the invariant culture, both for the same reason they are used in
///         <c>VfxShaderEmitter</c>: a shortest-round-trip literal reads back as the same float, and a
///         machine whose decimal separator is a comma must not emit source no compiler will parse.
///     </para>
///     <para>
///         <b>No type suffix, and no invented decimal point.</b> Whether a float literal is written
///         <c>2f</c>, <c>2.0</c> or <c>2.0f</c> is the target language's business, and this is shared
///         by every target. Padding <c>2</c> out to <c>2.0</c> here would put a decimal point into
///         source that a target then suffixes, and <c>2.0f</c> is not what any hand-written shader in
///         this repository looks like.
///     </para>
/// </remarks>
public static class Literal {
    /// <summary>A float, as digits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Its digits, without a suffix.</returns>
    public static string Of(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);
}
