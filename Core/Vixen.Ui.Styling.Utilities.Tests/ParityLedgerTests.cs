// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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

    /// <summary>The state column only ever holds one of the six states.</summary>
    [Fact]
    public void Every_state_is_one_of_the_six() {
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

    /// <summary>The rendered counts in the plan document match the table beside it.</summary>
    /// <remarks>
    ///     ⚠ <b>The prose was staler than the table, and by more.</b> When this was written the TSV said
    ///     <c>55 works / 29 partial / 9 inert</c> and the document's own summary two sections above it
    ///     said <c>51 / 29 / 13</c> — two copies of one measurement, disagreeing with each other and
    ///     both wrong. A number quoted in prose beside a table it is supposed to summarise is a third
    ///     copy of the registry, and it rots the same way the other two did.
    /// </remarks>
    [Fact]
    public void The_plan_document_quotes_the_tables_own_counts() {
        var (_, rows) = ParityLedger.Read(ParityLedger.Locate());
        var plan = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "43-web-styling-parity.md"));
        var missing = new List<string>();

        foreach (var state in ParityLedger.States) {
            var count = rows.Count(r => string.Equals(r.State, state, StringComparison.Ordinal));

            // The document's own five-state table row: `| **works** | …meaning… | **77** |`. Matched
            // whole rather than by looking for the number anywhere, or `**4**` occurring in an
            // unrelated sentence would pass the row that happens to hold 4.
            var row = System.Text.RegularExpressions.Regex.Match(
                plan,
                $@"\|\s*\*\*{state}\*\*\s*\|[^|\n]*\|\s*\*\*(\d+)\*\*\s*\|"
            );

            if (!row.Success) {
                missing.Add($"{state}: the document has no `| **{state}** | … | **n** |` row");
            } else if (row.Groups[1].Value != count.ToString()) {
                missing.Add($"{state}: the document says {row.Groups[1].Value}, the table holds {count}");
            }
        }

        Assert.True(
            missing.Count == 0,
            $"""
             docs/plan/43-web-styling-parity.md quotes counts the table beside it does not support:

               {string.Join("\n  ", missing)}

             Update Part 0's five-state table and the "By category" totals to the counts above.
             """
        );
    }
}
