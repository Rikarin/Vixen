// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Syntax;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>Resolution of type syntax to <see cref="TypeSymbol" />s.</summary>
public abstract partial class Binder {
    /// <summary>
    ///     Resolves a type annotation. Failures report once and yield
    ///     <see cref="ErrorTypeSymbol" /> so callers need no null checks.
    /// </summary>
    public TypeSymbol BindType(TypeSyntax? syntax) {
        if (syntax is null) {
            return ErrorTypeSymbol.Instance;
        }

        var type = BindTypeCore(syntax);
        Context.Record(syntax, new BoundTypeExpression(syntax, type));
        return type;
    }

    TypeSymbol BindTypeCore(TypeSyntax syntax) {
        switch (syntax) {
            case PredefinedTypeSyntax predefined: {
                var type = BuiltInTypes.FromKeyword(predefined.Keyword.Kind);
                if (type is null) {
                    Report(SemanticDiagnostics.TypeNotFound, syntax, predefined.Keyword.Text);
                    return ErrorTypeSymbol.Instance;
                }

                return type;
            }

            case IdentifierNameSyntax identifier:
                return BindNamedType(identifier.Identifier.ValueText, [], identifier);

            case GenericNameSyntax generic: {
                var arguments = generic.TypeArgumentList.Arguments.Select(BindType).ToArray();
                return BindNamedType(generic.Identifier.ValueText, arguments, generic);
            }

            case QualifiedNameSyntax qualified:
                return BindQualifiedType(qualified);

            case ArrayTypeSyntax array: {
                var element = BindType(array.ElementType);

                // `T[a][b]` nests right to left: `b` is the inner extent, so the type reads
                // "`a` arrays of `b` elements" — the same order C and GLSL give it. The order is
                // only observable once a rank carries a size; for `T[][]` either way is one type.
                for (var i = array.RankSpecifiers.Count - 1; i >= 0; i--) {
                    if (array.RankSpecifiers[i] is not { } rank) {
                        continue;
                    }

                    var size = BindArraySize(rank.Size);

                    // No length, and — for everything but a texture — nowhere one could go: both
                    // targets need a constant extent to lay an array out, and neither has a
                    // by-reference parameter that would let one travel without a size. Named here
                    // rather than at emit time, where the message would be about a lowered type and
                    // appear twice.
                    //
                    // A texture is the exception, and it is not a loosening of the rule but the rule
                    // meeting a case it did not have. An array of textures is an array of
                    // *descriptors*, which are not laid out at all — there is no stride, nothing to
                    // pack, and nothing for the host to size a buffer from. It is the bindless table
                    // (`docs/plan/23-bindless-materials.md`), and the length it does not have is exactly the
                    // point: the shader indexes it with a number and never asks how long it is.
                    if (size is null && rank.Size is null && !IsUnsizedTextureArray(element, rank)) {
                        Report(
                            SemanticDiagnostics.ArrayNeedsLength,
                            rank,
                            new ArrayTypeSymbol(element, rank.Commas.Count + 1).ToDisplayString(),
                            element.ToDisplayString(),
                            BufferTypeSymbol.ReadOnlyName
                        );
                    }

                    element = new ArrayTypeSymbol(element, rank.Commas.Count + 1, size);
                }

                return element;
            }

            case TupleTypeSyntax tuple: {
                List<TypeSymbol> types = [];
                List<string?> names = [];
                foreach (var element in tuple.Elements) {
                    types.Add(BindType(element.Type));
                    names.Add(element.Identifier?.ValueText);
                }

                return new TupleTypeSymbol(types, names);
            }

            default:
                Report(SemanticDiagnostics.NotAType, syntax, syntax.ToString().Trim());
                return ErrorTypeSymbol.Instance;
        }
    }

