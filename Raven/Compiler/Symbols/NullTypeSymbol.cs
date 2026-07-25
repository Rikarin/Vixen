namespace Vixen.Raven.Symbols;

/// <summary>
/// The type of the <c>null</c> literal before it is converted. It is not
/// nameable in source; the binder produces it and conversions turn it into
/// whatever the context expects.
/// </summary>
public sealed class NullTypeSymbol : TypeSymbol {
    public static readonly NullTypeSymbol Instance = new();

    NullTypeSymbol() { }

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name => "null";
    public override TypeKind TypeKind => TypeKind.Class;

    public override string ToDisplayString() => "null";
}
