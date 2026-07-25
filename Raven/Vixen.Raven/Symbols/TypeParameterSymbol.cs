using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>A generic parameter (<c>T</c>) declared by a type or method.</summary>
public sealed class TypeParameterSymbol : TypeSymbol {
    TypeSymbol[] constraintTypes = [];

    public override SymbolKind Kind => SymbolKind.TypeParameter;
    public override string Name { get; }
    public override TypeKind TypeKind => TypeKind.TypeParameter;
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode? DeclaringSyntax { get; }

    /// <summary>Position in the declaring type's or method's parameter list.</summary>
    public int Ordinal { get; }

    /// <summary>Types named by the parameter's <c>where</c> clause.</summary>
    public IReadOnlyList<TypeSymbol> ConstraintTypes => constraintTypes;

    public override IReadOnlyList<NamedTypeSymbol> Interfaces => constraintTypes.OfType<NamedTypeSymbol>().ToArray();

    internal TypeParameterSymbol(Symbol container, string name, int ordinal, SyntaxNode? syntax = null) {
        ContainingSymbol = container;
        Name = name;
        Ordinal = ordinal;
        DeclaringSyntax = syntax;
    }

    /// <summary>Members reachable on a value of this parameter come from its constraints.</summary>
    public override IReadOnlyList<Symbol> GetMembers(string name) {
        foreach (var constraint in constraintTypes) {
            var members = constraint.GetMembers(name);
            if (members.Count > 0) {
                return members;
            }
        }

        return [];
    }

    internal void SetConstraintTypes(TypeSymbol[] types) => constraintTypes = types;
}
