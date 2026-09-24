// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     Every <c>`File.cs:NNN`</c> citation in <c>docs/</c> and the module READMEs names a file that
///     exists and a line it has with something on it, and where the citation stands beside the symbol
///     it is evidence for, that symbol is on the cited line
///     (<a href="https://github.com/Rikarin/Vixen/issues/1356">#1356</a>,
///     <a href="https://github.com/Rikarin/Vixen/issues/1387">#1387</a>,
///     <a href="https://github.com/Rikarin/Vixen/issues/1388">#1388</a>).
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
///         file a later commit rewrote — is pinned to that commit, <c>`File.cs:NNN@1a2b3c4d5`</c>, and
///         checked there (<see cref="History" />); <see cref="ExemptPath" /> is the older form of the
///         same thing, and that list can only shrink: an entry whose citation now passes, or is gone,
///         fails.
///     </para>
///     <para>
///         ⚠ <b>Placement binds a symbol to a line, and a citation of what the code <i>says about</i>
///         the symbol is the shape it gets wrong.</b> Doc 46 wrote <c>`CodeBuffer` (`CodeBuffer.cs:49`)</c>
///         for the "No undo stack" remark, and line 49 is that remark — the evidence, correctly cited —
///         while the rule wanted the class declaration ten lines down. Moving the number to 59 would
///         have satisfied it and cited the wrong thing. The fix for that shape is the prose: say it is
///         the remarks being cited, so the parenthesis no longer follows the bare symbol.
///     </para>
///     <para>
///         ⚠ <b>Exact line, not a window, and the cost lands outside the edit's own closure.</b> The
///         exact rule is what catches <c>709</c> for <c>710</c>; a window of even two lines passes it.
///         The price is that one line inserted above a bound citation in a hot file — <c>UiElement.cs</c>,
///         <c>Commands.cs</c>, <c>Menus.cs</c>, <c>EditorParity.cs</c> — turns this project red, and
///         this project is not in those files' <c>ProjectReference</c> closure, so
///         <c>AffectedTests --since</c> never runs it. The whole-tree gate does, and the failure names the
///         document, the line and what is on it.
///     </para>
///     <para>
///         ⚠ <b>Unbound is most of it, and most of the drift.</b> Of 425 citations 75 bound. A
///         one-off measurement taken when the sweep was widened, by a scratch script that is not in
///         the tree — <c>git blame</c> for the commit that last wrote each citing line, then the
///         cited line at that commit beside the same line at HEAD — found 158 whose text had changed
///         since, 142 of them in plans. Nothing re-derives that number, and this class cannot: a
///         shallow CI clone has no history to blame. Read it as the size of the problem on the day
///         it was taken, not as a count anything keeps. A bound one fails here the day it moves and
///         an unbound one only when it lands on a blank or a brace
///         (<see cref="Every_cited_line_has_something_on_it" />), so a citation that should hold is
///         worth writing so it binds.
///     </para>
///     <para>
///         ⚠ <b>#1388 read every one of them</b>, with the same script widened to map each cited line
///         through <c>git diff</c> and to say whether the symbol beside it was on the line the day it
///         was written. Each was re-pointed where what it names is still there, pinned to the commit
///         it describes where it is not, and ⚠ eight turned out wrong <i>when written</i> — doc 49's
///         § 1.5 was three to five lines early at its own commit, doc 43's <c>LineWrapper.cs:779</c>
///         and doc 49's <c>MediaQuery.cs:146,151</c> cited a blank and a brace, doc 46's
///         <c>Strings.cs:56</c> the line above its field. Afterwards: 453 citations, 117 bound — the
///         bold and wrapped shapes below found five drifted citations on their first run — and 39
///         pinned. The 294 still unbound are held to a count per document that can only fall
///         (<see cref="Every_document_keeps_its_unbound_citations_to_the_recorded_count" />), so the
///         set whose drift nothing sees stops growing even where nobody re-reads it.
///     </para>
///     <para>
///         ⚠ <b>A bare <c>`:108`</c> continues the file named last on its line, and where a symbol of
///         another type stands between the two that is a guess, so it does not resolve</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1422">#1422</a>). Doc 50 wrote
///         <c>`EditorProject.cs:56`</c> and then <c>`EditorApplication.scene` (`:108` …)</c>, meaning
///         <c>EditorApplication.cs</c>, and the sweep read it as <c>EditorProject.cs:108</c> — a line
///         that was blank the day it was written, passed by resolution because the file is long enough.
///         The rule is deliberately blunt: <c>`Shell.Modes.Add` (`:475-476`)</c> after
///         <c>`TerrainModulePanels.cs:112-276`</c> meant that file and is refused anyway, because only
///         the prose knows whether a type is called in a file or declared in one. Naming the file costs
///         a few characters, and the three documents that tripped it the day it landed were rewritten so.
///     </para>
///     <para>
///         ⚠ <b>Both source languages.</b> Five of doc 49's six closed rows are closed by
///         <c>.vxml</c> citations alone, so a walker that indexed only <c>.cs</c> would call them
///         unresolvable and be wrong.
///     </para>
/// </remarks>
public class RealPlanCitationTests {
    /// <summary>The directory the sweep began with, relative to the checkout root, and what finds the checkout.</summary>
    const string PlanPath = "docs/plan";

    /// <summary>What the sweep reads, for the failure messages: every document in the tree that describes it.</summary>
    const string Swept = "docs/**/*.md and every README.md";

    /// <summary>Citations that record a file as it was, one per line: document, citation, reason.</summary>
    const string ExemptPath = "docs/PlanCitationExempt.txt";

    /// <summary>How many unbound, unpinned citations each document holds, one per line: count, document.</summary>
    const string UnboundPath = "docs/PlanCitationUnbound.txt";

