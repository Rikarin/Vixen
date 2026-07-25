// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Lowering;

/// <summary>Mapping the semantic type system onto the much smaller IR one.</summary>
public sealed partial class Lowerer {
    static IrVectorType Float4 => new(IrScalarType.Float, 4);

    /// <summary>
    ///     Lowers a semantic type. Anything with no GPU representation reports
    ///     <c>RVN3001</c> and yields <c>void</c>, which callers treat as "skip".
    /// </summary>
    IrType LowerType(TypeSymbol type, SyntaxNode? syntax) {
        if (typeCache.TryGetValue(type, out var cached)) {
            return cached;
        }

        var lowered = LowerTypeCore(type, syntax);
        typeCache[type] = lowered;
        return lowered;
    }

    IrType LowerTypeCore(TypeSymbol type, SyntaxNode? syntax) {
        // The binder already reported this one; stay quiet.
        if (type.IsErrorType) {
            return IrScalarType.Void;
        }

        switch (type) {
            case PrimitiveTypeSymbol primitive:
                return LowerPrimitive(primitive, syntax);

            case BuiltInNamedTypeSymbol builtIn:
                return builtIn.SpecialType switch {
                    SpecialType.Texture2D => new IrTextureType(IrTextureDimension.Texture2D, Float4),
                    SpecialType.Texture3D => new IrTextureType(IrTextureDimension.Texture3D, Float4),
                    SpecialType.TextureCube => new IrTextureType(IrTextureDimension.Cube, Float4),
                    SpecialType.Sampler => IrSamplerType.Instance,
                    _ => NotRepresentable(type, syntax)
                };

            case ArrayTypeSymbol { Rank: 1 } array: {
                var element = LowerType(array.ElementType, syntax);
                return element.IsVoid ? NotRepresentable(type, syntax) : new IrArrayType(element);
            }

            case NamedTypeSymbol { TypeKind: TypeKind.Enum }:
                // An enum is its underlying integer once the constants are folded.
                return IrScalarType.Int;

            case NamedTypeSymbol named when structs.TryGetValue(named, out var structType):
                return structType;

            default:
                return NotRepresentable(type, syntax);
        }
    }

    IrType LowerPrimitive(PrimitiveTypeSymbol type, SyntaxNode? syntax) {
        switch (type.TypeKind) {
            case TypeKind.Void:
                return IrScalarType.Void;

            case TypeKind.Scalar:
                return LowerScalar(type.SpecialType) ?? NotRepresentable(type, syntax);

            case TypeKind.Vector: {
                var component = LowerScalar(type.ComponentSpecialType);
                return component is null
                    ? NotRepresentable(type, syntax)
                    : new IrVectorType(component, type.ComponentCount);
            }

            case TypeKind.Matrix: {
                var component = LowerScalar(type.ComponentSpecialType);
                return component is null
                    ? NotRepresentable(type, syntax)
                    : new IrMatrixType(component, type.Rows, type.Columns);
            }

            default:
                return NotRepresentable(type, syntax);
        }
    }

    /// <summary>
    ///     Every scalar Raven has maps straight through — the types that had no GPU
    ///     representation were removed from the language rather than handled here.
    /// </summary>
    static IrScalarType? LowerScalar(SpecialType type) =>
        type switch {
            SpecialType.Bool => IrScalarType.Bool,
            SpecialType.Int => IrScalarType.Int,
            SpecialType.UInt => IrScalarType.UInt,
            SpecialType.Float => IrScalarType.Float,
            SpecialType.Double => IrScalarType.Double,
            _ => null
        };

    IrType NotRepresentable(TypeSymbol type, SyntaxNode? syntax) {
        diagnostics.Add(LoweringDiagnostics.TypeNotRepresentable, LocationOf(syntax), type.ToDisplayString());
        return IrScalarType.Void;
    }
}
