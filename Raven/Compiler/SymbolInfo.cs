using Vixen.Raven.Symbols;

namespace Vixen.Raven;

/// <summary>
/// What a name or expression referred to. When binding could not settle on one
/// symbol — an unresolved overload, say — <see cref="Symbol"/> is null and the
/// possibilities are in <see cref="CandidateSymbols"/>.
/// </summary>
public readonly struct SymbolInfo {
    public static readonly SymbolInfo None = new(null, []);

    internal SymbolInfo(Symbol? symbol, IReadOnlyList<Symbol> candidates) {
        Symbol = symbol;
        CandidateSymbols = candidates;
    }

    public Symbol? Symbol { get; }

    public IReadOnlyList<Symbol> CandidateSymbols { get; }

    public bool IsEmpty => Symbol is null && CandidateSymbols.Count == 0;
}

/// <summary>
/// An expression's own type and the type its context converted it to. For
/// <c>val x: float = 1</c> the literal's <see cref="Type"/> is <c>int</c> and its
/// <see cref="ConvertedType"/> is <c>float</c>.
/// </summary>
public readonly struct TypeInfo {
    public static readonly TypeInfo None = new(null, null);

    internal TypeInfo(TypeSymbol? type, TypeSymbol? convertedType) {
        Type = type;
        ConvertedType = convertedType ?? type;
    }

    public TypeSymbol? Type { get; }

    public TypeSymbol? ConvertedType { get; }

    public bool IsEmpty => Type is null;
}