    /// <summary>
    ///     How many citations the sweep has to find, and how many of those have to be bound to a
    ///     symbol, for a run to mean anything.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What this prints on the day the regex matches nothing is the question.</b> Every
    ///     assertion below is over a loop, so a pattern that parsed no row — a changed backtick, a
    ///     renamed directory — would pass three hundred citations without reading one. The sweep read
    ///     343 citations, 57 of them bound to a symbol, when this was written, and 425 with 75 bound
    ///     once it read every document rather than the plans alone (#1387, #1388) — read by raising
    ///     each floor out of reach and taking the number the failure printed; the floors sit well under
    ///     that and fail loudly on an empty read. ⚠ The total stays under what the plans alone hold
    ///     (350), so that it is <see cref="OutsidePlanFloor" /> and not this that names a sweep which
    ///     stopped reading the rest.
    /// </remarks>
    const int CitationFloor = 300;

    /// <inheritdoc cref="CitationFloor" />
    const int BoundFloor = 60;

    /// <summary>How many citations have to be pinned to a commit, so that the pin syntax is still being read.</summary>
    const int PinnedFloor = 20;

    /// <summary>
    ///     How many citations have to come from outside <c>docs/plan</c> — the overview, the guide, the
    ///     manual and the module READMEs (<a href="https://github.com/Rikarin/Vixen/issues/1387">#1387</a>).
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A floor of its own because the total cannot see this half go.</b> The plan documents
    ///     alone clear <see cref="CitationFloor" />, so a selection that stopped reading READMEs would
    ///     pass it with room to spare. 75 of the 425 were outside when the sweep was widened.
    /// </remarks>
    const int OutsidePlanFloor = 60;

    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a full checkout per agent: an index that walked it would
    ///     resolve a citation against somebody else's tree.
    ///     ⚠ <c>.nuke/</c> tracks only <c>parameters.json</c>, and its gitignored <c>temp/</c> is where the
    ///     build unpacks <c>Vixen.Sdk</c>'s package — README included. A checkout that has run
    ///     <c>./build.sh</c> therefore swept a stale copy of <c>Tools/Vixen.Sdk/README.md</c> as a
    ///     document of its own and went red on a line the real README had already re-pointed; a fresh
    ///     worktree never has it, so the branch was green and master was not.
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", ".nuke", "bin", "obj", "artifacts", "node_modules"];

    /// <summary>
    ///     Directories, relative to the checkout root, whose own files are walked and whose
    ///     subdirectories are not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>references/</c> holds gitignored clones of other engines beside its one tracked file,
    ///     the README that carries the clone commands (<c>.gitignore</c>, doc 02). A walk that entered a
    ///     clone would sweep third-party READMEs as documents and resolve citations against
    ///     third-party sources, so the test would go red, or pass a citation, on the one machine that
    ///     has the clones and never on CI. Rooted rather than matched by name, because
    ///     <c>Vixen.Graphics.Golden.Tests/References</c> is tracked and is ours.
    /// </remarks>
    static readonly string[] Shallow = ["references"];

    /// <summary>A backticked file citation: a path or file name, a colon, and lines.</summary>
    /// <remarks>
    ///     Lines are one number, a range (<c>38-49</c>) or a list (<c>321,368</c>). A bare
    ///     <c>`:557`</c> is a continuation and cites the file most recently named on the same line —
    ///     unless a symbol of another type stands between them (<see cref="TypedSymbol" />, #1422).
    ///     ⚠ One that opens a line, its file having been named on the line before, is not read: a
    ///     paragraph is wrapped wherever it falls, and binding across the wrap would guess.
    ///     <para>
    ///         ⚠ <b>A citation can be pinned to the commit it describes</b>, <c>`File.cs:NNN@1a2b3c4d5`</c>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1388">#1388</a>) — for a dated finding
    ///         about code a later commit rewrote, which is most of what a plan's audit sections cite. It
    ///         is checked against that commit rather than against HEAD (<see cref="History" />), so it
    ///         stays true for ever and says so on its face, where an unpinned number that "records the
    ///         tree at the time of writing" drifts under a green sweep and reads as evidence for code
    ///         that is no longer there.
    ///     </para>
    /// </remarks>
    static readonly Regex Citation = new(
        @"`(?:…/|\.\.\./|(?<root>\./))?(?<file>(?:[\w.-]+/)*[\w.-]+\.(?:cs|vxml|vcss|rvn|md|csproj|props|targets|txt|json|tsv|yml|yaml|py|sh|cmd|xml))?:(?<lines>\d+(?:\s*[-–,]\s*\d+)*)(?:@(?<commit>[0-9a-f]{7,40}))?`",
        RegexOptions.Compiled
    );

    /// <summary>A backticked symbol immediately before the opening parenthesis a citation sits in.</summary>
    /// <remarks>
    ///     ⚠ <b>A call written with its arguments binds too</b>, on the last dotted segment before the
    ///     parenthesis (<a href="https://github.com/Rikarin/Vixen/issues/1388">#1388</a>). It took
    ///     <c>()</c> and nothing else, so doc 49's <c>`Styles.Tree.SetAttribute(...)`
    ///     (`BuildContext.cs:711`)</c> bound nothing and stood 153 lines from its call under a green
    ///     sweep until a reviewer read it. ⚠ And a symbol in bold binds, <c>**`Defocus`** (`Focus.cs:343-391`)</c>:
    ///     a numbered finding opens with its subject in bold and then cites it, and that was the
    ///     commonest shape in doc 49 to bind nothing.
    /// </remarks>
    static readonly Regex BoundSymbol = new(
        @"(?<before>.{0,4})`(?<symbol>[A-Za-z_][\w.]*(?:<[^`]*>)?(?:\([^`]*\))?)`\**\s*\($",
        RegexOptions.Compiled
    );

