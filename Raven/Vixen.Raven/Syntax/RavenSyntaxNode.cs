using Vixen.Core.Syntax;
using Green = Vixen.Core.Syntax.InternalSyntax;

namespace Vixen.Raven.Syntax;

/// <summary>
///     Base of every Raven node. Adds the two things the shared tree cannot supply:
///     Raven's <see cref="SyntaxKind" /> and double dispatch over Raven's visitor.
/// </summary>
/// <remarks>
///     <para>
///         A generated <c>Accept</c> body calls <c>visitor.VisitIdentifierName(this)</c>,
///         so its parameter has to be the language's own visitor type — which is why
///         <see cref="SyntaxNode" /> declares no <c>Accept</c> and this class does. It is
///         the same split Roslyn makes between <c>SyntaxNode</c> and
///         <c>CSharpSyntaxNode</c>.
///     </para>
///     <para>
///         <see cref="SyntaxToken" /> and <see cref="SyntaxListNode" /> do <em>not</em>
///         derive from this: both are shared, and both dispatched to a fixed method
///         rather than a per-kind one. <see cref="SyntaxVisitor.Visit" /> routes them.
///     </para>
/// </remarks>
public abstract class RavenSyntaxNode : SyntaxNode {
    /// <summary>This node's kind. The shared tree stores it as <see cref="SyntaxNode.RawKind" />.</summary>
    public SyntaxKind Kind => (SyntaxKind)RawKind;

    internal RavenSyntaxNode(Green.GreenNode green, SyntaxNode? parent, int position) : base(green, parent, position) { }

    /// <summary>Dispatches to the visitor's method for this node's type.</summary>
    public abstract void Accept(SyntaxVisitor visitor);

    /// <summary>Dispatches to the visitor's method for this node's type, returning its result.</summary>
    public abstract TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor);
}
