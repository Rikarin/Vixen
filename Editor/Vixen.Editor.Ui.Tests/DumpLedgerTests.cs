// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     The panel ledger's list of committed tree dumps is the list the tree actually holds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The section this checks exists because a claim in a document is not a test — and the
///         section then made the same mistake twice.</b> <c>Vixen.Editor.Ui/README.md</c> carries nine
///         "byte-identical in N dumped states" rows whose comparisons were run once and deleted, and
///         beside them a table of the files that <i>do</i> dump a tree. That table said "three",
///         which was true when written; it was corrected to "eight", which lasted one wave — wave 9's
///         own <c>ComponentsViewDumpTests</c> was never added to it, while the file's own remarks
///         called it "a committed dump rather than a wave note". Both errors are the same error the
///         section is about, one level up.
///     </para>
///     <para>
///         ⚠ <b>So the table is derived rather than maintained.</b> A number nobody re-derives goes
///         stale on the first wave that does not think to look, and the evidence here is that it did
///         so twice in a row. This walks the editor's test sources for the two calls that produce a
///         dump and requires the answer to be exactly the table's rows — both directions, so a new
///         dump file cannot be written without the ledger growing and a row cannot outlive the file
///         it names.
///     </para>
///     <para>
///         ⚠ <b>A tree dump and a flags dump both count, and that is not a detail.</b> Wave 7 shipped
///         a panel that matched byte-for-byte in six states while carrying a binding that could not
///         work, because <c>Label</c>, <c>IsExpanded</c> and a drawer's <c>Number</c> appear in no
///         tree at all. <c>UiTest.Flags</c> is the answer to that, so a file that dumps only flags is
///         a dump file for this table's purpose.
///     </para>
/// </remarks>
public partial class DumpLedgerTests {
    /// <summary>A call that writes a subtree down: <c>UiTest.Tree</c> or <c>UiTest.Flags</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The excluded shape is the point.</b> <c>EditorSession.Tree(string id)</c> is a
    ///     different method entirely — it finds a <c>TreeView</c> control by id and returns it — and
    ///     it is called from suites that dump nothing. A string argument is what tells the two apart,
    ///     so a first argument that opens with a quote is not a dump. <c>Tree()</c> with no argument,
    ///     and <c>Tree(element)</c> with any expression, both are.
    /// </remarks>
    [GeneratedRegex(@"\.(?:Tree|Flags)\s*\(\s*(?:\)|[A-Za-z_])")]
    private static partial Regex DumpCall { get; }

    /// <summary>A row of the ledger's table, as <c>Project.Tests/FileName</c> in backticks.</summary>
    /// <remarks>
    ///     The form appears nowhere else in that README, which is what lets this be read without
    ///     locating the table's heading first — and if it ever does appear elsewhere, this test says
    ///     so by failing rather than by quietly widening.
    /// </remarks>
    [GeneratedRegex(@"`(?<project>Vixen\.[A-Za-z0-9_.]*\.Tests)/(?<file>[A-Za-z0-9_]+)`")]
    private static partial Regex LedgerRow { get; }

