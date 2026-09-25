// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Testing;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/89">#89</a>: the editor views still built in
///     C# rather than in markup are exactly the ones <c>docs/MarkupPending.txt</c> names, each with a
///     reason — so the ratio the issue says is owed is gated, and can only move one way.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two directions, and the second is what makes the list a ledger rather than a mute
///         button.</b> A view-shaped class with no <c>.vxml</c> beside it and no line in the ledger
///         is a new hand-built view that has not said why; a line whose file has grown a
///         <c>.vxml</c>, or has gone, is a port nobody recorded — delete the line in the same commit.
///         <c>NotRooted.txt</c> and <c>WhitespaceExempt.txt</c> are the same shape for the same
///         reason.
///     </para>
///     <para>
///         ⚠ <b>By declared class and not by file name</b>, which is where the issue's own audit went
///         wrong: it counted <c>InputDebugPanel.cs</c> and <c>AgentDebuggerPanel.cs</c> as unported,
///         and both are partials of <c>AssetEditorsModule</c> that <em>register</em> a view whose
///         <c>.vxml</c> exists. A name-shaped count reported two ports that were already done.
///     </para>
/// </remarks>
public class MarkupRatioTests {
    const string Ledger = "docs/MarkupPending.txt";

    /// <summary>A top-level class or record whose name ends the way a view's does.</summary>
    /// <remarks>
    ///     ⚠ Anchored at column zero, so a nested helper is not a view. <c>ImportPipeline</c>
    ///     declares a <c>SequentialView</c> and <c>EditorDiagnostics</c> a <c>ShownView</c>; both are
    ///     implementation details of the type around them, and a sweep that counted them would ask
    ///     for a <c>.vxml</c> beside a file that draws nothing.
    /// </remarks>
    static readonly Regex ViewShaped = new(
        @"^(?:public\s+|internal\s+)?(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:class|record)\s+(?<name>\w+(?:View|Panel|Inspector|Popup))\b",
        RegexOptions.Compiled | RegexOptions.Multiline
    );

