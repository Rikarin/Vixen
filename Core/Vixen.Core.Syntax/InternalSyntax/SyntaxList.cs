namespace Vixen.Core.Syntax.InternalSyntax;

/// <summary>
///     Green list node: an anonymous <see cref="SyntaxKinds.List" /> node whose
///     slots are its elements. Specialized shapes avoid array allocation for the
///     common small cases.
/// </summary>
abstract class SyntaxList : GreenNode {
    public override bool IsList => true;
    protected SyntaxList() : base(SyntaxKinds.List) { }

    public override SyntaxNode CreateRed(SyntaxNode? parent, int position) =>
        new SyntaxListNode(this, parent, position);

    public static GreenNode? List(GreenNode?[] children) =>
        children.Length switch {
            0 => null,
            1 => children[0],
            2 => new WithTwoChildren(children[0], children[1]),
            _ => new WithManyChildren(children)
        };

    public static GreenNode List(GreenNode child0, GreenNode child1) => new WithTwoChildren(child0, child1);

    sealed class WithTwoChildren : SyntaxList {
        readonly GreenNode? child0;
        readonly GreenNode? child1;

        internal WithTwoChildren(GreenNode? child0, GreenNode? child1) {
            SlotCount = 2;
            this.child0 = child0;
            this.child1 = child1;
            AdjustWidth(child0);
            AdjustWidth(child1);
        }

        public override GreenNode? GetSlot(int index) =>
            index switch {
                0 => child0,
                1 => child1,
                _ => null
            };
    }

    sealed class WithManyChildren : SyntaxList {
        readonly GreenNode?[] children;

        internal WithManyChildren(GreenNode?[] children) {
            SlotCount = children.Length;
            this.children = children;
            foreach (var child in children) {
                AdjustWidth(child);
            }
        }

        public override GreenNode? GetSlot(int index) => index >= 0 && index < children.Length ? children[index] : null;
    }
}

/// <summary>Accumulates green children before materializing a <see cref="SyntaxList" />.</summary>
sealed class SyntaxListBuilder {
    readonly List<GreenNode?> nodes = [];

    public int Count => nodes.Count;

    public void Add(GreenNode? node) => nodes.Add(node);

    public void AddRange(IEnumerable<GreenNode?> range) => nodes.AddRange(range);

    public GreenNode? ToListNode() => SyntaxList.List(nodes.ToArray());
}
