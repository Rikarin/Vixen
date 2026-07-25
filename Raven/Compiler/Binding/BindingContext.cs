using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>
/// State shared by every binder in one binding session: where diagnostics go,
/// and the syntax → bound-node / syntax → symbol maps the
/// <see cref="SemanticModel"/> answers queries from.
/// </summary>
public sealed class BindingContext(Compilation compilation, DiagnosticBag diagnostics) {
    public Compilation Compilation { get; } = compilation;

    public DiagnosticBag Diagnostics { get; } = diagnostics;

    /// <summary>Bound node produced for each syntax node that was bound.</summary>
    public Dictionary<SyntaxNode, BoundNode> BoundNodes { get; } = [];

    /// <summary>Symbol declared by each declaration syntax node.</summary>
    public Dictionary<SyntaxNode, Symbol> DeclaredSymbols { get; } = [];

    /// <summary>The type an expression was converted to by its surrounding context.</summary>
    public Dictionary<SyntaxNode, TypeSymbol> ConvertedTypes { get; } = [];

    internal void Record(SyntaxNode syntax, BoundNode bound) => BoundNodes[syntax] = bound;

    internal void RecordConversion(SyntaxNode syntax, TypeSymbol target) => ConvertedTypes[syntax] = target;

    internal void RecordDeclaration(SyntaxNode syntax, Symbol symbol) => DeclaredSymbols[syntax] = symbol;
}
