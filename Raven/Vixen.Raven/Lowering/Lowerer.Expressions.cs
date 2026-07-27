// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Lowering;

/// <summary>
///     Expression lowering. Every expression becomes a sequence of instructions
///     ending in one value; nothing stays implicit.
/// </summary>
public sealed partial class Lowerer {
    /// <summary>Lowers an expression and returns the value it produces.</summary>
    IrValue LowerExpression(BoundExpression expression) {
        var type = LowerType(expression.Type, expression.Syntax);

        switch (expression) {
            case BoundLiteralExpression literal:
                return Constant(type, literal.ConstantValue);

            case BoundConversionExpression conversion:
                return LowerConversion(conversion, type);

            case BoundLocalExpression
                or BoundParameterExpression
                or BoundFieldExpression
                or BoundArrayAccessExpression:
                return LowerAccess(expression, type);

            case BoundSelfExpression:
                if (SelfPlace is not { } self) {
                    ReportUnsupported(expression, "'self' as a value outside a struct method");
                    return Constant(type, null);
                }

                return Load(self);

            case BoundUnaryExpression unary:
                return LowerUnary(unary, type);

            case BoundBinaryExpression binary:
                return LowerBinary(binary, type);

            case BoundAssignmentExpression assignment:
                return LowerAssignment(assignment);

            case BoundInvocationExpression invocation:
                return LowerInvocation(invocation, type);

            case BoundObjectCreationExpression creation:
                return LowerObjectCreation(creation, type);

            case BoundPropertyExpression property:
                return LowerPropertyGet(property, type);

            case BoundConditionalExpression conditional: {
                // Only one arm runs when running the other one would be unsafe or observable;
                // otherwise a select is the cheaper, branch-free form. Same rule as `&&`.
                if (NeedsGuarding(conditional.WhenTrue) || NeedsGuarding(conditional.WhenFalse)) {
                    return LowerGuardedConditional(conditional, type);
                }

                var condition = LowerExpression(conditional.Condition);
                var whenTrue = LowerExpression(conditional.WhenTrue);
                var whenFalse = LowerExpression(conditional.WhenFalse);
                return Emit(result => new IrSelectInstruction(result, condition, whenTrue, whenFalse), type);
            }

            case BoundTupleExpression tuple: {
                var elements = tuple.Elements.Select(LowerExpression).ToArray();
                return Emit(result => new IrConstructInstruction(result, elements), type);
            }

            case BoundCollectionExpression collection:
                return LowerCollection(collection, type);

            case BoundErrorExpression:
                // Already reported by the binder.
                return Constant(type, null);

            default:
                ReportUnsupported(expression, Describe(expression));
                return Constant(type, null);
        }
    }

    /// <summary>
    ///     Lowers <c>[a, ..b, c]</c> to one construct of the flattened elements.
    /// </summary>
    /// <remarks>
    ///     A spread contributes its own elements rather than itself, so it is expanded into one
    ///     extract per index — which needs its length, and is exactly what a sized array now
    ///     carries. An <em>unsized</em> spread still cannot be flattened, and is refused by name
    ///     rather than lowered: the operand is the array itself, which would build an
    ///     <c>array&lt;i32&gt;</c> operand where the construct wants an <c>i32</c>, leaving only
    ///     the IR verifier between that and a backend.
    /// </remarks>
    IrValue LowerCollection(BoundCollectionExpression collection, IrType type) {
        List<IrValue> elements = [];

        foreach (var (expression, isSpread) in collection.Elements) {
            if (!isSpread) {
                elements.Add(LowerExpression(expression));
                continue;
            }

            if (expression.Type is not ArrayTypeSymbol { Length: { } length }) {
                ReportUnsupported(
                    expression,
                    "A spread of an unsized array — the number of elements it contributes is not "
                    + "known, so it"
                );
                continue;
            }

            // Lowered once and indexed, not re-lowered per element: the operand may be a call.
            var source = LowerExpression(expression);
            var elementType = LowerType(((ArrayTypeSymbol)expression.Type).ElementType, expression.Syntax);

            for (var i = 0; i < length; i++) {
                var index = Constant(IrScalarType.Int, i);
                elements.Add(
                    Emit(
                        result => new IrExtractInstruction(result, source, [new IrIndexAccess(index)]),
                        elementType
                    )
                );
            }
        }

        return Emit(result => new IrConstructInstruction(result, [.. elements]), type);
    }

    /// <summary>Lowers an expression whose value is discarded.</summary>
    void LowerExpressionForEffect(BoundExpression expression) {
        // A call statement must not leave a dangling value behind, so it is
        // lowered through the path that allows "no result".
        if (expression is BoundInvocationExpression invocation) {
            LowerCall(invocation);
            return;
        }

        LowerExpression(expression);
    }

    static string Describe(BoundExpression expression) =>
        expression switch {
            BoundRangeExpression => "A range outside a 'for' loop",
            BoundTypeExpression => "A type used as a value",
            _ => "This expression"
        };

    // --- Places and access -------------------------------------------------

