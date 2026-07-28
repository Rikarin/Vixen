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
    List<LexedToken>? split;

    /// <summary>The raw token list, trivia included; the last entry is end-of-file.</summary>
    protected IReadOnlyList<LexedToken> Tokens { get; private set; }

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

    /// <summary>
    ///     Splits the current token in two, the first <paramref name="firstWidth" /> characters
    ///     wide, and leaves the position on the first half.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The one place a maximal-munch lexer and a context-free grammar disagree: <c>&gt;&gt;</c>
    ///         is one token everywhere except where it closes two nested type-argument lists.
    ///         Roslyn's C# lexer takes the opposite route — it emits <c>&gt;</c> twice and the parser
    ///         merges them for a shift — but that spelling is fixed here by the token-stream
    ///         differential against the grammar oracle, so the split happens on this side instead.
    ///     </para>
    ///     <para>
    ///         The two halves cover the original's text and positions exactly, so a tree built from
    ///         them still reproduces the source byte for byte. Both keep the original's flags: a
    ///         splittable token is by construction neither trivia nor a line terminator.
    ///     </para>
    ///     <para>
    ///         Raw indices after the split point shift by one. That is safe for the same reason a
    ///         parser can be single-pass: every index a caller holds — a speculation mark, a
    ///         recovery-skipped index — was taken at or before the current position, and this only
    ///         ever inserts after it. Call it from a committed parse rather than from speculation,
    ///         so a scan that resets cannot leave a token it decided against split apart.
    ///     </para>
    /// </remarks>
    protected void SplitCurrentToken(int firstRawKind, int firstWidth, int secondRawKind) {
        var token = Current;

        if (firstWidth <= 0 || firstWidth >= token.Width) {
            throw new ArgumentOutOfRangeException(
                nameof(firstWidth),
                firstWidth,
                $"A split has to leave both halves non-empty; the token is {token.Width} characters wide."
            );
        }

        if (split is null) {
            split = [.. Tokens];
            Tokens = split;
        }

        split[RawPosition] = token with { RawKind = firstRawKind, Text = token.Text[..firstWidth] };

        split.Insert(
            RawPosition + 1,
            new(secondRawKind, token.Text[firstWidth..], token.Position + firstWidth, token.Flags)
        );
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

    /// <summary>Resumes at the first visible token at or after a raw index.</summary>
    /// <param name="rawPosition">Any raw index, trivia or not.</param>
    /// <remarks>
    ///     ⚠ <b>What <see cref="ResetTo" /> is not.</b> That one rewinds to a position the parser had
    ///     already been at, which is by construction a visible token; this takes an index computed
    ///     from a <i>text offset</i> — which is what an incremental reuse does when it looks up where
    ///     a reused node ends — and the token starting exactly there is very often the whitespace
    ///     before the next real one. Leaving <see cref="RawPosition" /> on trivia breaks the
    ///     invariant every <c>Kind</c> and <c>At</c> in a language parser rests on, and the symptom is
    ///     a parser that cannot see the token it is looking at: in VXML, an element whose close tag
    ///     went missing the moment its last child was reused.
    /// </remarks>
    protected void ResumeAt(int rawPosition) => RawPosition = SkipTrivia(rawPosition);

    int SkipTrivia(int index) {
        // The final token is never trivia, so this always lands on a visible token.
        var last = Tokens.Count - 1;
        while (index < last && Tokens[index].IsTrivia) {
            index++;
        }

        return Math.Min(index, last);
    }
}
