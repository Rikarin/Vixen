namespace Vixen.Raven.Syntax.InternalSyntax;

/// <summary>
///     Base of the internal <em>green</em> tree: immutable, position-independent
///     nodes that store only widths (never absolute positions or parent pointers),
///     so identical subtrees can be shared. The public <em>red</em> tree
///     (<see cref="Vixen.Raven.Syntax.SyntaxNode" />) is a lazy overlay that adds
///     parent and absolute position on top of green nodes.
/// </summary>
abstract class GreenNode {
    public SyntaxKind Kind { get; }

    /// <summary>Number of child slots. Terminals (tokens, trivia) report 0.</summary>
    public int SlotCount { get; protected set; }

    /// <summary>Total width in characters, <em>including</em> leading and trailing trivia.</summary>
    public int FullWidth { get; protected set; }

    public virtual bool IsToken => false;
    public virtual bool IsList => false;
    public virtual bool IsTrivia => false;

    /// <summary>Width of the node excluding surrounding trivia (the "significant" width).</summary>
    public int Width => FullWidth - GetLeadingTriviaWidth() - GetTrailingTriviaWidth();

    protected GreenNode(SyntaxKind kind) {
        Kind = kind;
    }

    protected GreenNode(SyntaxKind kind, int fullWidth) {
        Kind = kind;
        FullWidth = fullWidth;
    }

    public abstract GreenNode? GetSlot(int index);

    /// <summary>
    ///     Projects this green node into a public red node anchored at the given
    ///     parent and absolute <paramref name="position" />. Generated concrete green
    ///     nodes new up their matching red class; terminals/lists have hand-written
    ///     projections.
    /// </summary>
    public abstract SyntaxNode CreateRed(SyntaxNode? parent, int position);

    public virtual int GetLeadingTriviaWidth() => FullWidth == 0 ? 0 : GetFirstTerminal()?.GetLeadingTriviaWidth() ?? 0;

    public virtual int GetTrailingTriviaWidth() =>
        FullWidth == 0 ? 0 : GetLastTerminal()?.GetTrailingTriviaWidth() ?? 0;

    public GreenNode? GetFirstTerminal() {
        var node = this;

        while (node is { SlotCount: > 0 }) {
            GreenNode? first = null;
            for (var i = 0; i < node.SlotCount; i++) {
                first = node.GetSlot(i);
                if (first != null) {
                    break;
                }
            }

            node = first;
        }

        return node;
    }

    public GreenNode? GetLastTerminal() {
        var node = this;

        while (node is { SlotCount: > 0 }) {
            GreenNode? last = null;
            for (var i = node.SlotCount - 1; i >= 0; i--) {
                last = node.GetSlot(i);
                if (last != null) {
                    break;
                }
            }

            node = last;
        }

        return node;
    }

    /// <summary>Writes the full text (trivia included) of this subtree.</summary>
    public virtual void WriteTo(TextWriter writer) {
        for (var i = 0; i < SlotCount; i++) {
            GetSlot(i)?.WriteTo(writer);
        }
    }

    /// <summary>Full text of the subtree, including leading and trailing trivia.</summary>
    public override string ToString() {
        using var sw = new StringWriter();
        WriteTo(sw);
        return sw.ToString();
    }

    /// <summary>Accumulate a child's width into this node's <see cref="FullWidth" />.</summary>
    protected void AdjustWidth(GreenNode? child) {
        if (child != null) {
            FullWidth += child.FullWidth;
        }
    }
}