    /// <summary>
    ///     Whether this rank is the one unsized array both targets can express: a descriptor array
    ///     of textures.
    /// </summary>
    /// <param name="element">What the rank is an array of, as bound so far.</param>
    /// <param name="rank">The rank specifier, which must be the single one and have no commas.</param>
    /// <remarks>
    ///     <para>
    ///         One dimension only, and no <c>[,]</c>. A descriptor array is flat in both targets —
    ///         SPIR-V's <c>OpTypeRuntimeArray</c> takes one element type and GLSL writes one
    ///         <c>[]</c> — so <c>Texture2D[][]</c> and <c>Texture2D[,]</c> are shapes with nowhere to
    ///         go, and they keep <c>RVN2126</c> rather than being silently flattened into the one
    ///         that does. The array test is not redundant with the comma test: an
    ///         <see cref="ArrayTypeSymbol" /> reports its <em>element's</em> resource kind, so a
    ///         <c>Texture2D[]</c> reached as the element of an outer rank answers "texture" and would
    ///         let the second <c>[]</c> through.
    ///     </para>
    ///     <para>
    ///         The <em>position</em> is not checked here and cannot be: this is type resolution, and
    ///         a type does not know whether it is annotating a shader field or a local. A resource in
    ///         a struct is <c>RVN2053</c> and a stream of one is <c>RVN2103</c>; a resource-typed
    ///         <em>local</em> or parameter is accepted by nothing downstream and reported by nothing
    ///         here, which is a gap this shares with a plain <c>Texture2D</c> rather than one it
    ///         opens — a table is refused in exactly the places a texture is.
    ///     </para>
    /// </remarks>
    static bool IsUnsizedTextureArray(TypeSymbol element, ArrayRankSpecifierSyntax rank) =>
        element is not ArrayTypeSymbol
        && element.ResourceKind == ResourceKind.Texture
        && rank.Commas.Count == 0;

    /// <summary>
    ///     Folds an array size to its element count, or null when there is no size to fold.
    ///     A size that will not fold is reported and treated as absent, so the array degrades to
    ///     unsized rather than propagating an error type through everything that mentions it.
    /// </summary>
    int? BindArraySize(ExpressionSyntax? syntax) {
        if (syntax is null) {
            return null;
        }

        var bound = BindValue(syntax);

        if (bound.Type.IsErrorType) {
            // Already reported by BindValue.
            return null;
        }

        if (ConstantEvaluator.Evaluate(bound) is not { } value) {
            Report(SemanticDiagnostics.ArraySizeNotConstant, syntax, syntax.ToString().Trim());
            return null;
        }

        // A uint size is as good as an int one; anything else is not a count.
        var count = value switch {
            int i => i,
            uint u when u <= int.MaxValue => (int)u,
            _ => (int?)null
        };

        if (count is null or <= 0) {
            Report(
                SemanticDiagnostics.ArraySizeNotPositive,
                syntax,
                syntax.ToString().Trim(),
                count is null
                    ? $"of type '{bound.Type.ToDisplayString()}'"
                    : count.Value.ToString(CultureInfo.InvariantCulture)
            );
            return null;
        }

        return count;
    }

    TypeSymbol BindNamedType(string name, IReadOnlyList<TypeSymbol> typeArguments, SyntaxNode syntax) {
        if (BindBufferType(name, typeArguments, syntax) is { } buffer) {
            return buffer;
        }

        if (BindStorageImageType(name, typeArguments, syntax) is { } image) {
            return image;
        }

        if (BindSampledTextureType(name, typeArguments, syntax) is { } sampled) {
            return sampled;
        }

        var type = LookupType(name, typeArguments.Count);

        if (type is null) {
            // Found under a different arity? Say so rather than "not found".
            if (LookupAnyArity(name) is { } mismatched) {
                Report(
                    SemanticDiagnostics.WrongTypeArgumentCount,
                    syntax,
                    name,
                    mismatched.Arity,
                    typeArguments.Count
                );
                return ErrorTypeSymbol.Instance;
            }

            Report(SemanticDiagnostics.TypeNotFound, syntax, name);
            return ErrorTypeSymbol.Instance;
        }

        return Construct(type, typeArguments, syntax);
    }

    /// <summary>
    ///     Builds a <c>Buffer&lt;T&gt;</c> or <c>RWBuffer&lt;T&gt;</c>, or null when the name is
    ///     neither.
    /// </summary>
    /// <remarks>
    ///     Intercepted before the scope lookup, because the angle brackets are the only thing this
    ///     shares with a generic type: there is no declaration to find, and no substitution to do.
    ///     It is constructed structurally, exactly as <c>T[4]</c> is — so it never reaches the
    ///     monomorphiser at all.
    /// </remarks>
    TypeSymbol? BindBufferType(string name, IReadOnlyList<TypeSymbol> typeArguments, SyntaxNode syntax) {
        var writable = name == BufferTypeSymbol.ReadWriteName;

        if (!writable && name != BufferTypeSymbol.ReadOnlyName) {
            return null;
        }

        if (typeArguments.Count != 1) {
            Report(SemanticDiagnostics.WrongTypeArgumentCount, syntax, name, 1, typeArguments.Count);
            return ErrorTypeSymbol.Instance;
        }

        var element = typeArguments[0];

        if (element.IsErrorType) {
            return ErrorTypeSymbol.Instance;
        }

        // A buffer's element has to have a memory layout the host can write, which rules out the
        // opaque resources (a descriptor is not a value), void, and a nested buffer. Reported here
        // rather than at emit time, so the message names the element rather than a lowered type.
        if (element.ResourceKind is not ResourceKind.None || element.IsVoid || element is BufferTypeSymbol) {
            Report(SemanticDiagnostics.BufferElementNotStorable, syntax, element.ToDisplayString(), name);
            return ErrorTypeSymbol.Instance;
        }

        return new BufferTypeSymbol(element, writable);
    }

