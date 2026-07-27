// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.InternalSyntax;
using Vixen.Core.Syntax.Text;

namespace Vixen.Core.Syntax;

/// <summary>
///     Base of the public <em>red</em> tree: a lazy overlay over an immutable
///     <see cref="GreenNode" /> that adds a <see cref="Parent" /> pointer and an
///     absolute <see cref="Position" />. Child red nodes are realized on demand from
///     the green tree and cached, so navigation is cheap and identity-stable.
/// </summary>
public abstract class SyntaxNode {
    SyntaxNode?[]? cachedSlots;
    ISyntaxTree? syntaxTree;
    /// <summary>The enclosing node, or null at the root.</summary>
    public SyntaxNode? Parent { get; }

    /// <summary>
    ///     The node's kind as a raw integer. Each language re-exposes this as its own
    ///     enum — Raven's <c>RavenSyntaxNode.Kind</c> is <c>(SyntaxKind)RawKind</c>.
    /// </summary>
    public int RawKind => Green.RawKind;

    /// <summary>Number of child slots, including empty ones.</summary>
    public int SlotCount => Green.SlotCount;

    /// <summary>Whether this is the anonymous list node backing a <see cref="SyntaxList{TNode}" />.</summary>
    public bool IsList => Green.IsList;

    /// <summary>
    ///     The tree this node belongs to. Only the root carries the back-reference;
    ///     every other node walks up to it.
    /// </summary>
    public ISyntaxTree? SyntaxTree {
        get => syntaxTree ?? Parent?.SyntaxTree;
        internal set => syntaxTree = value;
    }

    /// <summary>Absolute span including leading/trailing trivia.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>Absolute span of the significant text, excluding surrounding trivia.</summary>
    public TextSpan Span =>
        TextSpan.FromBounds(
            Position + Green.GetLeadingTriviaWidth(),
            Position + Green.FullWidth - Green.GetTrailingTriviaWidth()
        );

    internal GreenNode Green { get; }
    internal int Position { get; }

    internal SyntaxNode(GreenNode green, SyntaxNode? parent, int position) {
        Green = green;
        Parent = parent;
        Position = position;
    }

    /// <summary>This node's <see cref="Span" /> as a diagnostic <see cref="Diagnostics.Location" />.</summary>
    public Location GetLocation() {
        var tree = SyntaxTree;
        return tree?.Text is { } text ? Location.Create(tree.FilePath, Span, text) : Location.None;
    }

    /// <summary>Red child at the given slot (node or token), or null.</summary>
    public abstract SyntaxNode? GetSlot(int index);

    /// <summary>The non-empty children, nodes and tokens alike, in source order.</summary>
    public IEnumerable<SyntaxNode> ChildNodesAndTokens() {
        for (var i = 0; i < SlotCount; i++) {
            var child = GetSlot(i);
            if (child != null) {
                yield return child;
            }
        }
    }

    /// <summary>
    ///     The children with list nodes flattened away, in source order.
    /// </summary>
    /// <remarks>
    ///     A list node is a shape of the tree rather than a construct of the language — nothing in
    ///     a grammar is spelled "list" — so every traversal below this line looks through one.
    ///     <see cref="ChildNodesAndTokens" /> deliberately does not: it is the raw slot walk the
    ///     tree dumper and the round-trip tests are written against.
    /// </remarks>
    public IEnumerable<SyntaxNode> ChildNodesAndTokensFlattened() {
        foreach (var child in ChildNodesAndTokens()) {
            if (child.IsList) {
                foreach (var element in child.ChildNodesAndTokensFlattened()) {
                    yield return element;
                }
            } else {
                yield return child;
            }
        }
    }

    /// <summary>The child nodes, without tokens and with list nodes flattened away.</summary>
    public IEnumerable<SyntaxNode> ChildNodes() =>
        ChildNodesAndTokensFlattened().Where(child => child is not SyntaxToken);

    /// <summary>The child tokens, with list nodes flattened away.</summary>
    public IEnumerable<SyntaxToken> ChildTokens() => ChildNodesAndTokensFlattened().OfType<SyntaxToken>();

    /// <summary>Everything beneath this node, nodes and tokens alike, in source order.</summary>
    public IEnumerable<SyntaxNode> DescendantNodesAndTokens() {
        foreach (var child in ChildNodesAndTokensFlattened()) {
            yield return child;

            foreach (var descendant in child.DescendantNodesAndTokens()) {
                yield return descendant;
            }
        }
    }

    /// <summary>Every node beneath this one, in source order. Tokens are not nodes here.</summary>
    public IEnumerable<SyntaxNode> DescendantNodes() =>
        DescendantNodesAndTokens().Where(node => node is not SyntaxToken);

    /// <summary>This node and every node beneath it.</summary>
    public IEnumerable<SyntaxNode> DescendantNodesAndSelf() => [this, .. DescendantNodes()];

    /// <summary>Every token beneath this node, in source order — the node's text, tokenized.</summary>
    public IEnumerable<SyntaxToken> DescendantTokens() => DescendantNodesAndTokens().OfType<SyntaxToken>();

    /// <summary>The enclosing nodes, innermost first.</summary>
    public IEnumerable<SyntaxNode> Ancestors() {
        for (var node = Parent; node is not null; node = node.Parent) {
            yield return node;
        }
    }

