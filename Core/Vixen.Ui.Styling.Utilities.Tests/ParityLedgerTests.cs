// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The parity ledger's measured columns are what the engine actually does.</summary>
/// <remarks>
///     <para>
///         <b>The requirement is <c>docs/plan/43</c> § Part 0's own closing caveat</b> — <i>"the table
///         was generated once, by a script, and the script is not in the tree"</i> — and the week that
///         followed is the argument. Eleven grid families, <c>space-*</c>, <c>divide-*</c>, the per-edge
///         border colours, the eight per-corner radii, three gradient families and the ring all landed
///         and none of them reached the file. Twenty roots read <c>absent</c> while the feature was
///         finished.
///     </para>
///     <para>
///         ⚠ <b>This is deliberately not a re-sync.</b> Correcting the numbers by hand fixes the file
///         for exactly as long as nobody lands a family, which was six days last time. The same
///         argument settled the three hand-copied <c>ui-box.frag</c> files: the fix is a test that
///         fails when the copies disagree, not a copy made to agree today.
///     </para>
///     <para>
///         <b>It costs nothing to run.</b> <see cref="UtilityConsumptionProbe.Take" /> is already
///         computed once per assembly for <see cref="UtilityConsumptionGateTests" /> and cached, so the
///         marginal cost here is reading a 328-line file and a few hundred dictionary lookups. The
///         probe itself is about ten seconds, paid once whether this test exists or not.
///     </para>
///     <para>
///         ⚠ <b>What it does not check, stated rather than left to be found.</b> Only three of the
///         fourteen columns are computed. The Tailwind side — which roots exist, which classes each one
///         covers — needs the package installed and is transcribed data; and which Vixen family answers
///         a Tailwind root is a judgement, because the two vocabularies collide on names where they can
///         mean different things (<c>bg</c>, <c>border</c>, <c>text</c> and <c>transition</c> are
///         <c>background-size</c>, <c>border-collapse</c>, <c>text-wrap</c> and
///         <c>transition-behavior</c> on the Tailwind side and none of those on Vixen's). Those stay
///         hand-kept. The completeness fact below is what stops that being a hiding place.
///     </para>
///     <para>
///         ⚠ <b>A collision is a question, not a verdict, and <c>block</c>/<c>inline</c> are the case
///         that proves it.</b> They were on that list — Tailwind's <c>block-*</c> is <c>block-size</c>
///         and Vixen's <c>block</c> was <c>display</c> — and the resolution was not to keep them apart
///         but to make the collision true: the family answers <c>display</c> bare and <c>height</c>
///         with a value, so both roots legitimately claim it and the join is right in both directions.
///     </para>
/// </remarks>
public class ParityLedgerTests {
    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the measurement back instead of asserting it.</summary>
    static bool Regenerating =>
        Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>Every computed column says what the engine does.</summary>
    /// <remarks>
    ///     One <c>Fact</c> over all 328 rows rather than a theory, for
    ///     <see cref="UtilityConsumptionGateTests" />' reason: the failure worth reading is the whole
    ///     drift at once — "these twenty roots say absent and the family landed" — and a theory reports
    ///     it as twenty unrelated red rows with no way to see they are one week's work.
    /// </remarks>
    [Fact]
    public void The_measured_columns_are_what_the_engine_does() {
        var path = ParityLedger.Locate();
        var (header, rows) = ParityLedger.Read(path);

        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows) {
            listed.UnionWith(ParityLedger.Split(row.Classes, ' '));

            if (row.Example.Length > 0) {
                listed.Add(row.Example);
            }
        }

        var measured = ParityLedger.Measure(listed);
        var drift = new List<string>();

