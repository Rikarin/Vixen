namespace Vixen.Core.Syntax;

/// <summary>
///     Red token: a public leaf wrapping an immutable green token
///     (<see cref="InternalSyntax.SyntaxToken" />). Kept as a <see cref="SyntaxNode" />
///     subclass (rather than a Roslyn-style value type) so tree traversal and the
///     dumper treat tokens uniformly with nodes.
/// </summary>
public class SyntaxToken : SyntaxNode {
    /// <summary>Exact source text of the token (without trivia).</summary>
    public string Text => GreenToken.Text;

    /// <summary>Strongly-typed value (identifier text, parsed literal, …).</summary>
    public object? Value => GreenToken.Value;

    /// <summary>The value rendered as text — for a literal, the value rather than the spelling.</summary>
    public string ValueText => GreenToken.ValueText;

    internal InternalSyntax.SyntaxToken GreenToken => (InternalSyntax.SyntaxToken)Green;

    internal SyntaxToken(InternalSyntax.SyntaxToken green, SyntaxNode? parent, int position)
        : base(green, parent, position) { }

    /// <summary>Always null: a token is a leaf and has no child slots.</summary>
    public override SyntaxNode? GetSlot(int index) => null;

    /// <summary>The token's text, without trivia.</summary>
    public override string ToString() => Text;
}