    /// <summary>
    ///     The storage an expression designates, or null when it has none — the
    ///     result of a call, for instance.
    /// </summary>
    IrPlace? TryGetPlace(BoundExpression expression) {
        switch (expression) {
            case BoundLocalExpression local:
                return variables.TryGetValue(local.Local, out var localVariable) ? new IrPlace(localVariable) : null;

            case BoundParameterExpression parameter:
                return variables.TryGetValue(parameter.Parameter, out var parameterVariable)
                    ? new IrPlace(parameterVariable)
                    : null;

            case BoundFieldExpression field:
                return TryGetFieldPlace(field);

            case BoundArrayAccessExpression access when access.Indices.Count == 1: {
                if (TryGetPlace(access.Receiver) is not { } target) {
                    return null;
                }

                var index = LowerExpression(access.Indices[0]);
                return target.With(new IrIndexAccess(index));
            }

            default:
                return null;
        }
    }

    IrPlace? TryGetFieldPlace(BoundFieldExpression expression) {
        var field = expression.Field;

        // A vector swizzle is an access chain step, not real storage of its own.
        if (IsSwizzle(field, out var components)) {
            return expression.Receiver is { } swizzled && TryGetPlace(swizzled) is { } place
                ? place.With(new IrSwizzleAccess(components))
                : null;
        }

        // A shader's fields are globals; there is no receiver at runtime.
        if (globals.TryGetValue(field, out var global)) {
            return new(global);
        }

        if (StructOf(field) is not { } structType) {
            return null;
        }

        var index = structType.IndexOf(field.Name);
        if (index < 0) {
            return null;
        }

        var basePlace = expression.Receiver is BoundSelfExpression && SelfPlace is { } self
            ? self
            // A constructor's field writes target the value it is building.
            : expression.Receiver is { } receiver
                ? TryGetPlace(receiver)
                : SelfPlace;

        return basePlace?.With(new IrFieldAccess(index));
    }

    /// <summary>True when the field is a synthesized vector swizzle such as <c>v.xy</c>.</summary>
    static bool IsSwizzle(FieldSymbol field, out int[] components) {
        components = [];

        if (field is not SynthesizedFieldSymbol
            || field.ContainingSymbol is not PrimitiveTypeSymbol { TypeKind: TypeKind.Vector }) {
            return false;
        }

        const string xyzw = "xyzw";
        const string rgba = "rgba";

        var set = xyzw.Contains(field.Name[0]) ? xyzw : rgba;
        var indices = new int[field.Name.Length];

        for (var i = 0; i < field.Name.Length; i++) {
            var index = set.IndexOf(field.Name[i]);
            if (index < 0) {
                return false;
            }

            indices[i] = index;
        }

        components = indices;
        return true;
    }

    IrValue LowerAccess(BoundExpression expression, IrType type) {
        // A `const` field is folded rather than loaded.
        if (expression is BoundFieldExpression { Field: { IsConst: true, ConstantValue: not null } constant }) {
            return Constant(type, constant.ConstantValue);
        }

        // `buffer.Length` is an operation on the *place*, not on a value: an unsized array cannot be
        // loaded, so there is nothing to hand an intrinsic. A sized array never reaches here — its
        // `Length` is a constant and the fold above already took it.
        if (expression is BoundFieldExpression { Field.Name: "Length", Receiver: { } lengthReceiver }
            && lengthReceiver.Type is ArrayTypeSymbol or BufferTypeSymbol) {
            if (TryGetPlace(lengthReceiver) is not { } source) {
                ReportUnsupported(lengthReceiver, "The length of an array with no storage");
                return Constant(IrScalarType.Int, 0);
            }

            return Emit(result => new IrArrayLengthInstruction(result, source), IrScalarType.Int);
        }

        if (TryGetPlace(expression) is { } place) {
            return Load(place);
        }

        // No storage: read the part out of the composite value instead.
        return expression switch {
            BoundFieldExpression field when field.Receiver is { } receiver && ExtractChain(field) is { } chain =>
                Emit(result => new IrExtractInstruction(result, LowerExpression(receiver), chain), type),
            BoundArrayAccessExpression access => Emit(
                result => new IrExtractInstruction(
                    result,
                    LowerExpression(access.Receiver),
                    [new IrIndexAccess(LowerExpression(access.Indices[0]))]
                ),
                type
            ),
            _ => Constant(type, null)
        };
    }

    IReadOnlyList<IrAccess>? ExtractChain(BoundFieldExpression expression) {
        if (IsSwizzle(expression.Field, out var components)) {
            return [new IrSwizzleAccess(components)];
        }

        if (StructOf(expression.Field) is { } structType
            && structType.IndexOf(expression.Field.Name) is var index and >= 0) {
            return [new IrFieldAccess(index)];
        }

        return null;
    }