        foreach (var row in rows) {
            var (emits, reads, state) = ParityLedger.Derive(row, measured);

            if (row.Emits == emits && row.Reads == reads && row.State == state) {
                continue;
            }

            if (Regenerating) {
                row.Emits = emits;
                row.Reads = reads;
                row.State = state;
                continue;
            }

            var what = new List<string>();

            if (row.State != state) {
                what.Add($"state `{row.State}` -> `{state}`");
            }

            if (row.Emits != emits) {
                what.Add($"vixen_emits `{row.Emits}` -> `{emits}`");
            }

            if (row.Reads != reads) {
                what.Add($"engine_reads `{row.Reads}` -> `{reads}`");
            }

            drift.Add($"  {row.Root,-24} {string.Join("; ", what)}");
        }

        if (Regenerating) {
            ParityLedger.Write(path, header, rows);
            return;
        }

        Assert.True(
            drift.Count == 0,
            $"""
             {drift.Count} row(s) of docs/plan/43-web-styling-parity.tsv disagree with what the engine
             does. The measurement is right and the file is stale — this is what the ledger drifting
             looks like, caught rather than discovered a week later by somebody sizing finished work.

             Re-run with VIXEN_REGENERATE=1 to write the measurement back, then read the diff: a root
             moving to `works` is a feature that landed, and a root moving to `inert` or `partial` is a
             regression or a family registered against a property nothing reads.

             {string.Join('\n', drift)}
             """
        );
    }

    /// <summary>Every registered family is claimed by some row.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this the whole file is a hiding place, because the drift being prevented is
    ///         an omission and not a wrong value.</b> A family that lands and is written into no row's
    ///         <c>vixen_family</c> leaves every computed column agreeing with itself — the row that
    ///         should have moved is not joined to anything, so nothing is measured for it and
    ///         <c>absent</c> stays true of the empty join. That is precisely last week: <c>grid-rows</c>,
    ///         <c>auto-cols</c>, <c>col-start</c> and eighteen more were in the registry, read by the
    ///         bridge, and claimed by no row.
    ///     </para>
    ///     <para>
    ///         <b>So the join is the one thing a person must maintain, and this is what makes them.</b>
    ///         Adding a family fails the run until somebody writes down which Tailwind root it answers,
    ///         which is a one-word edit and the moment at which the question "is this actually the same
    ///         thing Tailwind means?" gets asked by someone who knows.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_registered_family_is_claimed_by_a_row() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows) {
            claimed.UnionWith(ParityLedger.Split(row.Families));
        }

        var measured = ParityLedger.Measure([]);
        var orphans = measured.Registered.Where(f => !claimed.Contains(f)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            orphans.Count == 0,
            $"""
             {orphans.Count} utility family(ies) are registered and no row of
             docs/plan/43-web-styling-parity.tsv claims them in its `vixen_family` column:

               {string.Join("\n  ", orphans)}

             Put each one in the `vixen_family` cell of the Tailwind root it answers — a comma-separated
             list, several families to a row where Tailwind spells one root as several classes (the
             `display` row claims block, flex, grid, hidden, inline, inline-block and inline-flex).

             ⚠ Check that it really is the same thing Tailwind means before writing it in. Tailwind's
             `bg`, `border`, `text` and `transition` static roots are `background-size`,
             `border-collapse`, `text-wrap` and `transition-behavior`, none of which Vixen's like-named
             families emit. A family joined to the wrong root makes the ledger read `works` for a root
             nothing supports.

             ⚠ A family may legitimately be claimed by more than one row when it answers more than one
             Tailwind root — `flex` is claimed by three, and `block`/`inline` by the `display` row and
             their own sizing row, because the bare class is a display value and the valued class is a
             size. That is a judgement to make deliberately, not a way to quieten this test.
             """
        );
    }

    /// <summary>The state column only ever holds one of the five states.</summary>
    [Fact]
    public void Every_state_is_one_of_the_five() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var bad = rows
            .Where(r => !ParityLedger.States.Contains(r.State, StringComparer.Ordinal))
            .Select(r => $"{r.Root} = `{r.State}`")
            .ToList();

        Assert.True(
            bad.Count == 0,
            $"the ledger holds {bad.Count} state(s) outside "
            + $"{string.Join('/', ParityLedger.States)}:\n  {string.Join("\n  ", bad)}"
        );
    }

    /// <summary>The rendered summary's root and family counts are the registry's, not a transcript.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The family count was typed by hand in the paragraph that says nothing is typed by
    ///         hand any more, and it was wrong by roughly a factor of two.</b> The row read
    ///         <c>128 families</c> and the prose under it argued, correctly, that the figure moves every
    ///         week and so has to be read off the registry on the run that prints it. It was not being
    ///         read off anything. <see cref="ParityLedger.Measure" /> answers a number in the
    ///         two-hundreds. A paragraph explaining why a number must be derived is the single most
    ///         convincing place to leave one that is not.
    ///     </para>
    ///     <para>
    ///         Both cells are held, because they fail differently. The <b>roots</b> count is the ledger's
    ///         own row count and moves when a row is added; the <b>families</b> count is the registry's
    ///         and moves when a family lands, which is the event that has repeatedly happened without
    ///         the document noticing. ⚠ Neither is a floor. <c>Registered</c> is the same set
    ///         <see cref="Every_registered_family_is_claimed_by_a_row" /> already holds the ledger to, so
    ///         a family that lands now fails two tests rather than none.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The prose beside the row names no current number at all now, only the history.</b>
    ///         Leaving "and it is N today" in would recreate the defect one line below the fix, on a
    ///         copy this test does not read — which is precisely how <c>128</c> outlived the two figures
    ///         it was written to replace.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_rendered_summary_counts_are_read_off_the_registry() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var plan = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "43-web-styling-parity.md"));
        var shape = new Regex(
            @"^\|\s*Utility \*\*roots\*\*[^|]*\|\s*\*\*(?<roots>\d+)\*\*\s*\|\s*(?<families>\d+) families\s*\|\s*$"
        );

        var row = plan.Select(line => shape.Match(line)).FirstOrDefault(m => m.Success);

        Assert.True(
            row is not null,
            "docs/plan/43-web-styling-parity.md's rendered summary has no "
            + "`| Utility **roots** … | **n** | m families |` row. It carried a hand-typed family count "
            + "for months; if the row has been reshaped, reshape this with it rather than deleting it — "
            + "a sweep that matches nothing is the failure this whole suite is about."
        );

        var measured = ParityLedger.Measure([]);

        Assert.Equal(rows.Count.ToString(CultureInfo.InvariantCulture), row.Groups["roots"].Value);

        Assert.Equal(
            measured.Registered.Count.ToString(CultureInfo.InvariantCulture),
            row.Groups["families"].Value
        );
    }

    /// <summary>The rendered counts in the plan document match the table beside it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The prose was staler than the table, and by more.</b> When this was written the TSV
    ///         said <c>55 works / 29 partial / 9 inert</c> and the document's own summary two sections
    ///         above it said <c>51 / 29 / 13</c> — two copies of one measurement, disagreeing with each
    ///         other and both wrong. A number quoted in prose beside a table it is supposed to summarise
    ///         is a third copy of the registry, and it rots the same way the other two did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read as a whole table rather than a search per state, because the first version was
    ///         satisfied by a malformed one.</b> It asked <c>Regex.Match</c> for each state's row and
    ///         checked the number it found. <c>Match</c> returns the <i>first</i> hit, so a document
    ///         carrying two <c>absent</c> rows — which master did, at <c>ce585d78</c>, saying 91 and 90
    ///         — passed on the first and left the second to be read by a human. That is the "what does
    ///         it print on the day the document is malformed" question answered with "success", and it
    ///         is the same shape as a floor: a guard that only has to find one row is satisfied by
    ///         exactly the defect it exists to catch.
    ///     </para>
    ///     <para>
    ///         So the run of rows is lifted whole and its state names are compared to
    ///         <see cref="ParityLedger.States" /> <i>as a sequence</i>. That is an equality in both
    ///         directions and in order: a duplicated row is a longer sequence, a dropped one is a
    ///         shorter one, and a reordered table is neither of those and still fails. ⚠ The rows are
    ///         taken as a contiguous block rather than by matching six patterns anywhere in the file,
    ///         because the document has a second table of the same shape — the <c>| **Total** |</c> row
    ///         under "By category" — and a sweep of the whole file reaches into it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_plan_document_quotes_the_tables_own_counts() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var plan = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "43-web-styling-parity.md"));
        var table = StateTable(plan);

        Assert.True(
            table.Count != 0,
            "docs/plan/43-web-styling-parity.md has no `| **works** | … | **n** |` row at all, so Part 0's "
            + "state table has been renamed, reformatted or lost. Nothing below this can be trusted "
            + "until it is found again — a sweep that matches nothing reports nothing."
        );

        Assert.Equal(ParityLedger.States, table.Select(row => row.State).ToArray());

        var missing = new List<string>();

        foreach (var (state, quoted) in table) {
            var count = rows.Count(r => string.Equals(r.State, state, StringComparison.Ordinal));

            if (quoted != count) {
                missing.Add($"{state}: the document says {quoted}, the table holds {count}");
            }
        }

        Assert.True(
            missing.Count == 0,
            $"""
             docs/plan/43-web-styling-parity.md quotes counts the table beside it does not support:

               {string.Join("\n  ", missing)}

             Update Part 0's state table and the "By category" totals to the counts above.
             """
        );
    }

    /// <summary>How many of those rows are in that state.</summary>
    /// <param name="rows">The rows to count over — the whole ledger, or one category's slice of it.</param>
    /// <param name="state">The state.</param>
    /// <returns>The count.</returns>
    static int Held(IEnumerable<ParityRow> rows, string state) =>
        rows.Count(row => string.Equals(row.State, state, StringComparison.Ordinal));

    /// <summary>Part 0's state table, as the contiguous block of rows it is.</summary>
    /// <remarks>
    ///     Anchored on the <c>works</c> row and grown in both directions over lines of the same shape,
    ///     so an extra row is inside the block and a stray row elsewhere in the document is not.
    /// </remarks>
    /// <param name="plan">The document's lines.</param>
    /// <returns>The rows, in the order the document writes them.</returns>
    static List<(string State, int Count)> StateTable(string[] plan) {
        var shape = new Regex(@"^\|\s*\*\*(?<state>[A-Za-z]+)\*\*\s*\|[^|\n]*\|\s*\*\*(?<count>\d+)\*\*\s*\|\s*$");
        var anchor = Array.FindIndex(plan, line => shape.Match(line) is { Success: true } m
            && m.Groups["state"].Value == "works");

        if (anchor < 0) {
            return [];
        }

        var first = anchor;
        var last = anchor;

        while (first > 0 && shape.IsMatch(plan[first - 1])) {
            first--;
        }

        while (last + 1 < plan.Length && shape.IsMatch(plan[last + 1])) {
            last++;
        }

        return [
            .. plan[first..(last + 1)]
                .Select(line => shape.Match(line))
                .Select(m => (
                    m.Groups["state"].Value,
                    int.Parse(m.Groups["count"].Value, CultureInfo.InvariantCulture)
                ))
        ];
    }

    /// <summary>The "By category" table has one row per category and adds up to its own total.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing read this table at all until now, and the issue that asked for it believed
    ///         something did.</b> #537 reasoned that a duplicated category row "would double-count into
    ///         a <c>Total</c> the test does check" — the test checked Part 0's state table and only that,
    ///         so the whole fifteen-row cross-tabulation of the ledger was prose. It is the larger of
    ///         the two tables and the one a reader uses to decide what to work on next.
    ///     </para>
    ///     <para>
    ///         Held in three ways, because the failures are different shapes. The category <i>set</i> is
    ///         an equality against the ledger's own categories, so a category that appears twice, or a
    ///         category of roots nobody tabulated, is red. Each row's numbers are re-derived, one per
    ///         state plus the root count. And
    ///         the <c>**Total**</c> row is checked against the ledger rather than against the column
    ///         sums — summing the columns of a table to check that table is the tautology this file's
    ///         remark on re-syncing warns about, and it would pass a document in which every row was
    ///         internally consistent and collectively wrong.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_by_category_table_is_one_row_per_category_and_adds_up() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var plan = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "43-web-styling-parity.md"));
        // ⚠ The column count is derived from `ParityLedger.States` rather than written as a
        // literal. It was `{7}` for six states plus the root count, and dropping `unknown` made
        // the pattern match no row at all — which the category equality caught, loudly, only
        // because it compares two lists rather than looping over whatever was found.
        var shape = new Regex(
            @"^\|\s*(?<category>[^|*]+?)\s*\|(?<counts>(\s*\d+\s*\|){"
            + (ParityLedger.States.Length + 1).ToString(CultureInfo.InvariantCulture)
            + @"})\s*$"
        );
        var header = Array.FindIndex(
            plan, line => line.StartsWith("| Category | roots | works |", StringComparison.Ordinal)
        );

        Assert.True(
            header >= 0,
            "docs/plan/43-web-styling-parity.md has no `| Category | roots | works | …` header, so the "
            + "\"By category\" table has been renamed or reformatted. A sweep that matches nothing "
            + "reports nothing, which is why this is asserted rather than looped over."
        );

        var body = new List<(string Category, int[] Counts)>();

        for (var line = header + 2; line < plan.Length && shape.Match(plan[line]) is { Success: true } m; line++) {
            body.Add((
                m.Groups["category"].Value,
                [
                    .. m.Groups["counts"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Select(cell => int.Parse(cell.Trim(), CultureInfo.InvariantCulture))
                ]
            ));
        }

        Assert.Equal(
            rows.Select(row => row.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            body.Select(row => row.Category).Order(StringComparer.Ordinal)
        );

        var wrong = new List<string>();

        foreach (var (category, counts) in body) {
            var mine = rows.Where(row => string.Equals(row.Category, category, StringComparison.Ordinal)).ToList();
            var expected = new[] { mine.Count }
                .Concat(ParityLedger.States.Select(state => Held(mine, state)))
                .ToArray();

            if (!counts.SequenceEqual(expected)) {
                wrong.Add(
                    $"{category}: the document says {string.Join(' ', counts)}, "
                    + $"the ledger holds {string.Join(' ', expected)}"
                );
            }
        }

        // Against the ledger, not against the column sums: a table every row of which agrees with
        // the row above it can still be a table of the wrong measurement.
        var total = Array.FindIndex(plan, header, line => line.StartsWith("| **Total** |", StringComparison.Ordinal));

        Assert.True(total > header, "the \"By category\" table has no `| **Total** |` row.");

        var quoted = plan[total]
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(cell => int.Parse(cell.Trim().Trim('*'), CultureInfo.InvariantCulture))
            .ToArray();

        var ledger = new[] { rows.Count }
            .Concat(ParityLedger.States.Select(state => Held(rows, state)))
            .ToArray();

        if (!quoted.SequenceEqual(ledger)) {
            wrong.Add(
                $"Total: the document says {string.Join(' ', quoted)}, "
                + $"the ledger holds {string.Join(' ', ledger)}"
            );
        }

        Assert.True(
            wrong.Count == 0,
            $"""
             docs/plan/43-web-styling-parity.md's "By category" table disagrees with the ledger:

               {string.Join("\n  ", wrong)}

             The columns are roots, then works/partial/inert/absent/composed in that order.
             """
        );
    }
}
