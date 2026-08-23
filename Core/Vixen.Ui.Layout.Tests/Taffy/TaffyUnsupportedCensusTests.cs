// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
    ///     That the census can never be a census of nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Ask what this instrument prints on the day it does not run.</b> Every failure mode
    ///     that would make the census empty — a corpus that did not reach the output directory, a
    ///     <c>TaffyStyleMap</c> that stopped throwing, a file that was emptied instead of edited —
    ///     produces a green
    ///     <see cref="Every_refusal_is_recorded_with_its_reason_and_its_corpus" /> if both sides
    ///     agree on nothing. So the floor is asserted separately and from the measurement rather than
    ///     from the file: refusals exist, they exist in more than one corpus, and they have more than
    ///     one distinct reason between them.
    /// </remarks>
    [Fact]
    public void The_census_is_not_empty_and_did_not_census_nothing() {
        var measured = Measure();

        Assert.NotEmpty(measured);
        Assert.True(measured.Sum(bucket => bucket.Count) > 0, "no fixture was refused at all, which no run has ever produced");
        Assert.True(measured.Select(bucket => bucket.Reason).Distinct(StringComparer.Ordinal).Count() > 1, "one reason for every refusal");
        Assert.True(measured.Select(bucket => bucket.Category).Distinct(StringComparer.Ordinal).Count() > 1, "one corpus refused everything");

        // And the corpus behind it really was walked, all eight files of it.
        Assert.Equal(5524, TaffyCorpus.Categories.Sum(category => TaffyCorpus.Load(category).Count));
    }

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
