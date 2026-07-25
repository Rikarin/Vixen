// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     A <c>var</c> member with accessors. Raven's accessor set is wider than C#'s:
///     besides <c>get</c>/<c>set</c> a property may observe assignment through
///     <c>willSet</c>/<c>didSet</c>.
/// </summary>
public abstract class PropertySymbol : Symbol {
    public override SymbolKind Kind => SymbolKind.Property;

    public abstract TypeSymbol Type { get; }

    public abstract bool HasGetter { get; }

    public abstract bool HasSetter { get; }

    /// <summary>True for an expression-bodied or getter-only property.</summary>
    public bool IsReadOnly => !HasSetter;

    /// <summary>Parameters of an indexer; empty for an ordinary property.</summary>
    public virtual IReadOnlyList<ParameterSymbol> Parameters => [];

    public bool IsIndexer => Parameters.Count > 0;

    public override string ToDisplayString() => ContainingType is { } type ? $"{type.ToDisplayString()}.{Name}" : Name;
}
