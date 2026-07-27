// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;

namespace Vixen.Ui.Markup.Syntax;

/// <summary>Walks a VXML tree, one virtual method per node type.</summary>
public abstract partial class SyntaxVisitor {
    /// <summary>Dispatches <paramref name="node" /> to the right visit method.</summary>
    /// <remarks>
    ///     VXML nodes double-dispatch through <see cref="VxmlSyntaxNode.Accept(SyntaxVisitor)" />.
    ///     Tokens and list nodes come from the shared tree and carry no <c>Accept</c>, so they are
    ///     routed here — one type test in one place, rather than an override on every generated node.
    /// </remarks>
    /// <param name="node">The node to visit; null is ignored.</param>
    public virtual void Visit(SyntaxNode? node) {
        switch (node) {
            case null:
                break;
            case SyntaxToken token:
                VisitToken(token);
                break;
            case VxmlSyntaxNode vxml:
                vxml.Accept(this);
                break;
            default:
                DefaultVisit(node);
                break;
        }
    }

    /// <summary>Visits a token.</summary>
    /// <param name="token">The token.</param>
    public virtual void VisitToken(SyntaxToken token) => DefaultVisit(token);

    /// <summary>Visits any node no override claimed.</summary>
    /// <param name="node">The node.</param>
    public virtual void DefaultVisit(SyntaxNode node) { }
}

/// <summary>Walks a VXML tree producing a result per node.</summary>
/// <typeparam name="TResult">What a visit produces.</typeparam>
public abstract partial class SyntaxVisitor<TResult> {
    /// <inheritdoc cref="SyntaxVisitor.Visit" />
    /// <returns>Whatever the claiming method returned.</returns>
    public virtual TResult? Visit(SyntaxNode? node) =>
        node switch {
            null => default,
            SyntaxToken token => VisitToken(token),
            VxmlSyntaxNode vxml => vxml.Accept(this),
            _ => DefaultVisit(node)
        };

    /// <summary>Visits a token.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The visitor's result.</returns>
    public virtual TResult? VisitToken(SyntaxToken token) => DefaultVisit(token);

    /// <summary>Visits any node no override claimed.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The visitor's result.</returns>
    public virtual TResult? DefaultVisit(SyntaxNode node) => default;
}
