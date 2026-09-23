// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     Every <c>`File.cs:NNN`</c> citation in <c>docs/plan</c> names a file that exists and a line it
///     has, and where the citation stands beside the symbol it is evidence for, that symbol is on the
///     cited line (<a href="https://github.com/Rikarin/Vixen/issues/1356">#1356</a>).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A citation that drifts does not fail — it points at a neighbouring line and still reads
///         as evidence</b>, which is worse than pointing at nothing. Doc 49 cited
///         <c>`UiElement.cs:709`</c> for <c>UiElement.AccessKey</c> through two audits; 709 is the
///         <c>[UiProperty]</c> above it. These citations are the evidence half of every plan claim, and
///         until this test nothing read one of them.
///     </para>
///     <para>
///         Two rules. <b>Resolution</b> holds for every citation: the file exists (by the path as
///         written, or by any file whose path ends with it — the documents mostly cite a bare file name)
///         and has at least the cited line. It has no false positives and catches every deletion,
///         rename and large truncation. <b>Placement</b> holds where the citation is bound to a
///         symbol: <c>`Symbol` (`File.cs:NNN`)</c>, the parenthesis holding exactly the one
///         citation; a table cell holding exactly <c>`Symbol` `File.cs:NNN`</c>; or
///         <c>`File.cs:NNN` — `code`</c>. The symbol's last dotted segment has to appear
///         on the cited line, inside a cited range, or on one of a list of cited lines. A symbol that
///         is one of a list (<c>`A`, `B` (…)</c>) or the object of a negation (<c>has no `Cancel`
///         (…)</c>) is not bound to the line, and is skipped rather than guessed at.
///     </para>
///     <para>
///         ⚠ <b>Nothing here rewrites a number.</b> A citation pointing at the wrong symbol is a prose
///         error, and moving the number to wherever the symbol went would hide the thing worth
///         reading. A citation that records what the code <em>was</em> — a finding written against a
///         file a later commit removed — goes in <see cref="ExemptPath" /> with the commit that moved
///         it, and that list can only shrink: an entry whose citation now passes, or is gone, fails.
///     </para>
///     <para>
///         ⚠ <b>Both source languages.</b> Five of doc 49's six closed rows are closed by
///         <c>.vxml</c> citations alone, so a walker that indexed only <c>.cs</c> would call them
///         unresolvable and be wrong.
///     </para>
/// </remarks>
public class RealPlanCitationTests {
    /// <summary>The directory whose documents are swept, relative to the checkout root.</summary>
    const string PlanPath = "docs/plan";

    /// <summary>Citations that record a file as it was, one per line: document, citation, reason.</summary>
    const string ExemptPath = "docs/PlanCitationExempt.txt";

    /// <summary>
    ///     How many citations the sweep has to find, and how many of those have to be bound to a
    ///     symbol, for a run to mean anything.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What this prints on the day the regex matches nothing is the question.</b> Every
    ///     assertion below is over a loop, so a pattern that parsed no row — a changed backtick, a
    ///     renamed directory — would pass three hundred citations without reading one. The sweep read
    ///     343 citations, 57 of them bound to a symbol, when this was written; the floors
    ///     sit well under that and fail loudly on an empty read.
    /// </remarks>
    const int CitationFloor = 300;

    /// <inheritdoc cref="CitationFloor" />
    const int BoundFloor = 45;

    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a full checkout per agent: an index that walked it would
    ///     resolve a citation against somebody else's tree.
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    /// <summary>A backticked file citation: a path or file name, a colon, and lines.</summary>
    /// <remarks>
    ///     Lines are one number, a range (<c>38-49</c>) or a list (<c>321,368</c>). A bare
    ///     <c>`:557`</c> is a continuation and cites the file most recently named on the same line.
    ///     ⚠ One that opens a line, its file having been named on the line before, is not read: a
    ///     paragraph is wrapped wherever it falls, and binding across the wrap would guess.
    /// </remarks>
    static readonly Regex Citation = new(
        @"`(?:…/|\.\.\./)?(?<file>(?:[\w.-]+/)*[\w.-]+\.(?:cs|vxml|vcss|rvn|md|csproj|props|targets|txt|json|tsv|yml|yaml|py|sh|cmd|xml))?:(?<lines>\d+(?:\s*[-–,]\s*\d+)*)`",
        RegexOptions.Compiled
    );

