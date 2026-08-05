// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>A literal whose value the lexer/binder has already computed.</summary>
internal sealed class BoundLiteralExpression(SyntaxNode syntax, TypeSymbol type, object? value)
    : BoundExpression(syntax) {
    public override BoundKind Kind => BoundKind.LiteralExpression;
    public override TypeSymbol Type { get; } = type;
    public override object? ConstantValue { get; } = value;
}

/// <summary>A reference to a local variable.</summary>
internal sealed class BoundLocalExpression(SyntaxNode syntax, LocalSymbol local) : BoundExpression(syntax) {
    public LocalSymbol Local { get; } = local;
    public override BoundKind Kind => BoundKind.LocalExpression;
    public override TypeSymbol Type => Local.Type;
    public override Symbol Symbol => Local;
}

/// <summary>A reference to a parameter of the enclosing method or lambda.</summary>
internal sealed class BoundParameterExpression(SyntaxNode syntax, ParameterSymbol parameter) : BoundExpression(syntax) {
    public ParameterSymbol Parameter { get; } = parameter;
    public override BoundKind Kind => BoundKind.ParameterExpression;
    public override TypeSymbol Type => Parameter.Type;
    public override Symbol Symbol => Parameter;
}

/// <summary>A field access; <paramref name="receiver" /> is null for statics.</summary>
internal sealed class BoundFieldExpression(SyntaxNode syntax, BoundExpression? receiver, FieldSymbol field)
    : BoundExpression(syntax) {
    public BoundExpression? Receiver { get; } = receiver;
    public FieldSymbol Field { get; } = field;
    public override BoundKind Kind => BoundKind.FieldExpression;
    public override TypeSymbol Type => Field.Type;
    public override object? ConstantValue => Field.ConstantValue;
    public override Symbol Symbol => Field;
    public override IEnumerable<BoundNode> Children => Receiver is null ? [] : [Receiver];
}

/// <summary>A property or indexer access.</summary>
internal sealed class BoundPropertyExpression(
    SyntaxNode syntax,
    BoundExpression? receiver,
    PropertySymbol property,
    IReadOnlyList<BoundExpression> arguments
) : BoundExpression(syntax) {
    public BoundExpression? Receiver { get; } = receiver;
    public PropertySymbol Property { get; } = property;
    public IReadOnlyList<BoundExpression> Arguments { get; } = arguments;
    public override BoundKind Kind => BoundKind.PropertyExpression;
    public override TypeSymbol Type => Property.Type;
    public override Symbol Symbol => Property;

    public override IEnumerable<BoundNode> Children => Receiver is null ? Arguments : [Receiver, .. Arguments];
}

/// <summary><c>self</c>.</summary>
internal sealed class BoundSelfExpression(SyntaxNode syntax, TypeSymbol type) : BoundExpression(syntax) {
    public override BoundKind Kind => BoundKind.SelfExpression;
    public override TypeSymbol Type { get; } = type;
}

/// <summary><c>base</c>.</summary>
internal sealed class BoundBaseExpression(SyntaxNode syntax, TypeSymbol type) : BoundExpression(syntax) {
    public override BoundKind Kind => BoundKind.BaseExpression;
    public override TypeSymbol Type { get; } = type;
}

/// <summary>
///     A type in expression position — the receiver of a static member access, or
///     the callee of a construction such as <c>float4(…)</c>.
/// </summary>
internal sealed class BoundTypeExpression(SyntaxNode syntax, TypeSymbol type) : BoundExpression(syntax) {
    public TypeSymbol ReferencedType { get; } = type;
    public override BoundKind Kind => BoundKind.TypeExpression;
    public override TypeSymbol Type => ReferencedType;
    public override Symbol Symbol => ReferencedType;
}

/// <summary>A namespace in expression position; only ever a qualifier.</summary>
internal sealed class BoundNamespaceExpression(SyntaxNode syntax, NamespaceSymbol @namespace)
    : BoundExpression(syntax) {
    public NamespaceSymbol Namespace { get; } = @namespace;
    public override BoundKind Kind => BoundKind.NamespaceExpression;
    public override TypeSymbol Type => ErrorTypeSymbol.Instance;
    public override Symbol Symbol => Namespace;
}

