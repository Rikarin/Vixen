using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>
///     A local variable introduced by <c>val</c>/<c>var</c>, a <c>for</c> loop
///     binding, or a pattern designation.
/// </summary>
public sealed class LocalSymbol : Symbol {
    public override SymbolKind Kind => SymbolKind.Local;
    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode? DeclaringSyntax { get; }

    public TypeSymbol Type { get; private set; }

    /// <summary>Declared with <c>val</c>: assignable exactly once, at its declaration.</summary>
    public bool IsReadOnly { get; }

    internal LocalSymbol(Symbol? container, string name, TypeSymbol type, bool isReadOnly, SyntaxNode? syntax) {
        ContainingSymbol = container;
        Name = name;
        Type = type;
        IsReadOnly = isReadOnly;
        DeclaringSyntax = syntax;
    }

    public override string ToDisplayString() => $"{Name}: {Type.ToDisplayString()}";

    /// <summary>Fills in the type of a local whose declaration omitted it (<c>val x = expr</c>).</summary>
    internal void SetInferredType(TypeSymbol type) => Type = type;
}
