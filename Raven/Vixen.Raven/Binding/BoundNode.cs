using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>
///     A node of the <em>bound tree</em>: the syntax tree with every name resolved
///     to a <see cref="Symbol" />, every expression given a
///     <see cref="TypeSymbol" />, and every conversion made explicit. Phase 3 lowers
///     this to the target-independent IR.
/// </summary>
public abstract class BoundNode {
    /// <summary>The syntax this node was bound from.</summary>
    public SyntaxNode Syntax { get; }

    public abstract BoundKind Kind { get; }

    /// <summary>Child bound nodes, in source order.</summary>
    public virtual IEnumerable<BoundNode> Children => [];

    protected BoundNode(SyntaxNode syntax) {
        Syntax = syntax;
    }

    /// <summary>This node and every node beneath it.</summary>
    public IEnumerable<BoundNode> DescendantsAndSelf() {
        yield return this;
        foreach (var child in Children) {
            foreach (var descendant in child.DescendantsAndSelf()) {
                yield return descendant;
            }
        }
    }
}

/// <summary>A bound node that produces a value (or, for statements' sake, a type).</summary>
public abstract class BoundExpression : BoundNode {
    /// <summary>The expression's type; <see cref="ErrorTypeSymbol" /> when it could not be determined.</summary>
    public abstract TypeSymbol Type { get; }

    /// <summary>The compile-time value, when the expression is a constant.</summary>
    public virtual object? ConstantValue => null;

    /// <summary>The symbol this expression refers to, if it refers to one.</summary>
    public virtual Symbol? Symbol => null;

    protected BoundExpression(SyntaxNode syntax) : base(syntax) { }
}

/// <summary>A bound node that performs an action rather than producing a value.</summary>
public abstract class BoundStatement : BoundNode {
    protected BoundStatement(SyntaxNode syntax) : base(syntax) { }
}