    /// <summary>A backticked symbol immediately before the opening parenthesis a citation sits in.</summary>
    static readonly Regex BoundSymbol = new(
        @"(?<before>.{0,4})`(?<symbol>[A-Za-z_][\w.]*(?:<[^`]*>)?(?:\(\))?)`\s*\($",
        RegexOptions.Compiled
    );

    /// <summary>A table cell that is exactly a backticked symbol and then the citation: <c>| `Symbol` `File.cs:NNN` |</c>.</summary>
    static readonly Regex CellSymbol = new(@"\|\s*`(?<symbol>[A-Za-z_][\w.]*(?:\([^`]*\))?)`\s+$", RegexOptions.Compiled);

    /// <summary>Backticked code after an em dash, which is what the citation says is there.</summary>
    static readonly Regex BoundCode = new(@"^\s*—\s*`(?<code>[^`]+)`", RegexOptions.Compiled);

    sealed record Cited(string Document, int Line, string Text, string File, int[] Lines, bool Range, string? Symbol, string? Code);

    /// <summary>Every citation resolves to a file that has the cited lines.</summary>
    [Fact]
    public void Every_plan_citation_names_a_file_and_a_line_that_exist() {
        var (citations, index, exempt) = Sweep();
        List<string> failures = [];

        foreach (var cited in citations) {
            if (Resolve(cited, index) is { } problem && !exempt.ContainsKey((cited.Document, cited.Text))) {
                failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` — {problem}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} citation(s) in {PlanPath} name a file or line that is not there (#1356). Point each at "
            + $"what it means now, or — if it records what a removed file said — add it to {ExemptPath} with the commit:\n  "
            + string.Join("\n  ", failures)
        );

        Assert.True(citations.Count >= CitationFloor, $"the sweep found only {citations.Count} citation(s) in {PlanPath}");
    }

    /// <summary>A citation bound to a symbol points at a line where that symbol is.</summary>
    [Fact]
    public void Every_plan_citation_bound_to_a_symbol_points_at_it() {
        var (citations, index, exempt) = Sweep();
        List<string> failures = [];
        var bound = 0;

        foreach (var cited in citations.Where(cited => cited.Symbol is not null || cited.Code is not null)) {
            bound++;

            if (Resolve(cited, index) is not null || exempt.ContainsKey((cited.Document, cited.Text))) {
                continue;
            }

            if (!Candidates(cited, index).Any(path => Holds(cited, Lines(path)))) {
                var what = cited.Symbol is not null ? $"`{cited.Symbol}`" : $"`{cited.Code}`";
                var there = Candidates(cited, index)
                    .Where(path => cited.Lines[0] <= Lines(path).Length)
                    .Select(path => $"{path}:{cited.Lines[0]} is '{Lines(path)[cited.Lines[0] - 1].Trim()}'");
                failures.Add($"{cited.Document}:{cited.Line} cites {what} at `{cited.Text}`, and {string.Join("; ", there)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} citation(s) in {PlanPath} point at a line their own symbol is not on (#1356). Re-point "
            + "each by reading it — a citation to the wrong symbol is a prose error, not a number to move:\n  "
            + string.Join("\n  ", failures)
        );

        Assert.True(bound >= BoundFloor, $"only {bound} citation(s) in {PlanPath} were bound to a symbol");
    }

    /// <summary>The exemption list can only shrink: every entry is still cited and still fails.</summary>
    [Fact]
    public void Every_exempt_citation_is_still_cited_and_still_unresolvable() {
        var (citations, index, exempt) = Sweep();
        List<string> stale = [];

        foreach (var ((document, text), _) in exempt) {
            var matching = citations.Where(cited => cited.Document == document && cited.Text == text).ToList();

            if (matching.Count == 0) {
                stale.Add($"{document} `{text}` is no longer cited — delete the line");
            } else if (matching.All(cited => Resolve(cited, index) is null && (cited.Symbol is null && cited.Code is null
                                                                             || Candidates(cited, index).Any(path => Holds(cited, Lines(path)))))) {
                stale.Add($"{document} `{text}` resolves and holds now — delete the line");
            }
        }

        Assert.True(stale.Count == 0, $"{ExemptPath} has entries that no longer exempt anything:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>The instrument itself: known citations of each shape are read the way they are meant.</summary>
    [Fact]
    public void The_sweep_reads_the_known_shapes() {
        var (citations, _, _) = Sweep();

        // A bare continuation takes the file named before it on the same line.
        Assert.Contains(citations, cited => cited.Document.EndsWith("46-what-an-application-needs.md", StringComparison.Ordinal)
                                            && cited.Text == ":557" && cited.File.EndsWith("DockingHost.cs", StringComparison.Ordinal));

        // A .vxml citation is a citation.
        Assert.Contains(citations, cited => cited.File == "Shell.vxml" && cited.Lines.SequenceEqual([591]));

        // `Symbol` (`File:N`) binds the symbol; a list and a range are read as such.
        Assert.Contains(citations, cited => cited.Symbol == "UiElement.AccessKey" && cited.File == "UiElement.cs");
        Assert.Contains(citations, cited => cited.Range && cited.Lines.Length == 2);
        Assert.Contains(citations, cited => cited.Lines.Length > 2);

        // ⚠ And the negation is not bound: "`UiEvent` has no `Cancel` (`UiEvent.cs:…`)" cites where it is absent.
        Assert.DoesNotContain(citations, cited => cited.Symbol == "Cancel");
    }

    /// <summary>Why a citation does not resolve, or <see langword="null" /> when it does.</summary>
    static string? Resolve(Cited cited, Dictionary<string, List<string>> index) {
        var candidates = Candidates(cited, index);

        if (candidates.Count == 0) {
            return "no file in the tree is at or ends with that path";
        }

        if (candidates.Any(path => cited.Lines.All(line => line >= 1 && line <= Lines(path).Length))) {
            return null;
        }

        return "past the end of " + string.Join(", ", candidates.Select(path => $"{path} ({Lines(path).Length} lines)"));
    }

    /// <summary>Whether the bound symbol or code is on the cited line, in the cited range, or on a listed line.</summary>
    static bool Holds(Cited cited, string[] lines) {
        IEnumerable<string> cover = cited.Range
            ? lines[(cited.Lines[0] - 1)..Math.Min(cited.Lines[1], lines.Length)]
            : cited.Lines.Where(line => line <= lines.Length).Select(line => lines[line - 1]);

        if (cited.Symbol is { } symbol) {
            var name = Regex.Replace(symbol, @"<.*$|\(.*\)$", "").Split('.')[^1];

            return cover.Any(line => Regex.IsMatch(line, $@"\b{Regex.Escape(name)}\b"));
        }

        var code = Collapse(cited.Code!);

        return cover.Any(line => Collapse(line).Contains(code, StringComparison.Ordinal));
    }

    static string Collapse(string text) => Regex.Replace(text, @"\s+", "");

    static List<string> Candidates(Cited cited, Dictionary<string, List<string>> index) =>
        index.TryGetValue(Path.GetFileName(cited.File), out var paths)
            ? paths.Where(path => path == cited.File || path.EndsWith("/" + cited.File, StringComparison.Ordinal)).ToList()
            : [];

    static readonly Dictionary<string, string[]> LineCache = new(StringComparer.Ordinal);

    static string[] Lines(string relative) {
        lock (LineCache) {
            if (!LineCache.TryGetValue(relative, out var lines)) {
                // Line numbers are an editor's: \r\n and \n both end one, and a final newline starts none.
                var text = File.ReadAllText(Path.Combine(Root, relative));
                lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

                if (lines.Length > 0 && lines[^1].Length == 0) {
                    lines = lines[..^1];
                }

                LineCache[relative] = lines;
            }

            return lines;
        }
    }

    static (List<Cited> Citations, Dictionary<string, List<string>> Index, Dictionary<(string, string), string> Exempt) Sweep() {
        Dictionary<string, List<string>> index = new(StringComparer.Ordinal);
        Walk(Root, index);

        List<Cited> citations = [];

        foreach (var document in Directory.EnumerateFiles(Path.Combine(Root, PlanPath), "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
            var relative = Path.GetRelativePath(Root, document).Replace('\\', '/');
            var number = 0;

            foreach (var line in File.ReadLines(document)) {
                number++;
                string? file = null;

                foreach (Match match in Citation.Matches(line)) {
                    var named = match.Groups["file"];

                    if (named.Success) {
                        file = named.Value;
                    } else if (file is null) {
                        // A continuation with nothing before it on the line cites a file named on an
                        // earlier one, which is prose the reader resolves and a sweep cannot.
                        continue;
                    }

                    var spec = match.Groups["lines"].Value;
                    var lines = Regex.Matches(spec, @"\d+").Select(number => int.Parse(number.Value)).ToArray();
                    var range = Regex.IsMatch(spec, "[-–]");
                    var closes = match.Index + match.Length < line.Length && line[match.Index + match.Length] == ')';

                    string? symbol = null;
                    var rest = line[(match.Index + match.Length)..];

                    if (Regex.IsMatch(rest, @"^\s*\|") && CellSymbol.Match(line[..match.Index]) is { Success: true } cell) {
                        symbol = cell.Groups["symbol"].Value;
                    } else if (closes && BoundSymbol.Match(line[..match.Index]) is { Success: true } bound) {
                        var before = bound.Groups["before"].Value;

                        // ⚠ Not bound when it is one of a list or the object of a negation.
                        if (!Regex.IsMatch(before, @"(`,\s*|`\s+and\s+|`\s+or\s+|`\s*·\s*|\bno\s+)$")) {
                            symbol = bound.Groups["symbol"].Value;
                        }
                    }

                    var code = BoundCode.Match(line[(match.Index + match.Length)..]) is { Success: true } dash
                        ? dash.Groups["code"].Value
                        : null;

                    citations.Add(new(relative, number, match.Value.Trim('`'), file, lines, range, symbol, code));
                }
            }
        }