    /// <summary>The hand-built views are the ledger, and the ledger is the hand-built views.</summary>
    [Fact]
    public void The_views_without_markup_are_exactly_the_ones_the_ledger_names() {
        var root = RepositoryRoot();
        var (withMarkup, without) = Measure(root);
        var ledger = ReadLedger(root);

        var unlisted = without.Except(ledger.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var stale = ledger.Keys.Except(without, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unlisted.Count == 0,
            "View-shaped classes with no .vxml beside them and no line in " + Ledger + ": "
            + string.Join(", ", unlisted)
            + ". Port it, or add a line saying why it stays C# (#89)."
        );

        Assert.True(
            stale.Count == 0,
            "Lines in " + Ledger + " naming a file that now has a .vxml beside it, or is gone: "
            + string.Join(", ", stale)
            + ". Delete the line — the list can only shrink."
        );

        // ⚠ The instrument. A sweep that found no markup views at all would call an empty ledger
        // correct, and the tree has dozens.
        Assert.True(withMarkup.Count > 30, $"the sweep found only {withMarkup.Count} views with a .vxml beside them");
    }

    /// <summary>Every line says why, and starts with one of the four words the header defines.</summary>
    [Fact]
    public void Every_ledger_line_gives_one_of_the_four_reasons() {
        var ledger = ReadLedger(RepositoryRoot());

        Assert.NotEmpty(ledger);

        foreach (var (path, reason) in ledger) {
            Assert.True(
                Regex.IsMatch(reason, @"^(canvas|machinery|not-a-view|pending)\b"),
                $"{Ledger}: '{path}' has the reason '{reason}', which does not start with canvas, machinery, not-a-view or pending"
            );
        }
    }

    /// <summary>
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1342">#1342</a>: the module README's port
    ///     table — the half a porting wave actually reads — says the same thing as the ledger about
    ///     every view the ledger names.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ledger was gated in both directions and the reasoning beside it in nothing</b>,
    ///         and they disagreed for four waves: the README declined <c>SceneHierarchyView</c> as "not
    ///         a panel" while the ledger listed it as a candidate, and every wave read the README. A
    ///         port has to touch both files; this is what makes forgetting the second one red.
    ///     </para>
    ///     <para>
    ///         A row's verdict is its fourth cell with every <c>~~struck~~</c> span removed — the
    ///         table records each reversal by striking the old verdict rather than deleting it, so the
    ///         <em>live</em> verdict is what is left. It is a decline when it starts <c>no</c> or
    ///         <c>not a panel</c>, done when <c>done</c> is among its first three words, and open
    ///         otherwise.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_readme_port_table_agrees_with_the_ledger_about_every_view_it_names() {
        var root = RepositoryRoot();
        var (withMarkup, _) = Measure(root);
        var ledger = ReadLedger(root);
        var rows = ReadReadmeLedger(root);

        var ported = withMarkup.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var pending = ledger.Where(entry => entry.Value.StartsWith("pending", StringComparison.Ordinal))
            .Select(entry => Path.GetFileNameWithoutExtension(entry.Key)!)
            .ToHashSet(StringComparer.Ordinal);
        var declined = ledger.Keys.Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Except(pending, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        List<string> disagreements = [];

        foreach (var name in ledger.Keys.Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal)) {
            var named = rows.Where(row => row.Names.Contains(name)).ToList();

            if (named.Count == 0) {
                disagreements.Add($"{name} is in {Ledger} and in no row of the README's port table");

                continue;
            }

            foreach (var row in named) {
                if (pending.Contains(name) && row.Verdict != ReadmeVerdict.Open) {
                    disagreements.Add($"{name} is pending in {Ledger} and the README's row says {row.Verdict}: '{row.Live}'");
                } else if (declined.Contains(name) && row.Verdict != ReadmeVerdict.Declined) {
                    disagreements.Add($"{name} is '{ledger.First(entry => Path.GetFileNameWithoutExtension(entry.Key) == name).Value}' in {Ledger} and the README's row says {row.Verdict}: '{row.Live}'");
                }
            }
        }

        foreach (var row in rows.Where(row => row.Verdict == ReadmeVerdict.Declined)) {
            foreach (var name in row.Names.Where(ported.Contains)) {
                disagreements.Add($"{name} has a .vxml beside it and the README's row still declines it: '{row.Live}'");
            }
        }

        // A row whose every named view has markup and none is still pending has been ported; an open
        // verdict on it is the history's first sentence and not its last.
        foreach (var row in rows.Where(row => row.Verdict == ReadmeVerdict.Open)) {
            if (row.Names.Any(ported.Contains) && !row.Names.Any(pending.Contains)) {
                disagreements.Add($"{string.Join(" · ", row.Names.Where(ported.Contains))} has a .vxml beside it and the README's row reads as still open: '{row.Live}'");
            }
        }

        Assert.True(
            disagreements.Count == 0,
            "Editor/Vixen.Editor.Ui/README.md's port table (under '### The ledger') and " + Ledger
            + " disagree — a port or a decline owes both files (#1342):\n  " + string.Join("\n  ", disagreements)
        );

        // ⚠ The instrument. A parser that found no table, or a table whose verdicts all read as one
        // class, would pass every line above vacuously.
        Assert.True(rows.Count >= 25, $"only {rows.Count} row(s) of the README's port table were read");
        Assert.Contains(rows, row => row.Verdict == ReadmeVerdict.Done);
        Assert.Contains(rows, row => row.Verdict == ReadmeVerdict.Declined);
        Assert.Contains(rows, row => row.Verdict == ReadmeVerdict.Open);
        Assert.NotEmpty(pending);
    }

    enum ReadmeVerdict {
        Open,
        Declined,
        Done
    }

    sealed record ReadmeRow(HashSet<string> Names, string Live, ReadmeVerdict Verdict);

