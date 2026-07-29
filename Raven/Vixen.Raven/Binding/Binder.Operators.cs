// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;

namespace Vixen.Raven.Binding;

/// <summary>The operand and result types a binary operator was resolved to.</summary>
public readonly record struct BinaryOperatorSignature(
    TypeSymbol LeftType,
    TypeSymbol RightType,
    TypeSymbol ResultType
);

/// <summary>Built-in operator resolution.</summary>
public abstract partial class Binder {
    /// <summary>
    ///     <c>mat * vec</c>, <c>vec * mat</c> and <c>mat * mat</c>, which are real
    ///     products rather than element-wise operations.
    /// </summary>
    static BinaryOperatorSignature? ResolveLinearAlgebraMultiply(TypeSymbol left, TypeSymbol right) {
        var leftMatrix = left is PrimitiveTypeSymbol { TypeKind: TypeKind.Matrix } lhs ? lhs : null;
        var rightMatrix = right is PrimitiveTypeSymbol { TypeKind: TypeKind.Matrix } rhs ? rhs : null;

        if (leftMatrix is null && rightMatrix is null) {
            return null;
        }

        // matrix * scalar (either order) keeps the matrix type.
        if (leftMatrix is not null && right is PrimitiveTypeSymbol { TypeKind: TypeKind.Scalar }) {
            return new BinaryOperatorSignature(leftMatrix, BuiltInTypes.Float, leftMatrix);
        }

        if (rightMatrix is not null && left is PrimitiveTypeSymbol { TypeKind: TypeKind.Scalar }) {
            return new BinaryOperatorSignature(BuiltInTypes.Float, rightMatrix, rightMatrix);
        }

        if (leftMatrix is not null && right is PrimitiveTypeSymbol { TypeKind: TypeKind.Vector } columnVector) {
            if (columnVector.ComponentCount != leftMatrix.Columns) {
                return null;
            }

            var result = BuiltInTypes.Vector(SpecialType.Float, leftMatrix.Rows);
            return result is null ? null : new BinaryOperatorSignature(leftMatrix, columnVector, result);
        }

        if (rightMatrix is not null && left is PrimitiveTypeSymbol { TypeKind: TypeKind.Vector } rowVector) {
            if (rowVector.ComponentCount != rightMatrix.Rows) {
                return null;
            }

            var result = BuiltInTypes.Vector(SpecialType.Float, rightMatrix.Columns);
            return result is null ? null : new BinaryOperatorSignature(rowVector, rightMatrix, result);
        }

        if (leftMatrix is not null && rightMatrix is not null) {
            if (leftMatrix.Columns != rightMatrix.Rows) {
                return null;
            }

            var result = FindMatrix(leftMatrix.Rows, rightMatrix.Columns);
            return result is null ? null : new BinaryOperatorSignature(leftMatrix, rightMatrix, result);
        }

        return null;
    }

    static PrimitiveTypeSymbol? FindMatrix(int rows, int columns) =>
        BuiltInTypes.All
            .OfType<PrimitiveTypeSymbol>()
            .FirstOrDefault(m => m.TypeKind == TypeKind.Matrix && m.Rows == rows && m.Columns == columns);

    /// <summary>Comparing vectors yields a mask; comparing scalars yields a bool.</summary>
    static TypeSymbol ComparisonResult(TypeSymbol operandType) =>
        operandType is PrimitiveTypeSymbol { TypeKind: TypeKind.Vector } vector
            ? BuiltInTypes.Vector(SpecialType.Bool, vector.ComponentCount) ?? BuiltInTypes.Bool
            : BuiltInTypes.Bool;

    static bool IsBool(TypeSymbol type) => type.SpecialType == SpecialType.Bool;

    static bool IsBoolLike(TypeSymbol type) =>
        type is PrimitiveTypeSymbol primitive
        && primitive.TypeKind is TypeKind.Scalar or TypeKind.Vector
        && primitive.ComponentSpecialType == SpecialType.Bool;

    static bool IsIntegral(TypeSymbol type) =>
        type is PrimitiveTypeSymbol primitive
        && primitive.TypeKind is TypeKind.Scalar or TypeKind.Vector
        && primitive.ComponentSpecialType
            is SpecialType.Int or SpecialType.UInt or SpecialType.Int64 or SpecialType.UInt64;

