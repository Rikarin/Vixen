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
