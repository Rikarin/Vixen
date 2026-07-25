using System.Diagnostics;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Syntax;

/// <summary>
///     Rewrites a syntax tree by visiting every node and rebuilding only the parts
///     that changed. The per-node <c>Visit*</c> overrides are generated from
///     Syntax.xml; this partial supplies list handling, which the generator cannot
///     express generically.
/// </summary>
public partial class SyntaxRewriter {
    /// <summary>
    ///     Visits each element of <paramref name="list" />. Returns the original list
    ///     when nothing changed, so unchanged subtrees keep their identity and callers
    ///     can use a reference comparison to detect "no edit".
    /// </summary>
    public SyntaxList<TNode> VisitList<TNode>(SyntaxList<TNode> list) where TNode : SyntaxNode {
        List<TNode>? rewritten = null;

        for (int i = 0, n = list.Count; i < n; i++) {
            var item = list[i]!;
            var visited = Visit(item);

            // Only start copying once something actually changes; up to that point
            // the original list is still the correct answer.
            if (rewritten is null && !ReferenceEquals(item, visited)) {
                rewritten = new(n);
                for (var j = 0; j < i; j++) {
                    rewritten.Add(list[j]!);
                }
            }

            if (rewritten is not null) {
                Debug.Assert(
                    visited is not null && visited.RawKind != (int)SyntaxKind.None,
                    "A rewriter cannot remove a node from a list; return the original to keep it."
                );

                rewritten.Add((TNode)visited!);
            }
        }

        return rewritten is null ? list : new SyntaxList<TNode>(null).AddRange(rewritten);
    }
}
