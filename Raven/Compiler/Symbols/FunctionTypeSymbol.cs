namespace Vixen.Raven.Symbols;

/// <summary>
/// The type of a lambda: <c>(int, float) -&gt; bool</c>. Raven has no delegate
/// declarations, so a structural function type is what a lambda's value carries.
/// </summary>
public sealed class FunctionTypeSymbol : TypeSymbol, IEquatable<FunctionTypeSymbol> {
    public FunctionTypeSymbol(IReadOnlyList<TypeSymbol> parameterTypes, TypeSymbol returnType) {
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
    }

    public IReadOnlyList<TypeSymbol> ParameterTypes { get; }
    public TypeSymbol ReturnType { get; }

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Class;

    public override string ToDisplayString() =>
        $"({string.Join(", ", ParameterTypes.Select(p => p.ToDisplayString()))}) -> {ReturnType.ToDisplayString()}";

    public bool Equals(FunctionTypeSymbol? other) =>
        other is not null
        && ReturnType.Equals(other.ReturnType)
        && ParameterTypes.Count == other.ParameterTypes.Count
        && !ParameterTypes.Where((t, i) => !t.Equals(other.ParameterTypes[i])).Any();

    public override bool Equals(object? obj) => Equals(obj as FunctionTypeSymbol);

    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(ReturnType);
        foreach (var type in ParameterTypes) {
            hash.Add(type);
        }

        return hash.ToHashCode();
    }
}
