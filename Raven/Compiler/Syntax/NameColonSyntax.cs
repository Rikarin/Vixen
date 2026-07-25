namespace Vixen.Raven.Syntax;

public partial class NameColonSyntax {
    public override ExpressionSyntax Expression => Name;

    internal override BaseExpressionColonSyntax WithExpressionCore(ExpressionSyntax expression) {
        if (expression is IdentifierNameSyntax identifierName) {
            return WithName(identifierName);
        }

        return SyntaxFactory.ExpressionColon(expression);
    }
}

public partial class SyntaxFactory {
    public static NameColonSyntax NameColon(string name) =>
        NameColon(IdentifierName(name), Token(SyntaxKind.ColonToken));
}
