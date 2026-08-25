// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>One stretch of text that can be handed to a shaper as a unit.</summary>
/// <param name="Start">Where it starts in the source text, as a UTF-16 index.</param>
/// <param name="Length">How many UTF-16 units it covers.</param>
/// <param name="Level">Its bidi embedding level. Even runs left to right, odd runs right to left.</param>
/// <param name="Script">Its script.</param>
public readonly record struct TextItem(int Start, int Length, int Level, Script Script) {
    /// <summary>Whether the run runs right to left.</summary>
    public bool IsRightToLeft => (Level & 1) != 0;
}

/// <summary>Cuts a paragraph into the runs a shaper can be given.</summary>
/// <remarks>
///     <para>
///         A shaper works on one direction, one script and one font at a time, so something has to
///         decide where those change. That is this, and it is the part of text rendering Vixen
///         actually owns — HarfBuzz does the shaping, but it shapes whatever it is told to, so a
///         wrong answer here produces wrong glyphs out of a correct shaper. The
///         <c>text-rendering-tests</c> suite is sensitive to exactly this, which is why it is the
///         gate.
///     </para>
///     <para>
///         <b>Cutting too finely is as wrong as cutting in the wrong place.</b> Shaping depends on
///         context: a substitution can be conditioned on the character after the one it replaces,
///         and the Consortium has a test case named <i>Space Isn't Nothing</i> for the specific
///         mistake of treating a space as a run boundary. So the rule is to cut only where the
///         shaper genuinely cannot carry on, and to let everything neutral join whatever surrounds
///         it.
///     </para>
/// </remarks>
public static class TextItemizer {
    /// <summary>Cuts a paragraph into runs, in logical order.</summary>
    /// <param name="text">The paragraph.</param>
    /// <param name="direction">Its base direction.</param>
    /// <returns>The runs, in the order the text is read.</returns>
    public static List<TextItem> Itemize(string text, ParagraphDirection direction = ParagraphDirection.Auto) {
        ArgumentNullException.ThrowIfNull(text);

        var items = new List<TextItem>();
        if (text.Length == 0) {
            return items;
        }

        // The bidi algorithm works in code points and the rest of the world works in UTF-16, so the
        // decode happens once here and every index after it is a UTF-16 one.
        var codePoints = new List<int>();
        var offsets = new List<int>();
        var position = 0;

        while (position < text.Length) {
            offsets.Add(position);
            codePoints.Add(GraphemeBreaker.Decode(text, ref position));
        }

        offsets.Add(text.Length);

        var points = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(codePoints);
        var bidi = BidiAlgorithm.Resolve(points, direction);
        var scripts = ResolveScripts(points);

        var start = 0;
        for (var i = 1; i <= codePoints.Count; i++) {
            if (i < codePoints.Count && bidi.Levels[i] == bidi.Levels[start] && scripts[i] == scripts[start]) {
                continue;
            }

            items.Add(new TextItem(
                offsets[start],
                offsets[i] - offsets[start],
                bidi.Levels[start],
                scripts[start]
            ));

            start = i;
        }

        return items;
    }

    /// <summary>Puts runs into the order they are drawn in, left to right.</summary>
    /// <param name="items">The runs, in logical order.</param>
    /// <returns>Indices into <paramref name="items" />, in visual order.</returns>
    /// <remarks>
    ///     UAX#9's L2, applied to runs rather than to characters: from the deepest level down to the
    ///     shallowest odd one, reverse every maximal stretch at or above that level. Doing it to runs
    ///     is sound precisely because a run has one level throughout — which is why level is part of
    ///     what <see cref="Itemize" /> cuts on.
    /// </remarks>
    public static int[] VisualOrder(IReadOnlyList<TextItem> items) {
        ArgumentNullException.ThrowIfNull(items);

        var levels = new int[items.Count];
        for (var i = 0; i < levels.Length; i++) {
            levels[i] = items[i].Level;
        }

        return VisualOrder(levels);
    }