    /// <summary>A table cell that is exactly a backticked symbol and then the citation: <c>| `Symbol` `File.cs:NNN` |</c>.</summary>
    static readonly Regex CellSymbol = new(@"\|\s*`(?<symbol>[A-Za-z_][\w.]*(?:\([^`]*\))?)`\s+$", RegexOptions.Compiled);

    /// <summary>Backticked code after an em dash, which is what the citation says is there.</summary>
    static readonly Regex BoundCode = new(@"^\s*—\s*`(?<code>[^`]+)`", RegexOptions.Compiled);

    /// <summary>A backticked dotted symbol, and the type it names: <c>`EditorApplication.scene`</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Dotted only, and the undotted form was tried and refused.</b> Doc 49's
    ///     <c>`TextField.cs:1067-1070`</c> … <c>`CodeEditor` tests `Control` only (`:1375`)</c> meant
    ///     CodeEditor.cs and was read as TextField.cs, which a bare <c>`CodeEditor`</c> would have
    ///     caught — but widened to every capitalised name, the rule found six more and every one was a
    ///     member of the named file (<c>`Scoped`</c>, <c>`Root`</c>, <c>`Focused`</c>,
    ///     <c>`LoadDisabledPlugins`</c>, <c>`StandardIcons`</c>, <c>`ITerrainScene`</c>), because a
    ///     member is capitalised too. Six false refusals for one true one is a rule people learn to
    ///     write around; that one is pinned and names its file now (#1388).
    /// </remarks>
    static readonly Regex TypedSymbol = new(@"`(?<symbol>(?<type>[A-Z]\w*)(?:<[^`]*>)?\.[A-Za-z_][^`]*)`", RegexOptions.Compiled);

