using System.Collections;

namespace Vixen.Raven.Syntax;

/// <summary>
/// A list of nodes separated by tokens (e.g. comma-separated arguments). Backed
/// by a single green list whose slots interleave elements and separators:
/// <c>element, separator, element, separator, element</c>. Elements occupy even
/// slots, separators the odd ones.
/// </summary>
public readonly struct SeparatedSyntaxList<TNode> : IEnumerable<TNode> where TNode : SyntaxNode {
    internal SyntaxNode? Node { get; }

    internal SeparatedSyntaxList(SyntaxNode? node) => Node = node;

    int SlotCount => Node == null ? 0 : Node.Kind == SyntaxKind.ListKind ? Node.SlotCount : 1;

    public int Count => (SlotCount + 1) / 2;
    public int SeparatorCount => SlotCount / 2;

    public bool Any() => Node != null;

    public TNode this[int index] => (TNode)SlotAt(index * 2)!;

    public SyntaxToken GetSeparator(int index) => (SyntaxToken)SlotAt(index * 2 + 1)!;

    SyntaxNode? SlotAt(int slot) {
        if (Node == null) {
            return null;
        }

        return Node.Kind == SyntaxKind.ListKind ? Node.GetSlot(slot) : slot == 0 ? Node : null;
    }

    public IEnumerator<TNode> GetEnumerator() {
        for (var i = 0; i < Count; i++) {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator ==(SeparatedSyntaxList<TNode> left, SeparatedSyntaxList<TNode> right) =>
        left.Node == right.Node;

    public static bool operator !=(SeparatedSyntaxList<TNode> left, SeparatedSyntaxList<TNode> right) =>
        left.Node != right.Node;

    public bool Equals(SeparatedSyntaxList<TNode> other) => Node == other.Node;
    public override bool Equals(object? obj) => obj is SeparatedSyntaxList<TNode> other && Equals(other);
    public override int GetHashCode() => Node?.GetHashCode() ?? 0;
}
