namespace Vixen.Raven.Symbols;

/// <summary>
///     Replaces type parameters with type arguments. One map is built per
///     construction (<c>Box&lt;int&gt;</c>) and reused for every signature read
///     through it.
/// </summary>
public sealed class TypeMap {
    readonly Dictionary<TypeParameterSymbol, TypeSymbol> substitutions = [];

    public bool IsEmpty => substitutions.Count == 0;

    public TypeMap(IReadOnlyList<TypeParameterSymbol> parameters, IReadOnlyList<TypeSymbol> arguments) {
        for (var i = 0; i < parameters.Count && i < arguments.Count; i++) {
            substitutions[parameters[i]] = arguments[i];
        }
    }

    /// <summary>The type with every mapped parameter replaced, recursively.</summary>
    public TypeSymbol Substitute(TypeSymbol type) {
        switch (type) {
            case TypeParameterSymbol parameter:
                return substitutions.GetValueOrDefault(parameter, parameter);

            case ArrayTypeSymbol array: {
                var element = Substitute(array.ElementType);
                return element.Equals(array.ElementType) ? array : new(element, array.Rank);
            }


            case TupleTypeSymbol tuple: {
                var elements = tuple.ElementTypes.Select(Substitute).ToArray();
                return elements.SequenceEqual(tuple.ElementTypes)
                    ? tuple
                    : new(elements, tuple.ElementNames);
            }


            case NamedTypeSymbol { IsConstructed: true } constructed: {
                var arguments = constructed.TypeArguments.Select(Substitute).ToArray();
                return arguments.SequenceEqual(constructed.TypeArguments)
                    ? constructed
                    : new ConstructedNamedTypeSymbol(constructed.OriginalDefinition, arguments);
            }

            default:
                return type;
        }
    }
}