    /// <summary>
    ///     The IR struct a field belongs to, whether it was declared on a struct or synthesized for
    ///     a tuple element.
    /// </summary>
    /// <remarks>
    ///     Read off <c>ContainingSymbol</c> rather than <c>ContainingType</c>: the latter is a
    ///     <c>NamedTypeSymbol</c>, and a tuple is not one, so it answers null for a tuple's element.
    /// </remarks>
    /// <summary>The struct a field lives in, or null when it lives in none.</summary>
    /// <remarks>
    ///     While an instantiation is being lowered, a field of the open definition belongs to the
    ///     instantiation's struct: the body names <c>Box&lt;T&gt;.value</c> because that is what was
    ///     bound, and there is no struct for <c>Box&lt;T&gt;</c> — only for <c>Box&lt;float4&gt;</c>.
    /// </remarks>
    IrStructType? StructOf(FieldSymbol field) =>
        field.ContainingSymbol switch {
            NamedTypeSymbol named when currentInstantiation is { } instantiation
                && named.Equals(instantiation.OriginalDefinition) => structs.GetValueOrDefault(instantiation),
            NamedTypeSymbol named => structs.GetValueOrDefault(named),
            TupleTypeSymbol tuple => tuples.GetValueOrDefault(tuple),
            _ => null
        };

    // --- Operators ---------------------------------------------------------

    IrValue LowerConversion(BoundConversionExpression conversion, IrType type) {
        var sourceType = LowerType(conversion.Operand.Type, conversion.Syntax);

        // A literal takes the target's shape directly rather than being
        // converted at runtime.
        if (conversion.Operand.ConstantValue is { } constant && type.IsScalar) {
            return Constant(type, constant);
        }

        var operand = LowerExpression(conversion.Operand);

        if (sourceType.Equals(type)) {
            return operand;
        }

        var kind = conversion.Conversion.Kind switch {
            ConversionKind.ImplicitSplat => IrConversionKind.Splat,
            ConversionKind.ImplicitNumeric
                or ConversionKind.ExplicitNumeric
                or ConversionKind.ImplicitConstant
                or ConversionKind.Identity => IrConversionKind.Numeric,
            _ => (IrConversionKind?)null
        };

        if (kind is null) {
            ReportUnsupported(conversion, $"A '{conversion.Conversion.Kind}' conversion");
            return operand;
        }

        // A scalar widened into a vector or matrix is a broadcast either way.
        if (sourceType.IsScalar && type.Kind is IrTypeKind.Vector or IrTypeKind.Matrix) {
            kind = IrConversionKind.Splat;
        }

        return Emit(result => new IrConvertInstruction(result, kind.Value, operand), type);
    }

    IrValue LowerUnary(BoundUnaryExpression unary, IrType type) {
        switch (unary.OperatorKind) {
            case UnaryOperatorKind.Plus:
                return LowerExpression(unary.Operand);

            case UnaryOperatorKind.Minus:
                return EmitUnary(IrUnaryOp.Negate, unary.Operand, type);

            case UnaryOperatorKind.LogicalNot:
                return EmitUnary(IrUnaryOp.Not, unary.Operand, type);

            case UnaryOperatorKind.BitwiseNot:
                return EmitUnary(IrUnaryOp.BitwiseNot, unary.Operand, type);

            case UnaryOperatorKind.PreIncrement
                or UnaryOperatorKind.PreDecrement
                or UnaryOperatorKind.PostIncrement
                or UnaryOperatorKind.PostDecrement:
                return LowerIncrement(unary, type);

            default:
                ReportUnsupported(unary, $"The '{unary.OperatorKind}' operator");
                return Constant(type, null);
        }
    }

    IrValue EmitUnary(IrUnaryOp op, BoundExpression operand, IrType type) {
        var value = LowerExpression(operand);
        return Emit(result => new IrUnaryInstruction(result, op, value), type);
    }

    /// <summary>Desugars <c>++</c>/<c>--</c> into a load, an add and a store.</summary>
    IrValue LowerIncrement(BoundUnaryExpression unary, IrType type) {
        if (TryGetPlace(unary.Operand) is not { } place) {
            diagnostics.Add(LoweringDiagnostics.NotAddressable, unary.Syntax.GetLocation());
            return Constant(type, null);
        }

        var isIncrement = unary.OperatorKind is UnaryOperatorKind.PreIncrement or UnaryOperatorKind.PostIncrement;
        var isPrefix = unary.OperatorKind is UnaryOperatorKind.PreIncrement or UnaryOperatorKind.PreDecrement;

        var current = Load(place);
        var one = Constant(type, 1);
        var updated = Emit(
            result => new IrBinaryInstruction(result, isIncrement ? IrBinaryOp.Add : IrBinaryOp.Subtract, current, one),
            type
        );

        Emit(new IrStoreInstruction(place, updated));
        return isPrefix ? updated : current;
    }