    /// <summary>
    ///     Builds a <c>RWTexture2D&lt;T&gt;</c> or <c>RWTexture3D&lt;T&gt;</c>, or null when the
    ///     name is neither.
    /// </summary>
    /// <remarks>
    ///     Structural, exactly as a buffer is. The element has to be a four-lane vector of
    ///     <c>float</c>, <c>int</c> or <c>uint</c> — not a narrowing of the format's channel count —
    ///     because <c>imageLoad</c> and <c>OpImageRead</c> both hand back four components whatever
    ///     the format stores.
    /// </remarks>
    TypeSymbol? BindStorageImageType(string name, IReadOnlyList<TypeSymbol> typeArguments, SyntaxNode syntax) {
        var volume = name == StorageImageTypeSymbol.Texture3DName;

        if (!volume && name != StorageImageTypeSymbol.Texture2DName) {
            return null;
        }

        if (typeArguments.Count != 1) {
            Report(SemanticDiagnostics.WrongTypeArgumentCount, syntax, name, 1, typeArguments.Count);
            return ErrorTypeSymbol.Instance;
        }

        var element = typeArguments[0];

        if (element.IsErrorType) {
            return ErrorTypeSymbol.Instance;
        }

        if (element is not PrimitiveTypeSymbol {
                TypeKind: TypeKind.Vector, ComponentCount: 4,
                ComponentSpecialType: SpecialType.Float or SpecialType.Int or SpecialType.UInt
            } texel) {
            Report(
                SemanticDiagnostics.StorageImageElementNotATexel,
                syntax,
                element.ToDisplayString(),
                name
            );

            return ErrorTypeSymbol.Instance;
        }

        return new StorageImageTypeSymbol(texel, volume);
    }

    /// <summary>
    ///     Builds a <c>Texture2D&lt;int4&gt;</c> or <c>Texture2D&lt;uint4&gt;</c>, or null when the
    ///     name is not <c>Texture2D</c> or carries no type arguments.
    /// </summary>
    /// <remarks>
    ///     Null for the bare name, deliberately: <c>Texture2D</c> with no arguments falls through to
    ///     the scope lookup and resolves to the float-sampled built-in exactly as it always has. The
    ///     angle-bracket form is intercepted here, structurally, on the terms
    ///     <see cref="BindStorageImageType" /> sets — and a float element is refused rather than
    ///     accepted as a second spelling of the built-in, so the two cannot drift apart.
    /// </remarks>
    TypeSymbol? BindSampledTextureType(string name, IReadOnlyList<TypeSymbol> typeArguments, SyntaxNode syntax) {
        if (name != SampledTextureTypeSymbol.Texture2DName || typeArguments.Count == 0) {
            return null;
        }

        if (typeArguments.Count != 1) {
            Report(SemanticDiagnostics.WrongTypeArgumentCount, syntax, name, 1, typeArguments.Count);
            return ErrorTypeSymbol.Instance;
        }

        var element = typeArguments[0];

        if (element.IsErrorType) {
            return ErrorTypeSymbol.Instance;
        }

        if (element is not PrimitiveTypeSymbol {
                TypeKind: TypeKind.Vector, ComponentCount: 4,
                ComponentSpecialType: SpecialType.Int or SpecialType.UInt
            } texel) {
            Report(
                SemanticDiagnostics.SampledTextureElementNotIntegral,
                syntax,
                element.ToDisplayString(),
                name
            );

            return ErrorTypeSymbol.Instance;
        }

        return new SampledTextureTypeSymbol(texel);
    }

    TypeSymbol Construct(TypeSymbol type, IReadOnlyList<TypeSymbol> typeArguments, SyntaxNode syntax) {
        if (typeArguments.Count == 0 || type is not NamedTypeSymbol named) {
            return type;
        }

        CheckConstraints(named, typeArguments, syntax);
        return new ConstructedNamedTypeSymbol(named, typeArguments);
    }

