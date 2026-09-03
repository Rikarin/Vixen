// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using Vixen.Core.Syntax.Diagnostics;
using Xunit;

namespace Tests;

/// <summary>
///     How many diagnostics have the negative that proves they do not over-fire, counted from the
///     tree rather than written down.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The counts were the defect.</b> Four batches of negative-fixture work have run, and
///         every one of them found a figure in its own brief wrong — the id total, the number
///         covered, the number of <c>AssertNoDiagnostics</c> sites, the reachable ceiling, twice in
///         a correction that was itself off by one. That is not carelessness. A number in prose is a
///         claim nothing evaluates: it is right on the day it is typed and silently wrong from the
///         next commit, and the only reader who can tell is one who recounts, which is what every
///         batch spent its first hour doing.
///     </para>
///     <para>
///         So the numbers are derived here and the documents are held to them. Adding a descriptor
///         or a negative fails this file until <c>Raven/README.md</c>, <c>docs/overview.md</c> and
///         <see cref="Owed" /> agree with the code — in <em>both</em> directions, because a count
///         that only ever grew would still let a covered id be recorded as owed.
///     </para>
///     <para>
///         <b>What this is not.</b> It counts the <em>shape</em> of a negative — a
///         <c>NegativeDiagnosticTests.Silent</c> call, or an <c>Assert.DoesNotContain</c> naming an
///         id — and not whether the fixture is a near miss rather than an unrelated valid shader.
///         Nothing mechanical can check that; <c>NegativeDiagnosticTests</c>'s own remarks and
///         <c>Raven/README.md</c> carry the method, and each fixture names the positive test it
///         mirrors. ⚠ A negative written in some third shape is counted as owed, which is the safe
///         direction: it says "write this one" about an id that has one, rather than the reverse.
///     </para>
/// </remarks>
public class DiagnosticCoverageTests {
    /// <summary>
    ///     The ids with no negative today. ⚠ This list only ever shrinks — an id leaves it when a
    ///     fixture appears, and the two that can never leave say why.
    /// </summary>
    /// <remarks>
    ///     <c>RVN2003</c> and <c>RVN2014</c> cannot fire on any input, so no near miss exists to
    ///     write: <c>NotAType</c>'s two raise sites are both the <c>default:</c> arm of a switch
    ///     over a closed hierarchy whose other arms are exhaustive, and <c>SelfOutsideType</c> fires
    ///     when a binder has no containing type, which no binder has.
    ///     <c>UnprovenDiagnosticTests.The_two_that_cannot_fire_still_carry_a_usable_message</c>
    ///     carries the argument at length. They stay here, counted as owed and named as
    ///     unreachable, because deleting them from the list would make the ceiling arithmetic below
    ///     say the wrong thing.
    /// </remarks>
    static readonly string[] Owed = [
        "RVN1002", "RVN2002", "RVN2003", "RVN2004", "RVN2005", "RVN2006", "RVN2010", "RVN2013",
        "RVN2014", "RVN2015", "RVN2020", "RVN2021", "RVN2022", "RVN2023", "RVN2024", "RVN2025",
        "RVN2030", "RVN2031", "RVN2032", "RVN2034", "RVN2040", "RVN2041", "RVN2042", "RVN2043",
        "RVN2045", "RVN2070", "RVN2071", "RVN2074", "RVN2075", "RVN2080",
        "RVN2081", "RVN2092", "RVN2093", "RVN2094",
        "RVN2113", "RVN2114", "RVN2122", "RVN2131", "RVN3001",
        "RVN3003", "RVN3004", "RVN3007", "RVN3010", "RVN4001", "RVN4003", "RVN5003", "RVN5004",
        "RVN5005", "RVN5006"
    ];

    /// <summary>The two that have no reachable input, and so can never earn a negative.</summary>
    static readonly string[] Unreachable = ["RVN2003", "RVN2014"];