    IrValue LowerBinary(BoundBinaryExpression binary, IrType type) {
        // `&&` and `||` only promise not to evaluate the right operand when the left one
        // already decides the answer. See LowerShortCircuit for when that promise is kept.
        if (binary.OperatorKind is BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr
            && NeedsGuarding(binary.Right)) {
            return LowerShortCircuit(binary, type);
        }

        var left = LowerExpression(binary.Left);
        var right = LowerExpression(binary.Right);

        // A product involving a matrix is a real matrix multiply, not
        // componentwise; the binder already checked the shapes line up.
        var op = binary.OperatorKind == BinaryOperatorKind.Multiply
            && (left.Type.Kind == IrTypeKind.Matrix || right.Type.Kind == IrTypeKind.Matrix)
                ? IrBinaryOp.MatrixMultiply
                : MapBinary(binary.OperatorKind);

        return Emit(result => new IrBinaryInstruction(result, op, left, right), type);
    }

    static IrBinaryOp MapBinary(BinaryOperatorKind kind) =>
        kind switch {
            BinaryOperatorKind.Add => IrBinaryOp.Add,
            BinaryOperatorKind.Subtract => IrBinaryOp.Subtract,
            BinaryOperatorKind.Multiply => IrBinaryOp.Multiply,
            BinaryOperatorKind.Divide => IrBinaryOp.Divide,
            BinaryOperatorKind.Modulo => IrBinaryOp.Modulo,
            BinaryOperatorKind.LeftShift => IrBinaryOp.ShiftLeft,
            BinaryOperatorKind.RightShift => IrBinaryOp.ShiftRight,
            BinaryOperatorKind.UnsignedRightShift => IrBinaryOp.UnsignedShiftRight,
            BinaryOperatorKind.BitwiseAnd => IrBinaryOp.BitwiseAnd,
            BinaryOperatorKind.BitwiseOr => IrBinaryOp.BitwiseOr,
            BinaryOperatorKind.BitwiseXor => IrBinaryOp.BitwiseXor,
            BinaryOperatorKind.LogicalAnd => IrBinaryOp.LogicalAnd,
            BinaryOperatorKind.LogicalOr => IrBinaryOp.LogicalOr,
            BinaryOperatorKind.Equal => IrBinaryOp.Equal,
            BinaryOperatorKind.NotEqual => IrBinaryOp.NotEqual,
            BinaryOperatorKind.LessThan => IrBinaryOp.LessThan,
            BinaryOperatorKind.LessThanOrEqual => IrBinaryOp.LessThanOrEqual,
            BinaryOperatorKind.GreaterThan => IrBinaryOp.GreaterThan,
            _ => IrBinaryOp.GreaterThanOrEqual
        };

    /// <summary>
    ///     The IR function a call resolves to: the instantiation's if there is one, the
    ///     definition's otherwise.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The order is the whole of it. A call to <c>Box&lt;float4&gt;.Get</c> must reach
    ///         <c>Box_float4_Get</c> and not some single <c>Get</c>, so the substituted symbol is
    ///         tried first; a call to an ordinary method arrives already substituted only when the
    ///         binder read it through a map that changed nothing, and falls through to the
    ///         definition.
    ///     </para>
    ///     <para>
    ///         Canonicalised on the way in, because a constructed symbol is built fresh at each use
    ///         and two call sites writing <c>Swap&lt;float&gt;(…)</c> produce two objects for one
    ///         instantiation.
    ///     </para>
    /// </remarks>
    IrFunction? ResolveFunction(MethodSymbol callee, MethodSymbol definition) {
        if (functions.TryGetValue((Canonical(callee), BoundBodyKind.Method), out var instantiated)) {
            return instantiated;
        }

        return functions.GetValueOrDefault((definition, BoundBodyKind.Method));
    }

    // --- Short circuiting --------------------------------------------------

    /// <summary>
    ///     Lowers <c>a &amp;&amp; b</c> / <c>a || b</c> into a branch, so <paramref name="binary" />'s
    ///     right operand runs only when the left one has not already decided the answer.
    /// </summary>
    /// <remarks>
    ///     The result is a local rather than a value, because it is assigned from two places:
    ///     <c>t = a; if (t) { t = b }</c> for <c>&amp;&amp;</c>, and the same with the test negated
    ///     for <c>||</c>. Both targets take it — a structured <c>if</c> is what they are made of —
    ///     and the extra load is one the backends' own optimisers remove.
    /// </remarks>
    IrValue LowerShortCircuit(BoundBinaryExpression binary, IrType type) {
        var isAnd = binary.OperatorKind == BinaryOperatorKind.LogicalAnd;
        var result = Function.AddLocal(isAnd ? "and" : "or", type);
        var place = new IrPlace(result);

        Emit(new IrStoreInstruction(place, LowerExpression(binary.Left)));

        // `||` runs its right operand when the left one was *false*, so it tests the negation.
        var decided = Load(place);
        var test = isAnd ? decided : Emit(value => new IrUnaryInstruction(value, IrUnaryOp.Not, decided), type);

        var guarded = EmitInto(() => Emit(new IrStoreInstruction(place, LowerExpression(binary.Right))));
        Emit(new IrIfStatement(test, guarded, null));

        return Load(place);
    }

