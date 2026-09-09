// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     The per-category table in this directory's <c>README.md</c>, held to an actual census.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The last count in this directory that nothing contradicted, and the one that cannot
///         be checked against the suites' constants.</b> Each conformance suite pins a total across
///         the categories it owns — <c>TaffyGridConformanceTests</c> pins 2 104 / 16 / 0 for
///         <c>grid</c>, <c>blockgrid</c> and <c>gridflex</c> together — so a table row that moved
///         sixteen fixtures from <c>grid</c> into <c>gridflex</c> would agree with every constant in
///         the project. The rows are a finer-grained statement than anything asserted, and the only
///         thing that can answer them is a census per category. The file's own paragraph already
///         confessed that it "had drifted by 14 fixtures before anyone noticed, while every suite was
///         green".
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day the table is deleted or reworded is the whole design,</b>
///         and it is <see cref="TaffyGapsSummary" />'s rule one dimension up. A sweep that reads the
///         digits off whatever rows it happens to find agrees with a file the table was cut out of.
///         So the SHAPE is asserted before any number is looked at: exactly one header line, the
///         separator under it, then exactly the eight corpus categories in the corpus's own order,
///         then the totals row. A row set that is not that is a failure naming what it found, and
///         regenerating does not repair it — where a table belongs in a document written to be read
///         is a judgement and not a fact this can derive.
///     </para>
///     <para>
///         ⚠ <b>It costs nothing, and that was the question this had to answer before it was worth
///         doing at all.</b> A per-category census is another full run of all 5 524 fixtures, which
///         is roughly what this whole project costs. It is free here because it is not another run:
///         <see cref="TaffyCensus.TallyOf" /> memoises per category, and the four suites'
///         <c>The_corpus_stands_where_it_is_recorded_as_standing</c> already ask for all eight
///         between them. ⚠ Run this test <i>alone</i> under a filter and it pays the whole 5 524
///         itself, which is the honest price of a table nothing else can derive.
///     </para>
/// </remarks>
public class TaffyReadmeTableTests {
    /// <summary>
    ///     The eight corpus categories, in the order the table lists them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Written here rather than derived from the corpus directory, and that is deliberate.</b>
    ///     A ninth <c>Corpus/*.xml</c> that this list did not know about would make the table
    ///     incomplete, and a check that re-derived its own expectation from the directory would
    ///     quietly widen the table instead of failing. <c>TaffyCorpusCoverageTests</c> is what holds
    ///     the corpus itself to what is committed.
    /// </remarks>
    static readonly string[] Categories = [
        "flex", "leaf", "block", "blockflex", "blockgrid", "grid", "gridflex", "float"
    ];

    const string Header = "| Category | Fixtures | Pass | Fail | Refused |";
    const string Separator = "|---|--:|--:|--:|--:|";

    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the table back instead of asserting it.</summary>
    static bool Regenerating => Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>Every row of the README's table states what a census of that category answers.</summary>
    [Fact]
    public void The_readme_table_states_what_a_census_of_each_category_answers() {
        var path = TaffyGapsSummary.Locate("README.md");
        var lines = File.ReadAllLines(path);

        var headers = new List<int>();

        for (var i = 0; i < lines.Length; i++) {
            if (lines[i] == Header) {
                headers.Add(i);
            }
        }

        // ⚠ The instrument's own assertion, and it comes first for TaffyGapsSummary's reason: a sweep
        // that matched nothing would otherwise agree with every possible file, this one included.
        Assert.True(
            headers.Count == 1,
            $"README.md carries {headers.Count} tables headed \"{Header}\" and must carry exactly one. "
            + "It is the only per-category statement of how this corpus stands, and the four suites "
            + "pin totals across categories rather than each one, so nothing else can contradict it."
        );

        var top = headers[0];

        Assert.True(
            top + 2 + Categories.Length < lines.Length,
            "README.md ends inside the census table; it needs a separator, eight category rows and a total."
        );

        Assert.Equal(Separator, lines[top + 1]);

        var expected = new string[Categories.Length + 1];
        var totalPassed = 0;
        var totalFailed = 0;
        var totalUnsupported = 0;

        for (var i = 0; i < Categories.Length; i++) {
            var tally = TaffyCensus.TallyOf(Categories[i]);
            totalPassed += tally.Passed;
            totalFailed += tally.Failed;
            totalUnsupported += tally.Unsupported;

            expected[i] = Row(Categories[i], tally);

            // ⚠ The name is checked before the digits and against this list rather than against
            // whatever the row happens to say, so a table whose rows have been reordered or renamed
            // fails as a table rather than being silently rewritten into agreement.
            Assert.True(
                lines[top + 2 + i].StartsWith($"| `{Categories[i]}` |", StringComparison.Ordinal),
                $"README.md's census table row {i + 1} is \"{lines[top + 2 + i]}\", and row {i + 1} of "
                + $"this corpus is `{Categories[i]}`. The eight rows are the eight categories in the "
                + "corpus's own order."
            );
        }

        expected[Categories.Length] = Total(totalPassed, totalFailed, totalUnsupported);

        if (Regenerating) {
            // ⚠ Rewrites the rows it found and inserts none, so a table somebody has cut down to six
            // rows fails even here. Joined on "\n" because `WriteAllLines` uses the platform's
            // newline, and regenerating on Windows would otherwise hand `CheckWhitespace` a diff of
            // the whole document to explain one digit.
            Array.Copy(expected, 0, lines, top + 2, expected.Length);
            File.WriteAllText(path, string.Join('\n', lines) + '\n');

            return;
        }

        for (var i = 0; i < expected.Length; i++) {
            Assert.True(
                lines[top + 2 + i] == expected[i],
                $"README.md's census table disagrees with a census of the committed corpus:\n"
                + $"  file:   {lines[top + 2 + i]}\n  census: {expected[i]}\n"
                + "Re-run with VIXEN_REGENERATE=1 once the corpus is what it should be."
            );
        }
    }

    /// <summary>One category's row, written the way the table writes one.</summary>
    /// <param name="category">The corpus category.</param>
    /// <param name="tally">What a census of it answered.</param>
    /// <returns>The exact text of the row.</returns>
    static string Row(string category, TaffyTally tally) =>
        $"| `{category}` | {TaffyGapsSummary.Grouped(tally.Total)} | {TaffyGapsSummary.Grouped(tally.Passed)} "
        + $"| {TaffyGapsSummary.Grouped(tally.Failed)} | {TaffyGapsSummary.Grouped(tally.Unsupported)} |";

    /// <summary>The totals row, which is bold and has no name in the first column.</summary>
    /// <param name="passed">Fixtures that agree with Chrome.</param>
    /// <param name="failed">Fixtures that lay out and disagree.</param>
    /// <param name="unsupported">Fixtures the bridge refuses, which are skipped rather than run.</param>
    /// <returns>The exact text of the row.</returns>
    static string Total(int passed, int failed, int unsupported) =>
        $"| | **{TaffyGapsSummary.Grouped(passed + failed + unsupported)}** | **{TaffyGapsSummary.Grouped(passed)}** "
        + $"| **{TaffyGapsSummary.Grouped(failed)}** | **{TaffyGapsSummary.Grouped(unsupported)}** |";
}
