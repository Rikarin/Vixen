// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax.Parsing;

/// <summary>
///     Base for hand-written recursive-descent parsers over a <see cref="LexedToken" />
///     list: parser-visible navigation with arbitrary lookahead, and mark/reset for
///     the few places a grammar needs speculation (a cast versus a parenthesized
///     expression, a generic name versus a comparison). Token construction and
///     recovery policy stay in the language parser — kinds are the language's own.
/// </summary>
abstract class SyntaxParser {
    /// <summary>The raw token list, trivia included; the last entry is end-of-file.</summary>
    protected IReadOnlyList<LexedToken> Tokens { get; }

    /// <summary>Raw index of the current parser-visible token.</summary>
    protected int RawPosition { get; private set; }

    protected LexedToken Current => Tokens[RawPosition];

    protected SyntaxParser(IReadOnlyList<LexedToken> tokens) {
        if (tokens.Count == 0 || tokens[^1].IsTrivia) {
            throw new ArgumentException("The token list must end with a parser-visible end-of-file token.");
        }

        Tokens = tokens;
        RawPosition = SkipTrivia(0);
    }

    /// <summary>The nth parser-visible token ahead (0 = current).</summary>
    protected LexedToken Peek(int n = 0) => Tokens[PeekRawIndex(n)];

    /// <summary>Raw index of the nth parser-visible token ahead (0 = current).</summary>
    protected int PeekRawIndex(int n) {
        var index = RawPosition;
        while (n-- > 0) {
            index = SkipTrivia(index + 1);
        }

        return index;
    }

    /// <summary>
    ///     Consumes the current token and returns its raw index — what a language
    ///     parser hands to its token builder, which gathers the preceding trivia.
    /// </summary>
    protected int Advance() {
        var consumed = RawPosition;
        RawPosition = SkipTrivia(consumed + 1);
        return consumed;
    }

    /// <summary>Rewinds to a raw position previously read from <see cref="RawPosition" />.</summary>
    protected void ResetTo(int rawPosition) => RawPosition = rawPosition;

    int SkipTrivia(int index) {
        // The final token is never trivia, so this always lands on a visible token.
        var last = Tokens.Count - 1;
        while (index < last && Tokens[index].IsTrivia) {
            index++;
        }

        return Math.Min(index, last);
    }
}
