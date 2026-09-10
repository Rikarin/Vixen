// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     <c>docs/overview.md</c> Part 4's <c>Owed</c> cells, checked against the shape its own header
///     says a row may have.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Part 4 and § 1.x state the same facts twice, and the two copies have contradicted each
///         other in three successive batches</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1116">#1116</a>) — always the same way
///         round, because a § 1.x paragraph is re-read when its subsystem changes and a Part 4 row is
///         re-read only when somebody opens Part 4. Each time the cure was a sweep, and a sweep is not
///         a mechanism.
///     </para>
///     <para>
///         Part 4's header now states the rule: a row is the identifier, the subsystem, <em>one
///         sentence</em> naming what is left, the § 1.x section that holds the evidence, and the
///         issue — and it bars three shapes by name, because all three of the drifts were one of them.
///         ⚠ <b>A rule nothing enforces is what #1116 is about</b>, so this is the cheap mechanised
///         half the header asks for: a length cap on the cell, plus the three barred shapes as
///         patterns.
///     </para>
///     <para>
///         ⚠ <b>What this cannot do.</b> "This row agrees with § 1.11" is not mechanisable — it is a
///         claim about two prose paragraphs. What <em>is</em> mechanisable is the property that made
///         the disagreement possible: a row long enough to be a second copy of a § 1.x paragraph, and
///         a row carrying one of the three shapes that went stale. A green run here is not a claim
///         that Part 4 is true; it is a claim that no row is shaped like the ones that were false.
///     </para>
///     <para>
///         It reads the committed file in milliseconds and loads no workspace, which is
///         <see cref="RealGuideTests" />'s argument again: the fact is derivable from committed text,
///         so it should not wait for a gate nobody runs per branch.
///     </para>
/// </remarks>
public class RealOwedTableTests {
    /// <summary>The file this reads, relative to the checkout root.</summary>
    const string RelativePath = "docs/overview.md";

    /// <summary>The heading the table lives under.</summary>
    /// <remarks>
    ///     ⚠ Matched as a prefix, because the heading carries an em dash and a trailing phrase that
    ///     may be reworded. <see cref="The_table_is_found_and_its_rows_read" /> is what notices if it
    ///     stops matching altogether.
    /// </remarks>
    const string Heading = "# Part 4";

    /// <summary>
    ///     The longest an <c>Owed</c> cell's prose may be, in characters.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A length, and not a style preference.</b> The rows that went stale were 700 to
    ///     1 900 characters; the longest compliant one today is 328. ⚠ An earlier version of this
    ///     remark called 400 "four times the median" and put the median at 98 — the real median is
    ///     112, which makes it about three and a half, and a prose number in the file that
    ///     mechanises a rule against prose numbers is worth correcting rather than re-deriving. What
    ///     the cap is *for* is the durable half: a cell that needs more than this is restating
    ///     § 1.x, which is the thing #1116 is about — the evidence belongs in the
    ///     § 1.x paragraph and the row points at it. Link targets are not counted: see
    ///     <see cref="Prose" />.
    /// </remarks>
    internal const int Cap = 400;

    /// <summary>A number, spelled either way, as the barred count shape writes one.</summary>
    /// <remarks>
    ///     ⚠ The tens are spelled out because every writing of the compound count that went stale was
    ///     a word — "sixteen", then "thirty-one", then "thirty-four" — and a digits-only pattern would
    ///     have caught none of them.
    /// </remarks>
    const string Number =
        @"(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen"
        + @"|fifteen|sixteen|seventeen|eighteen|nineteen"
        + @"|(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)"
        + @"(?:[- ](?:one|two|three|four|five|six|seven|eight|nine))?)";

    /// <summary>The things Part 4 has miscounted, as the nouns a count is written against.</summary>
    const string Counted =
        @"(?:compounds?|kernels?|nodes?|marks?|files?|tests?|callers?|consumers?|implementations?"
        + @"|classes|entries)";

