// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     An iterable sequence of <see cref="ElementType" />, produced by a range
///     expression (<c>1..10</c>). It is what <c>for (i in …)</c> consumes alongside
///     arrays; Phase 3 lowers it to a counted loop.
/// </summary>
public sealed class SequenceTypeSymbol : TypeSymbol, IEquatable<SequenceTypeSymbol> {
    public TypeSymbol ElementType { get; }

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public SequenceTypeSymbol(TypeSymbol elementType) {
        ElementType = elementType;
    }

    public override string ToDisplayString() => $"{ElementType.ToDisplayString()}..";

    public bool Equals(SequenceTypeSymbol? other) => other is not null && ElementType.Equals(other.ElementType);

    public override bool Equals(object? obj) => Equals(obj as SequenceTypeSymbol);

    public override int GetHashCode() => HashCode.Combine(typeof(SequenceTypeSymbol), ElementType);
}
