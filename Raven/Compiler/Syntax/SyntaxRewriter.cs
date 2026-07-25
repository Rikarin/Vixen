namespace Vixen.Raven.Syntax;

public partial class SyntaxRewriter {
    // TODO: check this as I made this
    // public SeparatedSyntaxList<TNode> VisitList<TNode>(SeparatedSyntaxList<TNode> list) where TNode : SyntaxNode {
    //     throw new NotImplementedException();
    // }
    

    public SyntaxList<TNode> VisitList<TNode>(SyntaxList<TNode> list) where TNode : SyntaxNode {
        // TODO: review how to implement this
        
        // SyntaxListBuilder alternate = null;
        // List<SyntaxNode>? alternate = null;
        //
        // for (int i = 0, n = list.Count; i < n; i++) {
        //     var item = list[i];
        //     var visited = Visit(item);
        //     if (item != visited && alternate == null) {
        //         // alternate = new SyntaxListBuilder(n);
        //         alternate = [];
        //         alternate.AddRange(list);
        //     }
        //
        //     if (alternate != null) {
        //         Debug.Assert(
        //             visited != null && visited.Kind != SyntaxKind.None,
        //             "Cannot remove node using Syntax.InternalSyntax.SyntaxRewriter."
        //         );
        //         alternate.Add(visited);
        //     }
        // }
        //
        // if (alternate != null) {
        //     return alternate.ToList();
        // }
        //
        return list;
    }
}
