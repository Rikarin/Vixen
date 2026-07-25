using Vixen.Raven.Syntax.InternalSyntax;

namespace Vixen.Raven.Syntax;

/// <summary>
///     Red wrapper over a green list node (<see cref="SyntaxKind.ListKind" />). Not
///     part of the typed node hierarchy — it is the backing node behind a
///     <see cref="SyntaxList{TNode}" /> and realizes its elements lazily.
/// </summary>
public sealed class SyntaxListNode : SyntaxNode {
    internal SyntaxListNode(GreenNode green, SyntaxNode? parent, int position)
        : base(green, parent, position) { }

    public override SyntaxNode? GetSlot(int index) => GetRed(index);

    public override void Accept(SyntaxVisitor visitor) => visitor.DefaultVisit(this);

    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.DefaultVisit(this);
}
