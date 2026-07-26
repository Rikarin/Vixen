// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>
///     Root of the semantic model's entity hierarchy — a namespace, type, member,
///     parameter or local. Mirrors Roslyn's symbol model; the concrete classes are
///     the public surface (Raven does not split them into <c>ISymbol</c> interfaces
///     plus internal implementations).
/// </summary>
public abstract class Symbol {
    public abstract SymbolKind Kind { get; }

    public abstract string Name { get; }

    /// <summary>The symbol this one is declared in (namespace, type, or member).</summary>
    public virtual Symbol? ContainingSymbol => null;

    /// <summary>The nearest enclosing type, or null for namespaces and top-level entities.</summary>
    public NamedTypeSymbol? ContainingType {
        get {
            for (var symbol = ContainingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
                if (symbol is NamedTypeSymbol type) {
                    return type;
                }
            }

            return null;
        }
    }

    /// <summary>The nearest enclosing namespace, or null for intrinsic symbols.</summary>
    public NamespaceSymbol? ContainingNamespace {
        get {
            for (var symbol = ContainingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
                if (symbol is NamespaceSymbol ns) {
                    return ns;
                }
            }

            return null;
        }
    }

    public virtual bool IsStatic => false;

    /// <summary>True for symbols the compiler creates rather than reads from source.</summary>
    public virtual bool IsImplicitlyDeclared => DeclaringSyntax is null;

    /// <summary>The syntax that declared this symbol, or null when synthesized.</summary>
    public virtual SyntaxNode? DeclaringSyntax => null;

    /// <summary>A readable, fully-qualified-enough name for diagnostics and tests.</summary>
    public virtual string ToDisplayString() => Name;

    public override string ToString() => ToDisplayString();
}