    /// <summary>The rows of the table under <c>### The ledger</c> in the module README.</summary>
    static List<ReadmeRow> ReadReadmeLedger(string root) {
        const string Readme = "Editor/Vixen.Editor.Ui/README.md";
        var lines = File.ReadAllLines(Path.Combine(root, Readme));
        var heading = Array.IndexOf(lines, "### The ledger");

        Assert.True(heading >= 0, $"{Readme} has no '### The ledger' heading");

        var header = Array.FindIndex(lines, heading, line => line.StartsWith("| Panel |", StringComparison.Ordinal));

        Assert.True(header > heading, $"{Readme}: no '| Panel |' table under '### The ledger'");

        List<ReadmeRow> rows = [];

        // Header, then the |---| separator, then rows until the first line that is not one.
        for (var index = header + 2; index < lines.Length && lines[index].StartsWith('|'); index++) {
            // ⚠ A cell may carry an escaped pipe (`State \|= Checked`), which is not a column.
            var cells = Regex.Split(lines[index].Trim().Trim('|'), @"(?<!\\)\|");

            Assert.True(cells.Length >= 4, $"{Readme}: a port-table row with {cells.Length} cell(s): {lines[index]}");

            var names = Regex.Matches(cells[0], @"`(\w+)`").Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            var live = Regex.Replace(cells[3], "~~.*?~~", "").Replace("*", "").Trim();
            var verdict = Regex.IsMatch(live, @"^(?:no|not\s+(?:a\s+)?panels?)\b", RegexOptions.IgnoreCase)
                ? ReadmeVerdict.Declined
                : Regex.IsMatch(live, @"^(?:\w+\s+){0,2}done\b", RegexOptions.IgnoreCase)
                    ? ReadmeVerdict.Done
                    : ReadmeVerdict.Open;

            rows.Add(new(names, live.Length > 80 ? live[..80] + "…" : live, verdict));
        }

        // ⚠ A blank line inside a cell ends a Markdown table, and every row after it renders as loose
        // pipe text under a paragraph — which is how this table lost its whole decline half for three
        // weeks (#1342). A row-shaped line before the next heading is one the table no longer holds.
        for (var index = header + 2 + rows.Count; index < lines.Length && !lines[index].StartsWith('#'); index++) {
            Assert.False(
                lines[index].StartsWith("| `", StringComparison.Ordinal),
                $"{Readme}:{index + 1} is a port-table row outside the table — a blank line above it ended the table: {lines[index][..Math.Min(80, lines[index].Length)]}"
            );
        }

        return rows;
    }

    /// <summary>The two module partials the issue's audit miscounted are not views, and their views are markup.</summary>
    [Fact]
    public void A_module_partial_that_registers_a_markup_view_is_not_a_hand_built_view() {
        var root = RepositoryRoot();
        var (withMarkup, without) = Measure(root);

        Assert.DoesNotContain("Editor/Vixen.Editor.AssetEditors/Input/InputDebugPanel.cs", without);
        Assert.DoesNotContain("Editor/Vixen.Editor.AssetEditors/Ai/AgentDebuggerPanel.cs", without);
        Assert.Contains("Editor/Vixen.Editor.AssetEditors/Input/InputDebugView.cs", withMarkup);
        Assert.Contains("Editor/Vixen.Editor.AssetEditors/Ai/AgentDebuggerView.cs", withMarkup);
    }

    static (HashSet<string> WithMarkup, HashSet<string> Without) Measure(string root) {
        HashSet<string> withMarkup = new(StringComparer.Ordinal);
        HashSet<string> without = new(StringComparer.Ordinal);

        foreach (var path in SourceFiles(Path.Combine(root, "Editor"), "*.cs")) {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');

            if (relative.Contains(".Tests/", StringComparison.Ordinal)
                || relative.Contains("Generator", StringComparison.Ordinal)) {
                continue;
            }

            if (!ViewShaped.IsMatch(File.ReadAllText(path))) {
                continue;
            }

            var markup = Path.ChangeExtension(path, ".vxml");

            (File.Exists(markup) ? withMarkup : without).Add(relative);
        }

        return (withMarkup, without);
    }

    static Dictionary<string, string> ReadLedger(string root) {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(Path.Combine(root, Ledger))) {
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);

            Assert.True(space > 0, $"{Ledger}: '{line}' has no reason after the path");

            entries[line[..space]] = line[(space + 1)..].Trim();
        }

        return entries;
    }

    /// <summary>The files under a directory matching a pattern, as git defines the tree.</summary>
    /// <remarks>
    ///     ⚠ Not a directory walk (#1424): <c>.claude/worktrees/</c> holds a full checkout per agent,
    ///     and a walk pruned by a hand-kept list of names answered about whatever else was on the disk.
    /// </remarks>
    static List<string> SourceFiles(string directory, string pattern) {
        var found = RepositoryFiles.Files(directory, pattern);
        found.Sort(StringComparer.Ordinal);

        return found;
    }

    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