    /// <summary>Puts runs into the order they are drawn in, given only their levels.</summary>
    /// <param name="levels">Each run's embedding level, in logical order.</param>
    /// <returns>Indices into <paramref name="levels" />, in visual order.</returns>
    /// <remarks>
    ///     <para>
    ///         The same L2 as the overload above, and the overload above is written in terms of this
    ///         one. It exists because the *other* thing that has to reorder runs is not holding
    ///         <see cref="TextItem" />s: <c>Vixen.Ui</c>'s <c>TextLine</c> is a list of runs that have
    ///         already been shaped, each in its own face, and it has to lay them down left to right.
    ///         A second copy of this loop over there would be a copy of the one rule in UAX#9 whose
    ///         being wrong is invisible in a language the reviewer reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each entry must be a run of <i>uniform</i> level.</b> Reversing stretches of runs
    ///         is sound only because a run has one level throughout — that is why <see cref="Itemize" />
    ///         cuts on level, and why a caller that cuts on something else (a font, a size) has to
    ///         intersect its own boundaries with these before it can use this.
    ///     </para>
    /// </remarks>
    public static int[] VisualOrder(ReadOnlySpan<int> levels) {
        var order = new int[levels.Length];
        for (var i = 0; i < order.Length; i++) {
            order[i] = i;
        }

        if (levels.Length == 0) {
            return order;
        }

        var highest = 0;
        var lowestOdd = int.MaxValue;

        foreach (var level in levels) {
            highest = Math.Max(highest, level);

            if ((level & 1) != 0) {
                lowestOdd = Math.Min(lowestOdd, level);
            }
        }

        for (var level = highest; level >= lowestOdd; level--) {
            for (var i = 0; i < order.Length; i++) {
                if (levels[i] < level) {
                    continue;
                }

                var end = i;
                while (end + 1 < order.Length && levels[end + 1] >= level) {
                    end++;
                }

                Array.Reverse(order, i, end - i + 1);
                i = end;
            }
        }

        return order;
    }

    /// <summary>UAX#24 — the script of each code point, once the neutral ones have been settled.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>Common</c> and <c>Inherited</c> are not scripts, they are "whatever is around me":
    ///         a full stop, a space, a combining mark. Giving them a run of their own would cut a
    ///         word in half at its own punctuation, so they take the script of what precedes them,
    ///         and a stretch of them at the start of the text takes the script of what follows.
    ///     </para>
    ///     <para>
    ///         <b>Brackets are the interesting case</b> and the reason this is not a two-line loop.
    ///         In <c>ελληνικά (Greek)</c> the closing parenthesis should belong to the same script as
    ///         the opening one, not to whatever was last seen before it — so an opening bracket
    ///         remembers the script in force and its partner restores it. That is the same pairing
    ///         the bidi algorithm's N0 needs, so it reads the same table: a bracket table that
    ///         disagreed with itself between two subsystems would be a genuinely nasty bug.
    ///     </para>
    ///     <para>
    ///         <c>Unknown</c> — an unassigned or private-use code point — inherits rather than
    ///         breaking. A private-use icon in a run of Latin is a <i>font</i> question, and font
    ///         itemisation is where it gets answered; splitting the script run there would only
    ///         deny the shaper context it could have used.
    ///     </para>
    /// </remarks>
    static Script[] ResolveScripts(ReadOnlySpan<int> codePoints) {
        var scripts = new Script[codePoints.Length];
        var stack = new List<(int Closing, Script Script)>();
        var current = Script.Common;
        var firstReal = -1;

        for (var i = 0; i < codePoints.Length; i++) {
            var script = ScriptTable.Of(codePoints[i]);

            if (BidiBracketTable.TryGet(codePoints[i], out var paired, out var opens)) {
                if (opens) {
                    stack.Add((paired, current));
                } else {
                    for (var k = stack.Count - 1; k >= 0; k--) {
                        if (stack[k].Closing != codePoints[i]) {
                            continue;
                        }

                        current = stack[k].Script;
                        stack.RemoveRange(k, stack.Count - k);
                        break;
                    }
                }
            }

            if (script is Script.Common or Script.Inherited or Script.Unknown) {
                scripts[i] = current;
                continue;
            }

            if (firstReal < 0) {
                // Everything before the first real script was guessed as Common; it belongs with
                // what follows it, not in a run of its own.
                for (var j = 0; j < i; j++) {
                    scripts[j] = script;
                }

                // ⚠ And so does anything a bracket has already remembered. An opening bracket
                // before the first letter pushes the script "in force", which at that point is
                // Common — a script that never existed. Backfilling the characters but not the
                // stack makes the closing bracket restore the guess, so `(ಲ್ಲಿ)` comes out as
                // Kannada followed by a one-character run of nothing in particular.
                for (var k = 0; k < stack.Count; k++) {
                    if (stack[k].Script == Script.Common) {
                        stack[k] = (stack[k].Closing, script);
                    }
                }

                firstReal = i;
            }

            current = script;
            scripts[i] = script;
        }

        return scripts;
    }
}
