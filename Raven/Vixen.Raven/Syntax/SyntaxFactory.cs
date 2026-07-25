using Vixen.Core.Syntax;
using Green = Vixen.Core.Syntax.InternalSyntax;

namespace Vixen.Raven.Syntax;

public partial class SyntaxFactory {
    /// <summary>
    ///     Creates a keyword or punctuation token carrying its canonical text, so a
    ///     factory-built tree round-trips to source the same way a parsed one does.
    /// </summary>
    public static SyntaxToken Token(SyntaxKind kind) => RedToken(new((int)kind, SyntaxFacts.GetText(kind)));

    public static SyntaxToken Identifier(string text) =>
        RedToken(new Green.SyntaxIdentifier((int)SyntaxKind.IdentifierToken, text));

    public static IdentifierNameSyntax IdentifierName(string name) => IdentifierName(Identifier(name));

    public static SyntaxToken Literal(long value) =>
        RedToken(new Green.SyntaxTokenWithValue<long>((int)SyntaxKind.None, value.ToString(), value));

    public static SyntaxToken Literal(double value) =>
        RedToken(new Green.SyntaxTokenWithValue<double>((int)SyntaxKind.None, value.ToString(), value));

    public static SyntaxToken Global() => Token(SyntaxKind.GlobalKeyword);

    public static SyntaxToken Static() => Token(SyntaxKind.StaticKeyword);

    // Green tokens carry the source text/value; the red wrapper is projected off
    // a detached green token (the enclosing factory re-anchors it in the tree).
    static SyntaxToken RedToken(Green.SyntaxToken green) => (SyntaxToken)green.CreateRed(null, 0);
}
