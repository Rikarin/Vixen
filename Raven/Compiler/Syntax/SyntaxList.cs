using Vixen.Raven.Syntax.InternalSyntax;

namespace Vixen.Raven.Syntax;

/// <summary>
/// Visitor-facing helper for building list nodes. Takes already-realized red
/// children, assembles the corresponding immutable green list, and returns a
/// detached red wrapper (its position is provisional; the enclosing factory
/// only reads the green list, and the final tree is re-anchored from the root).
/// </summary>
static class SyntaxList {
    internal static SyntaxNode? List(SyntaxNode?[] children) {
        var greens = new GreenNode?[children.Length];
        for (var i = 0; i < children.Length; i++) {
            greens[i] = children[i]?.Green;
        }

        return InternalSyntax.SyntaxList.List(greens)?.CreateRed(null, 0);
    }
}
