// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     That every fixture which asserts nothing is written down, by reason and by corpus.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A skip reads as a pass in every summary anyone looks at, and 408 of them did.</b>
///         <c>TaffyStyleMap</c> refuses a property it cannot translate, the three conformance suites
///         turn that into <see cref="Assert.Skip" />, and the fixture never reaches the algorithm —
///         so it cannot fail, and it cannot be counted as a gap either. The suites did pin the
///         refusal COUNT, so nothing drifted silently; what no file recorded was what the refusals
///         were FOR. Four gap files described corpora that were nearly closed and not one of them
///         mentioned the largest bucket of fixtures in the project.
///     </para>
///     <para>
///         ⚠ <b>The point of the file this asserts is the split it forces, not the total.</b> A
///         refusal is either a HARNESS gap — a value the map never learned, on a feature the store
///         has — or an ENGINE gap. The first kind is worth nothing to anybody and costs the whole
///         fixture; the second is debt that belongs in writing. They were indistinguishable until
///         somebody grouped them, and the ratio turned out to be 124 against 284.
///     </para>
/// </remarks>
public class TaffyUnsupportedCensusTests {
    /// <summary>One line of the file: how many fixtures one corpus refuses for one reason.</summary>
    sealed record Bucket(int Count, string Category, string Reason);

    /// <summary>
    ///     The committed census, re-derived from an actual run and compared line for line.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every bucket, not just the sum. A total alone would let <c>scrollbar-width</c> lose four
    ///     fixtures to <c>float</c> gaining four and say nothing, which is the same silence this file
    ///     exists to end one level up.
    /// </remarks>
    [Fact]
    public void Every_refusal_is_recorded_with_its_reason_and_its_corpus() {
        var measured = Measure();
        var recorded = ReadCommittedCensus(out var recordedTotal);

        Assert.Equal(Render(recorded), Render(measured));

        // The stated total is a third statement about the same run, and it is not derived from the
        // lines above it — a file whose lines were all deleted would otherwise still agree with a
        // census that measured nothing.
        Assert.Equal(measured.Sum(bucket => bucket.Count), recordedTotal);
    }

    /// <summary>
    ///     That the census is empty because nothing was refused, not because nothing ran.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Ask what this instrument prints on the day it does not run.</b> Every failure mode
    ///         that would make the census empty — a corpus that did not reach the output directory, a
    ///         <c>TaffyStyleMap</c> that stopped throwing, a file emptied instead of edited — produces
    ///         a green <see cref="Every_refusal_is_recorded_with_its_reason_and_its_corpus" />,
    ///         because both sides agree on nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THIS TEST HAS NOW BEEN REWRITTEN TWICE BY ITS OWN SUCCESS, AND THE SECOND REWRITE
    ///         IS THE ONE THAT MATTERS.</b> It began as "more than one distinct reason", which the
    ///         corpus consumed on 2026-08-22 by converging on <c>float</c> alone. It became "the
    ///         reason set is exactly <c>float</c>", which the corpus consumed the next day by
    ///         implementing floats. Both were proxies for "the census is rich", and a proxy that
    ///         success eats is not a guard. So this is no longer a statement about the refusals at
    ///         all — there are none to make a statement about — and it is not
    ///         <c>Assert.Empty(measured)</c> either, which is the trivially true thing a deleted
    ///         corpus also satisfies.
    ///     </para>
    ///     <para>
    ///         It is three positive claims about a run that really happened:
    ///         <b>every one of the 5 524 fixtures reached a real outcome</b> — passed or failed, so
    ///         the count of things that asserted something is the whole corpus and not zero;
    ///         <b>every corpus contributed</b>, so no single file can be missing; and <b>the refusal
    ///         machinery is still live</b>, proved by feeding it a value it does not know and
    ///         requiring it to refuse. That last one is the answer to the question in the first
    ///         paragraph: a <c>TaffyStyleMap</c> that had stopped throwing would pass every other
    ///         assertion in this file and fail this one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_census_is_empty_because_nothing_is_refused_and_not_because_nothing_ran() {
        var measured = Measure();

        Assert.Empty(measured);

        // ⚠ The load-bearing half. `Unsupported == 0` on its own is what a corpus of zero fixtures
        // reports; `Passed + Failed == 5524` is not, and neither is a per-corpus floor.
        var asserted = 0;

        foreach (var category in TaffyCorpus.Categories) {
            var tally = TaffyCensus.Run(category, 0).Tally;

            Assert.True(tally.Passed + tally.Failed > 0, $"the '{category}' corpus asserted nothing at all");
            Assert.Equal(0, tally.Unsupported);
            Assert.Equal(TaffyCorpus.Load(category).Count, tally.Total);

            asserted += tally.Passed + tally.Failed;
        }

