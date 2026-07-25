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
                var condition = LowerExpression(conditional.Condition);
                var whenTrue = LowerExpression(conditional.WhenTrue);
                var whenFalse = LowerExpression(conditional.WhenFalse);
                return Emit(result => new IrSelectInstruction(result, condition, whenTrue, whenFalse), type);
            }

            case BoundCollectionExpression collection: {
                var elements = collection.Elements.Select(LowerExpression).ToArray();
                return Emit(result => new IrConstructInstruction(result, elements), type);
            }

            case BoundErrorExpression:
                // Already reported by the binder.
                return Constant(type, null);

            default:
                ReportUnsupported(expression, Describe(expression));
                return Constant(type, null);
        }
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
            BoundTupleExpression => "A tuple",
            BoundRangeExpression => "A range outside a 'for' loop",
            BoundIsPatternExpression => "An 'is' test",
            BoundSwitchExpression => "A switch expression",
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

        if (field.ContainingType is not { } containing || !structs.TryGetValue(containing, out var structType)) {
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

        // `array.Length` is an operation, not storage.
        if (expression is BoundFieldExpression { Field.Name: "Length", Receiver: { } arrayReceiver }
            && arrayReceiver.Type is ArrayTypeSymbol) {
            var array = LowerExpression(arrayReceiver);
            return Emit(
                result => new IrIntrinsicInstruction(result, IrIntrinsic.ArrayLength, [array]),
                IrScalarType.Int
            );
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

        if (expression.Field.ContainingType is { } containing
            && structs.TryGetValue(containing, out var structType)
            && structType.IndexOf(expression.Field.Name) is var index and >= 0) {
            return [new IrFieldAccess(index)];
        }

        return null;
    }

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

        if (!functions.TryGetValue((property.Property, BoundBodyKind.PropertySetter), out var setter)) {
            ReportUnsupported(assignment, $"Assigning to '{property.Property.Name}'");
            return Constant(type, null);
        }

        var value = LowerExpression(assignment.Value);

        if (assignment.OperatorKind is { } op) {
            var current = LowerPropertyGet(property, type);
            value = Emit(result => new IrBinaryInstruction(result, MapBinary(op), current, value), type);
        }

        var arguments = BuildArguments(property.Receiver, property.Property, [.. property.Arguments]);
        Emit(new IrCallInstruction(null, setter, [.. arguments, value]));
        return value;
    }

    IrValue LowerPropertyGet(BoundPropertyExpression property, IrType type) {
        if (!functions.TryGetValue((property.Property, BoundBodyKind.PropertyGetter), out var getter)) {
            ReportUnsupported(property, $"Reading '{property.Property.Name}'");
            return Constant(type, null);
        }

        var arguments = BuildArguments(property.Receiver, property.Property, [.. property.Arguments]);
        return Emit(result => new IrCallInstruction(result, getter, arguments), getter.ReturnType);
    }

    // --- Calls -------------------------------------------------------------

    IrValue LowerInvocation(BoundInvocationExpression invocation, IrType type) =>
        LowerCall(invocation) ?? Constant(type, null);

    /// <summary>Lowers a call; null when the callee returns nothing.</summary>
    IrValue? LowerCall(BoundInvocationExpression invocation) {
        var method = invocation.Method;
        var definition = method is SubstitutedMethodSymbol substituted ? substituted.OriginalDefinition : method;
        var type = LowerType(invocation.Type, invocation.Syntax);

        if (definition.MethodKind == MethodKind.Intrinsic) {
            return LowerIntrinsic(invocation, definition, type);
        }

        if (!functions.TryGetValue((definition, BoundBodyKind.Method), out var function)) {
            ReportUnsupported(invocation, $"A call to '{method.Name}'");
            return null;
        }

        var arguments = BuildArguments(invocation.Receiver, definition, invocation.Arguments);

        if (function.ReturnType.IsVoid) {
            Emit(new IrCallInstruction(null, function, arguments));
            return null;
        }

        return Emit(result => new IrCallInstruction(result, function, arguments), function.ReturnType);
    }

    IrValue LowerIntrinsic(BoundInvocationExpression invocation, MethodSymbol method, IrType type) {
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

        if (MapIntrinsic(method.Name) is not { } intrinsic) {
            ReportUnsupported(invocation, $"The intrinsic '{method.Name}'");
            return Constant(type, null);
        }

        return Emit(result => new IrIntrinsicInstruction(result, intrinsic, arguments), type);
    }

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
            "Sample" => IrIntrinsic.SampleTexture,
            "Load" => IrIntrinsic.LoadTexture,
            _ => null
        };

    /// <summary>
    ///     Builds a call's argument list, prepending the receiver for a member of a
    ///     struct. A shader's members take no receiver: their state is global.
    /// </summary>
    IrValue[] BuildArguments(BoundExpression? receiver, Symbol member, IReadOnlyList<BoundExpression> arguments) {
        var lowered = arguments.Select(LowerExpression).ToArray();

        if (member.ContainingType is not { } containing || !structs.ContainsKey(containing)) {
            return lowered;
        }

        var self = receiver switch {
            BoundSelfExpression when SelfPlace is { } place => Load(place),
            { } value => LowerExpression(value),
            _ when SelfPlace is { } place => Load(place),
            _ => null
        };

        return self is null ? lowered : [self, .. lowered];
    }

    // --- Construction ------------------------------------------------------

    IrValue LowerObjectCreation(BoundObjectCreationExpression creation, IrType type) {
        if (creation.Constructor is { } constructor) {
            if (!functions.TryGetValue((constructor, BoundBodyKind.Constructor), out var function)) {
                ReportUnsupported(creation, $"A call to '{constructor.ContainingType?.Name}' constructor");
                return Constant(type, null);
            }

            var arguments = creation.Arguments.Select(LowerExpression).ToArray();
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
