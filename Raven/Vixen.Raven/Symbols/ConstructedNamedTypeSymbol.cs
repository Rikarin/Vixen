// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>
///     A generic type with its arguments supplied — <c>Box&lt;int&gt;</c>. Members
///     are read through the definition and substituted on the way out, so
///     <c>Box&lt;int&gt;.Value</c> has type <c>int</c>.
/// </summary>
public sealed class ConstructedNamedTypeSymbol : NamedTypeSymbol, IEquatable<ConstructedNamedTypeSymbol> {
    readonly TypeMap map;
    readonly TypeSymbol[] typeArguments;
    Symbol[]? members;

    public override NamedTypeSymbol OriginalDefinition { get; }
    public override bool IsConstructed => true;
    public override string Name => OriginalDefinition.Name;
    public override TypeKind TypeKind => OriginalDefinition.TypeKind;
    public override SpecialType SpecialType => OriginalDefinition.SpecialType;
    public override Symbol? ContainingSymbol => OriginalDefinition.ContainingSymbol;    public override SyntaxNode? DeclaringSyntax => OriginalDefinition.DeclaringSyntax;
    public override IReadOnlyList<TypeParameterSymbol> TypeParameters => OriginalDefinition.TypeParameters;
    public override IReadOnlyList<TypeSymbol> TypeArguments => typeArguments;

    public override NamedTypeSymbol? BaseType =>
        OriginalDefinition.BaseType is { } baseType ? map.Substitute(baseType) as NamedTypeSymbol : null;

    public override IReadOnlyList<NamedTypeSymbol> Interfaces =>
        OriginalDefinition.Interfaces.Select(i => map.Substitute(i) as NamedTypeSymbol ?? i).ToArray();

    public ConstructedNamedTypeSymbol(NamedTypeSymbol definition, IReadOnlyList<TypeSymbol> typeArguments) {
        OriginalDefinition = definition;
        this.typeArguments = typeArguments.ToArray();
        map = new(definition.TypeParameters, this.typeArguments);
    }

    public override IReadOnlyList<Symbol> GetMembers() =>
        members ??= OriginalDefinition.GetMembers().Select(m => SubstitutedSymbols.Substitute(m, this, map)).ToArray();

    public bool Equals(ConstructedNamedTypeSymbol? other) =>
        other is not null
        && OriginalDefinition.Equals(other.OriginalDefinition)
        && typeArguments.Length == other.typeArguments.Length
        && !typeArguments.Where((t, i) => !t.Equals(other.typeArguments[i])).Any();

    public override bool Equals(object? obj) => Equals(obj as ConstructedNamedTypeSymbol);

    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(OriginalDefinition);
        foreach (var argument in typeArguments) {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }
}
