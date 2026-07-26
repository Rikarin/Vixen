// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;

namespace Vixen.Raven.Syntax;

public abstract partial class SyntaxVisitor {
    /// <summary>
    ///     Dispatches <paramref name="node" /> to the right visit method.
    /// </summary>
    /// <remarks>
    ///     Raven nodes double-dispatch through <see cref="RavenSyntaxNode.Accept(SyntaxVisitor)" />.
    ///     Tokens and list nodes come from the shared tree and carry no <c>Accept</c>, so
    ///     they are routed here — one type test in one place, rather than an override on
    ///     every generated node.
    /// </remarks>
    public virtual void Visit(SyntaxNode? node) {
        switch (node) {
            case null:
                break;
            case SyntaxToken token:
                VisitToken(token);
                break;
            case RavenSyntaxNode raven:
                raven.Accept(this);
                break;
            default:
                DefaultVisit(node);
                break;
        }
    }

    public virtual void VisitToken(SyntaxToken token) => DefaultVisit(token);

    public virtual void DefaultVisit(SyntaxNode node) { }
}

public abstract partial class SyntaxVisitor<TResult> {
    /// <inheritdoc cref="SyntaxVisitor.Visit" />
    public virtual TResult? Visit(SyntaxNode? node) =>
        node switch {
            null => default,
            SyntaxToken token => VisitToken(token),
            RavenSyntaxNode raven => raven.Accept(this),
            _ => DefaultVisit(node)
        };

    public virtual TResult? VisitToken(SyntaxToken token) => DefaultVisit(token);

    public virtual TResult? DefaultVisit(SyntaxNode node) => default;
}