/// <summary>
///     An unresolved set of same-named methods, produced when a method name appears
///     outside a call. Overload resolution turns it into a
///     <see cref="BoundInvocationExpression" />.
/// </summary>
internal sealed class BoundMethodGroupExpression(
    SyntaxNode syntax,
    BoundExpression? receiver,
    IReadOnlyList<MethodSymbol> methods,
    IReadOnlyList<TypeSymbol> typeArguments
) : BoundExpression(syntax) {
    public BoundExpression? Receiver { get; } = receiver;
    public IReadOnlyList<MethodSymbol> Methods { get; } = methods;
    public IReadOnlyList<TypeSymbol> TypeArguments { get; } = typeArguments;
    public override BoundKind Kind => BoundKind.MethodGroupExpression;
    public override TypeSymbol Type => ErrorTypeSymbol.Instance;
    public override Symbol? Symbol => Methods.Count == 1 ? Methods[0] : null;
    public override IEnumerable<BoundNode> Children => Receiver is null ? [] : [Receiver];
}

/// <summary>A resolved call. Arguments are already converted to the parameter types.</summary>
internal sealed class BoundInvocationExpression(
    SyntaxNode syntax,
    BoundExpression? receiver,
    MethodSymbol method,
    IReadOnlyList<BoundExpression> arguments
) : BoundExpression(syntax) {
    public BoundExpression? Receiver { get; } = receiver;
    public MethodSymbol Method { get; } = method;
    public IReadOnlyList<BoundExpression> Arguments { get; } = arguments;
    public override BoundKind Kind => BoundKind.InvocationExpression;
    public override TypeSymbol Type => Method.ReturnType;
    public override Symbol Symbol => Method;

    public override IEnumerable<BoundNode> Children => Receiver is null ? Arguments : [Receiver, .. Arguments];
}

/// <summary>
///     Construction of a value: <c>float3(1, 2, 3)</c>, <c>mat3(…)</c>, or a
///     user-defined type's <c>init</c>. <see cref="Constructor" /> is null for
///     built-in vector/matrix construction.
/// </summary>
internal sealed class BoundObjectCreationExpression(
    SyntaxNode syntax,
    TypeSymbol type,
    MethodSymbol? constructor,
    IReadOnlyList<BoundExpression> arguments
) : BoundExpression(syntax) {
    public MethodSymbol? Constructor { get; } = constructor;
    public IReadOnlyList<BoundExpression> Arguments { get; } = arguments;
    public override BoundKind Kind => BoundKind.ObjectCreationExpression;
    public override TypeSymbol Type { get; } = type;
    public override Symbol? Symbol => Constructor;
    public override IEnumerable<BoundNode> Children => Arguments;
}

/// <summary>An inserted conversion. Every representation change is explicit in the bound tree.</summary>
internal sealed class BoundConversionExpression(
    SyntaxNode syntax,
    BoundExpression operand,
    TypeSymbol type,
    Conversion conversion
) : BoundExpression(syntax) {
    public BoundExpression Operand { get; } = operand;
    public Conversion Conversion { get; } = conversion;
    public override BoundKind Kind => BoundKind.ConversionExpression;
    public override TypeSymbol Type { get; } = type;
    public override object? ConstantValue => Operand.ConstantValue;
    public override IEnumerable<BoundNode> Children => [Operand];
}

internal sealed class BoundUnaryExpression(
    SyntaxNode syntax,
    UnaryOperatorKind operatorKind,
    BoundExpression operand,
    TypeSymbol type
) : BoundExpression(syntax) {
    public UnaryOperatorKind OperatorKind { get; } = operatorKind;
    public BoundExpression Operand { get; } = operand;
    public override BoundKind Kind => BoundKind.UnaryExpression;
    public override TypeSymbol Type { get; } = type;
    public override IEnumerable<BoundNode> Children => [Operand];
}

internal sealed class BoundBinaryExpression(
    SyntaxNode syntax,
    BinaryOperatorKind operatorKind,
    BoundExpression left,
    BoundExpression right,
    TypeSymbol type
) : BoundExpression(syntax) {
    public BinaryOperatorKind OperatorKind { get; } = operatorKind;
    public BoundExpression Left { get; } = left;
    public BoundExpression Right { get; } = right;
    public override BoundKind Kind => BoundKind.BinaryExpression;
    public override TypeSymbol Type { get; } = type;
    public override IEnumerable<BoundNode> Children => [Left, Right];
}

