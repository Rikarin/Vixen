using Vixen.Core.Syntax.InternalSyntax;

namespace Vixen.Core.Syntax;

/// <summary>
///     Red wrapper over a green list node (<see cref="SyntaxKinds.List" />). Not
///     part of the typed node hierarchy — it is the backing node behind a
///     <see cref="SyntaxList{TNode}" /> and realizes its elements lazily.
/// </summary>
public sealed class SyntaxListNode : SyntaxNode {
    internal SyntaxListNode(GreenNode green, SyntaxNode? parent, int position)
        : base(green, parent, position) { }

    /// <summary>The element at <paramref name="index" />, realized on demand.</summary>
    public override SyntaxNode? GetSlot(int index) => GetRed(index);
}
