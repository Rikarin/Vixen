namespace Vixen.Raven.Symbols;

/// <summary>
/// <c>T?</c> — a type that additionally admits <c>null</c>. Wrapping is idempotent:
/// the binder never nests one nullable inside another.
/// </summary>
public sealed class NullableTypeSymbol : TypeSymbol, IEquatable<NullableTypeSymbol> {
    public NullableTypeSymbol(TypeSymbol underlyingType) => UnderlyingType = underlyingType;

    public TypeSymbol UnderlyingType { get; }

    public override SymbolKind Kind => SymbolKind.NullableType;
    public override TypeKind TypeKind => TypeKind.Nullable;
    public override string Name => UnderlyingType.Name;

    /// <summary>Members of the underlying type — access is unchecked in this phase.</summary>
    public override IReadOnlyList<Symbol> GetMembers() => UnderlyingType.GetMembers();

    public override IReadOnlyList<Symbol> GetMembers(string name) => UnderlyingType.GetMembers(name);

    public override string ToDisplayString() => UnderlyingType.ToDisplayString() + "?";

    public bool Equals(NullableTypeSymbol? other) =>
        other is not null && UnderlyingType.Equals(other.UnderlyingType);

    public override bool Equals(object? obj) => Equals(obj as NullableTypeSymbol);

    public override int GetHashCode() => HashCode.Combine(typeof(NullableTypeSymbol), UnderlyingType);
}
