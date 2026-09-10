// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     <c>docs/overview.md</c>'s rows, checked for the two ways a table stops saying what it says.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A cap applied by character count rather than at a sentence boundary left 282 note
///         cells ending mid-sentence</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/440">#440</a>), and the file the working
///         agreement calls the state is the worst place for that: a row whose qualifier was the half
///         that got cut still <em>wins</em> against a plan document, with the qualifier missing.
///         Twice in one session the cut half was the caveat and not the argument — the editor network
///         panel row read ✅ and stopped before saying the panel was empty in every pane.
///     </para>
///     <para>
///         ⚠ <b>The cap also cut through markup.</b> Part 4 row 76 lost a whole <c>~~…~~</c> span,
///         which is a row's way of saying what it no longer owes, so the row read as owing something
///         that was built; and four rows finished inside an inline <c>`a | b`</c>, where the restored
///         pipe silently splits the row into more columns than the table has.
///     </para>
///     <para>
///         Neither property needs a workspace or a generator: both are derivable from the committed
///         text in milliseconds, which is <see cref="RealOwedTableTests" />'s argument again.
///     </para>
/// </remarks>
public class RealOverviewNotesTests {
    /// <summary>The file this reads, relative to the checkout root.</summary>
    const string RelativePath = "docs/overview.md";

    /// <summary>
    ///     How many table rows the file has to have for a run to mean anything.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What this prints on the day it does not run is the question.</b> Both assertions below
    ///     are over a loop, so an empty read — the wrong file, a rename, a checkout that stopped
    ///     carrying Part 1 — passes them without examining anything. The file has held more than a
    ///     thousand rows since it was written; a floor a third of that fails loudly instead.
    /// </remarks>
    const int Floor = 300;

    /// <summary>The checkout this assembly was compiled in — the nearest root, never the outermost.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per parallel agent, so a walk that kept
    ///     going would leave a worktree's run asserting about the main tree's <c>docs/</c>.
    /// </remarks>
    static string OverviewPath {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            var relative = RelativePath.Replace('/', Path.DirectorySeparatorChar);

            while (directory is not null) {
                var candidate = Path.Combine(directory.FullName, relative);

                if (File.Exists(candidate)) {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No {RelativePath} above {AppContext.BaseDirectory}. This test reads the repository "
                + "it was compiled in, so an output directory outside the checkout breaks it."
            );
        }
    }

    /// <summary>Every table row in the file, as its cells.</summary>
    /// <remarks>
    ///     Split on unescaped pipes only: a <c>\|</c> is a pipe a cell means literally, which is how
    ///     an inline <c>`fragment \| compute`</c> stays one cell.
    /// </remarks>
    static IReadOnlyList<(int Line, string[] Cells)> Rows() {
        var rows = new List<(int, string[])>();
        var lines = File.ReadAllLines(OverviewPath);

        for (var index = 0; index < lines.Length; index++) {
            var line = lines[index];

            if (!line.StartsWith('|')) {
                continue;
            }

            var cells = new List<string>();
            var cell = new StringBuilder();

            for (var at = 1; at < line.Length; at++) {
                if (line[at] == '\\' && at + 1 < line.Length && line[at + 1] == '|') {
                    cell.Append("\\|");
                    at++;
                } else if (line[at] == '|') {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                } else {
                    cell.Append(line[at]);
                }
            }

            rows.Add((index + 1, [.. cells]));
        }

        return rows;
    }

    /// <summary>A cell that stops mid-sentence asserts less than the row's author wrote.</summary>
    [Fact]
    public void No_cell_ends_where_a_character_cap_stopped_it() {
        var rows = Rows();
        var cut = rows
            .SelectMany(row => row.Cells.Select(cell => (row.Line, Cell: cell)))
            .Where(entry => entry.Cell.EndsWith('…'))
            .Select(entry => $"{RelativePath}:{entry.Line}: …{entry.Cell[^Math.Min(70, entry.Cell.Length)..]}")
            .ToList();

        Assert.True(rows.Count >= Floor, $"only {rows.Count} rows read out of {OverviewPath}");
        Assert.True(
            cut.Count == 0,
            "Cells cut off mid-sentence — re-cap at the last whole sentence that fits, and keep the "
            + "⚠ clause that followed the cut where there is one:\n" + string.Join('\n', cut)
        );
    }