    /// <summary>
    ///     Checks each type argument against its parameter's <c>where</c> clause. A
    ///     constraint names a base or a protocol; an argument that is neither the
    ///     constraint, derived from it, nor an implementer is <c>RVN2096</c>.
    /// </summary>
    void CheckConstraints(NamedTypeSymbol type, IReadOnlyList<TypeSymbol> typeArguments, SyntaxNode syntax) {
        var parameters = type.TypeParameters;
        for (var i = 0; i < typeArguments.Count && i < parameters.Count; i++) {
            foreach (var constraint in parameters[i].ConstraintTypes) {
                if (!SatisfiesConstraint(typeArguments[i], constraint)) {
                    Report(
                        SemanticDiagnostics.TypeArgumentDoesNotSatisfyConstraint,
                        syntax,
                        typeArguments[i].ToDisplayString(),
                        constraint.ToDisplayString(),
                        parameters[i].Name,
                        type.Name
                    );
                }
            }
        }
    }

    static bool SatisfiesConstraint(TypeSymbol argument, TypeSymbol constraint) {
        // An argument that already failed to bind suppresses further errors.
        if (argument.IsErrorType || constraint.IsErrorType) {
            return true;
        }

        if (argument.IsSubtypeOf(constraint)) {
            return true;
        }

        // A type parameter passed through satisfies what its own constraints imply.
        return argument is TypeParameterSymbol parameter
            && parameter.ConstraintTypes.Any(c => SatisfiesConstraint(c, constraint));
    }

    NamedTypeSymbol? LookupAnyArity(string name) {
        for (var binder = this; binder is not null; binder = binder.Next) {
            List<Symbol> results = [];
            binder.LookupInScope(name, results);
            foreach (var symbol in results) {
                if (symbol is NamedTypeSymbol named) {
                    return named;
                }
            }
        }

        return null;
    }

    TypeSymbol BindQualifiedType(QualifiedNameSyntax syntax) {
        var container = BindNamespaceOrTypeQualifier(syntax.Left);
        if (container is null) {
            return ErrorTypeSymbol.Instance;
        }

        var (name, typeArguments) = SplitSimpleName(syntax.Right);

        var member = container switch {
            NamespaceSymbol ns => ns.GetTypeMember(name, typeArguments.Count) as TypeSymbol,
            TypeSymbol type => LookupMembers(type, name)
                .OfType<NamedTypeSymbol>()
                .FirstOrDefault(t => t.Arity == typeArguments.Count),
            _ => null
        };

        if (member is null) {
            Report(SemanticDiagnostics.TypeNotFound, syntax, syntax.ToString().Trim());
            return ErrorTypeSymbol.Instance;
        }

        return Construct(member, typeArguments, syntax);
    }

    /// <summary>Resolves the left side of a dotted name to a namespace or a type.</summary>
    Symbol? BindNamespaceOrTypeQualifier(NameSyntax syntax) {
        switch (syntax) {
            case QualifiedNameSyntax qualified: {
                var container = BindNamespaceOrTypeQualifier(qualified.Left);
                if (container is null) {
                    return null;
                }

                var (name, typeArguments) = SplitSimpleName(qualified.Right);

                var member = container switch {
                    NamespaceSymbol ns =>
                        (Symbol?)ns.GetTypeMember(name, typeArguments.Count) ?? ns.GetNamespace(name),
                    TypeSymbol type => LookupMembers(type, name)
                        .OfType<NamedTypeSymbol>()
                        .FirstOrDefault(t => t.Arity == typeArguments.Count),
                    _ => null
                };

                if (member is null) {
                    Report(SemanticDiagnostics.TypeNotFound, qualified, qualified.ToString().Trim());
                    return null;
                }

                return member is TypeSymbol type2 ? Construct(type2, typeArguments, qualified) : member;
            }

            case SimpleNameSyntax simple: {
                var (name, typeArguments) = SplitSimpleName(simple);
                var type = LookupType(name, typeArguments.Count);
                if (type is not null) {
                    return Construct(type, typeArguments, simple);
                }

                var ns = LookupNamespace(name);
                if (ns is not null) {
                    return ns;
                }

                Report(SemanticDiagnostics.TypeNotFound, simple, name);
                return null;
            }

            default:
                Report(SemanticDiagnostics.NotAType, syntax, syntax.ToString().Trim());
                return null;
        }
    }

    (string Name, IReadOnlyList<TypeSymbol> TypeArguments) SplitSimpleName(SimpleNameSyntax syntax) =>
        syntax is GenericNameSyntax generic
            ? (generic.Identifier.ValueText, generic.TypeArgumentList.Arguments.Select(BindType).ToArray())
            : (syntax.Identifier.ValueText, []);
}
