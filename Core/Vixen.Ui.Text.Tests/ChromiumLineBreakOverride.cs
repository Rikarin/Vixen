// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     Chromium's preferred soft wrap opportunities for printable ASCII, transcribed from Parley's
///     <c>parley_engine/src/break_overrides.rs</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>An oracle, not an implementation, and it lives in the test assembly for that reason.</b>
///         CSS Text 3 § 5.1 says outright that the specification "does not fully define where soft
///         wrap opportunities occur", so a browser is free to differ from UAX #14 and Chromium does.
///         Vixen implements UAX #14 as written and is judged by the Consortium's 19 338 cases; what
///         this file buys is knowing <i>which</i> pairs the two disagree about, so that a future
///         change to <see cref="LineBreaker" /> that moved one is visible instead of silent.
///     </para>
///     <para>
///         ⚠ <b>Transcribed in build order, because the rules overwrite each other.</b> Parley's
///         table is a fold: every printable pair is set to "no break", then a dozen ranges reopen or
///         re-close in a sequence where the last write wins. <c>('-', ALL, true)</c> comes before
///         <c>(ALL, '.', false)</c>, so <c>-</c> before a full stop does <i>not</i> break; reordering
///         two lines here changes hundreds of pairs and nothing about the shape of the file says so.
///         The order below is the order in <c>AsciiLineBreakTableBuilder::chromium</c>, line for line.
///     </para>
///     <para>
///         ⚠ <b>Two rules are not in the table at all</b>, and they are the two that need context a
///         pair does not have. A break is always allowed after a run of spaces — Chromium ignores
///         LB13 there, which is the deviation Parley's comment calls out by name — and a break
///         between <c>-</c> and a digit depends on the character <i>before</i> the hyphen, so that
///         <c>Subtract -5</c> holds together and <c>AAAA-2222</c> does not.
///     </para>
///     <para>
///         Licence: Parley is Apache-2.0 OR MIT; the entry is in the repository's <c>NOTICE</c> under
///         the conformance corpora, and <c>ConformanceCorpusNoticeTests</c> is what keeps it there.
///     </para>
/// </remarks>
public static class ChromiumLineBreakOverride {
    /// <summary>The lowest code point Chromium's pair table covers.</summary>
    /// <remarks>
    ///     ⚠ U+0021, not U+0020. A space is <i>below</i> the table, which is why the space rule has to
    ///     be a separate clause rather than a row — and why <c>Lookup('a', ' ')</c> defers.
    /// </remarks>
    public const char First = '!';

    /// <summary>The highest code point Chromium's pair table covers.</summary>
    public const char Last = '\u007F';

    static readonly bool?[,] Table = Build();

    /// <summary>What Chromium does between two adjacent characters, or null to defer to UAX #14.</summary>
    /// <param name="beforeBefore">The character before <paramref name="before" />, if any.</param>
    /// <param name="before">The character on the left of the position.</param>
    /// <param name="after">The character on the right of the position.</param>
    /// <returns>True to force an opportunity, false to suppress one, null to defer.</returns>
    public static bool? Of(char? beforeBefore, char before, char after) {
        // ⚠ Chromium treats the position after a space run as an opportunity unconditionally, which
        // UAX #14's LB13 does not: `a )` breaks before the bracket in a browser and does not here.
        // Parley's comment says this is the only deviation of its kind they are aware of.
        //
        // The mandatory break characters are excluded because LB6 forbids an opportunity immediately
        // before one — a rule Chromium keeps.
        if (before == ' '
            && after != ' '
            && after is not ('\n' or '\u000B' or '\u000C' or '\r' or '\u0085' or '\u2028' or '\u2029')) {
            return true;
        }

        // ⚠ The one rule that reads three characters. A hyphen before a digit is a minus sign after a
        // space or a bracket and a separator after a letter or a digit, and Chromium breaks only in
        // the second case. Start of text behaves as the first, matching Chromium's `last_last_ch == 0`.
        if (before == '-' && char.IsAsciiDigit(after)) {
            return beforeBefore is { } previous && char.IsAsciiLetterOrDigit(previous);
        }

        return Lookup(before, after);
    }

    /// <summary>The pair table alone, with no contextual rule applied.</summary>
    /// <param name="before">The character on the left of the position.</param>
    /// <param name="after">The character on the right of the position.</param>
    /// <returns>True to force an opportunity, false to suppress one, null to defer.</returns>
    public static bool? Lookup(char before, char after) =>
        before < 128 && after < 128 ? Table[before, after] : null;

    /// <summary>Parley's <c>AsciiLineBreakTableBuilder::chromium</c>, rule for rule and in order.</summary>
    static bool?[,] Build() {
        var table = new bool?[128, 128];

        // Every printable pair defaults to "no break", and the rules below carve out of it.
        Pairs(table, First, Last, First, Last, allow: false);
        Pairs(table, First, Last, '(', '(', allow: true);
        Pairs(table, First, Last, '<', '<', allow: true);
        Pairs(table, First, Last, '[', '[', allow: true);
        Pairs(table, First, Last, '{', '{', allow: true);
        Pairs(table, '-', '-', First, Last, allow: true);
        Pairs(table, '?', '?', First, Last, allow: true);
        Pairs(table, '-', '-', '$', '$', allow: false);
        Pairs(table, First, Last, '!', '!', allow: false);
        Pairs(table, '?', '?', '"', '"', allow: false);
        Pairs(table, '?', '?', '\'', '\'', allow: false);
        Pairs(table, First, Last, ')', ')', allow: false);
        Pairs(table, First, Last, ',', ',', allow: false);
        Pairs(table, First, Last, '.', '.', allow: false);
        Pairs(table, First, Last, '/', '/', allow: false);
        Pairs(table, '-', '-', '0', '9', allow: false);
        Pairs(table, First, Last, ':', ':', allow: false);
        Pairs(table, First, Last, ';', ';', allow: false);
        Pairs(table, First, Last, '?', '?', allow: false);
        Pairs(table, First, Last, ']', ']', allow: false);
        Pairs(table, First, Last, '}', '}', allow: false);
        Pairs(table, '$', '$', First, Last, allow: false);
        Pairs(table, '\'', '\'', First, Last, allow: false);
        Pairs(table, '(', '(', First, Last, allow: false);
        Pairs(table, '/', '/', First, Last, allow: false);
        Pairs(table, '0', '9', First, Last, allow: false);
        Pairs(table, '<', '<', First, Last, allow: false);
        Pairs(table, '@', '@', First, Last, allow: false);
        Pairs(table, 'A', 'Z', First, Last, allow: false);
        Pairs(table, '[', '[', First, Last, allow: false);
        Pairs(table, '^', '`', First, Last, allow: false);
        Pairs(table, 'a', 'z', First, Last, allow: false);
        Pairs(table, '{', '{', First, Last, allow: false);
        Pairs(table, Last, Last, First, Last, allow: false);

        return table;
    }

    static void Pairs(bool?[,] table, char beforeFrom, char beforeTo, char afterFrom, char afterTo, bool allow) {
        for (var before = beforeFrom; before <= beforeTo; before++) {
            for (var after = afterFrom; after <= afterTo; after++) {
                table[before, after] = allow;
            }
        }
    }
}
