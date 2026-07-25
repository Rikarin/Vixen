namespace Vixen.Core.Syntax;

/// <summary>
///     Either a node or a token, for the places a slot may hold one or the other.
///     Exactly one of the two properties is non-null.
/// </summary>
public readonly record struct SyntaxNodeOrToken {
    /// <summary>The token, when this holds one.</summary>
    public SyntaxToken? Token { get; }

    /// <summary>The node, when this holds one.</summary>
    public SyntaxNode? Node { get; }

    internal SyntaxNodeOrToken(SyntaxToken token) {
        Token = token;
    }

    internal SyntaxNodeOrToken(SyntaxNode node) {
        Node = node;
    }
}
