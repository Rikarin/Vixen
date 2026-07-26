// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Binding;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>
///     A <c>val</c> type parameter — <c>shader Blur&lt;val TapCount: int&gt;</c> — which
///     parameterises a shader by a compile-time constant rather than by a type.
/// </summary>
/// <remarks>
///     <para>
///         Modelled as a constant <em>member</em> rather than as a
///         <see cref="TypeParameterSymbol" />, because it is not a type: the shader's arity is
///         unchanged and every existing generic path is untouched. The body sees an ordinary
///         readable name, and because <see cref="IsConst" /> is true with a known
///         <see cref="ConstantValue" />, folding and dead-branch elimination handle it with no
///         special case — the same route a <c>[Permutation]</c> field takes.
///     </para>
///     <para>
///         The difference from a permutation field is that this has no default: a value is part
///         of the shader's signature, so compiling without one is an error rather than falling
///         back. Values arrive through the same <see cref="PermutationValues" /> channel and are
///         reported in <c>Compilation.UsedPermutationKeys</c>, because they affect codegen and
///         therefore belong in the cache key.
///     </para>
/// </remarks>
public sealed class SourceValueParameterSymbol : FieldSymbol {
    readonly Binder binder;
    readonly TypeParameterSyntax syntax;

    TypeSymbol? type;

    public override string Name => syntax.Identifier.ValueText;
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => syntax;

    /// <summary>Position in the declaring shader's parameter list.</summary>
    public int Ordinal { get; }

    public override TypeSymbol Type =>
        type ??= syntax.Type is { } annotation ? binder.BindType(annotation) : ErrorTypeSymbol.Instance;

    public override bool IsConst => true;
    public override bool IsStatic => true;
    public override bool IsReadOnly => true;
    public override bool IsValueParameter => true;
    /// <summary>A parameter is never data on the target — it is folded at every use.</summary>
    public override ResourceKind ResourceKind => ResourceKind.None;

    public override object? ConstantValue {
        get {
            // Reading the value is the moment the output starts depending on it.
            binder.Compilation.RecordPermutationUse(Name);

            var supplied = ContainingType is { } containing
                ? binder.Compilation.PermutationValues.GetValueOrDefault($"{containing.Name}.{Name}")
                  ?? binder.Compilation.PermutationValues.GetValueOrDefault(Name)
                : binder.Compilation.PermutationValues.GetValueOrDefault(Name);

            // A missing or mismatched value is reported once, at the declaration. Falling back
            // to the type's zero keeps binding well-typed rather than cascading nulls.
            return supplied is not null && MatchesDeclaredType(supplied) ? supplied : ZeroOfDeclaredType();
        }
    }

    internal SourceValueParameterSymbol(
        NamedTypeSymbol containingType,
        TypeParameterSyntax syntax,
        int ordinal,
        Binder binder
    ) {
        ContainingSymbol = containingType;
        this.syntax = syntax;
        Ordinal = ordinal;
        this.binder = binder;
    }

    /// <summary>Whether a supplied value has this parameter's declared type.</summary>
    internal bool MatchesDeclaredType(object value) =>
        (Type as PrimitiveTypeSymbol)?.SpecialType switch {
            SpecialType.Bool => value is bool,
            SpecialType.Int => value is int,
            SpecialType.UInt => value is uint,
            _ => false
        };

    object? ZeroOfDeclaredType() =>
        (Type as PrimitiveTypeSymbol)?.SpecialType switch {
            SpecialType.Bool => false,
            SpecialType.Int => 0,
            SpecialType.UInt => 0u,
            _ => null
        };
}
