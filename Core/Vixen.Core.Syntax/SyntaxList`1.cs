using System.Diagnostics;

namespace Vixen.Core.Syntax;

/// <summary>
///     A typed view over a run of sibling nodes. Backed either by an anonymous list
///     node or, when there is exactly one element, by that element directly — so a
///     one-item list costs no extra allocation.
/// </summary>
/// <typeparam name="TNode">Element type; every element is this type or a subtype.</typeparam>
public readonly struct SyntaxList<TNode> : IEquatable<SyntaxList<TNode>> where TNode : SyntaxNode {
    /// <summary>Number of elements. Zero when the list is empty.</summary>
    public int Count => Node == null ? 0 : Node.IsList ? Node.SlotCount : 1;

    internal SyntaxNode? Node { get; }

    /// <summary>The element at <paramref name="index" />, or null when the list is empty.</summary>
    public TNode? this[int index] {
        get {
            if (Node == null) {
                return null;
            }

            if (Node.IsList) {
                Debug.Assert(index >= 0);
                Debug.Assert(index <= Node.SlotCount);

                return (TNode?)Node.GetSlot(index);
            }

            if (index == 0) {
                return (TNode?)Node;
            }

            throw ExceptionUtilities.Unreachable();
        }
    }

    internal SyntaxList(SyntaxNode? node) {
        Node = node;
    }

    /// <summary>Whether the list has any elements.</summary>
    public bool Any() => Node != null;

    /// <summary>
    ///     Whether any element has this kind. Takes the raw value because the tree is
    ///     language-agnostic; callers pass <c>(int)SyntaxKind.Xxx</c>.
    /// </summary>
    public bool Any(int rawKind) {
        foreach (var element in this) {
            if (element.RawKind == rawKind) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Allocation-free enumerator over the elements.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>
    ///     Reference equality on the backing node. Two lists are equal when they are the
    ///     same list, not when they happen to hold equal elements.
    /// </summary>
    public static bool operator ==(SyntaxList<TNode> left, SyntaxList<TNode> right) => left.Node == right.Node;

    /// <inheritdoc cref="op_Equality" />
    public static bool operator !=(SyntaxList<TNode> left, SyntaxList<TNode> right) => left.Node != right.Node;

    /// <inheritdoc cref="op_Equality" />
    public bool Equals(SyntaxList<TNode> other) => Node == other.Node;

    /// <inheritdoc cref="op_Equality" />
    public override bool Equals(object? obj) => obj is SyntaxList<TNode> list && Equals(list);

    /// <summary>Hashes the backing node, consistent with the reference equality above.</summary>
    public override int GetHashCode() => Node != null ? Node.GetHashCode() : 0;

    /// <summary>Wraps a single node as a one-element list.</summary>
    public static implicit operator SyntaxList<TNode>(TNode node) => new(node);


    /// <summary>Returns a new list with <paramref name="nodes" /> appended.</summary>
    public SyntaxList<TNode> AddRange(IEnumerable<TNode> nodes) {
        ArgumentNullException.ThrowIfNull(nodes);

        var count = Count;
        var combined = new List<SyntaxNode?>(count);
        for (var i = 0; i < count; i++) {
            combined.Add(this[i]);
        }

        combined.AddRange(nodes);
        return new(SyntaxList.List([.. combined]));
    }

    /// <summary>Struct enumerator, so <c>foreach</c> over a list does not allocate.</summary>
    public struct Enumerator {
        readonly SyntaxList<TNode> list;
        int index;

        /// <summary>The element at the current position.</summary>
        public TNode Current => list[index]!;

        internal Enumerator(SyntaxList<TNode> list) {
            this.list = list;
            index = -1;
        }

        /// <summary>Advances to the next element, returning false at the end.</summary>
        public bool MoveNext() {
            var newIndex = index + 1;
            if (newIndex < list.Count) {
                index = newIndex;
                return true;
            }

            return false;
        }
    }
}