        Assert.Equal(5524, asserted);
        Assert.Equal(5524, TaffyCorpus.Categories.Sum(category => TaffyCorpus.Load(category).Count));

        // ⚠ And the map can still say no. `TaffyStyleMap` keeps refusal arms for values no fixture
        // writes today — `grid-template-areas`, the flow-relative `float` and `clear` keywords,
        // `min-content` as a length — precisely so that a refreshed corpus which starts writing one
        // is SKIPPED rather than mis-parsed into a silent pass. Nothing else in this directory would
        // notice if those arms were deleted, because nothing else in this directory reaches one.
        var refused = TaffyFixtureRunner.Run(Probe("float", "inline-start"));

        Assert.Equal(TaffyOutcome.Unsupported, refused.Outcome);
        Assert.Contains("float", refused.Detail, StringComparison.Ordinal);

        Assert.Equal(TaffyOutcome.Unsupported, TaffyFixtureRunner.Run(Probe("clear", "inline-end")).Outcome);
        Assert.Equal(TaffyOutcome.Unsupported, TaffyFixtureRunner.Run(Probe("grid-template-areas", "\"a b\"")).Outcome);

        // ⚠ …and it says no for the RIGHT reason. A map that threw `TaffyUnsupportedException` at
        // everything would satisfy the three lines above, so one declaration the map really does
        // understand has to come back through the same path as a genuine outcome.
        Assert.NotEqual(TaffyOutcome.Unsupported, TaffyFixtureRunner.Run(Probe("float", "left")).Outcome);
    }

    /// <summary>A one-box fixture that sets a single declaration, for asking the map a question.</summary>
    /// <remarks>
    ///     The expectation is deliberately unsatisfiable, so a <c>Pass</c> can never be mistaken for a
    ///     refusal: the only outcomes this can produce are <c>Unsupported</c> and <c>Fail</c>, and the
    ///     assertions above are all about which.
    /// </remarks>
    static TaffyFixture Probe(string attribute, string value) =>
        new(
            "probe",
            $"probe_{attribute}",
            UseRounding: true,
            float.NaN,
            float.NaN,
            new TaffyInput(
                IsText: false,
                Text: null,
                new Dictionary<string, string> { ["display"] = "block", [attribute] = value }.ToFrozenDictionary(StringComparer.Ordinal),
                []
            ),
            new TaffyExpected(-1f, -1f, -1f, -1f, [])
        );

    static IReadOnlyList<Bucket> Measure() =>
        TaffyCorpus
            .Categories
            .SelectMany(
                category => TaffyCensus
                    .Run(category, 0)
                    .Refusals
                    .Select(refusal => new Bucket(refusal.Value, category, refusal.Key))
            )
            .ToList();

    /// <summary>Sorted and rendered as text, so a mismatch reads as a diff rather than as a count.</summary>
    static string Render(IEnumerable<Bucket> buckets) {
        var text = new StringBuilder();

        foreach (var bucket in buckets.OrderBy(bucket => bucket.Category, StringComparer.Ordinal)
                     .ThenBy(bucket => bucket.Reason, StringComparer.Ordinal)) {
            text.AppendLine(CultureInfo.InvariantCulture, $"{bucket.Count,6}  {bucket.Category}  {bucket.Reason}");
        }

        return text.ToString();
    }

    static IReadOnlyList<Bucket> ReadCommittedCensus(out int total) {
        var path = Path.Combine(AppContext.BaseDirectory, "Taffy", "UnsupportedFixtures.txt");

        if (!File.Exists(path)) {
            throw new FileNotFoundException(
                $"The refusal census is missing from '{path}'. It is committed beside the gap files "
                + "and copied to the output directory by the test project, so this is a build "
                + "problem rather than a missing file.",
                path
            );
        }

        var buckets = new List<Bucket>();
        var stated = -1;

        foreach (var raw in File.ReadLines(path)) {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var fields = line.Split((char[]?) null, 3, StringSplitOptions.RemoveEmptyEntries);

            if (fields is ["total", var sum]) {
                stated = int.Parse(sum, CultureInfo.InvariantCulture);
                continue;
            }

            Assert.True(fields.Length == 3, $"'{line}' is not `count  corpus  reason` and not a `total` line");
            buckets.Add(new Bucket(int.Parse(fields[0], CultureInfo.InvariantCulture), fields[1], fields[2].Trim()));
        }

        Assert.True(stated >= 0, "UnsupportedFixtures.txt states no total; add a `total <n>` line");
        total = stated;

        return buckets;
    }
}