/// <summary>
///     An assignment. Compound forms (<c>x += y</c>) carry the underlying
///     <see cref="OperatorKind" />; a simple assignment leaves it null.
/// </summary>
internal sealed class BoundAssignmentExpression(
    SyntaxNode syntax,
    BoundExpression target,
    BoundExpression value,
    BinaryOperatorKind? operatorKind
) : BoundExpression(syntax) {
    public BoundExpression Target { get; } = target;
    public BoundExpression Value { get; } = value;
    public BinaryOperatorKind? OperatorKind { get; } = operatorKind;
    public override BoundKind Kind => BoundKind.AssignmentExpression;
    public override TypeSymbol Type => Target.Type;
    public override IEnumerable<BoundNode> Children => [Target, Value];
}

internal sealed class BoundConditionalExpression(
    SyntaxNode syntax,
    BoundExpression condition,
    BoundExpression whenTrue,
    BoundExpression whenFalse,
    TypeSymbol type
) : BoundExpression(syntax) {
    public BoundExpression Condition { get; } = condition;
    public BoundExpression WhenTrue { get; } = whenTrue;
    public BoundExpression WhenFalse { get; } = whenFalse;
    public override BoundKind Kind => BoundKind.ConditionalExpression;
    public override TypeSymbol Type { get; } = type;
    public override IEnumerable<BoundNode> Children => [Condition, WhenTrue, WhenFalse];
}

/// <summary>Indexing into an array or a vector/matrix.</summary>
internal sealed class BoundArrayAccessExpression(
    SyntaxNode syntax,
    BoundExpression receiver,
    IReadOnlyList<BoundExpression> indices,
    TypeSymbol type
) : BoundExpression(syntax) {
    public BoundExpression Receiver { get; } = receiver;
    public IReadOnlyList<BoundExpression> Indices { get; } = indices;
    public override BoundKind Kind => BoundKind.ArrayAccessExpression;
    public override TypeSymbol Type { get; } = type;
    public override IEnumerable<BoundNode> Children => [Receiver, .. Indices];
}

/// <summary><c>a..b</c>.</summary>
internal sealed class BoundRangeExpression(
    SyntaxNode syntax,
    BoundExpression? left,
    BoundExpression? right,
    TypeSymbol type
) : BoundExpression(syntax) {
    public BoundExpression? Left { get; } = left;
    public BoundExpression? Right { get; } = right;
    public override BoundKind Kind => BoundKind.RangeExpression;
    public override TypeSymbol Type { get; } = type;

    public override IEnumerable<BoundNode> Children {
        get {
            if (Left is not null) {
                yield return Left;
            }

            if (Right is not null) {
                yield return Right;
            }
        }
    }
}

internal sealed class BoundTupleExpression(
    SyntaxNode syntax,
    IReadOnlyList<BoundExpression> elements,
    TypeSymbol type
) : BoundExpression(syntax) {
    public IReadOnlyList<BoundExpression> Elements { get; } = elements;
    public override BoundKind Kind => BoundKind.TupleExpression;
    public override TypeSymbol Type { get; } = type;
    public override IEnumerable<BoundNode> Children => Elements;
}

/// <summary>
///     One entry of a collection expression. <paramref name="IsSpread" /> is recorded rather than
///     inferred from the type: a spread of <c>int[]</c> into an <c>int[]</c> is indistinguishable
///     from an element by type alone once <c>int[][]</c> exists, and lowering has to be able to
///     tell them apart — one contributes itself, the other contributes its elements.
/// </summary>
internal sealed record BoundCollectionElement(BoundExpression Expression, bool IsSpread);

/// <summary><c>[a, b, ..c]</c> — bound as an array of the elements' common type.</summary>
internal sealed class BoundCollectionExpression(
    SyntaxNode syntax,
    IReadOnlyList<BoundCollectionElement> elements,
    TypeSymbol type
) : BoundExpression(syntax) {
    public IReadOnlyList<BoundCollectionElement> Elements { get; } = elements;

    public override BoundKind Kind => BoundKind.CollectionExpression;
    public override TypeSymbol Type { get; } = type;
    public override IEnumerable<BoundNode> Children => Elements.Select(e => e.Expression);
}

/// <summary>
///     Stands in for an expression that failed to bind. It carries any operands that
///     did bind, so the semantic model still answers questions about them.
/// </summary>
internal sealed class BoundErrorExpression(SyntaxNode syntax, IReadOnlyList<BoundExpression>? operands = null)
    : BoundExpression(syntax) {
    public IReadOnlyList<BoundExpression> Operands { get; } = operands ?? [];
    public override BoundKind Kind => BoundKind.ErrorExpression;
    public override TypeSymbol Type => ErrorTypeSymbol.Instance;
    public override IEnumerable<BoundNode> Children => Operands;
}
