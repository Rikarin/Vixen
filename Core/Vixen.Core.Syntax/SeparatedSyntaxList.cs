using System.Collections;

namespace Vixen.Core.Syntax;

/// <summary>
///     A list of nodes separated by tokens (e.g. comma-separated arguments). Backed
///     by a single green list whose slots interleave elements and separators:
///     <c>element, separator, element, separator, element</c>. Elements occupy even
///     slots, separators the odd ones.
/// </summary>
/// <typeparam name="TNode">Element type; separators are always tokens.</typeparam>
public readonly struct SeparatedSyntaxList<TNode> : IEnumerable<TNode> where TNode : SyntaxNode {
    /// <summary>Number of elements, ignoring separators.</summary>
    public int Count => (SlotCount + 1) / 2;

    /// <summary>
    ///     Number of separators — one fewer than <see cref="Count" />, unless the list
    ///     has a trailing separator, in which case the two are equal.
    /// </summary>
    public int SeparatorCount => SlotCount / 2;

    internal SyntaxNode? Node { get; }

    int SlotCount => Node == null ? 0 : Node.IsList ? Node.SlotCount : 1;

    /// <summary>The element at <paramref name="index" />, skipping separators.</summary>
    public TNode this[int index] => (TNode)SlotAt(index * 2)!;

    internal SeparatedSyntaxList(SyntaxNode? node) {
        Node = node;
    }

    /// <summary>Whether the list has any elements.</summary>
    public bool Any() => Node != null;

    /// <summary>The separator following element <paramref name="index" />.</summary>
    public SyntaxToken GetSeparator(int index) => (SyntaxToken)SlotAt(index * 2 + 1)!;

    /// <summary>Enumerates the elements, skipping separators.</summary>
    public IEnumerator<TNode> GetEnumerator() {
        for (var i = 0; i < Count; i++) {
            yield return this[i];
        }
    }

    /// <summary>
    ///     Reference equality on the backing node. Two lists are equal when they are the
    ///     same list, not when they happen to hold equal elements.
    /// </summary>
    public static bool operator ==(SeparatedSyntaxList<TNode> left, SeparatedSyntaxList<TNode> right) =>
        left.Node == right.Node;

    /// <inheritdoc cref="op_Equality" />
    public static bool operator !=(SeparatedSyntaxList<TNode> left, SeparatedSyntaxList<TNode> right) =>
        left.Node != right.Node;

    /// <inheritdoc cref="op_Equality" />
    public bool Equals(SeparatedSyntaxList<TNode> other) => Node == other.Node;

    /// <inheritdoc cref="op_Equality" />
    public override bool Equals(object? obj) => obj is SeparatedSyntaxList<TNode> other && Equals(other);

    /// <summary>Hashes the backing node, consistent with the reference equality above.</summary>
    public override int GetHashCode() => Node?.GetHashCode() ?? 0;

    SyntaxNode? SlotAt(int slot) {
        if (Node == null) {
            return null;
        }

        return Node.IsList ? Node.GetSlot(slot) : slot == 0 ? Node : null;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
