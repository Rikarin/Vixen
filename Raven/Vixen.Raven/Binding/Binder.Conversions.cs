// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>Expression-aware conversion classification and insertion.</summary>
public abstract partial class Binder {
    /// <summary>
    ///     An untyped integer literal takes the shape the context asks for:
    ///     <c>val x: uint = 1</c> and <c>val v: float3 = 0</c> both work.
    /// </summary>
    static bool IsConstantConvertible(BoundExpression expression, TypeSymbol target) {
        if (expression.ConstantValue is not { } value || expression.Type is not PrimitiveTypeSymbol source) {
            return false;
        }

        if (source.TypeKind != TypeKind.Scalar || source.SpecialType is SpecialType.Bool) {
            return false;
        }

        if (target is not PrimitiveTypeSymbol { TypeKind: TypeKind.Scalar or TypeKind.Vector } destination) {
            return false;
        }

        return destination.ComponentSpecialType switch {
            // Only a non-negative value may take an unsigned shape.
            SpecialType.UInt => value switch {
                int i => i >= 0,
                uint => true,
                _ => false
            },
            // A real constant never silently becomes an integer.
            SpecialType.Int => value is int or uint,

            // A literal is the one thing that may widen to 64 bits without being asked to. It has
            // no type of its own to be surprised by, and refusing it would make `atomicMax(p, 0)`
            // read `atomicMax(p, uint64(0))` for no gain — while a *variable* still has to say
            // `uint64(x)`, which is what keeps an atomic's width the author's choice.
            SpecialType.Int64 => value is int or uint,
            SpecialType.UInt64 => value switch {
                int i => i >= 0,
                uint => true,
                _ => false
            },

            SpecialType.Float or SpecialType.Double => value is int or uint or float or double,
            _ => false
        };
    }

    /// <summary>
    ///     Classifies the conversion from an expression to a target type. This adds
    ///     the cases that depend on the expression rather than just its type — a
    ///     constant literal that fits the target, above all.
    /// </summary>
    internal static Conversion ClassifyConversion(BoundExpression expression, TypeSymbol target) {
        var direct = Conversions.Classify(expression.Type, target);
        if (direct.Exists && direct.IsImplicit) {
            return direct;
        }

        return IsConstantConvertible(expression, target)
            ? new(ConversionKind.ImplicitConstant)
            : direct;
    }

    /// <summary>
    ///     Converts an expression to <paramref name="target" />, materializing the
    ///     conversion in the bound tree. Reports when no implicit conversion exists.
    /// </summary>
    internal BoundExpression Convert(BoundExpression expression, TypeSymbol target, SyntaxNode syntax) {
        var conversion = ClassifyConversion(expression, target);
        Context.RecordConversion(syntax, target);

        if (!conversion.Exists || !conversion.IsImplicit) {
            if (!expression.Type.IsErrorType && !target.IsErrorType) {
                Report(
                    SemanticDiagnostics.CannotConvert,
                    syntax,
                    expression.Type.ToDisplayString(),
                    target.ToDisplayString()
                );
            }

            return new BoundConversionExpression(syntax, expression, target, Conversion.None);
        }

        return conversion.IsIdentity
            ? expression
            : new BoundConversionExpression(syntax, expression, target, conversion);
    }

    /// <summary>Converts for an explicit cast, where non-implicit conversions are allowed.</summary>
    internal BoundExpression ConvertExplicit(BoundExpression expression, TypeSymbol target, SyntaxNode syntax) {
        var conversion = ClassifyConversion(expression, target);
        Context.RecordConversion(syntax, target);

        if (!conversion.Exists) {
            if (!expression.Type.IsErrorType && !target.IsErrorType) {
                Report(
                    SemanticDiagnostics.NoExplicitConversion,
                    syntax,
                    expression.Type.ToDisplayString(),
                    target.ToDisplayString()
                );
            }

            return new BoundConversionExpression(syntax, expression, target, Conversion.None);
        }

        return conversion.IsIdentity
            ? expression
            : new BoundConversionExpression(syntax, expression, target, conversion);
    }

    /// <summary>Checks an expression is usable as a condition and converts it to <c>bool</c>.</summary>
    internal BoundExpression BindCondition(ExpressionSyntax syntax) {
        var condition = BindValue(syntax);

        if (condition.Type.IsErrorType || condition.Type.SpecialType == SpecialType.Bool) {
            return condition;
        }

        var conversion = ClassifyConversion(condition, BuiltInTypes.Bool);
        if (conversion.Exists && conversion.IsImplicit) {
            return new BoundConversionExpression(syntax, condition, BuiltInTypes.Bool, conversion);
        }

        Report(SemanticDiagnostics.ConditionMustBeBool, syntax, condition.Type.ToDisplayString());
        return condition;
    }
}