    /// <summary>
    ///     Every declared id is either covered by a negative or on the owed list, and nothing is on
    ///     both.
    /// </summary>
    /// <remarks>
    ///     This is the whole instrument. The failure message names the ids that moved and which way,
    ///     because the useful thing to say to whoever lands the next fixture is "delete this line",
    ///     not "a number is wrong".
    /// </remarks>
    [Fact]
    public void Every_declared_id_is_covered_or_recorded_as_owed() {
        var declared = DeclaredIds();
        var covered = CoveredIds();

        // Both halves asserted before they are compared, so a broken path or a regex that stopped
        // matching fails as itself rather than as a hundred ids that "became owed".
        Assert.NotEmpty(declared);
        Assert.NotEmpty(covered);

        Assert.All(covered, id => Assert.Contains(id, declared));

        var newlyCovered = Owed.Where(covered.Contains).ToArray();
        var newlyOwed = declared.Where(id => !covered.Contains(id) && !Owed.Contains(id)).ToArray();

        Assert.True(
            newlyCovered.Length == 0 && newlyOwed.Length == 0,
            $"The owed list in {nameof(DiagnosticCoverageTests)} disagrees with the suite.\n"
            + $"Now covered, so delete from Owed: {Join(newlyCovered)}\n"
            + $"Now owed, so add to Owed (or write the negative): {Join(newlyOwed)}"
        );
    }

    /// <summary>
    ///     <c>Raven/README.md</c>'s four figures are the derived ones.
    /// </summary>
    /// <remarks>
    ///     ⚠ Parsing prose is brittle, and that is the point: the sentences are load-bearing
    ///     documentation of this suite's state, so a rewording that loses one fails here loudly
    ///     rather than letting the number rot quietly. Every failure mode of this test is "go and
    ///     look at the README", which is the correct one.
    /// </remarks>
    [Fact]
    public void The_ravens_readme_carries_the_numbers_the_tree_has() {
        var readme = File.ReadAllText(Path.Combine(RavenDirectory(), "README.md"));

        var declared = DeclaredIds();
        var covered = CoveredIds();

        Assert.Equal(declared.Count, Number(readme, @"There are (\d+) diagnostic ids"));

        var (statedCovered, statedOwed) = Pair(readme, @"(\d+) ids have a negative today and (\d+)\s+do not");
        Assert.Equal(covered.Count, statedCovered);
        Assert.Equal(declared.Count - covered.Count, statedOwed);

        var (inFile, ofTotal) = Pair(readme, @"holds (\d+) of the (\d+)");
        Assert.Equal(SilentIds().Count, inFile);
        Assert.Equal(covered.Count, ofTotal);

        // Derived rather than typed: the ceiling is what is left after the ids no input can reach.
        Assert.Equal(declared.Count - Unreachable.Length, Number(readme, @"reachable ceiling at (\d+)"));
    }

    /// <summary>
    ///     And so is <c>docs/overview.md</c>'s row, which is the file the working agreement says
    ///     wins where a plan document disagrees — so it is the worst of the three to leave stale.
    /// </summary>
    /// <remarks>
    ///     ⚠ It was the stale one: <b>127 ids; 69 have the negative … 58 do not</b>, which is
    ///     internally consistent and absolutely wrong, against a README two directories away that
    ///     had 128/70/58 right.
    /// </remarks>
    [Fact]
    public void The_overview_row_carries_the_same_numbers() {
        var overview = File.ReadAllText(
            Path.Combine(RavenDirectory(), "..", "docs", "overview.md")
        );

        var declared = DeclaredIds();
        var covered = CoveredIds();

        var match = Regex.Match(
            overview,
            @"\*\*(\d+) ids; (\d+) have the negative that proves the rule does not over-fire, (\d+) do not\*\*"
        );

        Assert.True(match.Success, "docs/overview.md § 1.8 no longer carries the sentence this reads.");

        Assert.Equal(declared.Count, int.Parse(match.Groups[1].Value));
        Assert.Equal(covered.Count, int.Parse(match.Groups[2].Value));
        Assert.Equal(declared.Count - covered.Count, int.Parse(match.Groups[3].Value));

        // ⚠ The row states two more numbers in the sentence after that one, and neither was read
        // here — the ceiling was checked against Raven/README.md alone, and the owed count is
        // repeated in the row's own prose. So the row could have gone on saying "two of the 54"
        // after the 54 had become 49, in the one document that is supposed to be the state. That
        // is this file's own failure mode in this file's own subject matter.
        Assert.Equal(
            declared.Count - covered.Count,
            Number(overview, @"and two of the (\d+), `RVN2003` and `RVN2014`, cannot fire", "docs/overview.md")
        );

        Assert.Equal(
            declared.Count - Unreachable.Length,
            Number(overview, @"puts the reachable ceiling at (\d+)", "docs/overview.md")
        );
    }

    // --- Deriving the two sets ---------------------------------------------