    /// <summary>
    ///     § 1.15's guide counts are the tree's, and its <c>DocsExempt.txt</c> count is the file's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every one of the four numbers in that row had moved by the time it was read</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/113">#113</a>): 247 pages against
    ///         248, editor 43 against 44, 3 322 exempt types against 3 336, and "<c>Vixen.Raven</c>'s
    ///         share is now 0" against one line that is still there. None of them is wrong in a way a
    ///         reader would notice, which is the argument for checking them rather than for deleting
    ///         them: the row is the only place that says how much of the manual exists.
    ///     </para>
    ///     <para>
    ///         Part 4 bars a count outright and § 1.x does not, because a § 1.x paragraph is where the
    ///         evidence goes. A count that something checks is evidence; one nothing checks is the
    ///         snapshot Part 4's header is about.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_guide_counts_in_section_1_15_are_the_trees() {
        var root = Directory.GetParent(Path.GetDirectoryName(OverviewPath)!)!.FullName;
        var guide = Path.Combine(root, "docs", "guide");
        var areas = Directory.GetDirectories(guide)
            .ToDictionary(
                directory => Path.GetFileName(directory)!,
                directory => Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories).Length);

        var row = File.ReadAllLines(OverviewPath)
            .FirstOrDefault(line => line.Contains("](guide) holds", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("§ 1.15's guide row is gone, or its link is written differently.");

        var stated = System.Text.RegularExpressions.Regex.Match(
            row, @"\*\*(?<pages>\d+) pages across (?<areas>\w+) areas\*\* \((?<list>[^)]*)\)");

        Assert.True(stated.Success, $"§ 1.15's guide row does not state its counts the usual way: {row[..120]}");
        Assert.Equal(areas.Values.Sum(), int.Parse(stated.Groups["pages"].Value));

        var listed = stated.Groups["list"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split(' '))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]));

        Assert.Equal(areas.OrderBy(pair => pair.Key), listed.OrderBy(pair => pair.Key));

        var exempt = File.ReadAllLines(Path.Combine(root, "docs", "DocsExempt.txt"));
        var types = exempt.Count(line => line.StartsWith("T:", StringComparison.Ordinal));
        var statedTypes = System.Text.RegularExpressions.Regex.Match(row, @"\(([\d  ]+)`T:` lines");

        Assert.True(statedTypes.Success, "§ 1.15's guide row no longer states a `T:` count.");
        Assert.Equal(
            types,
            int.Parse(statedTypes.Groups[1].Value.Replace(" ", string.Empty).Replace(" ", string.Empty)));
    }

    /// <summary>A row with more cells than its table has columns is a row nothing renders.</summary>
    [Fact]
    public void Every_row_has_its_table_s_column_count() {
        var rows = Rows();
        var byLine = rows.ToDictionary(row => row.Line, row => row.Cells);
        var wrong = new List<string>();
        var columns = 0;
        var previous = -1;

        foreach (var (line, cells) in rows) {
            // A gap in the line numbers is a new table, whose header row is not yet measured against
            // anything — the count comes from the separator that follows it.
            if (line != previous + 1) {
                columns = 0;
            }

            previous = line;

            var separator = cells.Length > 0
                && cells.All(cell => cell.Length > 0 && cell.Trim(['-', ':', ' ']).Length == 0);

            if (separator) {
                columns = byLine.TryGetValue(line - 1, out var header) ? header.Length : 0;

                continue;
            }

            if (columns > 0 && cells.Length != columns) {
                wrong.Add($"{RelativePath}:{line}: {cells.Length} cells against {columns} columns — "
                    + $"{cells[0]}");
            }
        }

        Assert.True(rows.Count >= Floor, $"only {rows.Count} rows read out of {OverviewPath}");
        Assert.True(
            wrong.Count == 0,
            "Rows whose cell count disagrees with their table's header — an unescaped `|` inside a "
            + "cell is the way this happens:\n" + string.Join('\n', wrong)
        );
    }
}