    /// <summary>This node and its enclosing nodes, innermost first.</summary>
    public IEnumerable<SyntaxNode> AncestorsAndSelf() => [this, .. Ancestors()];

    /// <summary>
    ///     The innermost <typeparamref name="TNode" /> at or above this node, or null.
    /// </summary>
    /// <remarks>
    ///     The question every feature over a cursor position asks second, after
    ///     <see cref="FindToken" />: which declaration is the caret in, which statement does this
    ///     expression belong to.
    /// </remarks>
    public TNode? FirstAncestorOrSelf<TNode>() where TNode : SyntaxNode {
        for (SyntaxNode? node = this; node is not null; node = node.Parent) {
            if (node is TNode found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>The first token of this subtree, or null if it has none.</summary>
    public SyntaxToken? GetFirstToken() => DescendantNodesAndTokens().OfType<SyntaxToken>().FirstOrDefault();

    /// <summary>The last token of this subtree, or null if it has none.</summary>
    public SyntaxToken? GetLastToken() => DescendantNodesAndTokens().OfType<SyntaxToken>().LastOrDefault();

    /// <summary>Whether <paramref name="node" /> is this node or lies beneath it.</summary>
    public bool Contains(SyntaxNode? node) {
        for (; node is not null; node = node.Parent) {
            if (ReferenceEquals(node, this)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The token whose full span — trivia included — contains <paramref name="position" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Full span rather than span, so every position in the file answers: a caret inside a
    ///         comment or in the indentation belongs to the token that comment leads, and asking
    ///         "what is under the cursor" has to work everywhere the cursor can be. The position
    ///         one past the end answers with the last token, which is what a caret at end-of-file
    ///         is.
    ///     </para>
    ///     <para>
    ///         Descends rather than scanning, so the cost is the tree's depth. Zero-width tokens —
    ///         the ones recovery fabricates — are skipped while descending: they contain no
    ///         position, so they can only be reached by asking for the node they are in.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="position" /> lies outside this node's <see cref="FullSpan" />.
    /// </exception>
    public SyntaxToken FindToken(int position) {
        if (position < Position || position > Position + Green.FullWidth) {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                $"The position is outside {FullSpan}."
            );
        }

        var node = this;

        while (node is not SyntaxToken) {
            SyntaxNode? next = null;

            foreach (var child in node.ChildNodesAndTokensFlattened()) {
                if (child.Green.FullWidth == 0) {
                    continue;
                }

                next = child;

                // The first child that has not ended yet owns the position.
                if (position < child.Position + child.Green.FullWidth) {
                    break;
                }
            }

            if (next is null) {
                throw new InvalidOperationException($"{RawKind} has width but no token at {position}.");
            }

            node = next;
        }

        return (SyntaxToken)node;
    }

    /// <summary>
    ///     The innermost node whose <see cref="Span" /> covers <paramref name="span" />, or null
    ///     when nothing beneath this node does.
    /// </summary>
    /// <remarks>
    ///     A node rather than a token, because what a selection maps to is a construct: this is
    ///     what a refactoring asks of a highlighted range, and what maps a generated-source span
    ///     back to the graph node that produced it.
    /// </remarks>
    public SyntaxNode? FindNode(TextSpan span) {
        if (!Span.Contains(span)) {
            return null;
        }

        var innermost = this;

        while (true) {
            var deeper = innermost.ChildNodes().FirstOrDefault(child => child.Span.Contains(span));

            if (deeper is null) {
                return innermost;
            }

            innermost = deeper;
        }
    }

    /// <summary>
    ///     Whether this subtree and <paramref name="other" /> say the same thing, ignoring trivia.
    /// </summary>
    /// <remarks>
    ///     Structure and token text, not whitespace: reformatting a function does not change what
    ///     it means, and an incremental reparse that produced an equivalent tree has produced the
    ///     same program. Compares green nodes, so an identical subtree that was reused by
    ///     reference answers in one reference check.
    /// </remarks>
    public bool IsEquivalentTo(SyntaxNode? other) => other is not null && GreenNode.Equivalent(Green, other.Green);

    /// <summary>Full source text of this subtree, including all trivia (byte-for-byte).</summary>
    public string ToFullString() => Green.ToString();

    /// <summary>Significant text of this subtree, excluding outer trivia.</summary>
    public override string ToString() {
        var full = Green.ToString();
        var leading = Green.GetLeadingTriviaWidth();
        var trailing = Green.GetTrailingTriviaWidth();
        return full.Substring(leading, full.Length - leading - trailing);
    }

    // No Accept here: a generated `Accept` calls `visitor.VisitIdentifierName(this)`,
    // so its parameter must be the language's own visitor type. Each front end derives
    // a base node that declares it — see Raven's RavenSyntaxNode.

    /// <summary>Absolute position of the child occupying green slot <paramref name="index" />.</summary>
    internal int GetChildPosition(int index) {
        var position = Position;
        for (var i = 0; i < index; i++) {
            position += Green.GetSlot(i)?.FullWidth ?? 0;
        }

        return position;
    }

    /// <summary>Realize (and cache) the red child at the given green slot, or null if empty.</summary>
    protected SyntaxNode? GetRed(int index) {
        var green = Green.GetSlot(index);
        if (green == null) {
            return null;
        }

        cachedSlots ??= new SyntaxNode?[SlotCount];
        return cachedSlots[index] ??= green.CreateRed(this, GetChildPosition(index));
    }
}
