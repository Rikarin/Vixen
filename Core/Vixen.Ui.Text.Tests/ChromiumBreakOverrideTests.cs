// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     Where Chromium knowingly departs from UAX #14, enumerated, against Parley's
///     <c>parley_engine/src/break_overrides.rs</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not a conformance suite — a ledger of a disagreement, and the disagreement is the
///         point.</b> CSS Text 3 § 5.1 says the specification "does not fully define where soft wrap
///         opportunities occur"; Vixen implements UAX #14 as written and Chromium does not, so
///         neither is wrong and both answers are worth having written down. What this file exists to
///         prevent is the third state: a change to <see cref="LineBreaker" /> that moves one of these
///         470 pairs and nobody notices, because the Consortium's 19 338 cases say nothing about a
///         browser's preferences and never will.
///     </para>
///     <para>
///         ⚠ <b>The listing is committed and compared byte for byte, and that is the instrument
///         decision.</b> A test that recomputed both sides and asserted they differ somewhere would
///         pass on the day either side stopped being computed at all — a comparator that called two
///         empty manifests identical is a real thing this repository has shipped. So
///         <c>ChromiumBreakDeltas.txt</c> is data: the run rebuilds it and demands the same bytes,
///         <see cref="The_ledger_is_not_empty_and_runs_both_ways" /> demands that it hold witnesses
///         of both directions, and a genuine change is a reviewable diff rather than a number.
///         <c>VIXEN_REGENERATE=1</c> rewrites it, as everywhere else in this tree.
///     </para>
///     <para>
///         ⚠ <b>Pairs in isolation, which is what Parley's table is and is therefore the only honest
///         comparison.</b> Each row asks both sides about a two-character string, so UAX #14's
///         context-sensitive rules — the numeric run of LB25, the Hebrew hyphen of LB21a — never
///         arise, and a delta here is a disagreement about the pair rather than about a paragraph.
///         The one exception is stated where it lives: Chromium's <c>-</c>-before-a-digit rule reads
///         a third character, so it is asserted separately in
///         <see cref="A_hyphen_before_a_digit_reads_the_character_before_the_hyphen" /> and enters
///         the ledger with no preceding character, which is the case Chromium calls
///         <c>last_last_ch == 0</c>.
///     </para>
///     <para>
///         Licence: Parley is Apache-2.0 OR MIT; the entry is in the repository's <c>NOTICE</c> under
///         the conformance corpora, and <c>ConformanceCorpusNoticeTests</c> is what keeps it there.
///     </para>
/// </remarks>
public class ChromiumBreakOverrideTests {
    /// <summary>Parley's <c>chromium_hyphen_digit_depends_on_preceding_char</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The rule that makes <c>Subtract -5</c> and <c>AAAA-2222</c> break differently, and the
    ///     only one in Chromium's table that reads three characters.</b> A hyphen is a minus sign when
    ///     what precedes it is not alphanumeric — including nothing at all, which is Chromium's
    ///     <c>last_last_ch == 0</c> and is why a start-of-text hyphen suppresses rather than defers.
    /// </remarks>
    /// <param name="beforeBefore">The character before the hyphen, or the null character for none.</param>
    /// <param name="after">The character after the hyphen.</param>
    /// <param name="expected">What Chromium answers.</param>
    [Theory]
    [InlineData('D', '1', true)]
    [InlineData('4', '5', true)]
    [InlineData(' ', '1', false)]
    [InlineData('(', '1', false)]
    [InlineData('\0', '1', false)]
    public void A_hyphen_before_a_digit_reads_the_character_before_the_hyphen(
        char beforeBefore,
        char after,
        bool expected
    ) => Assert.Equal(expected, ChromiumLineBreakOverride.Of(beforeBefore == '\0' ? null : beforeBefore, '-', after));