    /// <summary>
    ///     Lowers <c>c ? a : b</c> into a branch, for the same reason
    ///     <see cref="LowerShortCircuit" /> exists: a select evaluates both arms.
    /// </summary>
    IrValue LowerGuardedConditional(BoundConditionalExpression conditional, IrType type) {
        var result = Function.AddLocal("cond", type);
        var place = new IrPlace(result);
        var condition = LowerExpression(conditional.Condition);

        var whenTrue = EmitInto(() => Emit(new IrStoreInstruction(place, LowerExpression(conditional.WhenTrue))));
        var whenFalse = EmitInto(() => Emit(new IrStoreInstruction(place, LowerExpression(conditional.WhenFalse))));

        Emit(new IrIfStatement(condition, whenTrue, whenFalse));
        return Load(place);
    }

    /// <summary>
    ///     Whether <paramref name="expression" /> must not be evaluated unless control flow
    ///     actually reaches it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Evaluating an operand that the source says is unreachable is only observable three
    ///         ways, and each is checked for rather than assumed:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             an <b>index</b>, because <c>i &lt; n &amp;&amp; data[i] &gt; 0</c> is the guard the
    ///             whole feature exists for and an out-of-range read is undefined on both targets;
    ///         </item>
    ///         <item>a <b>call</b> to a declared function, which may store into a writable resource;</item>
    ///         <item>an <b>assignment</b> or increment, whose effect is the point of writing it.</item>
    ///     </list>
    ///     <para>
    ///         Everything else — arithmetic, swizzles, loads, and the intrinsic library, which is
    ///         pure by construction — stays in the branch-free form, so an ordinary
    ///         <c>a &gt; 0 &amp;&amp; b &lt; 1</c> still emits one <c>&amp;&amp;</c> and no local. That
    ///         matters more here than in a CPU language: a branch costs a GPU the whole warp, and
    ///         moving an implicit-LOD texture sample under one would make its derivatives undefined.
    ///     </para>
    /// </remarks>
    static bool NeedsGuarding(BoundExpression expression) =>
        expression.DescendantsAndSelf()
            .Any(
                node => node switch {
                    BoundArrayAccessExpression => true,
                    BoundAssignmentExpression => true,
                    BoundPropertyExpression => true,
                    BoundObjectCreationExpression creation => creation.Constructor is not null,
                    BoundInvocationExpression invocation => Definition(invocation.Method).MethodKind
                        != MethodKind.Intrinsic,
                    BoundUnaryExpression unary => unary.OperatorKind
                        is UnaryOperatorKind.PreIncrement or UnaryOperatorKind.PreDecrement
                        or UnaryOperatorKind.PostIncrement or UnaryOperatorKind.PostDecrement,
                    _ => false
                }
            );

    static MethodSymbol Definition(MethodSymbol method) =>
        method is SubstitutedMethodSymbol substituted ? substituted.OriginalDefinition : method;

    // --- Assignment --------------------------------------------------------

    IrValue LowerAssignment(BoundAssignmentExpression assignment) {
        if (assignment.Target is BoundPropertyExpression property) {
            return LowerPropertySet(assignment, property);
        }

        var type = LowerType(assignment.Target.Type, assignment.Syntax);

        if (TryGetPlace(assignment.Target) is not { } place) {
            diagnostics.Add(LoweringDiagnostics.NotAddressable, assignment.Syntax.GetLocation());
            return Constant(type, null);
        }

        IrValue value;

        // `x += y` becomes load, operate, store — and the target is read before
        // the right-hand side runs, as it is in the source language.
        if (assignment.OperatorKind is { } op) {
            var current = Load(place);
            var operand = LowerExpression(assignment.Value);
            value = Emit(result => new IrBinaryInstruction(result, MapBinary(op), current, operand), type);
        } else {
            value = LowerExpression(assignment.Value);
        }

        Emit(new IrStoreInstruction(place, value));
        return value;
    }

    IrValue LowerPropertySet(BoundAssignmentExpression assignment, BoundPropertyExpression property) {
        var type = LowerType(property.Type, assignment.Syntax);

        if (!functions.TryGetValue((Canonical(property.Property), BoundBodyKind.PropertySetter), out var setter)) {
            ReportUnsupported(assignment, $"Assigning to '{property.Property.Name}'");
            return Constant(type, null);
        }

        var value = LowerExpression(assignment.Value);

        if (assignment.OperatorKind is { } op) {
            var current = LowerPropertyGet(property, type);
            value = Emit(result => new IrBinaryInstruction(result, MapBinary(op), current, value), type);
        }

        var arguments = BuildArguments(property.Receiver, property.Property, [.. property.Arguments]);
        Emit(new IrCallInstruction(null, setter, [.. arguments.Arguments, IrArgument.Of(value)]));
        return value;
    }

    IrValue LowerPropertyGet(BoundPropertyExpression property, IrType type) {
        if (!functions.TryGetValue((Canonical(property.Property), BoundBodyKind.PropertyGetter), out var getter)) {
            ReportUnsupported(property, $"Reading '{property.Property.Name}'");
            return Constant(type, null);
        }

        var arguments = BuildArguments(property.Receiver, property.Property, [.. property.Arguments]);
        return Emit(result => new IrCallInstruction(result, getter, arguments.Arguments), getter.ReturnType);
    }

