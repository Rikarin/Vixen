namespace Vixen.Raven.Syntax;

/// <summary>
/// Red token: a public leaf wrapping an immutable green token
/// (<see cref="InternalSyntax.SyntaxToken"/>). Kept as a <see cref="SyntaxNode"/>
/// subclass (rather than a Roslyn-style value type) so tree traversal and the
/// dumper treat tokens uniformly with nodes.
/// </summary>
public class SyntaxToken : SyntaxNode {
    internal SyntaxToken(InternalSyntax.SyntaxToken green, SyntaxNode? parent, int position)
        : base(green, parent, position) { }

    internal InternalSyntax.SyntaxToken GreenToken => (InternalSyntax.SyntaxToken)Green;

    /// <summary>Exact source text of the token (without trivia).</summary>
    public string Text => GreenToken.Text;

    /// <summary>Strongly-typed value (identifier text, parsed literal, …).</summary>
    public object? Value => GreenToken.Value;

    public string ValueText => GreenToken.ValueText;

    public override SyntaxNode? GetSlot(int index) => null;

    public override void Accept(SyntaxVisitor visitor) => visitor.VisitToken(this);

    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitToken(this);

    public override string ToString() => Text;
}
