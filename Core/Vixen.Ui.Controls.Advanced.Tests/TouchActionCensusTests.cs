// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Markup.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Every control that captures a finger says what a scroll view around it may do with that finger.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A gate rather than a sweep, because this class of defect regrows.</b> #1337 declared
///         <c>touch-action</c> on the five tags it had measured, and a sweep one batch later found
///         thirty-one more capture sites declaring nothing (#1357) — each one a control a finger drives
///         <i>and</i> scrolls the panel around at the same time, because a capture redirects the raw
///         pointer events and the <c>DragEvent</c> the recogniser raises from them bubbles past it.
///         <c>TouchActionCensus.txt</c> lists every production <c>CapturePointer(</c> call by file and
///         the row that answers it, and this holds the file to the tree.
///     </para>
///     <para>
///         ⚠ <b>Per file with a count, not per call site with a line number.</b> A line number rots on
///         every edit above it and would make this red for nothing; a count goes red exactly when a
///         capture is added or removed, which is the moment somebody has to say what answers it.
///     </para>
///     <para>
///         ⚠ <b>The answers are checked against the cascade, not against the sheet's text.</b> An
///         element of each tag is created in a document with both themes installed and asked what it
///         resolved, so a row a later rule overrode, a selector spelt wrong, or a layer that loses is
///         caught — a regex over <c>.vcss</c> would have read all three as declared.
///     </para>
/// </remarks>
public class TouchActionCensusTests {
    const string CensusFile = "Core/Vixen.Ui.Controls.Advanced.Tests/TouchActionCensus.txt";

    /// <summary>The call a capture site makes, compared with the whitespace removed.</summary>
    const string Needle = "CapturePointer(";

    /// <summary>The areas a production capture can live in.</summary>
    static readonly string[] Swept = ["Core", "Editor", "Samples"];

    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude</c> holds a full checkout per agent, so a walk that did not prune it would
    ///     answer about somebody else's tree.
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    [Fact]
    public void Every_pointer_capture_in_the_tree_is_in_the_census_and_nothing_else_is() {
        var found = Captures(productionOnly: true);
        var census = Census();

        var missing = found
            .Where(pair => !census.TryGetValue(pair.Key, out var row) || row.Sites != pair.Value)
            .Select(pair => $"  {pair.Key} | {pair.Value} (census says {(census.TryGetValue(pair.Key, out var row) ? row.Sites.ToString() : "nothing")})")
            .ToList();

        var stale = census.Keys.Where(path => !found.ContainsKey(path)).Select(path => $"  {path}").ToList();

        Assert.True(
            missing.Count == 0 && stale.Count == 0,
            $"""
             {CensusFile} disagrees with the tree.

             Captures the census does not account for — a finger on each of these drives the control AND
             scrolls any scroll view around it until a `touch-action` row says otherwise. Add the row to
             the theme that styles the element the finger lands on, and the line to the census:
             {Joined(missing)}

             Census rows for files that no longer capture:
             {Joined(stale)}
             """
        );
    }

    /// <summary>What the gate prints on the day it does not run, asked before anything else is believed.</summary>
    /// <remarks>
    ///     ⚠ <b>The exclusion is asserted against the unfiltered sweep.</b> The obvious spelling —
    ///     that no production result is a test path — examines a list the filter already emptied of
    ///     test paths, and is green for a filter that dropped the whole repository. The same needle
    ///     must find test captures with the filter off (<c>TouchActionTests</c> captures by hand) and
    ///     not carry them with it on.
    /// </remarks>
    [Fact]
    public void The_sweep_finds_the_repository_and_leaves_the_tests_out() {
        var all = Captures(productionOnly: false);
        var production = Captures(productionOnly: true);

        Assert.True(production.Values.Sum() >= 30, $"the sweep found only {production.Values.Sum()} production captures");
        Assert.True(production.Count >= 15, $"the sweep found only {production.Count} files that capture");

        Assert.Contains(all.Keys, IsTest);
        Assert.DoesNotContain(production.Keys, IsTest);

        // Both halves of the tree the rows are about: the editor's paint views are the reason the
        // sweep is not confined to `Core`.
        Assert.Contains(production.Keys, path => path.StartsWith("Editor/", StringComparison.Ordinal));
        Assert.Contains(production.Keys, path => path.StartsWith("Core/Vixen.Ui.Controls.Advanced/", StringComparison.Ordinal));
    }

    /// <summary>A capture in a view's <c>@code</c> block is production C#, so the sweep has to read <c>.vxml</c> too.</summary>
    /// <remarks>
    ///     No view captures today; what this proves is that the reader would see one if it did, rather
    ///     than trusting a <c>*.cs</c> glob on the day a panel's code block is the first to.
    /// </remarks>
    [Fact]
    public void A_capture_in_a_view_code_block_is_counted_and_one_in_its_markup_prose_is_not() {
        string[] view = [
            "<!-- CapturePointer(this) in a comment is prose -->",
            "<div>CapturePointer(this) as text</div>",
            "@code {",
            "    // CapturePointer(this) in a C# comment",
            "    void Pressed() => Document.CapturePointer(Root);",
            "}"
        ];

        Assert.Equal(1, CountIn(view, markup: true));
    }

