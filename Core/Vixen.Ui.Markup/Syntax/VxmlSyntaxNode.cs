// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Green = Vixen.Core.Syntax.InternalSyntax;

namespace Vixen.Ui.Markup.Syntax;

/// <summary>
///     Base of every VXML node. Adds the two things the shared tree cannot supply: VXML's
///     <see cref="SyntaxKind" /> and double dispatch over VXML's visitor.
/// </summary>
/// <remarks>
///     The same split Raven makes, and for the same reason: a generated <c>Accept</c> body calls
///     <c>visitor.VisitElement(this)</c>, so its parameter has to be the language's own visitor
///     type. <see cref="SyntaxNode" /> therefore declares no <c>Accept</c> and this class does.
/// </remarks>
public abstract class VxmlSyntaxNode : SyntaxNode {
    /// <summary>This node's kind. The shared tree stores it as <see cref="SyntaxNode.RawKind" />.</summary>
    public SyntaxKind Kind => (SyntaxKind)RawKind;

    internal VxmlSyntaxNode(Green.GreenNode green, SyntaxNode? parent, int position)
        : base(green, parent, position) { }

    /// <summary>Dispatches to the visitor's method for this node's type.</summary>
    public abstract void Accept(SyntaxVisitor visitor);

    /// <summary>Dispatches to the visitor's method for this node's type, returning its result.</summary>
    public abstract TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor);
}