    /// <summary>
    ///     One citation. <paramref name="Across" /> is the symbol naming another type that stands between a
    ///     bare continuation and the file it would continue, which makes the continuation a guess, and
    ///     <paramref name="Commit" /> the commit a pinned citation describes.
    /// </summary>
    sealed record Cited(
        string Document,
        int Line,
        string Text,
        string File,
        int[] Lines,
        bool Range,
        string? Symbol,
        string? Code,
        string? Across = null,
        string? Commit = null
    );

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
            $"{failures.Count} citation(s) in {Swept} name a file or line that is not there (#1356, #1387). Point each at "
            + $"what it means now, or — if it records what a removed file said — add it to {ExemptPath} with the commit:\n  "
            + string.Join("\n  ", failures)
        );

        Assert.True(citations.Count >= CitationFloor, $"the sweep found only {citations.Count} citation(s) in {Swept}");

        var outside = citations.Count(cited => !cited.Document.StartsWith(PlanPath + "/", StringComparison.Ordinal));
        Assert.True(
            outside >= OutsidePlanFloor,
            $"the sweep found only {outside} citation(s) outside {PlanPath}, so it has stopped reading the overview, the guide or the READMEs"
        );
    }

    /// <summary>A citation bound to a symbol points at a line where that symbol is.</summary>
    [Fact]
    public void Every_plan_citation_bound_to_a_symbol_points_at_it() {
        var (citations, index, exempt) = Sweep();
        List<string> failures = [];
        var bound = 0;

        foreach (var cited in citations.Where(cited => cited.Symbol is not null || cited.Code is not null)) {
            bound++;

            if (Resolve(cited, index) is not null || Unverifiable(cited) || exempt.ContainsKey((cited.Document, cited.Text))) {
                continue;
            }

            if (!Targets(cited, index).Any(target => Holds(cited, target.Lines))) {
                var what = cited.Symbol is not null ? $"`{cited.Symbol}`" : $"`{cited.Code}`";
                var there = Targets(cited, index)
                    .Where(target => cited.Lines[0] <= target.Lines.Length)
                    .Select(target => $"{target.Path}:{cited.Lines[0]} is '{target.Lines[cited.Lines[0] - 1].Trim()}'");
                failures.Add($"{cited.Document}:{cited.Line} cites {what} at `{cited.Text}`, and {string.Join("; ", there)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} citation(s) in {Swept} point at a line their own symbol is not on (#1356). Re-point "
            + "each by reading it — a citation to the wrong symbol is a prose error, not a number to move:\n  "
            + string.Join("\n  ", failures)
        );

        Assert.True(bound >= BoundFloor, $"only {bound} citation(s) in {Swept} were bound to a symbol");
    }

    /// <summary>A citation of a line cites a line with something on it, and not a brace or a blank.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one thing an unbound citation can be held to without guessing what it
    ///         meant</b> (<a href="https://github.com/Rikarin/Vixen/issues/1388">#1388</a>). Placement
    ///         needs a symbol and most citations have none within reach of the parser, so resolution
    ///         was all they got, and in a file of a thousand lines resolution almost never fails
    ///         however far the number drifts. But a number that drifted lands, often enough, on a line
    ///         that says nothing: the first run of this found the overview citing a closing brace for
    ///         <c>ReplicationServer.Acknowledge</c>'s soak caller, and the Ui README citing a blank
    ///         line for <c>UiDocument.Surfaces</c> and <c>) {</c> for <c>SurfaceOf</c> — all three
    ///         green under resolution.
    ///     </para>
    ///     <para>
    ///         A single line and every line of a list must carry a word; a range needs one, because a
    ///         range legitimately ends on the brace that closes what it spans. Nobody cites a blank line
    ///         or a lone <c>}</c> as evidence, so this has no false positive worth the name — and a
    ///         citation that means one on purpose goes in <see cref="ExemptPath" /> like any other.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_cited_line_has_something_on_it() {
        var (citations, index, exempt) = Sweep();
        List<string> failures = [];

        foreach (var cited in citations) {
            if (Resolve(cited, index) is not null || Unverifiable(cited) || exempt.ContainsKey((cited.Document, cited.Text))) {
                continue;
            }

            if (!Targets(cited, index).Any(target => Substantial(cited, target.Lines))) {
                var there = Targets(cited, index)
                    .Where(target => cited.Lines.All(line => line <= target.Lines.Length))
                    .Select(target => $"{target.Path}:{string.Join(",", cited.Lines)} is "
                                      + string.Join(" / ", cited.Lines.Select(line => $"'{target.Lines[line - 1].Trim()}'")));
                failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` — {string.Join("; ", there)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} citation(s) in {Swept} point at a blank line or a lone brace, which is what a number that "
            + "drifted lands on (#1388). Re-point each by reading it:\n  " + string.Join("\n  ", failures)
        );
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
            } else if (matching.All(cited => Resolve(cited, index) is null
                                             && Targets(cited, index).Any(target => Substantial(cited, target.Lines))
                                             && (cited.Symbol is null && cited.Code is null
                                                 || Targets(cited, index).Any(target => Holds(cited, target.Lines))))) {
                stale.Add($"{document} `{text}` resolves and holds now — delete the line");
            }
        }

        Assert.True(stale.Count == 0, $"{ExemptPath} has entries that no longer exempt anything:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    ///     Every <c>File.cs:NNN</c> citation in a source comment — <c>&lt;c&gt;…&lt;/c&gt;</c> in a doc
    ///     comment, backticks in a line comment — names a file that exists, one file, and a line it
    ///     has with something on it (<a href="https://github.com/Rikarin/Vixen/issues/1427">#1427</a>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing checked these at all.</b> <c>WorldRenderer.cs</c> cited
    ///         <c>UiApplication.cs:1012</c> for the call that loads the UI shaders — a <c>for</c> over
    ///         surfaces by then — and <c>PlatformCursorTests</c> cited <c>UiApplication.cs:497</c> and
    ///         <c>EditorHost.cs:296</c> for two <c>PlatformCursor.Apply</c> calls, an initialiser and a
    ///         <c>&lt;summary&gt;</c>. The first measurement found 18 of 40 changed under them.
    ///     </para>
    ///     <para>
    ///         Resolution, one file, and something on the line: the rules the documents get, less
    ///         placement, because a comment's prose binds a symbol far less regularly than a plan's. A
    ///         pin works here too. ⚠ Read the same way the documents are, a citation is its whole
    ///         token — <c>&lt;c&gt;File.cs:12&lt;/c&gt;</c> as an <i>example</i> of a format is in
    ///         <see cref="SourceExamples" /> rather than silently skipped.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_source_comment_citation_names_a_file_and_a_line_that_exist() {
        var (_, index, _) = Sweep();
        List<string> failures = [];
        var read = 0;

        foreach (var relative in index.Values.SelectMany(paths => paths).Order(StringComparer.Ordinal)) {
            if (!SourceExtensions.Contains(Path.GetExtension(relative)) || relative == SelfPath) {
                continue;
            }

            var number = 0;

            foreach (var line in File.ReadLines(Path.Combine(Root, relative))) {
                number++;

                foreach (Match match in SourceCitation.Matches(line)) {
                    var text = match.Groups["text"].Value;

                    if (SourceExamples.Contains((relative, text))) {
                        continue;
                    }

                    read++;
                    var file = (match.Groups["root"].Success ? "./" : "") + match.Groups["file"].Value;
                    var lines = Regex.Matches(match.Groups["lines"].Value, @"\d+").Select(digits => int.Parse(digits.Value)).ToArray();
                    var commit = match.Groups["commit"] is { Success: true } pin ? pin.Value : null;
                    Cited cited = new(relative, number, text, file, lines, Regex.IsMatch(match.Groups["lines"].Value, "[-–]"), null, null, null, commit);

                    if (Resolve(cited, index) is { } problem) {
                        failures.Add($"{relative}:{number} `{text}` — {problem}");
                    } else if (!Unverifiable(cited) && !Targets(cited, index).Any(target => Substantial(cited, target.Lines))) {
                        failures.Add($"{relative}:{number} `{text}` — a blank line or a lone brace");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} citation(s) in source comments name a file or a line that is not there, or a blank (#1427). "
            + "Re-point each by reading it, or pin it to the commit it describes (`File.cs:N@sha`):\n  " + string.Join("\n  ", failures)
        );

        // The instrument: 40 when this was written, from 25 files.
        Assert.True(read >= 30, $"the source sweep read only {read} citation(s), so it has stopped reading comments");
    }

    /// <summary>The source languages a comment citation is looked for in.</summary>
    static readonly HashSet<string> SourceExtensions = [".cs", ".vxml", ".rvn", ".vcss"];

    /// <summary>This file, whose remarks and cases quote citations as they were written wrong on purpose.</summary>
    const string SelfPath = "Tools/Vixen.DocGen.Tests/RealPlanCitationTests.cs";

    /// <summary>A citation in a source comment: in a doc comment's <c>&lt;c&gt;</c>, or backticked in a line comment.</summary>
    static readonly Regex SourceCitation = new(
        @"(?:`|<c>)(?<text>(?:(?<root>\./))?(?<file>(?:[\w.-]+/)*[\w.-]+\.(?:cs|vxml|vcss|rvn|md|csproj|props|targets)):(?<lines>\d+(?:\s*[-–,]\s*\d+)*)(?:@(?<commit>[0-9a-f]{7,40}))?)(?:`|</c>)",
        RegexOptions.Compiled
    );

    /// <summary>Source comments that show the format with a file that is not meant to exist.</summary>
    static readonly HashSet<(string, string)> SourceExamples = [
        // `Effect(…)`'s origin parameter, documented by the shape a caller formats it in.
        ("Core/Vixen.Ui/Diagnostics.cs", "File.cs:12")
    ];

    /// <summary>
    ///     Every document holds exactly as many unbound, unpinned citations as <see cref="UnboundPath" />
    ///     records for it, so the set whose drift nothing can see only ever shrinks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The class #1388 is about, made a ratchet rather than a census.</b> An unbound
    ///         citation that drifts goes red only on a blank or a brace; a bound or pinned one goes red
    ///         the day it is wrong. So a new citation has to bind or pin, or the document's count has
    ///         to be raised in the same diff — where a reviewer sees it — and a citation that stops
    ///         being unbound has to lower it, exactly as the exemption lists here fail on an entry that
    ///         has become clean.
    ///     </para>
    ///     <para>
    ///         A count per document rather than a line per citation, on purpose: re-pointing an
    ///         unbound citation by reading it is the work this should encourage, and a list keyed by the
    ///         citation's text would make every such fix an edit to a three-hundred-line file too.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_document_keeps_its_unbound_citations_to_the_recorded_count() {
        var (citations, _, exempt) = Sweep();

        var counted = citations
            .Where(cited => cited.Symbol is null && cited.Code is null && cited.Commit is null)
            .Where(cited => !exempt.ContainsKey((cited.Document, cited.Text)))
            .GroupBy(cited => cited.Document)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Dictionary<string, int> recorded = new(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(Path.Combine(Root, UnboundPath))) {
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

            Assert.True(parts.Length == 2 && int.TryParse(parts[0], out _), $"{UnboundPath}: '{line}' is not 'count document'");

            recorded[parts[1]] = int.Parse(parts[0]);
        }

        var wrong = counted.Keys.Union(recorded.Keys)
            .Order(StringComparer.Ordinal)
            .Select(document => (document, now: counted.GetValueOrDefault(document), was: recorded.GetValueOrDefault(document)))
            .Where(entry => entry.now != entry.was)
            .Select(entry => entry.now > entry.was
                ? $"{entry.document} has {entry.now} unbound citation(s) and {UnboundPath} allows {entry.was} — bind the new one "
                  + "(`Symbol` (`File.cs:N`), or `File.cs:N` — `code`), pin it to a commit (`File.cs:N@sha`), or raise the count"
                : $"{entry.document} has {entry.now} unbound citation(s) and {UnboundPath} records {entry.was} — lower it to {entry.now}")
            .ToList();

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} document(s) disagree with {UnboundPath}, whose counts can only fall (#1388):\n  " + string.Join("\n  ", wrong)
        );
    }

    /// <summary>
    ///     A pinned citation names a commit this checkout has, wherever the checkout has its history.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The other facts skip a pin whose commit is missing</b>, because CI checks out one commit
    ///     and has nothing to check it against. Without this a mistyped hash would be skipped
    ///     everywhere, for ever — a citation that reads as pinned and is checked by nothing. So a full
    ///     clone, the only place one is written, refuses it, and a shallow one says it cannot tell.
    /// </remarks>
    [Fact]
    public void Every_pinned_citation_names_a_commit_this_checkout_has() {
        var (citations, _, _) = Sweep();
        var pinned = citations.Where(cited => cited.Commit is not null).ToList();

        Assert.True(pinned.Count >= PinnedFloor, $"the sweep found only {pinned.Count} pinned citation(s) in {Swept}");

        if (History.IsShallow) {
            return;
        }

        var missing = pinned.Where(cited => !History.Has(cited.Commit!))
            .Select(cited => $"{cited.Document}:{cited.Line} `{cited.Text}`")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} pinned citation(s) name a commit this full clone does not have, so nothing anywhere checks them:\n  "
            + string.Join("\n  ", missing)
        );
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

    /// <summary>
    ///     A bare continuation with a symbol of another type between it and the file it would continue
    ///     does not resolve, and one without does (<a href="https://github.com/Rikarin/Vixen/issues/1422">#1422</a>).
    /// </summary>
    /// <remarks>
    ///     Doc 50's row as it stood before <c>1c14dab07</c>: the author meant
    ///     <c>EditorApplication.cs:108</c>, the sweep read <c>EditorProject.cs:108</c> — a blank line
    ///     that day, passed by resolution because the file is long enough. The real documents cannot
    ///     carry the shape any more, which is why the old wording is fed in here rather than found.
    /// </remarks>
    [Fact]
    public void A_continuation_across_another_type_is_a_guess() {
        const string Doc50 =
            "| **the active scene** | `EditorProject.ActiveDocument` (`EditorProject.cs:56`) | `EditorApplication.scene` "
            + "(`:108` — *\"half the editor holds the active scene\"*), plus `Shown => inspected ?? scene` |";

        var (_, index, _) = Sweep();
        var guessed = Parse("docs/plan/50-the-editor-as-bounded-contexts.md", 274, Doc50).Single(cited => cited.Text == ":108");

        Assert.Equal("EditorProject.cs", guessed.File);
        Assert.Equal("EditorApplication.scene", guessed.Across);
        Assert.Contains("#1422", Resolve(guessed, index));

        // ⚠ And the shape the continuation exists for still binds: doc 46's two `DockingHost.cs` lines.
        const string Doc46 = "| `\"Previous tab\"` · `\"Next tab\"` | `Vixen.Ui.Controls.Advanced/DockingHost.cs:548`, `:557` |";
        var continued = Parse("docs/plan/46-what-an-application-needs.md", 590, Doc46).Single(cited => cited.Text == ":557");

        Assert.Null(continued.Across);
        Assert.Null(Resolve(continued, index));

        // A symbol of the file's own type is no guess: `Menu.cs:1` … `Menu.Open` (`:2`).
        Assert.Null(Parse("x.md", 1, "`Menu.cs:1` and `Menu.Open` (`:2`)").Last().Across);
    }

    /// <summary>
    ///     The shapes #1388 taught the sweep, each fed in as the documents wrote it: bold and wrapped
    ///     symbols bind, a pin reads its commit, a bare name several files share is a guess, and a
    ///     root file can still be named.
    /// </summary>
    [Fact]
    public void The_sweep_reads_the_shapes_it_was_taught_for_1388() {
        var (_, index, _) = Sweep();

        // A numbered finding with its subject in bold binds, and before this it bound nothing.
        Assert.Equal("Defocus", Parse("x.md", 1, "5. **`Defocus`** (`Focus.cs:343-391`): a press that lands").Single().Symbol);

        // A symbol ending one line binds the parenthesis opening the next.
        var wrapped = Parse("x.md", 2, "   (`UiDocument.cs:1414`); a button's", "   `Tick` is what calls it, `RaiseCommandsInvalidated()`").Single();
        Assert.Equal("RaiseCommandsInvalidated()", wrapped.Symbol);

        // A pin is read, and a continuation after a pinned file continues the pin.
        var pinned = Parse("x.md", 3, "nothing else (`Focus.cs:91-93@10523d70f`), writes `Focused` at `:100`, and").ToList();
        Assert.All(pinned, cited => Assert.Equal("10523d70f", cited.Commit));
        Assert.Equal(["Focus.cs:91-93@10523d70f", ":100"], pinned.Select(cited => cited.Text));

        // ⚠ A bare name several files share is a guess, which is what doc 48's `README.md:577` was.
        Assert.Contains("files end with", Resolve(Parse("x.md", 4, "— `README.md:577`, and").Single(), index));
        Assert.Contains("files end with", Resolve(Parse("x.md", 5, "`Directory.Build.props:69` sets it").Single(), index));

        // And the root file, which has no longer suffix to write, is named from the root.
        var root = Parse("x.md", 6, "`./Directory.Build.props:69` sets it").Single();
        Assert.Equal("./Directory.Build.props", root.File);
        Assert.Null(Resolve(root, index));
    }

    /// <summary>A pin is checked against its commit and not against HEAD, in both directions.</summary>
    /// <remarks>
    ///     Doc 49's § 1.5 as the audit had it: at <c>10523d70f</c> line 90 opened <c>Focus</c> and line 91
    ///     was its <c>Focusable</c> gate. At HEAD neither line is either, so a pin read against HEAD would
    ///     fail the first and a pin not read at all would pass the second.
    /// </remarks>
    [Fact]
    public void A_pinned_citation_is_held_to_its_commit() {
        if (!History.Has("10523d70f")) {
            Assert.Skip("this checkout has no history to hold a pin to (a shallow clone)");
        }

        var (_, index, _) = Sweep();
        var right = Parse("x.md", 1, "`Focus` (`Focus.cs:90@10523d70f`)").Single();
        var wrong = Parse("x.md", 2, "`Focus` (`Focus.cs:91@10523d70f`)").Single();

        Assert.Null(Resolve(right, index));
        Assert.Contains(Targets(right, index), target => Holds(right, target.Lines));
        Assert.DoesNotContain(Targets(right, index), target => Holds(right, Lines(target.Path)));
        Assert.DoesNotContain(Targets(wrong, index), target => Holds(wrong, target.Lines));
    }

    /// <summary>
    ///     The walk reads <c>references/README.md</c> and nothing a reference clone brings with it, and
    ///     still enters a directory that is only <i>named</i> like it.
    /// </summary>
    /// <remarks>
    ///     A scratch tree rather than the checkout, because the clones are gitignored and neither CI
    ///     nor most machines have one: asked of the real tree, "no clone is swept" is a predicate that
    ///     cannot be false there.
    /// </remarks>
    [Fact]
    public void The_walk_stops_at_the_reference_clones() {
        var root = Directory.CreateTempSubdirectory("vixen-citation-walk-").FullName;

        try {
            string[] tree = [
                "references/README.md",
                "references/godot/README.md",
                "references/godot/core/Node.cs",
                "docs/plan/01.md",
                "Platform/Golden.Tests/References/README.md"
            ];

            foreach (var relative in tree) {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
            }

            Dictionary<string, List<string>> index = new(StringComparer.Ordinal);
            Walk(root, root, index);
            var walked = index.Values.SelectMany(paths => paths).Order(StringComparer.Ordinal).ToArray();

            string[] expected = ["Platform/Golden.Tests/References/README.md", "docs/plan/01.md", "references/README.md"];

            Assert.Equal(expected, walked);
            Assert.Equal(expected, Documents(index));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Why a citation does not resolve, or <see langword="null" /> when it does.</summary>
    static string? Resolve(Cited cited, Dictionary<string, List<string>> index) {
        if (cited.Across is { } across) {
            return $"a bare continuation after `{across}`, which names a type other than {cited.File}, so which file it "
                   + "continues is a guess — name the file (#1422)";
        }

        // A pinned citation this checkout has no history for — a shallow clone — is not checked here;
        // Every_pinned_citation_names_a_commit_this_checkout_has is what holds a full one to it.
        if (Unverifiable(cited)) {
            return null;
        }

        var targets = Targets(cited, index);
        var at = cited.Commit is { } commit ? $" at {commit}" : "";

        if (targets.Count == 0) {
            return $"no file in the tree{at} is at or ends with that path";
        }

        // ⚠ A bare name that several files share is resolved against whichever has the line, which
        // is a guess: doc 48's `README.md:577` meant Raven's and would have passed on any README long
        // enough, and there are over forty.
        if (targets.Count > 1) {
            return $"{targets.Count} files{at} end with {cited.File} ({string.Join(", ", targets.Select(target => target.Path))}), "
                   + "so which one it cites is a guess — give enough of the path to name one";
        }

        if (targets.Any(target => cited.Lines.All(line => line >= 1 && line <= target.Lines.Length))) {
            return null;
        }

        return "past the end of " + string.Join(", ", targets.Select(target => $"{target.Path}{at} ({target.Lines.Length} lines)"));
    }

    /// <summary>A pinned citation whose commit this checkout does not have, so nothing can be said about it.</summary>
    static bool Unverifiable(Cited cited) => cited.Commit is { } commit && !History.Has(commit);

    /// <summary>The files a citation can mean, with their lines: at HEAD, or at the commit it is pinned to.</summary>
    static List<(string Path, string[] Lines)> Targets(Cited cited, Dictionary<string, List<string>> index) {
        if (cited.Commit is not { } commit) {
            return Candidates(cited, index).Select(path => (path, Lines(path))).ToList();
        }

        return History.Paths(commit)
            .Where(path => Names(path, cited.File))
            .Select(path => (path, History.Lines(commit, path)))
            .Where(target => target.Item2 is not null)
            .Select(target => (target.path, target.Item2!))
            .ToList();
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

    /// <summary>Whether the cited lines say anything: every one of a line or a list, and one of a range.</summary>
    static bool Substantial(Cited cited, string[] lines) {
        static bool Worded(string line) => Regex.IsMatch(line, @"\w");

        // A candidate too short for the citation says nothing; resolution has already found one that is not.
        if (!cited.Lines.All(line => line >= 1 && line <= lines.Length)) {
            return false;
        }

        return cited.Range
            ? lines[(cited.Lines[0] - 1)..Math.Min(cited.Lines[1], lines.Length)].Any(Worded)
            : cited.Lines.All(line => Worded(lines[line - 1]));
    }

    static string Collapse(string text) => Regex.Replace(text, @"\s+", "");

    /// <summary>
    ///     Whether a path is what a citation names: the path itself, any path ending with it, or — for
    ///     one written <c>./Directory.Build.props</c> — only the file at the checkout root.
    /// </summary>
    /// <remarks>
    ///     ⚠ The root form exists because a root file cannot otherwise be named uniquely: every
    ///     <c>Directory.Build.props</c> ends with <c>Directory.Build.props</c>, and there is no longer
    ///     suffix of the root one to write.
    /// </remarks>
    static bool Names(string path, string file) =>
        file.StartsWith("./", StringComparison.Ordinal)
            ? path == file[2..]
            : path == file || path.EndsWith("/" + file, StringComparison.Ordinal);

    static List<string> Candidates(Cited cited, Dictionary<string, List<string>> index) =>
        index.TryGetValue(Path.GetFileName(cited.File), out var paths)
            ? paths.Where(path => Names(path, cited.File)).ToList()
            : [];

    static readonly Dictionary<string, string[]> LineCache = new(StringComparer.Ordinal);

    static string[] Lines(string relative) {
        lock (LineCache) {
            if (!LineCache.TryGetValue(relative, out var lines)) {
                LineCache[relative] = lines = Split(File.ReadAllText(Path.Combine(Root, relative)));
            }

            return lines;
        }
    }

    /// <summary>Line numbers are an editor's: <c>\r\n</c> and <c>\n</c> both end one, and a final newline starts none.</summary>
    static string[] Split(string text) {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>What a pinned citation is checked against: the repository's own history, read through <c>git</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Absent history is not a failure here, and is one in the fact that asks.</b> CI checks out
    ///     one commit, so a pinned citation has nothing to be checked against there and is skipped; a
    ///     full clone has everything, and
    ///     <see cref="Every_pinned_citation_names_a_commit_this_checkout_has" /> fails a pin whose
    ///     commit it lacks — which is what catches a mistyped hash on the machine that wrote it.
    /// </remarks>
    static class History {
        static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);

        /// <summary>Whether this checkout has the commit.</summary>
        public static bool Has(string commit) => Git("cat-file", "-e", commit + "^{commit}") is not null;

        /// <summary>Whether this checkout is a shallow clone, which has only the history it was given.</summary>
        public static bool IsShallow => Git("rev-parse", "--is-shallow-repository")?.Trim() != "false";

        /// <summary>Every file at the commit.</summary>
        public static string[] Paths(string commit) => Split(Git("ls-tree", "-r", "--name-only", commit) ?? "");

        /// <summary>A file's lines at the commit, or <see langword="null" /> if it did not exist.</summary>
        public static string[]? Lines(string commit, string path) => Git("show", $"{commit}:{path}") is { } text ? Split(text) : null;

        /// <summary>What git printed, or <see langword="null" /> when it failed or is not installed.</summary>
        static string? Git(params string[] arguments) {
            var key = string.Join('\0', arguments);

            lock (Cache) {
                if (Cache.TryGetValue(key, out var cached)) {
                    return cached;
                }

                string? output = null;

                try {
                    var start = new System.Diagnostics.ProcessStartInfo("git") {
                        WorkingDirectory = Root,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };

                    foreach (var argument in arguments) {
                        start.ArgumentList.Add(argument);
                    }

                    using var process = System.Diagnostics.Process.Start(start)!;
                    var read = process.StandardOutput.ReadToEndAsync();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    output = process.ExitCode == 0 ? read.Result : null;
                } catch (System.ComponentModel.Win32Exception) {
                    // No git on this machine: every pin is unverifiable, which is the shallow case.
                }

                return Cache[key] = output;
            }
        }
    }

    static (List<Cited> Citations, Dictionary<string, List<string>> Index, Dictionary<(string, string), string> Exempt) Sweep() {
        Dictionary<string, List<string>> index = new(StringComparer.Ordinal);
        Walk(Root, Root, index);

        List<Cited> citations = [];

        foreach (var relative in Documents(index)) {
            var document = Path.Combine(Root, relative);
            var number = 0;
            string? previous = null;

            foreach (var line in File.ReadLines(document)) {
                citations.AddRange(Parse(relative, ++number, line, previous));
                previous = line;
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

    /// <summary>The citations on one line of a document, each with what it is bound to.</summary>
    /// <param name="relative">The document.</param>
    /// <param name="number">The line's number in it.</param>
    /// <param name="line">The line.</param>
    /// <param name="previous">The line before it, which a citation opening this one may be bound across.</param>
    static IEnumerable<Cited> Parse(string relative, int number, string line, string? previous = null) {
        string? file = null;
        string? pinned = null;
        var namedEnd = 0;

        foreach (Match match in Citation.Matches(line)) {
            var named = match.Groups["file"];
            string? across = null;

            if (named.Success) {
                file = match.Groups["root"].Success ? "./" + named.Value : named.Value;
                pinned = match.Groups["commit"] is { Success: true } own ? own.Value : null;
                namedEnd = match.Index + match.Length;
            } else if (file is null) {
                // A continuation with nothing before it on the line cites a file named on an
                // earlier one, which is prose the reader resolves and a sweep cannot.
                continue;
            } else {
                // ⚠ And one with a symbol of another type between it and that file is a guess too
                // (#1422): `EditorProject.cs:56` … `EditorApplication.scene` (`:108`) meant
                // EditorApplication.cs and was checked against EditorProject.cs.
                var stem = Path.GetFileNameWithoutExtension(file);
                across = TypedSymbol.Matches(line[namedEnd..match.Index])
                    .Where(symbol => symbol.Groups["type"].Value != stem)
                    .Select(symbol => symbol.Groups["symbol"].Value)
                    .FirstOrDefault();
            }

            var spec = match.Groups["lines"].Value;
            var lines = Regex.Matches(spec, @"\d+").Select(digits => int.Parse(digits.Value)).ToArray();
            var range = Regex.IsMatch(spec, "[-–]");
            var closes = match.Index + match.Length < line.Length && line[match.Index + match.Length] == ')';

            string? symbol = null;
            var rest = line[(match.Index + match.Length)..];
            var head = line[..match.Index];

            // ⚠ A symbol that ends the line before and a parenthesis that opens this one are the same
            // shape as on one line, split by the wrap: `Tick` is what calls it,\n`RaiseCommandsInvalidated()`
            // (`UiDocument.cs:1414`). Read line by line it bound nothing, so it drifted invisibly.
            if (previous is not null && head.Trim() == "(") {
                head = previous.TrimEnd() + " (";
            }

            if (Regex.IsMatch(rest, @"^\s*\|") && CellSymbol.Match(line[..match.Index]) is { Success: true } cell) {
                symbol = cell.Groups["symbol"].Value;
            } else if (closes && BoundSymbol.Match(head) is { Success: true } bound) {
                var before = bound.Groups["before"].Value;

                // ⚠ Not bound when it is one of a list or the object of a negation.
                if (!Regex.IsMatch(before, @"(`,\s*|`\s+and\s+|`\s+or\s+|`\s*·\s*|\bno\s+)$")) {
                    symbol = bound.Groups["symbol"].Value;
                }
            }

            var code = BoundCode.Match(rest) is { Success: true } dash ? dash.Groups["code"].Value : null;
            var commit = match.Groups["commit"] is { Success: true } pin ? pin.Value : pinned;

            yield return new(relative, number, match.Value.Trim('`'), file, lines, range, symbol, code, across, commit);
        }
    }

    /// <summary>
    ///     The documents swept: every Markdown file under <c>docs/</c> and every <c>README.md</c> in the
    ///     tree, in path order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sweep began at <c>docs/plan</c> and the documents trusted most were outside it</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1387">#1387</a>). CLAUDE.md makes
    ///         <c>docs/overview.md</c> the state, winning over any plan document, and the module
    ///         READMEs the reasoning for each subsystem, so a citation there is read by the people who
    ///         trust it most and was checked by nothing.
    ///     </para>
    ///     <para>
    ///         Taken from the walk's own index rather than a second walk, so a directory the index
    ///         refuses — somebody else's worktree under <c>.claude/</c>, build output — is refused here
    ///         for the same reason. <c>CHANGELOG.md</c> and the analyzers' release notes are history on
    ///         purpose and are not swept.
    ///     </para>
    /// </remarks>
    static IEnumerable<string> Documents(Dictionary<string, List<string>> index) =>
        index.Values
            .SelectMany(paths => paths)
            .Where(path => path.EndsWith(".md", StringComparison.Ordinal)
                           && (path.StartsWith("docs/", StringComparison.Ordinal) || Path.GetFileName(path) == "README.md"))
            .Order(StringComparer.Ordinal);

    static void Walk(string root, string directory, Dictionary<string, List<string>> index) {
        foreach (var file in Directory.EnumerateFiles(directory)) {
            var name = Path.GetFileName(file);

            if (!index.TryGetValue(name, out var paths)) {
                index[name] = paths = [];
            }

            paths.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        if (Array.IndexOf(Shallow, Path.GetRelativePath(root, directory).Replace('\\', '/')) >= 0) {
            return;
        }

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (Array.IndexOf(Unwalked, Path.GetFileName(child)) < 0) {
                Walk(root, child, index);
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
