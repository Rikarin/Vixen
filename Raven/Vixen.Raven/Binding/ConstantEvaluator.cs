// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;

namespace Vixen.Raven.Binding;

/// <summary>
///     Evaluates a bound expression to a compile-time value, or null when it has none.
///     This is what gives an enum member's initializer and a <c>const</c> field's
///     initializer their values — <c>C = B</c>, <c>D = 2 + 3</c>, <c>E = -1</c> —
///     where reading <see cref="BoundExpression.ConstantValue" /> alone only sees
///     literals and const-field references.
/// </summary>
public static class ConstantEvaluator {
    /// <summary>The expression's compile-time value, or null when it is not a constant.</summary>
    public static object? Evaluate(BoundExpression expression) {
        switch (expression) {
            case BoundLiteralExpression literal:
                return literal.ConstantValue;

            // A const field's value may itself still be evaluating — a cycle — in
            // which case it answers null and the caller reports.
            case BoundFieldExpression { Field.IsConst: true } field:
                return field.Field.ConstantValue;

            case BoundConversionExpression conversion:
                return Convert(Evaluate(conversion.Operand), conversion.Type);

            case BoundUnaryExpression unary:
                return EvaluateUnary(unary.OperatorKind, Evaluate(unary.Operand));

            case BoundBinaryExpression binary:
                return EvaluateBinary(binary.OperatorKind, Evaluate(binary.Left), Evaluate(binary.Right));

            default:
                return null;
        }
    }

    static object? Convert(object? value, TypeSymbol type) {
        if (value is null) {
            return null;
        }

        try {
            return (type as PrimitiveTypeSymbol)?.SpecialType switch {
                SpecialType.Bool => System.Convert.ToBoolean(value),
                SpecialType.Int => System.Convert.ToInt32(value),
                SpecialType.UInt => System.Convert.ToUInt32(value),
                SpecialType.Float => System.Convert.ToSingle(value),
                SpecialType.Double => System.Convert.ToDouble(value),
                SpecialType.Int64 => System.Convert.ToInt64(value),
                SpecialType.UInt64 => System.Convert.ToUInt64(value),
                _ => null
            };
        } catch (Exception e) when (e is OverflowException or InvalidCastException or FormatException) {
            return null;
        }
    }

    static object? EvaluateUnary(UnaryOperatorKind kind, object? operand) =>
        (kind, operand) switch {
            (UnaryOperatorKind.Plus, int or uint or float or double) => operand,
            (UnaryOperatorKind.Minus, int i) => -i,
            (UnaryOperatorKind.Minus, float f) => -f,
            (UnaryOperatorKind.Minus, double d) => -d,
            (UnaryOperatorKind.BitwiseNot, int i) => ~i,
            (UnaryOperatorKind.BitwiseNot, uint u) => ~u,
            (UnaryOperatorKind.LogicalNot, bool b) => !b,
            _ => null
        };

    static object? EvaluateBinary(BinaryOperatorKind kind, object? left, object? right) {
        if (left is null || right is null) {
            return null;
        }

        try {
            return (left, right) switch {
                (bool a, bool b) => EvaluateBool(kind, a, b),
                (int a, int b) => EvaluateInt(kind, a, b),
                (uint a, uint b) => EvaluateUInt(kind, a, b),
                // A shift's right operand is a count and stays int even for a uint left.
                (uint a, int b) => EvaluateUIntShift(kind, a, b),
                (float a, float b) => EvaluateDouble(kind, a, b) is double d ? (float)d : EvaluateComparison(kind, a, b),
                (double a, double b) => EvaluateDouble(kind, a, b) ?? EvaluateComparison(kind, a, b),
                _ => null
            };
        } catch (Exception e) when (e is DivideByZeroException or OverflowException) {
            return null;
        }
    }

    static object? EvaluateBool(BinaryOperatorKind kind, bool a, bool b) =>
        kind switch {
            BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.BitwiseAnd => a && b,
            BinaryOperatorKind.LogicalOr or BinaryOperatorKind.BitwiseOr => a || b,
            BinaryOperatorKind.BitwiseXor => a ^ b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            _ => null
        };

    static object? EvaluateInt(BinaryOperatorKind kind, int a, int b) =>
        kind switch {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => a / b,
            BinaryOperatorKind.Modulo => a % b,
            BinaryOperatorKind.LeftShift => a << b,
            BinaryOperatorKind.RightShift => a >> b,
            BinaryOperatorKind.UnsignedRightShift => a >>> b,
            BinaryOperatorKind.BitwiseAnd => a & b,
            BinaryOperatorKind.BitwiseOr => a | b,
            BinaryOperatorKind.BitwiseXor => a ^ b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null
        };

    static object? EvaluateUInt(BinaryOperatorKind kind, uint a, uint b) =>
        kind switch {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => a / b,
            BinaryOperatorKind.Modulo => a % b,
            BinaryOperatorKind.BitwiseAnd => a & b,
            BinaryOperatorKind.BitwiseOr => a | b,
            BinaryOperatorKind.BitwiseXor => a ^ b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => EvaluateUIntShift(kind, a, (int)b)
        };

    static object? EvaluateUIntShift(BinaryOperatorKind kind, uint a, int b) =>
        kind switch {
            BinaryOperatorKind.LeftShift => a << b,
            BinaryOperatorKind.RightShift => a >> b,
            BinaryOperatorKind.UnsignedRightShift => a >>> b,
            _ => null
        };

    static object? EvaluateDouble(BinaryOperatorKind kind, double a, double b) =>
        kind switch {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => a / b,
            BinaryOperatorKind.Modulo => a % b,
            _ => null
        };

    static object? EvaluateComparison(BinaryOperatorKind kind, double a, double b) =>
        kind switch {
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null
        };
}