    // --- Calls -------------------------------------------------------------

    IrValue LowerInvocation(BoundInvocationExpression invocation, IrType type) =>
        LowerCall(invocation) ?? Constant(type, null);

    /// <summary>Lowers a call; null when the callee returns nothing.</summary>
    IrValue? LowerCall(BoundInvocationExpression invocation) {
        var callee = invocation.Method;
        var method = callee;
        var definition = method is SubstitutedMethodSymbol substituted ? substituted.OriginalDefinition : method;
        var type = LowerType(invocation.Type, invocation.Syntax);

        if (definition.MethodKind == MethodKind.Intrinsic) {
            return LowerIntrinsic(invocation, definition, type);
        }

        var receiver = invocation.Receiver;

        // A call through a compose slot was bound against the protocol, whose method has no
        // body. Swap in the bound shader's implementation, and drop the receiver: the slot
        // holds no value, and a shader method is a free function.
        if (receiver is BoundFieldExpression { Field: { IsCompose: true } slot }) {
            if (slot.ComposedType is not { } bound
                || FindImplementation(bound, definition) is not { } implementation) {
                // Why it could not be resolved was already reported at the declaration.
                ReportUnsupported(invocation, $"A call to '{method.Name}' through compose slot '{slot.Name}'");
                return null;
            }

            definition = implementation;
            callee = implementation;
            receiver = null;
        }

        if (ResolveFunction(callee, definition) is not { } function) {
            ReportUnsupported(invocation, $"A call to '{method.Name}'");
            return null;
        }

        // The callee rather than the definition: a receiver is prepended when the member's
        // containing type has a struct, and `Box<T>` has none — only `Box<float4>` does.
        var arguments = BuildArguments(receiver, callee, invocation.Arguments, callee.Parameters);

        if (function.ReturnType.IsVoid) {
            Emit(new IrCallInstruction(null, function, arguments.Arguments));
            EmitCopyOut(arguments);
            return null;
        }

        var result = Emit(
            value => new IrCallInstruction(value, function, arguments.Arguments),
            function.ReturnType
        );

        // After the call and before the result is used, so a caller that passes the same storage it
        // reads the result into sees the copy-out, not a stale value.
        EmitCopyOut(arguments);
        return result;
    }

    IrValue? LowerIntrinsic(BoundInvocationExpression invocation, MethodSymbol method, IrType type) {
        // A member intrinsic (`texture.Sample(…)`) takes its receiver first, and
        // the receiver is evaluated before the arguments.
        var receiver = method.ContainingSymbol is not null && invocation.Receiver is { } value
            ? LowerExpression(value)
            : null;

        var lowered = invocation.Arguments.Select(LowerExpression);
        var arguments = receiver is null ? lowered.ToArray() : [receiver, .. lowered];

        // `mul` is a matrix product, which the IR expresses as an operator.
        if (method.Name == "mul" && arguments.Length == 2) {
            return Emit(
                result => new IrBinaryInstruction(result, IrBinaryOp.MatrixMultiply, arguments[0], arguments[1]),
                type
            );
        }

        if (MapIntrinsic(method.Name, method.ContainingSymbol) is not { } intrinsic) {
            ReportUnsupported(invocation, $"The intrinsic '{method.Name}'");
            return Constant(type, null);
        }

        // A texel store is the one intrinsic with no result, so it is emitted as a statement.
        if (type.IsVoid) {
            Emit(new IrIntrinsicInstruction(null, intrinsic, arguments));
            return null;
        }

        return Emit(result => new IrIntrinsicInstruction(result, intrinsic, arguments), type);
    }

    /// <summary>
    ///     The opcode an intrinsic's name means.
    /// </summary>
    /// <param name="name">The method name as declared.</param>
    /// <param name="container">
    ///     The type the method is a member of, or null for a free function. Three names —
    ///     <c>Load</c>, <c>Store</c> and <c>GetDimensions</c> — mean different instructions on a
    ///     sampled texture and on a storage image, and the receiver's type is what says which.
    /// </param>
    static IrIntrinsic? MapIntrinsic(string name, Symbol? container) =>
        container is StorageImageTypeSymbol
            ? name switch {
                "Load" => IrIntrinsic.LoadImage,
                "Store" => IrIntrinsic.StoreImage,
                "GetDimensions" => IrIntrinsic.ImageSize,
                _ => null
            }
            : MapIntrinsic(name);

