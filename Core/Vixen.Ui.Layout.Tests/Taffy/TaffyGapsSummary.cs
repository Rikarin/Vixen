// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     The one line in a gaps file that states how the corpus stands, held to the constants the
///     suite asserts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Each of these files opens by telling a reader how many fixtures pass, and until this
///         existed that sentence was the only number in the whole arrangement that nothing checked.</b>
///         <c>GridKnownGaps.txt</c> said "2 080 of the 2 120 … pass, 40 fail" for two batches after
///         <c>TaffyGridConformanceTests</c> had pinned 2 104 and 16 — the body of the same file
///         already carried the new figures in the heading that moved them, and only the summary a
///         reader meets first was stale. These files exist to be read as the answer to "what is
///         missing from this algorithm", so the number at the top is the one a reader trusts and was
///         the one nothing could contradict.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day the line is deleted or reworded is the whole design.</b> A
///         regex sweep that finds nothing reports no disagreement, which is how a check like this
///         passes for ever after somebody rewrites the paragraph around it. So the count of matching
///         lines is asserted to be exactly one <i>before</i> the numbers on it are looked at: zero is
///         a failure naming the file, and two is a failure as well, because a second summary is how a
///         stale one survives a correction made to the wrong copy.
///     </para>
///     <para>
///         ⚠ <b>It is held against the pinned constants and not against a fresh tally</b>, which is
///         cheap on purpose — the tally is already asserted equal to those constants by each suite's
///         <c>The_corpus_stands_where_it_is_recorded_as_standing</c>, so the chain from the sentence
///         to the corpus is complete without running 5 000 fixtures a second time to close it.
///     </para>
///     <para>
///         <c>VIXEN_REGENERATE=1</c> rewrites the line in place, as everywhere else in this tree. It
///         rewrites and does not insert: a file that has lost the line fails even when regenerating,
///         because where the sentence belongs in a document written to be read is a judgement and not
///         a fact this can derive.
///     </para>
/// </remarks>
static class TaffyGapsSummary {
    /// <summary>
    ///     What the summary line opens with, and the only thing the sweep looks for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The tag is shared by the scan and by the writer on purpose.</b> A pattern that
    ///     disagreed with what regeneration emits would write a file that immediately fails, or worse
    ///     write a second line beside the one it could not see.
    /// </remarks>
    const string Tag = "# COUNTS, generated";

    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the counts back instead of asserting them.</summary>
    static bool Regenerating =>
        Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>Asserts one gaps file's summary line states the counts its suite pins.</summary>
    /// <param name="fileName">The gaps file, by name, beside this source file.</param>
    /// <param name="passing">The suite's <c>ExpectedPassing</c>.</param>
    /// <param name="failing">The suite's <c>ExpectedFailing</c>.</param>
    /// <param name="unsupported">The suite's <c>ExpectedUnsupported</c>.</param>
    public static void Check(string fileName, int passing, int failing, int unsupported) {
        var path = Locate(fileName);
        var lines = File.ReadAllLines(path);
        var expected = Line(passing, failing, unsupported);
        var matches = new List<int>();

        for (var i = 0; i < lines.Length; i++) {
            if (lines[i].StartsWith(Tag, StringComparison.Ordinal)) {
                matches.Add(i);
            }
        }

        // ⚠ The instrument's own assertion, and it comes first. A sweep that matched nothing would
        // otherwise agree with every possible file, including one this line has been deleted out of.
        Assert.True(
            matches.Count == 1,
            $"{fileName} carries {matches.Count} summary lines and must carry exactly one. It is the "
            + "sentence a reader meets first and the only statement of the counts nothing else "
            + $"checks; write it as:\n{expected}"
        );

        if (Regenerating) {
            lines[matches[0]] = expected;

            // ⚠ Joined on "\n" rather than written with `WriteAllLines`, which uses the platform's
            // newline: regenerating on Windows would otherwise rewrite every line of the file to CRLF
            // and hand `CheckWhitespace` a diff of the whole document to explain one number.
            File.WriteAllText(path, string.Join('\n', lines) + '\n');

            return;
        }

        Assert.True(
            lines[matches[0]] == expected,
            $"{fileName}'s summary line disagrees with the counts {nameof(TaffyGapsSummary)}'s caller "
            + $"pins:\n  file:  {lines[matches[0]]}\n  suite: {expected}\n"
            + "Re-run with VIXEN_REGENERATE=1 once the suite's constants are what they should be."
        );
    }

    /// <summary>The line the file is required to carry, for the counts given.</summary>
    /// <remarks>
    ///     The digits are grouped with a space at the thousand, which is how every other number in
    ///     these files is written — a summary that reads differently from the prose around it is one a
    ///     reader skips.
    /// </remarks>
    /// <param name="passing">Fixtures that agree with Chrome.</param>
    /// <param name="failing">Fixtures that lay out and disagree.</param>
    /// <param name="unsupported">Fixtures the bridge refuses, which are skipped rather than run.</param>
    /// <returns>The exact text.</returns>
    public static string Line(int passing, int failing, int unsupported) =>
        $"{Tag} — VIXEN_REGENERATE=1: {Grouped(passing + failing + unsupported)} fixtures, "
        + $"{Grouped(passing)} pass, {Grouped(failing)} fail, {Grouped(unsupported)} refused.";

    /// <summary>Finds a gaps file in the tree rather than the copy in <c>bin</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The walk is anchored on <c>Vixen.slnx</c> above <see cref="AppContext.BaseDirectory" />
    ///     and not on a <c>[CallerFilePath]</c>.</b> CI sets <c>ContinuousIntegrationBuild</c>, which
    ///     turns on <c>DeterministicSourcePaths</c>, which rewrites every compiled source path to
    ///     <c>/_/…</c> — so a test that anchors a repository walk on its own path passes here and
    ///     fails on all three runners at once.
    /// </remarks>
    /// <param name="fileName">The gaps file, by name.</param>
    /// <returns>Its path in the working tree.</returns>
    public static string Locate(string fileName) {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                return Path.Combine(
                    directory.FullName,
                    "Core",
                    "Vixen.Ui.Layout.Tests",
                    "Taffy",
                    fileName
                );
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No Vixen.slnx above {AppContext.BaseDirectory}, so {fileName} cannot be found in the tree. "
            + "This check regenerates, so it must read the committed file and not the copy in bin."
        );
    }

    /// <summary>A number written the way these files write numbers.</summary>
    /// <param name="value">The count.</param>
    /// <returns>Its digits, grouped at the thousand with a space.</returns>
    public static string Grouped(int value) {
        var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        format.NumberGroupSeparator = " ";
        format.NumberDecimalDigits = 0;

        return value.ToString("N", format);
    }
}