    [Fact]
    public void Every_answer_in_the_census_is_what_the_themes_resolve() {
        using var fixture = new AdvancedFixture();

        var answers = Census().Values.SelectMany(row => row.Answers).Distinct().ToList();
        Assert.True(answers.Count >= 15, $"the census names only {answers.Count} tag(s)");

        List<string> wrong = [];

        foreach (var (tag, expected) in answers) {
            var element = fixture.Document.Create(tag, fixture.Document.Root);
            fixture.Update();

            var resolved = fixture.Document.TouchActionBetween(element, element);

            if (resolved != expected) {
                wrong.Add($"  {tag}: the census says {expected}, the themes resolve {resolved}");
            }

            element.Remove();
        }

        Assert.True(wrong.Count == 0, $"{CensusFile} and the themes disagree:\n{string.Join('\n', wrong)}");
    }

    // ── The census ───────────────────────────────────────────────────────────────────────────────

    sealed record Row(int Sites, List<(string Tag, TouchAction Value)> Answers);

    static Dictionary<string, Row> Census() {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);

        foreach (var raw in File.ReadLines(Path.Combine(Root(), CensusFile))) {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            Assert.True(cells.Length == 3, $"a census row is `path | sites | answer`: {line}");

            rows.Add(cells[0], new Row(int.Parse(cells[1], System.Globalization.CultureInfo.InvariantCulture), Answers(cells[2])));
        }

        return rows;
    }

    /// <summary>A row's answer: <c>tag: value</c> pairs, or <c>undecided:</c> and the tags that must still declare nothing.</summary>
    static List<(string Tag, TouchAction Value)> Answers(string cell) {
        const string Undecided = "undecided:";

        if (cell.StartsWith(Undecided, StringComparison.Ordinal)) {
            return cell[Undecided.Length..]
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(tag => (tag, TouchAction.Auto))
                .ToList();
        }

        return cell.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(':', StringSplitOptions.TrimEntries))
            .Select(pair => (pair[0], pair[1] switch {
                "none" => TouchAction.None,
                "pan-y" => TouchAction.PanY,
                "pan-x" => TouchAction.PanX,
                var other => throw new InvalidDataException($"the census cannot read `{other}` in `{cell}`")
            }))
            .ToList();
    }

    // ── The sweep ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Capture calls per file, keyed by the repository-relative path with forward slashes.</summary>
    static Dictionary<string, int> Captures(bool productionOnly) {
        var root = Root();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var area in Swept) {
            foreach (var path in Files(Path.Combine(root, area))) {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');

                if (productionOnly && IsTest(relative)) {
                    continue;
                }

                var count = CountIn(File.ReadLines(path), path.EndsWith(".vxml", StringComparison.Ordinal));

                if (count > 0) {
                    found[relative] = count;
                }
            }
        }

        return found;
    }

    /// <summary>Live capture calls in one file, past comments, prose and the method's own declaration.</summary>
    static int CountIn(IEnumerable<string> lines, bool markup) {
        IEnumerable<string> code = markup
            ? VxmlLines.Read(lines).Where(line => line.Region == VxmlRegion.Code).Select(line => line.Text)
            : lines;

        var count = 0;

        foreach (var line in code) {
            var text = line.TrimStart();

            if (text.StartsWith("//", StringComparison.Ordinal)
                || text.StartsWith('*')
                || text.StartsWith("/*", StringComparison.Ordinal)
                || text.StartsWith("public void CapturePointer(", StringComparison.Ordinal)) {
                continue;
            }

            var squeezed = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

            for (var at = squeezed.IndexOf(Needle, StringComparison.Ordinal);
                 at >= 0;
                 at = squeezed.IndexOf(Needle, at + Needle.Length, StringComparison.Ordinal)) {
                count++;
            }
        }

        return count;
    }

    static bool IsTest(string relative) => relative.Contains(".Tests/", StringComparison.Ordinal);

    static IEnumerable<string> Files(string directory) {
        if (!Directory.Exists(directory)) {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory)) {
            if (file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".vxml", StringComparison.Ordinal)) {
                yield return file;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (Unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                continue;
            }

            foreach (var file in Files(child)) {
                yield return file;
            }
        }
    }

    static string Joined(List<string> lines) => lines.Count == 0 ? "  (none)" : string.Join('\n', lines);

    /// <summary>The working tree's root, found by a directory only it has.</summary>
    static string Root() {
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
