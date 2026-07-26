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
///     A candidate survives when no change touches its old full span, with one
///     character of margin on each side so an edit that is merely <em>adjacent</em>
///     cannot glue itself onto the node's first or last token; its position is then
///     shifted by the length delta of every change before it. Reuse is by exact new
///     full-start match, and the parser re-verifies that the node's width lands on a
///     token boundary of the new stream — a candidate that fails either check is
///     simply reparsed, so the blender can only ever make the parse faster, not
///     different. The equal-trees-to-a-full-reparse property is pinned by test.
/// </remarks>
sealed class Blender {
    readonly Dictionary<int, GreenNode> reusable = [];

    public Blender(IEnumerable<SyntaxNode> candidates, IReadOnlyList<TextChangeRange> changes) {
        foreach (var candidate in candidates) {
            var start = candidate.Position;
            var end = start + candidate.Green.FullWidth;

            if (Affected(start, end, changes)) {
                continue;
            }

            var delta = 0;
            foreach (var change in changes) {
                if (change.Span.End <= start) {
                    delta += change.Delta;
                }
            }

            reusable[start + delta] = candidate.Green;
        }
    }

    /// <summary>The old green node whose full span starts here in the new text, or null.</summary>
    public GreenNode? TryReuse(int newFullStart) => reusable.GetValueOrDefault(newFullStart);

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
