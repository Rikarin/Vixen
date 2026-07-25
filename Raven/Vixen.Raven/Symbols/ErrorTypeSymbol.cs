// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     Stand-in for a type that could not be resolved. The binder produces it once,
///     reports one diagnostic, and then lets it flow through the rest of the
///     expression so a single mistake does not cascade into a wall of errors.
/// </summary>
public sealed class ErrorTypeSymbol : NamedTypeSymbol {
    public static readonly ErrorTypeSymbol Instance = new("?");

    public override SymbolKind Kind => SymbolKind.ErrorType;
    public override string Name { get; }
    public override TypeKind TypeKind => TypeKind.Error;

    ErrorTypeSymbol(string name) {
        Name = name;
    }

    public override string ToDisplayString() => Name;
}
