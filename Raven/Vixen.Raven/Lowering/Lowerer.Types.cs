// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Lowering;

/// <summary>Mapping the semantic type system onto the much smaller IR one.</summary>
public sealed partial class Lowerer {
    static IrVectorType Float4 => new(IrScalarType.Float, 4);

    /// <summary>
    ///     Lowers a semantic type. Anything with no GPU representation reports
    ///     <c>RVN3001</c> and yields <c>void</c>, which callers treat as "skip".
    /// </summary>
    IrType LowerType(TypeSymbol type, SyntaxNode? syntax) {
        // Substitution first, and before the cache: inside `Box<float4>` a `T` is an `f32`, and
        // caching the unsubstituted symbol would hand the next instantiation the wrong answer.
        if (substitution is { IsEmpty: false }) {
            type = substitution.Substitute(type);
        }

        // Two uses of one instantiation must reach one struct, and a constructed symbol is built
        // fresh at each use — so the canonical instance is what the struct table is keyed by.
        if (type is ConstructedNamedTypeSymbol constructed && monomorphiser is not null) {
            type = monomorphiser.Canonical(constructed);
        }

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
                    SpecialType.AccelerationStructure => IrAccelerationStructureType.Instance,
                    SpecialType.DepthTexture2D => IrDepthTextureType.Instance,
                    SpecialType.ComparisonSampler => IrComparisonSamplerType.Instance,
                    _ => NotRepresentable(type, syntax)
                };

            case ArrayTypeSymbol { Rank: 1 } array: {
                var element = LowerType(array.ElementType, syntax);
                return element.IsVoid ? NotRepresentable(type, syntax) : new IrArrayType(element, array.Length);
            }

            // A storage buffer *is* a runtime-sized array in the IR. Nothing else is needed: the
            // block that wraps it is the backends' business, the std430 layout comes from the
            // binding kind, and read-only-ness from the binding's flag. Modelling it as its own IR
            // type would have added a second array-like thing for indexing to know about.
            case BufferTypeSymbol buffer: {
                var element = LowerType(buffer.ElementType, syntax);
                return element.IsVoid ? NotRepresentable(type, syntax) : new IrArrayType(element);
            }

            // The same IR type the built-in Texture2D lowers to, with the element as the sampled
            // type — which is all the backends need: OpTypeImage's sampled type and GLSL's
            // `utexture2D` prefix both come off IrTextureType.SampledType.
            case SampledTextureTypeSymbol sampled: {
                var element = LowerType(sampled.ElementType, syntax);
                return element.IsVoid
                    ? NotRepresentable(type, syntax)
                    : new IrTextureType(IrTextureDimension.Texture2D, element);
            }

            // Its own IR type rather than a flag on IrTextureType, because a sampled image and a
            // storage image are two descriptor types and two SPIR-V image types. The format is
            // already on the symbol — the binder folded the declaration's `[Format]` in — and it
            // has to survive to both backends, so it travels in the type.
            case StorageImageTypeSymbol { Format: { } format } image:
                return new IrStorageImageType(
                    image.IsVolume ? IrTextureDimension.Texture3D : IrTextureDimension.Texture2D,
                    LowerType(image.ElementType, syntax),
                    format
                );

            // Only reachable when RVN2123 already refused the declaration, which stops the
            // compilation before this runs; kept honest rather than assumed.
            case StorageImageTypeSymbol:
                return NotRepresentable(type, syntax);

            case NamedTypeSymbol { TypeKind: TypeKind.Enum }:
                // An enum is its underlying integer once the constants are folded.
                return IrScalarType.Int;

            case NamedTypeSymbol named when structs.TryGetValue(named, out var structType):
                return structType;

            case TupleTypeSymbol tuple:
                return LowerTuple(tuple, syntax);

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
            SpecialType.Int64 => IrScalarType.Int64,
            SpecialType.UInt64 => IrScalarType.UInt64,
            _ => null
        };

    /// <summary>
    ///     Lowers a tuple to a struct of its elements.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A tuple is the only way a Raven function returns two values: there are no <c>out</c>
    ///         parameters, and the IR has no by-reference arguments to build them from. So this is
    ///         not sugar — removing tuples would remove the capability.
    ///     </para>
    ///     <para>
    ///         The element names come from the symbol, which already gives an unnamed element
    ///         <c>Item1</c>, <c>Item2</c>, … — so member access needs nothing special and the
    ///         backends see an ordinary struct. One struct per distinct shape, named after its
    ///         element types so the name is stable across compilations rather than being a counter
    ///         that depends on the order types happened to be lowered in.
    ///     </para>
    /// </remarks>
    /// <param name="tuple">The tuple type to lower.</param>
    /// <param name="syntax">Where to report a element type that has no representation.</param>
    IrType LowerTuple(TupleTypeSymbol tuple, SyntaxNode? syntax) {
        if (tuples.TryGetValue(tuple, out var existing)) {
            return existing;
        }

        var fields = new List<IrField>(tuple.ElementTypes.Count);
        var members = tuple.GetMembers().OfType<FieldSymbol>().ToArray();

        foreach (var member in members) {
            var elementType = LowerType(member.Type, syntax);
            if (elementType.IsVoid) {
                // The element's own diagnostic has already been reported.
                return IrScalarType.Void;
            }

            fields.Add(new(member.Name, elementType));
        }

        var name = TupleName(fields);

        // A linked library's copy of the same shape, reused rather than duplicated. This is
        // necessary, not an optimisation: a tuple has no declaration to match on, so without it a
        // library function returning `(float, float)` would return a different type from the one the
        // caller's local holds, and storing the result would fail the verifier with two structs of
        // the same name. Matching by name is sound precisely because a tuple's name is derived from
        // its element types — the name *is* the structural identity.
        if (importedStructsByName.TryGetValue(name, out var linked)) {
            tuples[tuple] = linked;
            return linked;
        }

        var structType = new IrStructType(name);
        structType.SetFields([.. fields]);

        tuples[tuple] = structType;
        module.Add(structType);
        return structType;
    }

    /// <summary>
    ///     A deterministic, identifier-safe name for a tuple shape: <c>Tuple_f32_i32</c>.
    /// </summary>
    static string TupleName(IReadOnlyList<IrField> fields) {
        var parts = fields.Select(f => Identifier(f.Type.Name));
        return "Tuple_" + string.Join('_', parts);
    }

    /// <summary>An IR type name reduced to an identifier: <c>vec&lt;f32,3&gt;</c> → <c>vec_f32_3</c>.</summary>
    static string Identifier(string name) {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        while (cleaned.Contains("__", StringComparison.Ordinal)) {
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);
        }

        return cleaned.Trim('_');
    }

    IrType NotRepresentable(TypeSymbol type, SyntaxNode? syntax) {
        diagnostics.Add(LoweringDiagnostics.TypeNotRepresentable, LocationOf(syntax), type.ToDisplayString());
        return IrScalarType.Void;
    }
}
