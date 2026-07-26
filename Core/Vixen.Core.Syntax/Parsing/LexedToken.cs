// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax.Parsing;

/// <summary>How a lexed token participates in parsing and in the tree.</summary>
[Flags]
enum LexedTokenFlags : byte {
    None = 0,

    /// <summary>Whitespace or a comment: invisible to the parser, carried as token trivia.</summary>
    Trivia = 1,

    /// <summary>
    ///     A line terminator. The parser sees it — statement grammars are
    ///     newline-sensitive — but the tree carries it as end-of-line trivia on the
    ///     following token, never as a token of its own.
    /// </summary>
    EndOfLine = 2
}

/// <summary>
///     One token out of a hand-written lexer: a raw kind in the language's own
///     vocabulary, the exact source text, the absolute position, and flags saying
///     whether the parser sees it. The raw token list is the single source of truth
///     for trivia attachment: a tree token's leading trivia is every trivia and
///     newline token immediately preceding it in the list.
/// </summary>
readonly record struct LexedToken(int RawKind, string Text, int Position, LexedTokenFlags Flags) {
    public bool IsTrivia => (Flags & LexedTokenFlags.Trivia) != 0;
    public bool IsEndOfLine => (Flags & LexedTokenFlags.EndOfLine) != 0;

    /// <summary>Whether the parser navigates over this token (everything except pure trivia).</summary>
    public bool IsParserVisible => !IsTrivia;

    public int Width => Text.Length;
    public int End => Position + Text.Length;
}
