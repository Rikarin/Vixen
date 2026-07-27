// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>Which way a paragraph runs, before anything in it has been looked at.</summary>
public enum ParagraphDirection : byte {
    /// <summary>Work it out from the first strong character. What almost everything should use.</summary>
    Auto,

    /// <summary>Left to right, whatever the text says.</summary>
    LeftToRight,

    /// <summary>Right to left, whatever the text says.</summary>
    RightToLeft
}

/// <summary>The result of running the bidirectional algorithm over one paragraph.</summary>
/// <param name="ParagraphLevel">0 for a left-to-right paragraph, 1 for a right-to-left one.</param>
/// <param name="Levels">The embedding level of each code point.</param>
/// <param name="VisualOrder">
///     The code point indices in the order they are drawn, left to right, with the characters L3
///     and L4 remove already removed.
/// </param>
public readonly record struct BidiResult(int ParagraphLevel, int[] Levels, int[] VisualOrder);

/// <summary>The Unicode bidirectional algorithm, UAX#9.</summary>
/// <remarks>
///     <para>
///         Text is stored in the order it is read and drawn in the order it is seen, and for Arabic
///         and Hebrew those are not the same. Worse, they are not the same for the <i>English</i> in
///         an Arabic sentence either: a phone number inside a right-to-left paragraph runs left to
///         right inside a right-to-left run, and the brackets around it swap which way they point.
///         Nothing about this can be approximated — a paragraph is either laid out correctly or it is
///         gibberish to the person reading it.
///     </para>
///     <para>
///         The algorithm has four phases and each is a different shape of problem. <b>Explicit
///         levels</b> (X1–X8) are a stack machine over embedding and isolate controls.
///         <b>Isolating run sequences</b> (BD13) chop the paragraph into the units everything after
///         works on, and getting them wrong makes every later rule look broken. <b>Resolution</b>
///         (W, N, I) is a series of passes that rewrite types in place. <b>Reordering</b> (L) turns
///         levels into an order by reversing runs from the deepest level up.
///     </para>
///     <para>
///         Judged by 91 707 of the Consortium's own cases, which is the only sane way to write this.
///     </para>
/// </remarks>
public static class BidiAlgorithm {
    /// <summary>The deepest an embedding can go. UAX#9's, and it is not negotiable.</summary>
    public const int MaximumDepth = 125;

    /// <summary>Runs the algorithm over a paragraph.</summary>
    /// <param name="codePoints">The paragraph's code points.</param>
    /// <param name="direction">Its base direction.</param>
    /// <returns>The levels and the visual order.</returns>
    public static BidiResult Resolve(ReadOnlySpan<int> codePoints, ParagraphDirection direction = ParagraphDirection.Auto) {
        var classes = new BidiClass[codePoints.Length];
        for (var i = 0; i < codePoints.Length; i++) {
            classes[i] = BidiClassTable.Of(codePoints[i]);
        }

        var paragraphLevel = direction switch {
            ParagraphDirection.LeftToRight => 0,
            ParagraphDirection.RightToLeft => 1,
            _ => AutoLevel(classes, 0, classes.Length)
        };

        var state = new BidiState(codePoints, classes, paragraphLevel);
        state.ResolveExplicitLevels();
        state.ResolveSequences();
        state.ApplyL1();

        return new BidiResult(paragraphLevel, state.Levels, state.VisualOrder());
    }

    /// <summary>Runs the algorithm over a string.</summary>
    /// <param name="text">The paragraph.</param>
    /// <param name="direction">Its base direction.</param>
    /// <returns>The levels and the visual order, indexed by code point.</returns>
    public static BidiResult Resolve(string text, ParagraphDirection direction = ParagraphDirection.Auto) {
        ArgumentNullException.ThrowIfNull(text);

        var codePoints = new List<int>();
        var position = 0;

        while (position < text.Length) {
            codePoints.Add(GraphemeBreaker.Decode(text, ref position));
        }

        return Resolve(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(codePoints), direction);
    }

    /// <summary>P2 and P3 — the level implied by the first strong character.</summary>
    /// <remarks>
    ///     Characters inside an isolate are skipped, because an isolate is exactly a promise that
    ///     what is inside it does not affect what is outside. Getting that wrong makes a paragraph
    ///     that begins with an isolated Hebrew name run right to left in its entirety.
    /// </remarks>
    internal static int AutoLevel(BidiClass[] classes, int from, int to) {
        var isolates = 0;

        for (var i = from; i < to; i++) {
            switch (classes[i]) {
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    isolates++;
                    break;

                case BidiClass.PDI:
                    if (isolates > 0) {
                        isolates--;
                    }

                    break;

                case BidiClass.L when isolates == 0:
                    return 0;

                case BidiClass.R or BidiClass.AL when isolates == 0:
                    return 1;

                default:
                    break;
            }
        }

        return 0;
    }
}