    static IrIntrinsic? MapIntrinsic(string name) =>
        name switch {
            "abs" => IrIntrinsic.Abs,
            "sign" => IrIntrinsic.Sign,
            "floor" => IrIntrinsic.Floor,
            "ceil" => IrIntrinsic.Ceil,
            "round" => IrIntrinsic.Round,
            "trunc" => IrIntrinsic.Truncate,
            "frac" => IrIntrinsic.Fract,
            "saturate" => IrIntrinsic.Saturate,
            "sqrt" => IrIntrinsic.Sqrt,
            "rsqrt" => IrIntrinsic.InverseSqrt,
            "exp" => IrIntrinsic.Exp,
            "exp2" => IrIntrinsic.Exp2,
            "log" => IrIntrinsic.Log,
            "log2" => IrIntrinsic.Log2,
            "sin" => IrIntrinsic.Sin,
            "cos" => IrIntrinsic.Cos,
            "tan" => IrIntrinsic.Tan,
            "asin" => IrIntrinsic.Asin,
            "acos" => IrIntrinsic.Acos,
            "atan" => IrIntrinsic.Atan,
            "atan2" => IrIntrinsic.Atan2,
            "radians" => IrIntrinsic.Radians,
            "degrees" => IrIntrinsic.Degrees,
            "ddx" => IrIntrinsic.DdX,
            "ddy" => IrIntrinsic.DdY,
            "min" => IrIntrinsic.Min,
            "max" => IrIntrinsic.Max,
            "pow" => IrIntrinsic.Pow,
            "mod" => IrIntrinsic.Mod,
            "step" => IrIntrinsic.Step,
            "clamp" => IrIntrinsic.Clamp,
            "lerp" or "mix" => IrIntrinsic.Lerp,
            "smoothstep" => IrIntrinsic.SmoothStep,
            "length" => IrIntrinsic.Length,
            "distance" => IrIntrinsic.Distance,
            "dot" => IrIntrinsic.Dot,
            "cross" => IrIntrinsic.Cross,
            "normalize" => IrIntrinsic.Normalize,
            "reflect" => IrIntrinsic.Reflect,
            "refract" => IrIntrinsic.Refract,
            "transpose" => IrIntrinsic.Transpose,
            "all" => IrIntrinsic.All,
            "any" => IrIntrinsic.Any,
            "asfloat" or "asint" or "asuint" => IrIntrinsic.BitCast,
            "Sample" => IrIntrinsic.SampleTexture,
            "SampleLevel" => IrIntrinsic.SampleTextureLevel,
            "Load" => IrIntrinsic.LoadTexture,
            "GetDimensions" => IrIntrinsic.TextureSize,
            _ => null
        };