    /// <summary>Maps an operator token's text to the operation it performs.</summary>
    internal static BinaryOperatorKind? MapBinaryOperator(string text) =>
        text switch {
            "+" => BinaryOperatorKind.Add,
            "-" => BinaryOperatorKind.Subtract,
            "*" => BinaryOperatorKind.Multiply,
            "/" => BinaryOperatorKind.Divide,
            "%" => BinaryOperatorKind.Modulo,
            "<<" => BinaryOperatorKind.LeftShift,
            ">>" => BinaryOperatorKind.RightShift,
            ">>>" => BinaryOperatorKind.UnsignedRightShift,
            "&" => BinaryOperatorKind.BitwiseAnd,
            "|" => BinaryOperatorKind.BitwiseOr,
            "^" => BinaryOperatorKind.BitwiseXor,
            "&&" => BinaryOperatorKind.LogicalAnd,
            "||" => BinaryOperatorKind.LogicalOr,
            "==" => BinaryOperatorKind.Equal,
            "!=" => BinaryOperatorKind.NotEqual,
            "<" => BinaryOperatorKind.LessThan,
            "<=" => BinaryOperatorKind.LessThanOrEqual,
            ">" => BinaryOperatorKind.GreaterThan,
            ">=" => BinaryOperatorKind.GreaterThanOrEqual,
            _ => null
        };

    /// <summary>The operator a compound assignment (<c>+=</c>) applies before storing.</summary>
    internal static BinaryOperatorKind? MapCompoundAssignment(string text) =>
        text.Length > 1 && text.EndsWith('=') ? MapBinaryOperator(text[..^1]) : null;

    /// <summary>
    ///     Resolves a built-in binary operator, or null when it is not defined for
    ///     these operand types.
    /// </summary>
    internal static BinaryOperatorSignature? ResolveBinaryOperator(
        BinaryOperatorKind kind,
        TypeSymbol left,
        TypeSymbol right
    ) {
        // An operand that already failed to bind suppresses further errors.
        if (left.IsErrorType || right.IsErrorType) {
            return new BinaryOperatorSignature(left, right, ErrorTypeSymbol.Instance);
        }

        switch (kind) {
            case BinaryOperatorKind.Multiply when ResolveLinearAlgebraMultiply(left, right) is { } product:
                return product;

            case BinaryOperatorKind.Add:
            case BinaryOperatorKind.Subtract:
            case BinaryOperatorKind.Multiply:
            case BinaryOperatorKind.Divide:
            case BinaryOperatorKind.Modulo: {
                if (!left.IsNumericLike || !right.IsNumericLike) {
                    return null;
                }

                var common = Conversions.FindCommonType(left, right);
                return common is null ? null : new BinaryOperatorSignature(common, common, common);
            }

            case BinaryOperatorKind.LeftShift:
            case BinaryOperatorKind.RightShift:
            case BinaryOperatorKind.UnsignedRightShift:
                // The shift amount is independent of the value's type.
                return IsIntegral(left) && IsIntegral(right)
                    ? new BinaryOperatorSignature(left, BuiltInTypes.Int, left)
                    : null;

            case BinaryOperatorKind.BitwiseAnd:
            case BinaryOperatorKind.BitwiseOr:
            case BinaryOperatorKind.BitwiseXor: {
                if ((IsBoolLike(left) && IsBoolLike(right)) || (IsIntegral(left) && IsIntegral(right))) {
                    var common = Conversions.FindCommonType(left, right);
                    return common is null ? null : new BinaryOperatorSignature(common, common, common);
                }

                return null;
            }

            case BinaryOperatorKind.LogicalAnd:
            case BinaryOperatorKind.LogicalOr:
                return IsBool(left) && IsBool(right)
                    ? new BinaryOperatorSignature(BuiltInTypes.Bool, BuiltInTypes.Bool, BuiltInTypes.Bool)
                    : null;

            case BinaryOperatorKind.Equal:
            case BinaryOperatorKind.NotEqual: {
                var common = Conversions.FindCommonType(left, right);
                return common is null
                    ? null
                    : new BinaryOperatorSignature(common, common, ComparisonResult(common));
            }

            case BinaryOperatorKind.LessThan:
            case BinaryOperatorKind.LessThanOrEqual:
            case BinaryOperatorKind.GreaterThan:
            case BinaryOperatorKind.GreaterThanOrEqual: {
                if (!left.IsNumericLike || !right.IsNumericLike) {
                    return null;
                }

                var common = Conversions.FindCommonType(left, right);
                return common is null
                    ? null
                    : new BinaryOperatorSignature(common, common, ComparisonResult(common));
            }

            default:
                return null;
        }
    }

    /// <summary>Resolves a built-in unary operator, or null when it is not defined.</summary>
    internal static TypeSymbol? ResolveUnaryOperator(UnaryOperatorKind kind, TypeSymbol operand) {
        if (operand.IsErrorType) {
            return ErrorTypeSymbol.Instance;
        }

        return kind switch {
            UnaryOperatorKind.Plus or UnaryOperatorKind.Minus => operand.IsNumericLike ? operand : null,
            UnaryOperatorKind.BitwiseNot => IsIntegral(operand) ? operand : null,
            UnaryOperatorKind.LogicalNot => IsBoolLike(operand) ? operand : null,
            UnaryOperatorKind.PreIncrement
                or UnaryOperatorKind.PreDecrement
                or UnaryOperatorKind.PostIncrement
                or UnaryOperatorKind.PostDecrement => operand.IsNumericLike ? operand : null,
            _ => null
        };
    }
}
