// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>A tuple type: <c>(int, string)</c> or <c>(code: int, message: string)</c>.</summary>
public sealed class TupleTypeSymbol : TypeSymbol, IEquatable<TupleTypeSymbol> {
    readonly SynthesizedFieldSymbol[] elements;

    public IReadOnlyList<TypeSymbol> ElementTypes { get; }
    public IReadOnlyList<string?> ElementNames { get; }

    public override SymbolKind Kind => SymbolKind.TupleType;
    public override TypeKind TypeKind => TypeKind.Tuple;
    public override string Name => string.Empty;

    public TupleTypeSymbol(IReadOnlyList<TypeSymbol> elementTypes, IReadOnlyList<string?> elementNames) {
        ElementTypes = elementTypes;
        ElementNames = elementNames;

        elements = new SynthesizedFieldSymbol[elementTypes.Count];
        for (var i = 0; i < elementTypes.Count; i++) {
            // Unnamed elements are still reachable positionally as Item1, Item2, …
            var name = elementNames.Count > i && elementNames[i] is { Length: > 0 } given
                ? given
                : "Item" + (i + 1);
            elements[i] = new(this, name, elementTypes[i], true);
        }
    }

    public override IReadOnlyList<Symbol> GetMembers() => elements;

    public override string ToDisplayString() {
        var parts = ElementTypes.Select((type, index) => {
                var name = ElementNames.Count > index ? ElementNames[index] : null;
                return name is { Length: > 0 } ? $"{name}: {type.ToDisplayString()}" : type.ToDisplayString();
            }
        );

        return "(" + string.Join(", ", parts) + ")";
    }

    public bool Equals(TupleTypeSymbol? other) {
        if (other is null || ElementTypes.Count != other.ElementTypes.Count) {
            return false;
        }

        // Element names are not part of tuple identity, matching C#.
        for (var i = 0; i < ElementTypes.Count; i++) {
            if (!ElementTypes[i].Equals(other.ElementTypes[i])) {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as TupleTypeSymbol);

    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(typeof(TupleTypeSymbol));
        foreach (var type in ElementTypes) {
            hash.Add(type);
        }

        return hash.ToHashCode();
    }
}
