// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.InternalSyntax;
using Vixen.Core.Syntax.Text;

namespace Vixen.Core.Syntax.Parsing;

/// <summary>
///     Feeds an incremental reparse with reusable green nodes from the previous
///     tree (docs/plan/18 step 7). The language parser offers candidate nodes at a
///     granularity of its choosing — Raven offers member declarations — and asks at
///     matching boundaries whether the old tree already has that node.
/// </summary>
/// <remarks>
///     <para>
///         A candidate survives when no change touches its old full span, with one
///         character of margin on each side so an edit that is merely <em>adjacent</em>
///         cannot glue itself onto the node's first or last token; its position is then
///         shifted by the length delta of every change before it, and reuse is by exact
///         new full-start match.
///     </para>
///     <para>
///         ⚠ <b>Untouched characters are not the same thing as unchanged tokens.</b> A
///         templating lexer carries a mode stack, so an edit far above a node decides what
///         its characters lex <em>as</em> — <c>&lt;panel class="root"&gt;</c> is a start tag
///         in content and a run of stray characters inside a tag somebody left open. The
///         span test above cannot see that, so before a candidate is handed over its old
///         tokens are compared against the new ones, kind for kind and character for
///         character. That is what makes the blender an optimisation rather than a second,
///         quieter parser: a candidate that fails is simply reparsed.
///     </para>
///     <para>
///         The comparison runs one visible token past the node, because a node's own parse
///         can read one — whether an <c>@if</c> has an <c>else</c> arm is decided by the
///         token after its body, and that token is not part of the node when the answer is
///         no. The equal-trees-to-a-full-reparse property is pinned by test.
///     </para>
///     <para>
///         ⚠ <b>Position and tokens are still not the whole question: the same characters, lexed
///         the same way, mean different things to different parse loops.</b> A front end usually has
///         more than one — Raven parses a type body and an enum body with separate grammars, and
///         only the first can produce a method — so a candidate carries the
///         <see cref="ReuseCandidate.Context" /> it was parsed in and a reuse site names the one it
///         is standing in. An edit that dissolves an <c>enum</c> header leaves its members' text
///         untouched and lexing identically at what is now a member boundary, and without the
///         context the blender would hand an enum member to the member loop, which is a tree no
///         full parse can produce. Found by <c>Vixen.Fuzz</c>'s <c>raven</c> target; the smallest
///         case renames an enum to a keyword and is a row in <c>IncrementalParseTests</c>.
///     </para>
/// </remarks>
sealed class Blender {
    /// <summary>A lendable node, and where it sat in the text the previous parse read.</summary>
    readonly record struct Candidate(GreenNode Green, int OldFullStart);

    readonly Dictionary<(int Context, int FullStart), Candidate> reusable = [];
    readonly IReadOnlyList<LexedToken> oldTokens;

    public Blender(
        IEnumerable<ReuseCandidate> candidates,
        IReadOnlyList<TextChangeRange> changes,
        IReadOnlyList<LexedToken> oldTokens
    ) {
        this.oldTokens = oldTokens;

        foreach (var (node, context) in candidates) {
            var start = node.Position;
            var end = start + node.Green.FullWidth;

            if (Affected(start, end, changes)) {
                continue;
            }

            var delta = 0;
            foreach (var change in changes) {
                if (change.Span.End <= start) {
                    delta += change.Delta;
                }
            }

            reusable[(context, start + delta)] = new(node.Green, start);
        }
    }

    /// <summary>The old green node whose full span starts here in the new text, or null.</summary>
    /// <param name="context">The parse loop asking, which has to be the one the node came out of.</param>
    /// <param name="newFullStart">Where the pending trivia run begins in the new text.</param>
    /// <param name="newTokens">The new text's tokens, which have to read the node the old way.</param>
    /// <param name="resumeAt">The index in <paramref name="newTokens" /> the node ends at.</param>
    /// <returns>A node to splice in, or null to parse the characters again.</returns>
    public GreenNode? TryReuse(
        int context,
        int newFullStart,
        IReadOnlyList<LexedToken> newTokens,
        out int resumeAt
    ) {
        resumeAt = 0;

        return reusable.TryGetValue((context, newFullStart), out var candidate)
            && LexesTheSame(candidate, newFullStart, newTokens, out resumeAt)
                ? candidate.Green
                : null;
    }

    /// <summary>
    ///     Whether the new token stream reads the candidate's characters exactly as the stream the
    ///     node was parsed from did.
    /// </summary>
    bool LexesTheSame(Candidate candidate, int newFullStart, IReadOnlyList<LexedToken> newTokens, out int resumeAt) {
        resumeAt = -1;

        if (IndexAt(oldTokens, candidate.OldFullStart) is not { } before
            || IndexAt(newTokens, newFullStart) is not { } after) {
            return false;
        }

        var oldEnd = candidate.OldFullStart + candidate.Green.FullWidth;

        while (before < oldTokens.Count && after < newTokens.Count) {
            var one = oldTokens[before++];
            var other = newTokens[after++];

            if (one.RawKind != other.RawKind
                || one.Flags != other.Flags
                || !string.Equals(one.Text, other.Text, StringComparison.Ordinal)) {
                return false;
            }

            if (one.Position < oldEnd) {
                continue;
            }

            // The first token at or after the node's end is where the parser picks the stream back
            // up; the trivia before the next visible one belongs to whatever comes next.
            if (resumeAt < 0) {
                // A node that ends inside a token has nowhere to hand the stream back, which can
                // only happen if it was built from something other than whole tokens.
                if (one.Position != oldEnd) {
                    return false;
                }

                resumeAt = after - 1;
            }

            if (!one.IsTrivia) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The index of the token starting exactly at a position, or null.</summary>
    static int? IndexAt(IReadOnlyList<LexedToken> tokens, int position) {
        int low = 0, high = tokens.Count - 1;

        while (low <= high) {
            var middle = (low + high) / 2;
            var start = tokens[middle].Position;

            if (start == position) {
                return middle;
            }

            if (start < position) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return null;
    }

    static bool Affected(int start, int end, IReadOnlyList<TextChangeRange> changes) {
        foreach (var change in changes) {
            // One character of margin: an edit touching either boundary could merge
            // tokens across it, so adjacency counts as affected.
            var changeStart = change.Span.Start - 1;
            var changeEnd = change.Span.End + 1;
            if (changeStart < end && start < changeEnd) {
                return true;
            }
        }

        return false;
    }
}