    /// <summary>The three shapes Part 4's header bars, with the barred text as the pattern's name.</summary>
    /// <remarks>
    ///     <para>
    ///         Each is deliberately narrow. A pattern that fired on every row would be excused
    ///         everywhere and enforce nothing, and the cost of a false positive here is a correct row
    ///         somebody has to reword — so these match the three sentence shapes the drifts actually
    ///         took rather than the topics they were about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Today every row these fire on is also over <see cref="Cap" />.</b> That is not a
    ///         reason to drop them: the row they exist for is the <em>short</em> one that says
    ///         "nothing calls X", which is what row 93 said for a whole batch after `PaintMeshView`
    ///         landed, and a length cap alone would have let it through.
    ///     </para>
    /// </remarks>
    static readonly (string Shape, Regex Pattern)[] Barred = [
        (
            "a count",
            new Regex(
                // ⚠ The lookbehind is a section number, not an optimisation: "§1.4 of this file" is a
                // citation and reads as "4 of" to a pattern that does not refuse a preceding dot.
                $@"(?<![.\d])\b{Number}[- ]of\b|(?<![.\d])\b{Number}[- ]{Counted}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "\"nothing calls X\" / \"X does not exist\"",
            new Regex(
                @"\bnothing\b[^.|]{0,60}\b(?:calls?|reads?|uses?|constructs?|shows?|carries|writes?|implements?|registers?|consumes?|authors?)\b"
                + @"|\bno (?:caller|consumer|reader|producer|node|pane|host|verb|route|panel)s?\b"
                + @"|\bdoes not exist\b|\bexists nowhere\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "history",
            new Regex(
                @"\bthis (?:row|cell)\b|\bthe generated row\b|\bused to (?:say|read|state|carry|restate)\b"
                + @"|\b(?:row|cell) was written\b|\bcontradicted\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
    ];

    /// <summary>A markdown link, reduced to the text a reader sees.</summary>
    static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);

    /// <summary>A struck-through span, which states what a row no longer owes.</summary>
    static readonly Regex Struck = new("~~.*?~~", RegexOptions.CultureInvariant);

    /// <summary>A row of the table.</summary>
    static readonly Regex RowStart = new(@"^\|\s*(\d+[a-z]?)\s*\|", RegexOptions.CultureInvariant);

    /// <summary>
    ///     Rows known to break the rule, with the issue that fixes them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A tripwire, not a suppression</b>, the shape
    ///         <see cref="RealGuideTests" /> settled on: every entry names its issue, and
    ///         <see cref="Every_excused_row_still_breaks_the_rule" /> requires the row to still be
    ///         broken — so a row somebody trims turns this list red rather than leaving a stale excuse
    ///         behind. The list can only shrink.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is left of the rows that were over the cap when the rule was mechanised, and
    ///         none of them is material authoring's.</b> They are enumerated with their lengths in
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1125">#1125</a>, which is the issue
    ///         each entry names and which closes when the last of them is trimmed. Trimming a row means moving its evidence
    ///         into the § 1.x paragraph that owns the subsystem and checking that paragraph carries
    ///         it — which for rows 91 and 92 it already did, and those two were trimmed rather than
    ///         excused. Rows 22 and 44 were trimmed the harder way on 2026-09-09: the evidence
    ///         genuinely was not in § 1.x, so it was written there first — the second-window owned
    ///         surface and the binary chain that outranks a timeline wait into § 1.4's two rows, and
    ///         <c>EmitCompilerGeneratedFiles</c> into § 1.7's CLI-emit row — and only then was the
    ///         cell reduced to a pointer. Rows 61 and 81 went the same way the same day, and both
    ///         were re-derived from the tree before being written down rather than copied: the four
    ///         absent input device classes into § 1.10's <c>Vixen.Input</c> row, and the two-group
    ///         drawing limit into § 1.11's selectable-wires row, where it belongs because it is
    ///         <c>Vixen.Ui.Controls.Advanced</c>'s single <c>GraphNode.Group</c> back-pointer against
    ///         the editor model's unconstrained <c>List&lt;NodeId&gt;</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The list is empty as of 2026-09-10, so this is a mechanism with nothing in it
    ///         rather than a suppression with something in it.</b> The last seven —
    ///         <c>Vixen.Core.Threading</c>, <c>Vixen.Sdk</c>, <c>Vixen.Cli</c>, the two editor rows,
    ///         the editor's packaging row and Build/CI — went the same way: what only the Part 4 cell
    ///         said was written into § 1.2's job-priority row, § 1.6's SDK and CLI rows, § 1.11's
    ///         asset-field, composed-viewport and redraw rows and § 1.1's content-determinism row,
    ///         and the cell was then reduced to a pointer. ⚠ Two of them were re-derived from the
    ///         tree rather than copied and both had gone stale: `AppendChild` is amortised constant
    ///         and caret blink is built, which is <a href="https://github.com/Rikarin/Vixen/issues/440">#440</a>'s
    ///         finding as much as this one's. An entry added here later still names its issue and
    ///         still has to stay broken, which is what stops the list growing quietly.
    ///     </para>
    /// </remarks>
    static readonly (string Row, string Issue)[] Excused = [
    ];

    /// <summary>The checkout this assembly was compiled in — the nearest root, never the outermost.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per parallel agent, so a walk that kept
    ///     going would leave a worktree's run asserting about the main tree's <c>docs/</c> — a
    ///     directory it cannot change, while missing the one it can.
    /// </remarks>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            var relative = RelativePath.Replace('/', Path.DirectorySeparatorChar);

            while (directory is not null) {
                if (File.Exists(Path.Combine(directory.FullName, relative))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No {RelativePath} above {AppContext.BaseDirectory}. This test reads the repository "
                + "it was compiled in, so an output directory outside the checkout breaks it."
            );
        }
    }

    /// <summary>What a reader sees in a cell, with link targets removed and the edges trimmed.</summary>
    /// <remarks>
    ///     ⚠ <b>A URL is not prose and must not count against the cap.</b> Row 91 carried six issue
    ///     links, which is 280 characters of <c>https://github.com/Rikarin/Vixen/issues/…</c> — a cap
    ///     over the raw cell would have charged a row for citing its evidence, which is the one thing
    ///     the rule wants rows to do.
    /// </remarks>
    internal static string Prose(string cell) => Link.Replace(cell, "$1").Trim();

    /// <summary>Every way one <c>Owed</c> cell breaks Part 4's stated rule, in the order stated.</summary>
    /// <remarks>
    ///     ⚠ <b>Struck-through spans are read past for the shape patterns and not for the cap.</b> A
    ///     <c>~~…~~</c> span is a row saying what it no longer owes, so "~~`GradientEditor` has no
    ///     consumer~~" is a resolution rather than the barred claim — but it still costs the reader
    ///     the length it takes, so it counts toward the cap.
    /// </remarks>
    internal static IReadOnlyList<string> Breaches(string cell) {
        var prose = Prose(cell);
        var live = Struck.Replace(prose, " ");
        var breaches = new List<string>();

        if (prose.Length > Cap) {
            breaches.Add(
                $"{prose.Length} characters of prose against a cap of {Cap} — a cell this long is a "
                + "second copy of the § 1.x paragraph rather than one sentence pointing at it"
            );
        }

        foreach (var (shape, pattern) in Barred) {
            var match = pattern.Match(live);

            if (match.Success) {
                breaches.Add($"the barred shape {shape}, at \"{match.Value}\"");
            }
        }

        return breaches;
    }

    /// <summary>Part 4's rows, in file order.</summary>
    static IReadOnlyList<(string Number, int Line, string Owed)> Rows() {
        var lines = File.ReadAllLines(Path.Combine(Root, RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rows = new List<(string, int, string)>();
        var inside = false;

        for (var index = 0; index < lines.Length; index++) {
            var line = lines[index];

            if (line.StartsWith(Heading, StringComparison.Ordinal)) {
                inside = true;

                continue;
            }

            if (!inside) {
                continue;
            }

            var match = RowStart.Match(line);

            if (!match.Success) {
                // ⚠ The table ends here, and reading past it is not a harmless over-read: § 4.2's
                // exit criteria are numbered 1-12 in a table of their own, so a walk that kept going
                // collects a second `| 1 |` and silently answers about the wrong table.
                if (rows.Count > 0) {
                    break;
                }

                continue;
            }

            // The identifier, the subsystem, the owed cell and the issue, between five pipes.
            var columns = line.Split('|');

            if (columns.Length < 5) {
                continue;
            }

            rows.Add((match.Groups[1].Value, index + 1, columns[3]));
        }

        return rows;
    }

    /// <summary>
    ///     The heading matched, the rows parsed, and the cells are not empty — so a green run below is
    ///     not a run over nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Ask what this file prints on the day Part 4 is renamed.</b> Without this the answer is
    ///     "success": <see cref="Rows" /> returns nothing, every check below holds vacuously, and the
    ///     table drifts unwatched for as long as nobody opens it — which is the failure #1116 is
    ///     about, reproduced one level up in the instrument meant to catch it. ⚠ <b>It has already
    ///     caught two readers</b>: one that walked past the table's last row and collected § 4.2's
    ///     exit criteria, which are numbered from 1 again, and one that stopped at row <c>18b</c>
    ///     because a row identifier is not always digits. The floor is far below the table — 82 rows
    ///     when this was written, against the 15 the second of those returned — because it is an
    ///     instrument check and not a census: Part 4's own header says the numbers are stable
    ///     identifiers, so rows are added and never removed.
    /// </remarks>
    [Fact]
    public void The_table_is_found_and_its_rows_read() {
        var rows = Rows();

        Assert.True(
            rows.Count >= 70,
            $"{RelativePath} yielded {rows.Count} Part 4 row(s), which is too few to be this table. "
            + $"Either the `{Heading}` heading has been reworded past the prefix this matches, or the "
            + "rows are no longer a markdown table — and every check in this file would hold over "
            + "nothing while saying so."
        );

        Assert.True(
            rows.Count(row => Prose(row.Owed).Length > 40) >= 60,
            "Part 4's rows parsed but their Owed cells came back near-empty, so the column index is "
            + "wrong and the cap below is measuring the subsystem name."
        );
    }

    /// <summary>
    ///     No <c>Owed</c> cell is longer than a sentence or shaped like one of the three claims that
    ///     went stale.
    /// </summary>
    [Fact]
    public void Every_owed_cell_keeps_to_the_shape_part_4_states() {
        var excused = Excused.Select(entry => entry.Row).ToHashSet();

        var problems = Rows()
            .Where(row => !excused.Contains(row.Number))
            .SelectMany(row => Breaches(row.Owed)
                .Select(breach => $"{RelativePath}:{row.Line}: row {row.Number} — {breach}"))
            .ToArray();

        Assert.True(
            problems.Length == 0,
            $"Part 4 row(s) breaking the rule its own header states:{Environment.NewLine}  "
            + string.Join($"{Environment.NewLine}  ", problems)
            + $"{Environment.NewLine}A row is the identifier, the subsystem, one sentence naming what "
            + "is left, the § 1.x section holding the evidence, and the issue. Move the narrative "
            + "into that § 1.x paragraph — do not delete it — and leave the row pointing at it."
        );
    }

    /// <summary>
    ///     Every excused row still breaks the rule, so the list expires as the rows are trimmed.
    /// </summary>
    [Fact]
    public void Every_excused_row_still_breaks_the_rule() {
        var rows = Rows().ToDictionary(row => row.Number);

        var stale = Excused
            .Select(entry => {
                if (!rows.TryGetValue(entry.Row, out var row)) {
                    return $"row {entry.Row} ({entry.Issue}) is not in Part 4 any more";
                }

                return Breaches(row.Owed).Count == 0
                    ? $"row {entry.Row} ({entry.Issue}) keeps to the rule now"
                    : null;
            })
            .OfType<string>()
            .ToArray();

        Assert.True(
            stale.Length == 0,
            $"Excused row(s) that no longer need excusing:{Environment.NewLine}  "
            + string.Join($"{Environment.NewLine}  ", stale)
            + $"{Environment.NewLine}Delete the entry in the commit that trimmed the row. The list can "
            + "only shrink, which is what stops it becoming the suppression it looks like."
        );
    }

    /// <summary>A cell of the shape Part 4 asks for is not a breach.</summary>
    [Theory]
    [InlineData("`GpuUploadRing`")]
    [InlineData("The three narrower bake routes, with the evidence in § 1.11 and the issue below")]
    [InlineData("~~`GradientEditor` has no consumer~~ (no *editor* consumer, deliberately — §1.7)")]
    public void A_cell_of_the_stated_shape_passes(string cell) => Assert.Empty(Breaches(cell));

    /// <summary>
    ///     Each barred shape is caught in a cell short enough that the cap does not catch it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>These are the three drifts, reduced to one sentence each.</b> Written long they would
    ///     be caught by the cap whatever the patterns did, and the test would be proving the cap
    ///     three times over — which is exactly the instrument that cannot fail this repository keeps
    ///     shipping.
    /// </remarks>
    [Theory]
    [InlineData("Patterns four of seven, and the grunges are a family of eight", "a count")]
    [InlineData("31 compounds against § 4.9's marks", "a count")]
    [InlineData("Thirty-one compounds against § 4.9's marks", "a count")]
    [InlineData("None of it has a caller — nothing shows a `.vxlayers`' model", "\"nothing calls")]
    [InlineData("`TexturedMaterialLayersFeature` has no consumer outside its own tests", "\"nothing calls")]
    [InlineData("The 3D projection is owed — `PaintMeshView` does not exist", "\"nothing calls")]
    [InlineData("This row said the projection was unstarted and it landed on 2026-09-08", "history")]
    [InlineData("The count used to say sixteen, which contradicted § 1.11", "history")]
    public void Each_barred_shape_is_caught_short_of_the_cap(string cell, string shape) {
        Assert.True(Prose(cell).Length <= Cap, "The fixture must be shorter than the cap it is not testing.");
        Assert.Contains(Breaches(cell), breach => breach.Contains(shape, StringComparison.Ordinal));
    }

    /// <summary>A cell over the cap is caught on its length alone.</summary>
    [Fact]
    public void A_cell_over_the_cap_is_caught_on_its_length() {
        var cell = new string('x', Cap + 1);

        Assert.Contains(Breaches(cell), breach => breach.Contains("against a cap of", StringComparison.Ordinal));
    }

    /// <summary>A link's target does not count against the cap.</summary>
    /// <remarks>
    ///     Six issue links is 280 characters of URL, so a cap over the raw cell would charge a row for
    ///     citing its evidence.
    /// </remarks>
    [Fact]
    public void A_links_target_does_not_count_against_the_cap() {
        var link = "[#1073](https://github.com/Rikarin/Vixen/issues/1073)";
        var cell = new string('x', Cap - 60) + " " + string.Concat(Enumerable.Repeat(link, 6));

        Assert.True(cell.Length > Cap, "The fixture must be over the cap before its links are collapsed.");
        Assert.Empty(Breaches(cell));
    }
}
