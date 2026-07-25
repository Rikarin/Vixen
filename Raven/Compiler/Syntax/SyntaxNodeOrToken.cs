namespace Vixen.Raven.Syntax;

public readonly record struct SyntaxNodeOrToken {
    public SyntaxToken? Token { get; }
    public SyntaxNode? Node { get; }
    
    internal SyntaxNodeOrToken(SyntaxToken token) => Token = token;
    internal SyntaxNodeOrToken(SyntaxNode node) => Node = node;
}
