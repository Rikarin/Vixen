// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     A named type the compiler supplies rather than reading from source:
///     <c>string</c>, <c>object</c>, and the GPU resource types. Instances are
///     singletons owned by <see cref="BuiltInTypes" />.
/// </summary>
public sealed class BuiltInNamedTypeSymbol : NamedTypeSymbol {
    Symbol[] members = [];

    public override string Name { get; }
    public override SpecialType SpecialType { get; }
    public override TypeKind TypeKind { get; }

    /// <summary>How a field of this type binds on the GPU.</summary>
    public override ResourceKind ResourceKind { get; }

    internal BuiltInNamedTypeSymbol(
        string name,
        SpecialType specialType,
        TypeKind typeKind,
        ResourceKind resourceKind = ResourceKind.None
    ) {
        Name = name;
        SpecialType = specialType;
        TypeKind = typeKind;
        ResourceKind = resourceKind;
    }

    public override IReadOnlyList<Symbol> GetMembers() => members;

    public override string ToDisplayString() => Name;

    internal void SetMembers(Symbol[] value) => members = value;
}