    /// <summary>Parley's <c>chromium_ignores_uax_14_lb13</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The deviation Parley names outright, and the one with a Chromium source line beside
    ///     it.</b> LB13 forbids an opportunity before a closing bracket, a comma or an exclamation
    ///     mark <i>even after a space</i>; Blink allows a break after a space run unconditionally. So
    ///     a browser puts <c>)</c> at the start of the next line where this store keeps it on the
    ///     previous one, and no amount of UAX #14 conformance will produce the browser's answer.
    /// </remarks>
    /// <param name="after">The character after the space.</param>
    [Theory]
    [InlineData('}')]
    [InlineData(')')]
    [InlineData(']')]
    [InlineData('!')]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData('/')]
    [InlineData(':')]
    [InlineData(';')]
    [InlineData('?')]
    [InlineData('b')]
    [InlineData('(')]
    public void A_break_after_a_space_is_always_allowed(char after) =>
        Assert.Equal(true, ChromiumLineBreakOverride.Of(null, ' ', after));

    /// <summary>Parley's <c>chromium_hyphen_non_digit_defers_to_table</c>.</summary>
    [Fact]
    public void A_hyphen_before_a_non_digit_ignores_what_precedes_it() {
        Assert.Equal(true, ChromiumLineBreakOverride.Of('D', '-', 'b'));
        Assert.Equal(true, ChromiumLineBreakOverride.Of(null, '-', 'b'));

        // Non-ASCII on either side is outside the table and defers to UAX #14 entirely.
        Assert.Null(ChromiumLineBreakOverride.Of('D', '-', 'é'));
    }

    /// <summary>Parley's <c>chromium_suppresses_ascii_punctuation_breaks</c>.</summary>
    [Fact]
    public void Chromium_suppresses_a_break_around_ascii_punctuation() {
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('a', '/'));
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('/', 'b'));
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('a', '.'));
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('a', ':'));
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('a', 'b'));
    }

    /// <summary>Parley's <c>chromium_allows_some_breaks</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Every assertion here is about the ORDER the table is folded in, which is the one thing
    ///     a transcription of it can get wrong without looking wrong.</b> <c>) (</c> breaks and
    ///     <c>a (</c> does not, because the "break before an opening bracket" rule is written early
    ///     and the "no break after a letter" rule is written later and overwrites it. Sort the rules
    ///     alphabetically, or group them by allow/suppress, and these five change while every other
    ///     test in this file stays green.
    /// </remarks>
    [Fact]
    public void Chromium_allows_a_break_where_a_later_rule_did_not_close_it() {
        Assert.Equal(true, ChromiumLineBreakOverride.Lookup(')', '('));
        Assert.Equal(true, ChromiumLineBreakOverride.Lookup(')', '<'));
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('a', '('));
        Assert.Equal(true, ChromiumLineBreakOverride.Lookup('-', 'b'));
        Assert.Equal(true, ChromiumLineBreakOverride.Lookup('?', 'b'));
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('-', '5'));
        Assert.Equal(false, ChromiumLineBreakOverride.Lookup('?', '"'));
    }

    /// <summary>Parley's <c>non_ascii_pairs_defer_to_icu</c>.</summary>
    /// <remarks>
    ///     ⚠ The third assertion is the one worth keeping: U+0020 is below the table's first row, so
    ///     a pair <i>ending</i> in a space defers even though a pair <i>beginning</i> with one is the
    ///     table's loudest override. The two are different questions and this store's own
    ///     <c>First</c> constant is where that is written down.
    /// </remarks>
    [Fact]
    public void A_pair_outside_printable_ascii_defers_to_uax14() {
        Assert.Null(ChromiumLineBreakOverride.Lookup('a', 'é'));
        Assert.Null(ChromiumLineBreakOverride.Lookup('é', 'a'));
        Assert.Null(ChromiumLineBreakOverride.Lookup('a', ' '));
    }

    /// <summary>The ledger: every pair the two disagree about, byte for byte.</summary>
    /// <remarks>
    ///     ⚠ <b>What this prints on the day it does not run.</b> If <see cref="LineBreaker" /> stopped
    ///     answering, every pair would become a delta and the file would grow by thousands of lines;
    ///     if the transcribed table stopped answering, every pair would defer and the file would
    ///     empty. Both are loud, and neither is a number that could be nudged — which is why the
    ///     listing is compared rather than counted.
    /// </remarks>
    [Fact]
    public void The_deltas_are_exactly_the_ones_recorded() {
        var built = Ledger();
        var path = LedgerPath();

        if (Environment.GetEnvironmentVariable("VIXEN_REGENERATE") == "1") {
            File.WriteAllText(path, built);
        }

        Assert.Equal(built, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>⚠ And the ledger holds witnesses of both directions, so it cannot go quietly empty.</summary>
    /// <remarks>
    ///     <b>The instrument's instrument.</b> Two of these three assertions would survive a ledger
    ///     that had lost half its meaning: a file recording only suppressions would still be
    ///     non-empty, and a file recording only the LB13 space rule would still run both ways. All
    ///     three together say the comparison is live in both directions and that the deviation Parley
    ///     documents by name is still in it.
    /// </remarks>
    [Fact]
    public void The_ledger_is_not_empty_and_runs_both_ways() {
        var lines = File.ReadAllLines(LedgerPath())
            .Where(line => line.Length > 0 && line[0] != '#')
            .ToList();

        Assert.NotEmpty(lines);
        Assert.Contains(lines, line => line.EndsWith("chromium=break  uax14=none", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith("chromium=none  uax14=break", StringComparison.Ordinal));

        // The LB13 deviation, which is the one Parley cites a Chromium line number for. Ten closers
        // and separators a browser will start a line with and this store will not.
        foreach (var after in ")],.;:?!}/") {
            Assert.Contains(lines, line => line.StartsWith(Row(' ', after), StringComparison.Ordinal));
        }
    }

    /// <summary>Every printable-ASCII pair the two answer differently, one to a line.</summary>
    static string Ledger() {
        var text = new StringBuilder();

        text.Append("# Where Chromium's preferred line breaking departs from UAX #14 as Vixen implements it.\n");
        text.Append("# Generated by ChromiumBreakOverrideTests; regenerate with VIXEN_REGENERATE=1.\n");
        text.Append("# Chromium's answers are transcribed in ChromiumLineBreakOverride from Parley's\n");
        text.Append("# parley_engine/src/break_overrides.rs (Apache-2.0 OR MIT); Vixen's are LineBreaker's,\n");
        text.Append("# asked about the two-character string and nothing around it.\n");
        text.Append('\n');

        var opportunities = new List<int>();

        for (var before = ' '; before <= ChromiumLineBreakOverride.Last; before++) {
            for (var after = ' '; after <= ChromiumLineBreakOverride.Last; after++) {
                if (ChromiumLineBreakOverride.Of(null, before, after) is not { } chromium) {
                    continue;
                }

                LineBreaker.Collect([before, after], opportunities);
                var vixen = opportunities.Contains(1);

                if (vixen == chromium) {
                    continue;
                }

                text.Append(Row(before, after));
                text.Append("chromium=");
                text.Append(chromium ? "break" : "none");
                text.Append("  uax14=");
                text.Append(vixen ? "break" : "none");
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>One row's stable prefix: the two code points, and how they read.</summary>
    static string Row(char before, char after) {
        int first = before;
        int second = after;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{first:X4} {second:X4}  {Readable(before)} {Readable(after)}  "
        );
    }

    /// <summary>A character as a two-column token, so the listing stays aligned and greppable.</summary>
    static string Readable(char value) =>
        value switch {
            ' ' => "SP",
            '\u007F' => "DL",
            _ => $" {value}"
        };

    /// <summary>The committed listing, in the source tree rather than in the output directory.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither an embedded resource nor a copied one, and both of those were tried and are
    ///     wrong here.</b> The file is DATA UNDER REVIEW: a regenerating run has to rewrite the copy a
    ///     reviewer will read in the diff, and a copy in <c>bin</c> is neither. Walking up to the
    ///     repository root is what <c>ConformanceCorpusNoticeTests</c> does for the NOTICE and for the
    ///     same reason — and it lands in whichever worktree the assembly was built in, which is the
    ///     one whose answer is being judged.
    /// </remarks>
    static string LedgerPath() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return Path.Combine(
                    directory.FullName,
                    "Core",
                    "Vixen.Ui.Text.Tests",
                    "ChromiumBreakDeltas.txt"
                );
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
