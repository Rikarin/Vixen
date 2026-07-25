
namespace Vixen.Raven.Symbols;

/// <summary>What kind of entity a <see cref="Symbol" /> denotes.</summary>
public enum SymbolKind {
    Namespace,
    NamedType,
    ArrayType,
    NullableType,
    TupleType,
    TypeParameter,
    ErrorType,
    Method,
    Field,
    Property,
    Parameter,
    Local
}