    /// <summary>Every id the compiler declares, read off the descriptor classes.</summary>
    /// <remarks>
    ///     Reflection rather than a grep over <c>Diagnostics/</c>, because the remarks in those
    ///     files cite ids they do not declare — a plain <c>RVN[0-9]{4}</c> sweep returns one more
    ///     than there are descriptors, and that off-by-one is one of the ones that got written down.
    /// </remarks>
    /// <returns>The ids.</returns>
    static IReadOnlySet<string> DeclaredIds() {
        HashSet<string> ids = [];

        foreach (var owner in new[] {
                     typeof(Vixen.Raven.Diagnostics.SyntaxDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.SemanticDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.LoweringDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.BackendDiagnostics),
                     typeof(Vixen.Raven.Diagnostics.LibraryDiagnostics)
                 }) {
            foreach (var field in owner.GetFields(BindingFlags.Public | BindingFlags.Static)) {
                if (field.FieldType == typeof(DiagnosticDescriptor)) {
                    ids.Add(((DiagnosticDescriptor)field.GetValue(null)!).Id);
                }
            }
        }

        return ids;
    }

    /// <summary>Every id some fixture in this suite holds to its fire.</summary>
    /// <returns>The ids.</returns>
    static IReadOnlySet<string> CoveredIds() {
        var ids = new HashSet<string>(SilentIds(), StringComparer.Ordinal);

        // The other shape: a rule guarded from inside the suite that covers it positively, where
        // moving the fixture to NegativeDiagnosticTests would separate it from the test it mirrors.
        // One line, so the pattern is anchored to one — a `.` that crossed newlines would pair a
        // DoesNotContain with an id several assertions further down.
        foreach (var source in TestSources()) {
            foreach (Match match in Regex.Matches(source, @"DoesNotContain\([^\n]*?d\.Id\s*==\s*""(RVN\d{4})""")) {
                ids.Add(match.Groups[1].Value);
            }
        }

        return ids;
    }

    /// <summary>The ids <c>NegativeDiagnosticTests</c> names in a <c>Silent</c> call.</summary>
    /// <returns>The ids.</returns>
    static IReadOnlySet<string> SilentIds() {
        var path = Path.Combine(TestsDirectory(), "NegativeDiagnosticTests.cs");

        Assert.True(File.Exists(path), $"NegativeDiagnosticTests is not at {path}.");

        HashSet<string> ids = [];

        // \s* rather than a single-line anchor: most of these calls put the id on the next line.
        foreach (Match match in Regex.Matches(File.ReadAllText(path), @"\bSilent\(\s*""(RVN\d{4})""")) {
            ids.Add(match.Groups[1].Value);
        }

        return ids;
    }

    static IEnumerable<string> TestSources() =>
        Directory
            .EnumerateFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))

            // This file names ids in its own Owed list, and counting those as coverage would make
            // the instrument agree with itself about everything.
            .Where(path => Path.GetFileName(path) != $"{nameof(DiagnosticCoverageTests)}.cs")
            .Select(File.ReadAllText);

    static string TestsDirectory() {
        var directory = Path.Combine(RavenDirectory(), "Vixen.Raven.Tests");

        Assert.True(Directory.Exists(directory), $"The test sources are not at {directory}.");

        return directory;
    }

    static string RavenDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <param name="text">The document.</param>
    /// <param name="pattern">A regex whose first group is the figure.</param>
    /// <param name="document">
    ///     Which document, for the failure message. ⚠ Named rather than hard-coded because both of
    ///     these read <c>docs/overview.md</c> now as well, and a message saying the wrong file is
    ///     the kind of small lie that costs somebody an afternoon.
    /// </param>
    static int Number(string text, string pattern, string document = "Raven/README.md") {
        var match = Regex.Match(text, pattern);

        Assert.True(match.Success, $"Nothing in {document} matches /{pattern}/ any more.");

        return int.Parse(match.Groups[1].Value);
    }

    /// <inheritdoc cref="Number" />
    static (int First, int Second) Pair(string text, string pattern, string document = "Raven/README.md") {
        var match = Regex.Match(text, pattern);

        Assert.True(match.Success, $"Nothing in {document} matches /{pattern}/ any more.");

        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    static string Join(IEnumerable<string> ids) {
        var text = string.Join(", ", ids.Order(StringComparer.Ordinal));

        return text.Length == 0 ? "(none)" : text;
    }
}
