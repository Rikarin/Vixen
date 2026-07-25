// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>A <c>val</c>/<c>var</c> member of a type, or an enum member.</summary>
public abstract class FieldSymbol : Symbol {
    public override SymbolKind Kind => SymbolKind.Field;

    public abstract TypeSymbol Type { get; }

    /// <summary>Declared with <c>val</c> (or synthesized read-only) — assignable only in an initializer or constructor.</summary>
    public virtual bool IsReadOnly => false;

    /// <summary>Declared <c>const</c>: a compile-time constant.</summary>
    public virtual bool IsConst => false;

    /// <summary>The folded value of a <c>const</c> field, when it could be computed.</summary>
    public virtual object? ConstantValue => null;

    /// <summary>
    ///     Declared <c>[Permutation]</c>: a constant whose value the caller supplies per
    ///     effect variant rather than the source fixing it.
    /// </summary>
    /// <remarks>
    ///     A permutation key behaves as a constant everywhere downstream —
    ///     <see cref="IsConst" /> is true and <see cref="ConstantValue" /> answers with the
    ///     supplied value, or the declared default when none was supplied — so folding and
    ///     dead-branch elimination need no special case for it.
    /// </remarks>
    public virtual bool IsPermutation => false;

    /// <summary>
    ///     Declared <c>compose</c>: a protocol-typed slot filled by a concrete shader chosen
    ///     when the shader is compiled.
    /// </summary>
    public virtual bool IsCompose => false;

    /// <summary>
    ///     The shader bound to this <c>compose</c> slot, or null when the field is not a slot
    ///     or nothing valid is bound. Calls through the slot go straight to this type's
    ///     members, so there is no dispatch at runtime.
    /// </summary>
    public virtual NamedTypeSymbol? ComposedType => null;

    /// <summary>How this field binds on the GPU when it is a shader member.</summary>
    public virtual ResourceKind ResourceKind => ResourceKind.None;

    /// <summary>The pipeline semantic from a <c>[Semantic("…")]</c> attribute, or null.</summary>
    public virtual string? SemanticName => null;

    public override string ToDisplayString() => ContainingType is { } type ? $"{type.ToDisplayString()}.{Name}" : Name;
}

/// <summary>
///     A field the compiler makes up rather than reading from source: vector
///     swizzles (<c>v.xy</c>), tuple elements, <c>array.Length</c>.
/// </summary>
public sealed class SynthesizedFieldSymbol : FieldSymbol {
    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override TypeSymbol Type { get; }
    public override bool IsReadOnly { get; }
    public override Accessibility DeclaredAccessibility => Accessibility.Public;

    internal SynthesizedFieldSymbol(TypeSymbol containingType, string name, TypeSymbol type, bool isReadOnly) {
        ContainingSymbol = containingType;
        Name = name;
        Type = type;
        IsReadOnly = isReadOnly;
    }
}