    /// <summary>
    ///     Builds a call's argument list, prepending the receiver for a member of a
    ///     struct. A shader's members take no receiver: their state is global.
    /// </summary>
    /// <summary>
    ///     The method on <paramref name="implementer" /> that satisfies
    ///     <paramref name="declaration" />: same name, same parameter types.
    /// </summary>
    /// <remarks>
    ///     Matching is by signature rather than through an <c>override</c> link, because a
    ///     protocol member and its implementation are separate declarations that the
    ///     compilation relates only through the compose binding.
    /// </remarks>
    static MethodSymbol? FindImplementation(NamedTypeSymbol implementer, MethodSymbol declaration) {
        for (var current = implementer; current is not null; current = current.BaseType) {
            foreach (var candidate in current.GetMembers(declaration.Name).OfType<MethodSymbol>()) {
                if (candidate.Parameters.Count != declaration.Parameters.Count) {
                    continue;
                }

                var matches = true;
                for (var i = 0; i < candidate.Parameters.Count; i++) {
                    if (!candidate.Parameters[i].Type.Equals(declaration.Parameters[i].Type)) {
                        matches = false;
                        break;
                    }
                }

                if (matches) {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     A call's lowered arguments, and the copies that have to run after it returns.
    /// </summary>
    /// <param name="Arguments">The arguments, in order, ready for an <see cref="IrCallInstruction" />.</param>
    /// <param name="CopyOut">
    ///     One entry per <c>inout</c> argument: the caller's storage, and the temp whose value has to
    ///     be written back into it. In argument order, which is what makes the result defined when
    ///     two <c>inout</c> arguments name the same storage.
    /// </param>
    readonly record struct LoweredArguments(
        IrArgument[] Arguments,
        List<(IrPlace Place, IrVariable Temp)> CopyOut
    );

    /// <summary>
    ///     Lowers a call's arguments, giving every <c>inout</c> argument a temp to be passed by
    ///     reference.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The temp is not an optimisation and not a choice: SPIR-V requires a pointer argument
    ///         to be a memory object declaration, so an access chain like <c>d.color</c> cannot be
    ///         handed over, and a global's storage class could never match the parameter's. Copying
    ///         through a function-scoped temp is the one shape both targets accept.
    ///     </para>
    ///     <para>
    ///         It also puts copy-in/copy-out in the IR where it can be read, rather than leaving each
    ///         backend to lean on its own language's rules and hoping the two agree.
    ///     </para>
    /// </remarks>
    LoweredArguments BuildArguments(
        BoundExpression? receiver,
        Symbol member,
        IReadOnlyList<BoundExpression> arguments,
        IReadOnlyList<ParameterSymbol>? parameters = null
    ) {
        var lowered = new IrArgument[arguments.Count];
        List<(IrPlace, IrVariable)> copyOut = [];

        for (var i = 0; i < arguments.Count; i++) {
            var direction = parameters is not null && i < parameters.Count
                ? parameters[i].RefKind
                : RefKind.None;

            if (direction != RefKind.InOut) {
                lowered[i] = IrArgument.Of(LowerExpression(arguments[i]));
                continue;
            }

            var type = LowerType(arguments[i].Type, arguments[i].Syntax);

            // The binder has already refused anything without storage (RVN2110), so a missing place
            // here means the two disagree about what is addressable rather than bad source.
            if (TryGetPlace(arguments[i]) is not { } place) {
                ReportUnsupported(arguments[i], $"Passing this expression to inout parameter '{parameters![i].Name}'");
                lowered[i] = IrArgument.Of(Constant(type, null));
                continue;
            }

            var temp = Function.AddLocal($"{parameters![i].Name}#inout", type);
            Emit(new IrStoreInstruction(new(temp), Load(place)));

            lowered[i] = IrArgument.ByReference(temp);
            copyOut.Add((place, temp));
        }

        if (member.ContainingType is not { } containing || !structs.ContainsKey(containing)) {
            return new(lowered, copyOut);
        }

        // An operator takes every operand as an explicit parameter, and a static member has no
        // receiver at all, so neither gets one prepended — and prepending one from an enclosing
        // struct method would be silently wrong. This mirrors `Lowerer.SelfTypeFor`, which decides
        // the signature; the two have to agree or the call has the wrong arity.
        if (member is MethodSymbol { MethodKind: MethodKind.Operator } or { IsStatic: true }) {
            return new(lowered, copyOut);
        }

        var self = receiver switch {
            BoundSelfExpression when SelfPlace is { } place => Load(place),
            { } value => LowerExpression(value),
            _ when SelfPlace is { } place => Load(place),
            _ => null
        };

        return self is null
            ? new(lowered, copyOut)
            : new([IrArgument.Of(self), .. lowered], copyOut);
    }

    /// <summary>Writes every <c>inout</c> temp back into the caller's storage.</summary>
    void EmitCopyOut(LoweredArguments arguments) {
        foreach (var (place, temp) in arguments.CopyOut) {
            Emit(new IrStoreInstruction(place, Load(new(temp))));
        }
    }

    // --- Construction ------------------------------------------------------

    IrValue LowerObjectCreation(BoundObjectCreationExpression creation, IrType type) {
        if (creation.Constructor is { } constructor) {
            if (!functions.TryGetValue((Canonical(constructor), BoundBodyKind.Constructor), out var function)) {
                ReportUnsupported(creation, $"A call to '{constructor.ContainingType?.Name}' constructor");
                return Constant(type, null);
            }

            var arguments = creation.Arguments.Select(argument => IrArgument.Of(LowerExpression(argument))).ToArray();
            return Emit(result => new IrCallInstruction(result, function, arguments), function.ReturnType);
        }

        if (type is IrStructType structType && creation.Arguments.Count == 0) {
            // A struct with no constructor is zero-initialized.
            var zeros = structType.Fields.Select(f => Constant(f.Type, null)).ToArray();
            return Emit(result => new IrConstructInstruction(result, zeros), type);
        }

        if (type.Kind is not (IrTypeKind.Vector or IrTypeKind.Matrix or IrTypeKind.Struct or IrTypeKind.Array)) {
            ReportUnsupported(creation, $"Constructing a value of type '{creation.Type.ToDisplayString()}'");
            return Constant(type, null);
        }

        var parts = creation.Arguments.Select(LowerExpression).ToArray();

        // `float3(0)` broadcasts rather than building from parts.
        if (parts.Length == 1 && parts[0].Type.IsScalar && type.Kind is IrTypeKind.Vector or IrTypeKind.Matrix) {
            return Emit(result => new IrConvertInstruction(result, IrConversionKind.Splat, parts[0]), type);
        }

        return Emit(result => new IrConstructInstruction(result, parts), type);
    }

    // --- Primitives --------------------------------------------------------

    IrValue Load(IrPlace place) => Emit(result => new IrLoadInstruction(result, place), place.Type);

    /// <summary>Materializes a constant, coerced to the IR type it is being used at.</summary>
    IrValue Constant(IrType type, object? value) =>
        Emit(result => new IrConstantInstruction(result, Coerce(value, type)), type);

    static object? Coerce(object? value, IrType type) {
        if (value is null) {
            return null;
        }

        try {
            return type.Kind switch {
                IrTypeKind.Bool => Convert.ToBoolean(value),
                IrTypeKind.Int => Convert.ToInt32(value),
                IrTypeKind.UInt => Convert.ToUInt32(value),
                IrTypeKind.Float => Convert.ToSingle(value),
                IrTypeKind.Double => Convert.ToDouble(value),
                // A scalar used at a vector or matrix type is a broadcast of it.
                IrTypeKind.Vector or IrTypeKind.Matrix => Coerce(value, type.ComponentType),
                _ => value
            };
        } catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) {
            return value;
        }
    }
}