        Dictionary<(string, string), string> exempt = [];

        foreach (var line in File.ReadLines(Path.Combine(Root, ExemptPath))) {
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

            Assert.True(parts.Length == 3, $"{ExemptPath}: '{line}' is not 'document citation reason'");

            exempt[(parts[0], parts[1])] = parts[2];
        }

        return (citations, index, exempt);
    }

    static void Walk(string directory, Dictionary<string, List<string>> index) {
        foreach (var file in Directory.EnumerateFiles(directory)) {
            var name = Path.GetFileName(file);

            if (!index.TryGetValue(name, out var paths)) {
                index[name] = paths = [];
            }

            paths.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
        }

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (Array.IndexOf(Unwalked, Path.GetFileName(child)) < 0) {
                Walk(child, index);
            }
        }
    }

    /// <summary>The checkout this assembly was compiled in — the nearest root, never the outermost.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per parallel agent, so a walk that kept
    ///     going would leave a worktree's run asserting about the main tree's <c>docs/</c>.
    /// </remarks>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null) {
                if (Directory.Exists(Path.Combine(directory.FullName, "docs", "plan"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No {PlanPath} above {AppContext.BaseDirectory}. This test reads the repository it was "
                + "compiled in, so an output directory outside the checkout breaks it."
            );
        }
    }
}
