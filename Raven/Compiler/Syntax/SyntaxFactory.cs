namespace Vixen.Raven.Syntax;

public partial class SyntaxFactory {
    // Green tokens carry the source text/value; the red wrapper is projected off
    // a detached green token (the enclosing factory re-anchors it in the tree).
    static SyntaxToken RedToken(InternalSyntax.SyntaxToken green) => (SyntaxToken)green.CreateRed(null, 0);

    // TODO (1b): keyword/punctuation text should come from SyntaxFacts.GetText(kind)
    // so these tokens round-trip. For now they carry no text.
    public static SyntaxToken Token(SyntaxKind kind) => RedToken(new InternalSyntax.SyntaxToken(kind, string.Empty));

    public static SyntaxToken Identifier(string text) => RedToken(new InternalSyntax.SyntaxIdentifier(text));

    public static IdentifierNameSyntax IdentifierName(string name) => IdentifierName(Identifier(name));

    public static SyntaxToken Literal(long value) =>
        RedToken(new InternalSyntax.SyntaxTokenWithValue<long>(SyntaxKind.None, value.ToString(), value));

    public static SyntaxToken Literal(double value) =>
        RedToken(new InternalSyntax.SyntaxTokenWithValue<double>(SyntaxKind.None, value.ToString(), value));

    public static SyntaxToken Global() => Token(SyntaxKind.GlobalKeyword);
    public static SyntaxToken Static() => Token(SyntaxKind.StaticKeyword);
}
