namespace Vixen.Raven.Symbols;

/// <summary>An array type: <c>T[]</c>, <c>T[,]</c>, <c>T[][]</c>.</summary>
public sealed class ArrayTypeSymbol : TypeSymbol, IEquatable<ArrayTypeSymbol> {
    public TypeSymbol ElementType { get; }

    /// <summary>Number of dimensions; <c>T[,]</c> has rank 2.</summary>
    public int Rank { get; }

    public override SymbolKind Kind => SymbolKind.ArrayType;
    public override TypeKind TypeKind => TypeKind.Array;
    public override string Name => string.Empty;
    public override Symbol? ContainingSymbol => null;

    public ArrayTypeSymbol(TypeSymbol elementType, int rank = 1) {
        ElementType = elementType;
        Rank = rank;
    }

    /// <summary>Arrays expose <c>Length</c>; indexing is handled by the binder.</summary>
    public override IReadOnlyList<Symbol> GetMembers() =>
        [new SynthesizedFieldSymbol(this, "Length", BuiltInTypes.Int, true)];

    public override string ToDisplayString() => ElementType.ToDisplayString() + "[" + new string(',', Rank - 1) + "]";

    public bool Equals(ArrayTypeSymbol? other) =>
        other is not null && Rank == other.Rank && ElementType.Equals(other.ElementType);

    public override bool Equals(object? obj) => Equals(obj as ArrayTypeSymbol);

    public override int GetHashCode() => HashCode.Combine(ElementType, Rank);
}
