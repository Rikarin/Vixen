// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Text;

namespace Vixen.Core.Syntax;

/// <summary>
///     A run of insignificant text — whitespace, a line break, a comment — carried on the leading
///     or trailing edge of a <see cref="SyntaxToken" />.
/// </summary>
/// <remarks>
///     A value rather than a node, because trivia occupies no slot: it hangs off a token, which is
///     what keeps <see cref="SyntaxNode.ChildNodesAndTokens" /> seeing tokens as leaves. It is
///     public for the things that have to look at what the parser threw away — a syntax
///     highlighter colouring comments, a formatter deciding where a blank line went, a doc-comment
///     reader.
/// </remarks>
/// <param name="RawKind">
///     The trivia's kind in the owning language's vocabulary, as an integer for the same reason
///     <see cref="SyntaxNode.RawKind" /> is.
/// </param>
/// <param name="Text">The exact source text.</param>
/// <param name="Position">Absolute position of the first character.</param>
public readonly record struct SyntaxTrivia(int RawKind, string Text, int Position) {
    /// <summary>The trivia's span in the source.</summary>
    public TextSpan Span => new(Position, Text.Length);

    /// <summary>The trivia's text, so a trivia prints as what it is.</summary>
    public override string ToString() => Text;
}
