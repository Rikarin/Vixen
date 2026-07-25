using System.Diagnostics;

namespace Vixen.Raven.Syntax;

/// <summary>
///     This is a wrapper around non-generic SyntaxList
/// </summary>
/// <typeparam name="TNode"></typeparam>
public readonly struct SyntaxList<TNode> : IEquatable<SyntaxList<TNode>> where TNode : SyntaxNode {
    public int Count => Node == null ? 0 : Node.Kind == SyntaxKind.ListKind ? Node.SlotCount : 1;
    internal SyntaxNode? Node { get; }

    public TNode? this[int index] {
        get {
            if (Node == null) {
                return null;
            }

            if (Node.Kind == SyntaxKind.ListKind) {
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

    public bool Any() => Node != null;

    public bool Any(SyntaxKind kind) {
        foreach (var element in this) {
            if (element.Kind == kind) {
                return true;
            }
        }

        return false;
    }

    public Enumerator GetEnumerator() => new(this);
    public static bool operator ==(SyntaxList<TNode> left, SyntaxList<TNode> right) => left.Node == right.Node;
    public static bool operator !=(SyntaxList<TNode> left, SyntaxList<TNode> right) => left.Node != right.Node;
    public bool Equals(SyntaxList<TNode> other) => Node == other.Node;
    public override bool Equals(object? obj) => obj is SyntaxList<TNode> list && Equals(list);
    public override int GetHashCode() => Node != null ? Node.GetHashCode() : 0;

    public static implicit operator SyntaxList<TNode>(TNode node) => new(node);


    public SyntaxList<TNode> AddRange(IEnumerable<TNode> nodes) => throw new NotImplementedException();

    // return this.InsertRange(this.Count, nodes);
    public struct Enumerator {
        readonly SyntaxList<TNode> list;
        int index;

        public TNode Current => list[index]!;

        internal Enumerator(SyntaxList<TNode> list) {
            this.list = list;
            index = -1;
        }

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