    /// <summary>The table in the README, as <c>Project/File</c> strings.</summary>
    public static IReadOnlySet<string> Ledger {
        get {
            var text = File.ReadAllText(
                Path.Combine(RepositoryRoot(), "Editor", "Vixen.Editor.Ui", "README.md")
            );

            var rows = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in LedgerRow.Matches(text)) {
                rows.Add($"{match.Groups["project"].Value}/{match.Groups["file"].Value}");
            }

            return rows;
        }
    }

    /// <summary>Every editor test source that writes a subtree down, as <c>Project/File</c>.</summary>
    public static IReadOnlySet<string> Dumps {
        get {
            var found = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in Sources()) {
                // ⚠ This file writes both calls out as string literals, in the assertion that proves
                // the discriminator still discriminates — so scanning it would make it a dump file
                // and the ledger would have to name the test that checks the ledger.
                // `TypeSelectorReachTests` excludes itself for the same reason.
                if (Path.GetFileName(path).Equals("DumpLedgerTests.cs", StringComparison.Ordinal)) {
                    continue;
                }

                var project = Project(path);

                if (project is null || found.Contains($"{project}/{Path.GetFileNameWithoutExtension(path)}")) {
                    continue;
                }

                foreach (var text in File.ReadLines(path)) {
                    // A dump call named in a doc comment is prose — this file's own remarks name
                    // both of them, and so do several of the dump files' explanations of themselves.
                    var trimmed = text.TrimStart();

                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) {
                        continue;
                    }

                    if (DumpCall.IsMatch(text)) {
                        found.Add($"{project}/{Path.GetFileNameWithoutExtension(path)}");
                        break;
                    }
                }
            }

            return found;
        }
    }

    /// <summary>The ledger's table names every committed dump and nothing else.</summary>
    [Fact]
    public void The_ledger_lists_every_file_that_dumps_a_tree() {
        var ledger = Ledger;
        var dumps = Dumps;

        var unlisted = dumps.Except(ledger, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var stale = ledger.Except(dumps, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unlisted.Count == 0,
            $"{string.Join(", ", unlisted)} dumps a tree and the panel ledger's table does not say "
            + "so. That table is what a reader uses to tell a committed dump from a wave note, and "
            + "it has now gone stale twice — add the row rather than deleting this assertion."
        );

        Assert.True(
            stale.Count == 0,
            $"the panel ledger names {string.Join(", ", stale)} as a committed dump and no such file "
            + "dumps anything. Either the file was deleted or its dump was, and the row is a claim "
            + "about coverage that no longer exists."
        );
    }

    /// <summary>Both halves of the comparison found something to compare.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the assertion above passes loudest on the day it stops running.</b> Two
    ///     empty sets are equal, and every way this can break — a moved repository root, a walk that
    ///     descends into nothing, a regex that stops matching, a README that is renamed — breaks it
    ///     by emptying one side or both. So each side is asserted to be a corpus, and the
    ///     discriminator that makes the scan mean anything is asserted directly: <c>FactRowTests</c>
    ///     dumps and is found, <c>MenuTests</c> is a behaviour suite beside it and is not.
    /// </remarks>
    [Fact]
    public void The_scan_actually_ran() {
        Assert.True(Sources().Count >= 50, "almost no editor test sources were found to scan.");
        Assert.True(Ledger.Count >= 5, "the panel ledger's table of committed dumps did not parse.");

        Assert.Contains("Vixen.Editor.Ui.Tests/FactRowTests", Dumps);
        Assert.DoesNotContain("Vixen.Editor.Ui.Tests/MenuTests", Dumps);

        // And the exclusion that keeps `EditorSession.Tree("hierarchy")` from counting as a dump.
        Assert.DoesNotMatch(DumpCall, "var view = editor.Tree(\"hierarchy\");");
        Assert.Matches(DumpCall, "var text = test.Tree();");
        Assert.Matches(DumpCall, "var flags = harness.Ui.Flags(view);");
    }

    /// <summary>Which test project a source belongs to, or null if it is not in one.</summary>
    static string? Project(string path) {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
             directory is not null;
             directory = directory.Parent) {
            if (directory.Name.EndsWith(".Tests", StringComparison.Ordinal)) {
                return directory.Name;
            }
        }

        return null;
    }

    /// <summary>Every C# source under <c>Editor/</c>.</summary>
    static List<string> Sources() {
        List<string> found = [];
        Walk(Path.Combine(RepositoryRoot(), "Editor"), found);
        found.Sort(StringComparer.Ordinal);

        return found;
    }

    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude</c> is the one that matters and it is not housekeeping: agent worktrees under
    ///     <c>.claude/worktrees/</c> are full checkouts, so a sweep that walks them asserts about
    ///     other people's uncommitted work. <c>TypeSelectorReachTests</c> failed a gate run exactly
    ///     that way, by finding a defect in a tree nobody was asking about.
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    static void Walk(string directory, List<string> into) {
        into.AddRange(Directory.EnumerateFiles(directory, "*.cs"));

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (!Unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                Walk(child, into);
            }
        }
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
