// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Testing;
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
///         pinned. The 294 still unbound are held to a count per document that cannot move silently
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
    const string Swept = "docs/**/*.md, every README.md and CONTRIBUTING.md";

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

    /// <summary>A backticked file citation: a path or file name, a colon, and lines.</summary>
    /// <remarks>
    ///     Lines are one number, a range (<c>38-49</c>) or a list (<c>321,368</c>). A bare
    ///     <c>`:557`</c> is a continuation and cites the file most recently named in its paragraph — by
    ///     a citation or by a bare <c>`File.cs`</c> (<see cref="NamedFile" />) — unless a symbol of
    ///     another type stands between them (<see cref="TypedSymbol" />, #1422).
    ///     ⚠ <b>One that opens a line was not read at all</b>, on the reasoning that a paragraph wraps
    ///     wherever it falls and binding across the wrap would guess
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1455">#1455</a>). But the wrap is not where
    ///     the guess is — the paragraph is the prose's own scope, and the other-type rule reads across
    ///     the wrap as well as it reads along a line — and the skip left a class nothing checked, a
    ///     continuation of a pin among it. So the file carries to the end of the paragraph
    ///     (<see cref="Opens" />), and a continuation with no file before it there is refused rather
    ///     than skipped.
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
    /// <remarks>
    ///     ⚠ <b><paramref name="Lines" /> is every number the citation wrote, and
    ///     <see cref="Spans" /> is what they mean</b>
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1495">#1495</a>). Doc 50 wrote
    ///     <c>`EditorApplication.cs:792-832, 2966-2973`</c> — two ranges — and the sweep read it as one
    ///     range, 792 to 832, with two numbers nobody checked; the second range was never read at all.
    ///     <paramref name="Column" /> is where on its line the citation starts, so the prose around it
    ///     can be found again.
    /// </remarks>
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
        string? Commit = null,
        int Column = -1
    ) {
        /// <summary>Each single line or range the citation names, in order: <c>792-832, 2966-2973</c> is two.</summary>
        public (int From, int To)[] Spans {
            get {
                var spec = Text[(Text.LastIndexOf(':') + 1)..];
                spec = spec.Contains('@') ? spec[..spec.IndexOf('@')] : spec;

                return spec.Split(',')
                    .Select(part => Regex.Matches(part, @"\d+").Select(digits => int.Parse(digits.Value)).ToArray())
                    .Where(numbers => numbers.Length > 0)
                    .Select(numbers => (numbers[0], numbers[^1]))
                    .ToArray();
            }
        }
    }

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
    ///         A single line and every line of a list must carry a word; a range must open on something
    ///         other than a blank or a closing brace and close on something other than a blank, because
    ///         a range legitimately ends on the brace that closes what it spans. ⚠ It used to need only
    ///         one worded line anywhere in it, which every range in a long file has — doc 50's
    ///         <c>`EditorApplication.cs:792-832`</c> ended on a blank and passed, and the ends rule found
    ///         twenty-one more on its first run (#1495). Nobody cites a blank line or a lone <c>}</c> as
    ///         evidence, so this has no false positive worth the name — and a citation that means one
    ///         on purpose goes in <see cref="ExemptPath" /> like any other.
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

    /// <summary>
    ///     An unbound range uses, somewhere in it, a name the prose it is evidence for uses
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1495">#1495</a>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A range was the one form nothing could make false.</b> Resolution passes any range
    ///         inside the file, and "something on it" passed any range with a word in it, which in a
    ///         four-thousand-line file is every range. Doc 50 § F9 cited
    ///         <c>`EditorApplication.cs:792-832, 2966-2973`</c> for <c>is StandardFrameDocument</c>,
    ///         <c>is ShaderGraphDocument</c>, <c>is MaterialView</c> and <c>is TextureImportView</c>
    ///         — a <c>FileSystemWatcher</c> remark and the inspector factory's <c>&lt;para&gt;</c> — and
    ///         <c>`:1076-1094`</c> for three <c>I…Scene</c> properties over <c>Extensions</c>' summary,
    ///         and the sweep passed all four.
    ///     </para>
    ///     <para>
    ///         The source comments' rule (<see cref="Misplaced" />, #1454), with the document's own
    ///         prose for the comment: some span of the citation has to use, as a whole word, a name of
    ///         the clause the citation closes — the text since the citation before it in its paragraph
    ///         or table cell (<see cref="ProseAround" />) — or carry one of its backticked fragments
    ///         whole. A range whose clause names nothing in code cannot be held to this and is
    ///         skipped rather than guessed at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The clause before the citation and not the paragraph</b>, because a table row in
    ///         doc 50 names forty types and a range of a hundred lines in the editor's biggest file
    ///         uses one of them by accident. Read that wide, the rule passed § F9's four.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_cited_range_uses_a_name_its_prose_uses() {
        var (citations, index, exempt) = Sweep();
        List<string> failures = [];
        var held = 0;

        foreach (var cited in citations) {
            // Code only: a range of a guide page or a README is prose citing prose, and shares words
            // with its clause rather than names — doc 49's `UiApplication`'s frame loop is a paragraph
            // of the desktop guide that says "the loop".
            if (cited.Symbol is not null || cited.Code is not null || cited.Spans.All(span => span.From == span.To)
                || !SourceExtensions.Contains(Path.GetExtension(cited.File))) {
                continue;
            }

            if (Resolve(cited, index) is not null || Unverifiable(cited) || exempt.ContainsKey((cited.Document, cited.Text))) {
                continue;
            }

            // ⚠ Not where the clause cites an absence: "`UiEvent` has no `Cancel` (`UiEvent.cs:46-58`)"
            // cites the lines the name is missing from — the negation BoundSymbol refuses to bind.
            if (Regex.IsMatch(ProseAround(cited), @"\bno\s+`[^`]+`[^`(]*\(\s*$")) {
                continue;
            }

            if (Unnamed(cited, index) is { } problem) {
                failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` {problem}");
            } else if (ProseNames(cited).Count > 0 || ProseCode(cited).Count > 0) {
                held++;
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} range citation(s) in {Swept} span no line that uses a name their own clause uses, which is what a "
            + "range that drifted onto other code looks like (#1495). Re-point each by reading it, or pin it to the commit it "
            + "describes (`File.cs:N-M@sha`):\n  " + string.Join("\n  ", failures)
        );

        // The instrument: a rule that holds nothing is not reading ranges. 50 when this was written,
        // read by raising the floor out of reach.
        Assert.True(held >= 40, $"only {held} range citation(s) were held to their prose, so the rule has stopped reading");
    }

    /// <summary>
    ///     Why a range citation spans nothing its clause names, or <see langword="null" /> when some
    ///     line of every span does — or when the clause names nothing in code to look for.
    /// </summary>
    static string? Unnamed(Cited cited, Dictionary<string, List<string>> index) {
        if (!SourceExtensions.Contains(Path.GetExtension(cited.File))) {
            return null;
        }

        var names = ProseNames(cited);
        var fragments = ProseCode(cited);

        if (names.Count == 0 && fragments.Count == 0) {
            return null;
        }

        bool Named(string line) => Regex.Matches(line, @"[A-Za-z_][A-Za-z0-9_]*").Any(word => names.Contains(word.Value));

        // A clause's code may be all keywords — `default: return false` — and still be what the
        // span says, across its lines.
        bool Quoted(IEnumerable<string> span) {
            var text = Collapse(string.Concat(span));

            return fragments.Any(fragment => text.Contains(fragment, StringComparison.Ordinal));
        }

        // ⚠ Any span rather than every one: a list names its places, and the clause names what
        // they are about once. A range that drifted onto other code shares nothing with it.
        if (Targets(cited, index).Any(target => cited.Spans.Any(span => span.To <= target.Lines.Length
                                                                        && (target.Lines[(span.From - 1)..span.To].Any(Named)
                                                                            || Quoted(target.Lines[(span.From - 1)..span.To]))))) {
            return null;
        }

        return $"spans no line using any of {string.Join(", ", names.Order(StringComparer.Ordinal).Take(6).Concat(fragments.Take(2)).Select(name => $"`{name}`"))}";
    }

    /// <summary>The clause's backticked code, collapsed, where it is long enough to mean something.</summary>
    static List<string> ProseCode(Cited cited) {
        var places = Places(cited);

        return CodeSpan.Matches(NamedFile.Replace(Citation.Replace(ProseAround(cited), " "), " "))
            .Select(span => Collapse(span.Groups["code"].Value))
            .Where(code => code.Length >= 4 && !places.Contains(code) && !Regex.IsMatch(code, "^(?=.*[0-9])[0-9a-f]{7,40}$"))
            .ToList();
    }

    /// <summary>
    ///     The files the citation's prose names — its own and any other on its line — which are places
    ///     and not code: the context map's "`PlayPhysics`, play diagnostics — `EditorParity.cs:…`,
    ///     `PlayPhysics.cs`" names a file, and no line of <c>EditorParity.cs</c> need carry it.
    /// </summary>
    static HashSet<string> Places(Cited cited) {
        HashSet<string> places = new(StringComparer.Ordinal) { Path.GetFileNameWithoutExtension(cited.File) };

        places.UnionWith(NamedFile.Matches(Lines(cited.Document)[cited.Line - 1])
            .Select(file => Path.GetFileNameWithoutExtension(file.Groups["file"].Value)));

        return places;
    }

    /// <summary>Words that are C# or English before they are names, so a clause's <c>`is X`</c> lends only <c>X</c>.</summary>
    static readonly HashSet<string> Keywords = new(
        ["as", "is", "if", "in", "of", "or", "and", "the", "new", "var", "for", "not", "null", "true", "false", "this", "base",
            "void", "int", "bool", "string", "object", "return", "static", "public", "private", "internal", "readonly", "class",
            "struct", "record", "get", "set", "init", "using", "case", "switch", "default", "with", "out", "ref", "params"],
        StringComparer.Ordinal
    );

    /// <summary>The names a range citation's clause uses, from <see cref="ProseAround" />, as <see cref="CommentNames" /> reads a comment.</summary>
    /// <remarks>
    ///     Less the cited file's own name, which says where and not what: <c>`ForwardPlus`</c> withholds
    ///     the ambient (<c>`ForwardPlus.rvn:456-469`</c>) names its file, and no line of it need.
    /// </remarks>
    static HashSet<string> ProseNames(Cited cited) {
        var prose = NamedFile.Replace(Citation.Replace(ProseAround(cited), " "), " ");
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (Match span in CodeSpan.Matches(prose)) {
            foreach (Match name in Regex.Matches(span.Groups["code"].Value, @"[A-Za-z_][A-Za-z0-9_]*")) {
                if (name.Length >= 2 && !Keywords.Contains(name.Value) && !Regex.IsMatch(name.Value, "^(?=.*[0-9])[0-9a-f]{7,40}$")) {
                    names.Add(name.Value);
                }
            }
        }

        foreach (Match word in CodeWord.Matches(Regex.Replace(prose, "`[^`]*`", " "))) {
            names.Add(word.Value);
        }

        names.ExceptWith(Places(cited));

        return names;
    }

    /// <summary>
    ///     The clause a document citation is evidence for: the text from the citation before it — or
    ///     the start of its paragraph or table cell — up to it, and, where the citation stands bare in
    ///     its sentence rather than in a parenthesis, the rest of that sentence too.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A paragraph is read across its wraps, bounded the way <see cref="ParseDocument" />
    ///         bounds a continuation (<see cref="Opens" />); a table row is one line and its cell is the
    ///         bound.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which side the subject is on depends on the parenthesis.</b> <c>`X` (`F.cs:1-9`)</c>
    ///         cites for what precedes it; <c>`F.cs:1-9` lists `.ttf` and `.otf`</c> cites for what
    ///         follows. Read only before, the second shape was held to the previous sentence's names.
    ///     </para>
    /// </remarks>
    static string ProseAround(Cited cited) {
        if (cited.Column < 0) {
            return "";
        }

        var lines = Lines(cited.Document);
        var at = cited.Line - 1;
        var line = lines[at];
        string text;
        int start;

        if (Unquoted(line).StartsWith('|')) {
            var open = line.LastIndexOf('|', Math.Max(cited.Column - 1, 0));
            var close = line.IndexOf('|', cited.Column);
            text = line[(open + 1)..(close < 0 ? line.Length : close)];
            start = cited.Column - open - 1;
        } else {
            var first = at;

            while (first > 0 && !Opens(lines[first])) {
                first--;
            }

            // A list item opens its paragraph and belongs to it; a blank, a heading or a row above it does not.
            if (first < at && Opens(lines[first]) && !ListItem.IsMatch(Unquoted(lines[first]))) {
                first++;
            }

            var last = at;

            while (last + 1 < lines.Length && !Opens(lines[last + 1])) {
                last++;
            }

            var before = string.Join('\n', lines[first..at]) + (first < at ? "\n" : "");
            text = before + string.Join('\n', lines[at..(last + 1)]);
            start = before.Length + cited.Column;
        }

        var citations = Citation.Matches(text).ToList();
        var own = citations.Single(match => match.Index == start);
        var previous = citations.LastOrDefault(match => match.Index < start);
        var prose = text[(previous is null ? 0 : previous.Index + previous.Length)..start];

        // A clause ends at a semicolon or a full stop as well as at a citation: doc 50's F9 row is
        // "…, `DiagnosticsModule`'s since #1296); `StandardIcons` keyed by … (`StandardIcons.cs:89-115`)".
        if (Regex.Matches(prose, @"[.;](\s|$)").LastOrDefault() is { } stop) {
            prose = prose[(stop.Index + stop.Length)..];
        }

        if (prose.TrimEnd().EndsWith('(')) {
            return prose;
        }

        var end = own.Index + own.Length;
        var next = citations.FirstOrDefault(match => match.Index > start)?.Index ?? text.Length;
        var sentence = Regex.Match(text[end..next], @"[.;:,](\s|$)");

        return prose + text[end..(sentence.Success ? end + sentence.Index : next)];
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
                                                 || Targets(cited, index).Any(target => Holds(cited, target.Lines)))
                                             && (cited.Symbol is not null || cited.Code is not null
                                                 || cited.Spans.All(span => span.From == span.To)
                                                 || Unnamed(cited, index) is null))) {
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
    ///         Resolution, one file, and something on the line: the rules the documents get. Placement
    ///         is a looser rule of its own, because a comment's prose binds a symbol far less regularly
    ///         than a plan's (<see cref="Every_source_comment_citation_is_on_a_line_its_comment_names" />,
    ///         #1454). A pin works here too. ⚠ Read the same way the documents are, a citation is its whole
    ///         token — <c>&lt;c&gt;File.cs:12&lt;/c&gt;</c> as an <i>example</i> of a format is in
    ///         <see cref="SourceExamples" /> rather than silently skipped.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_source_comment_citation_names_a_file_and_a_line_that_exist() {
        var (_, index, _) = Sweep();
        List<string> failures = [];
        var read = 0;

        foreach (var (cited, _) in SourceCitations(index)) {
            read++;

            if (Resolve(cited, index) is { } problem) {
                failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` — {problem}");
            } else if (!Unverifiable(cited) && !Targets(cited, index).Any(target => Substantial(cited, target.Lines))) {
                failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` — a blank line or a lone brace");
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
    ///     Every citation in a source comment, with the comment around it: the contiguous comment
    ///     lines within <see cref="Reach" /> of it.
    /// </summary>
    static IEnumerable<(Cited Cited, string Comment)> SourceCitations(Dictionary<string, List<string>> index) {
        foreach (var relative in index.Values.SelectMany(paths => paths).Order(StringComparer.Ordinal)) {
            if (!SourceExtensions.Contains(Path.GetExtension(relative)) || relative == SelfPath) {
                continue;
            }

            var source = Lines(relative);

            for (var at = 0; at < source.Length; at++) {
                foreach (var found in SourceCitationsOn(relative, source, at)) {
                    yield return found;
                }
            }
        }
    }

    /// <summary>The citations on one line of a source file, each with its comment.</summary>
    /// <param name="relative">The source file.</param>
    /// <param name="source">Its lines.</param>
    /// <param name="at">The 0-based line.</param>
    static IEnumerable<(Cited Cited, string Comment)> SourceCitationsOn(string relative, string[] source, int at) {
        string? comment = null;

        foreach (Match match in SourceCitation.Matches(source[at])) {
            var text = match.Groups["text"].Value;

            if (SourceExamples.Contains((relative, text))) {
                continue;
            }

            var file = (match.Groups["root"].Success ? "./" : "") + match.Groups["file"].Value;
            var lines = Regex.Matches(match.Groups["lines"].Value, @"\d+").Select(digits => int.Parse(digits.Value)).ToArray();
            var commit = match.Groups["commit"] is { Success: true } pin ? pin.Value : null;
            Cited cited = new(relative, at + 1, text, file, lines, Regex.IsMatch(match.Groups["lines"].Value, "[-–]"), null, null, null, commit);

            yield return (cited, comment ??= CommentAround(source, at));
        }
    }

    /// <summary>
    ///     How far a source citation's comment reaches, in lines either way: far enough to take in the
    ///     <c>&lt;see cref&gt;</c> or the sentence that says what the citation is evidence for, near
    ///     enough that a long remark does not lend a citation every name it mentions.
    /// </summary>
    const int Reach = 8;

    /// <summary>
    ///     The comment a source citation sits in: its own line and the contiguous comment lines within
    ///     <see cref="Reach" /> of it, in any of the four languages.
    /// </summary>
    static string CommentAround(string[] source, int at) {
        // Which lines are comment: `//` and `///` lines, and every line of a `/* */` or `<!-- -->`.
        var comment = new bool[source.Length];
        var open = false;

        for (var line = 0; line < source.Length; line++) {
            var text = source[line].TrimStart();
            comment[line] = open || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal)
                            || text.StartsWith("<!--", StringComparison.Ordinal) || text.StartsWith('*');

            var opens = source[line].LastIndexOf("/*", StringComparison.Ordinal) is var a and >= 0
                ? a : source[line].LastIndexOf("<!--", StringComparison.Ordinal);
            var closes = Math.Max(source[line].LastIndexOf("*/", StringComparison.Ordinal), source[line].LastIndexOf("-->", StringComparison.Ordinal));

            if (opens > closes) {
                open = true;
            } else if (closes >= 0) {
                open = false;
            }
        }

        var from = at;
        var to = at;

        while (from > 0 && at - from < Reach && comment[from - 1]) {
            from--;
        }

        while (to < source.Length - 1 && to - at < Reach && comment[to + 1]) {
            to++;
        }

        return string.Join('\n', source[from..(to + 1)]);
    }

    /// <summary>Code in a comment: <c>&lt;c&gt;…&lt;/c&gt;</c>, backticks, and a <c>cref</c>'s target.</summary>
    static readonly Regex CodeSpan = new(@"<c>(?<code>.*?)</c>|`(?<code>[^`\n]+)`|cref=""(?:\w:)?(?<code>[^""]+)""", RegexOptions.Compiled);

    /// <summary>A word that is a name in code even in prose: <c>PlatformCursor</c>, <c>in_uv</c>, <c>isLost</c>.</summary>
    static readonly Regex CodeWord = new(@"\b(?:[A-Z][a-z0-9]+[A-Z]\w*|[a-z][a-z0-9]*[A-Z]\w*|[A-Za-z]\w*_\w+)\b", RegexOptions.Compiled);

    /// <summary>
    ///     The names a comment uses: every identifier in its code, and every word of prose that is
    ///     spelled like code. The citations themselves are taken out first, so a file's own name is not
    ///     a name the line it cites has to carry.
    /// </summary>
    static HashSet<string> CommentNames(string comment) {
        var text = SourceCitation.Replace(comment, " ");
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (Match span in CodeSpan.Matches(text)) {
            foreach (Match name in Regex.Matches(span.Groups["code"].Value, @"[A-Za-z_][A-Za-z0-9_]*")) {
                if (name.Length >= 2) {
                    names.Add(name.Value);
                }
            }
        }

        foreach (Match word in CodeWord.Matches(text)) {
            names.Add(word.Value);
        }

        return names;
    }

    /// <summary>
    ///     Why a source citation is not on a line its comment names, or <see langword="null" /> when
    ///     it is: a name of the comment's is a whole word on the cited line, on each line of a list, or
    ///     on some line of a range.
    /// </summary>
    static string? Misplaced(Cited cited, string comment, Dictionary<string, List<string>> index) {
        var names = CommentNames(comment);

        bool Named(string line) =>
            Regex.Matches(line, @"[A-Za-z_][A-Za-z0-9_]*").Any(word => names.Contains(word.Value));

        bool Holds(string[] lines) {
            if (!cited.Lines.All(line => line >= 1 && line <= lines.Length)) {
                return false;
            }

            return cited.Spans.All(span => lines[(span.From - 1)..span.To].Any(Named));
        }

        var targets = Targets(cited, index);

        if (targets.Any(target => Holds(target.Lines))) {
            return null;
        }

        var there = targets
            .Where(target => cited.Lines.All(line => line >= 1 && line <= target.Lines.Length))
            .Select(target => string.Join(" / ", cited.Lines.Take(2).Select(line => $"'{target.Lines[line - 1].Trim()}'")));

        return $"is {string.Join("; ", there)}, which uses none of the names its comment does";
    }

    /// <summary>
    ///     Source citations the placement rule is wrong about, each with the line it was read
    ///     against: comment, citation, the cited line's text, why the rule cannot see the binding.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The cited line's text is part of the entry</b>, so an exempt citation is not an
    ///     unchecked one: the day its line changes under it — drift included — the entry stops
    ///     matching and the citation is read again. And the list can only shrink: an entry whose
    ///     citation is gone, or that the rule now places, fails.
    /// </remarks>
    static readonly (string Comment, string Text, string Line, string Why)[] SourcePlacementExempt = [
        // The emitter's two `Reserve` calls, described as "a location" and "`in_uv`/`in_normalWS`" —
        // the name the line builds, not one it spells.
        ("Platform/Vixen.Graphics.OpenGL.Tests/GlslTranslatorEsslTests.cs", "GlslEmitter.cs:611,628", "var name = Reserve(\"in_\" + stream.Name);",
            "names the varying the line builds, \"in_\" + a stream's name, and not anything on it"),
        ("Raven/Vixen.Raven.Transpile.Tests/CrossCompilationTests.cs", "GlslEmitter.cs:611,628", "var name = Reserve(\"in_\" + stream.Name);",
            "names the varying the line builds, \"in_\" + a stream's name, and not anything on it"),
        ("Raven/Vixen.Raven.Transpile/SpirvCrossTranspiler.cs", "GlslEmitter.cs:611,628", "var name = Reserve(\"in_\" + stream.Name);",
            "names the varying the line builds, \"in_\" + a stream's name, and not anything on it"),
        ("Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn", "Raven/Library/Core/ColorSpaces.rvn:29", "static func SrgbToLinearChannel(c: float): float {",
            "a doc comment on the copy, citing the original by what it computes (\"the sRGB EOTF\"), whose name is on the next line"),
        ("Editor/Vixen.Editor.TextureGraph.Tests/TextureKernelLanguageSeamTests.cs", "Checker.rvn:66", "val value = mod(cell.x + cell.y, 2f)",
            "\"the live line\" — the comment is about the line's being live, not about what it says"),
        ("Raven/Vixen.Raven.Tests/DepthTextureTests.cs", "VulkanDevice.Pipelines.cs:404", "if (declared.IsComparisonSampler && !sampler.Compares) {",
            "\"refuse a mismatch\": the refusal in prose, the comparison in code"),
        ("Raven/Vixen.Raven.Tests/DepthTextureTests.cs", "WebGpuDevice.Resources.cs:653", "if (declared.Kind == DescriptorKind.Sampler && sampler.Compares != declared.IsComparisonSampler) {",
            "\"refuse a mismatch\": the refusal in prose, the comparison in code"),
        ("Samples/02-HelloUi/Shell.vxml", "Vixen.Editor.App/WorkspaceModule.cs:285", "static void RecentProjects(PluginContext context) {",
            "an XML comment describing what the method builds — a recent list — and not naming it"),
        ("Core/Vixen.Ui.Controls/SegmentedControl.cs", "EditorTheme.vcss:289@e57dfa401", "⚠ A segmented control is one box with the seams *inside* it, which is the",
            "a sheet's own comment cited for what it says, which is prose on both sides")
    ];

    /// <summary>
    ///     Every citation in a source comment is on a line that uses a name its comment uses
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1454">#1454</a>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Resolution and "not blank" are not placement.</b> #1427's sweep passed
    ///         <c>PlatformCursorTests</c> citing <c>EditorHost.cs:296</c> — a <c>&lt;summary&gt;</c>
    ///         line — for a <c>PlatformCursor.Apply</c> call, because a summary has words on it. The
    ///         drift it was written to find is exactly the drift it could not see.
    ///     </para>
    ///     <para>
    ///         A plan citation binds by the shape of its prose; a comment's prose is too free for that,
    ///         and a recorded line text for every citation would turn every edit above a cited line
    ///         red. So the rule is looser and still able to be false: the cited line, each line of a
    ///         list, or some line of a range has to use, as a whole word, a name the comment around
    ///         the citation uses — an identifier in its <c>&lt;c&gt;</c>, backticks or
    ///         <c>cref</c>, or a word of its prose spelled like code. A citation that drifts onto a
    ///         line about something else stops sharing a name with the comment that cites it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It has false positives, and they are named.</b> A comment that describes its line
    ///         in prose — "the emitter writes a location" for a line that says <c>Reserve</c> — shares
    ///         nothing with it. Those are in <see cref="SourcePlacementExempt" /> with the line's text,
    ///         or are better pinned. A rule with no false positives here would be one the defect
    ///         satisfies.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_source_comment_citation_is_on_a_line_its_comment_names() {
        var (_, index, _) = Sweep();
        List<string> failures = [];
        HashSet<(string, string)> exemptUsed = [];
        var placed = 0;

        foreach (var (cited, comment) in SourceCitations(index)) {
            if (Resolve(cited, index) is not null || Unverifiable(cited)) {
                continue;
            }

            var problem = Misplaced(cited, comment, index);
            var exempt = SourcePlacementExempt.Where(entry => entry.Comment == cited.Document && entry.Text == cited.Text).ToList();

            if (exempt.Count > 0) {
                exemptUsed.Add((cited.Document, cited.Text));
                var line = Targets(cited, index).Select(target => target.Lines[cited.Lines[0] - 1].Trim()).FirstOrDefault();

                if (problem is null) {
                    failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` is placed now — delete its {nameof(SourcePlacementExempt)} entry");
                } else if (!exempt.Any(entry => entry.Line == line)) {
                    failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` is exempt as '{exempt[0].Line}' and the line is '{line}' now — re-read it");
                }

                continue;
            }

            if (problem is not null) {
                failures.Add($"{cited.Document}:{cited.Line} `{cited.Text}` {problem}");
            } else {
                placed++;
            }
        }

        failures.AddRange(SourcePlacementExempt
            .Where(entry => !exemptUsed.Contains((entry.Comment, entry.Text)))
            .Select(entry => $"{entry.Comment} `{entry.Text}` is no longer cited — delete its {nameof(SourcePlacementExempt)} entry"));

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} source comment citation(s) are on a line that uses no name their comment uses, which is what a "
            + "number that drifted onto another worded line looks like (#1454). Re-point each by reading it, pin it to the commit "
            + $"it describes (`File.cs:N@sha`), or — where the comment names its line only in prose — add it to {nameof(SourcePlacementExempt)} "
            + "with the line's text:\n  " + string.Join("\n  ", failures)
        );

        // The instrument: a rule that places nothing is a rule that is not reading.
        Assert.True(placed >= 20, $"only {placed} source citation(s) were placed, so the placement rule has stopped reading lines");
    }

    /// <summary>
    ///     The drift #1427 reported and its sweep passed: <c>PlatformCursorTests</c>' citation put back
    ///     on <c>EditorHost.cs:296</c>, a <c>&lt;summary&gt;</c> line, is refused, and the line it
    ///     means is not (<a href="https://github.com/Rikarin/Vixen/issues/1454">#1454</a>).
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Fed in, not found</b>, so it is red on the old rule's terms too: the old sweep's two
    ///     checks — resolves, not blank — both pass the drifted line, which is asserted first.
    ///     <c>EditorHost.cs</c> is read at <c>24d187463</c>, a commit, because the live file moves.
    /// </remarks>
    [Fact]
    public void A_source_citation_drifted_onto_a_worded_line_is_refused() {
        if (!History.Has("24d187463")) {
            Assert.Skip("this checkout has no history to read EditorHost.cs at a fixed commit (a shallow clone)");
        }

        var (_, index, _) = Sweep();
        var source = Lines("Platform/Vixen.Platform.Ui.Tests/PlatformCursorTests.cs").ToArray();
        var at = Array.FindIndex(source, line => line.Contains("<c>EditorHost.cs:", StringComparison.Ordinal));

        Assert.True(at > 0, "PlatformCursorTests no longer cites EditorHost.cs, so this fixture needs a new witness");

        (Cited Cited, string Comment) Read(int cited) {
            source[at] = Regex.Replace(source[at], @"<c>EditorHost\.cs:[^<]*</c>", $"<c>EditorHost.cs:{cited}@24d187463</c>");

            return SourceCitationsOn("Platform/Vixen.Platform.Ui.Tests/PlatformCursorTests.cs", source, at).Single();
        }

        var drifted = Read(296);
        var meant = Read(388);

        Assert.Null(Resolve(drifted.Cited, index));
        Assert.Contains(Targets(drifted.Cited, index), target => Substantial(drifted.Cited, target.Lines));
        Assert.Contains("uses none of the names", Misplaced(drifted.Cited, drifted.Comment, index));

        Assert.Null(Misplaced(meant.Cited, meant.Comment, index));
    }

    /// <summary>
    ///     Every document holds exactly as many unbound, unpinned citations as <see cref="UnboundPath" />
    ///     records for it, so the set whose drift nothing can see grows only by an edit to that file.
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
            $"{wrong.Count} document(s) disagree with {UnboundPath}, whose counts change only where a diff says so (#1388):\n  " + string.Join("\n  ", wrong)
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
        Assert.Contains(citations, cited => cited.File == "Shell.vxml" && cited.Lines.SequenceEqual([610]));

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
    ///     A bare continuation opening a line continues the file its paragraph named last, in every
    ///     line form, across the wrap and no further (<a href="https://github.com/Rikarin/Vixen/issues/1455">#1455</a>).
    /// </summary>
    /// <remarks>
    ///     ⚠ Before this, <c>Parse</c> returned nothing at all for such a citation, so every assertion
    ///     below about what one resolves to had nothing to hold: the old sweep passed all of them by
    ///     not reading the line.
    /// </remarks>
    [Fact]
    public void A_continuation_that_opens_a_line_continues_its_paragraph() {
        var (_, index, _) = Sweep();

        // Each line form, carried across two wraps.
        var carried = ParseDocument("x.md", [
            "the menu (`Menus.cs:12,15`) and",
            "its rows `:20-25`, then the",
            "separators at `:30,31`."
        ]);

        Assert.All(carried, cited => Assert.Equal("Menus.cs", cited.File));
        Assert.Equal(new[] { new[] { 12, 15 }, [20, 25], [30, 31] }, carried.Select(cited => cited.Lines));
        Assert.Equal(new[] { false, true, false }, carried.Select(cited => cited.Range));

        // A pin carries with its file, so the continuation is read at the pinned commit too.
        Assert.Equal("10523d70f", ParseDocument("x.md", ["(`Focus.cs:91-93@10523d70f`), and", "`:123-124` after"])[1].Commit);

        // A file named with no line is a file named: `ProjectBrowser.cs` 2 — a pair (`:1167-1175`).
        var named = ParseDocument("x.md", ["`EditorShell.cs` 0, `ProjectBrowser.cs` 2 — a", "pair, `Scan()` (`:1167-1175`)"]).Single();
        Assert.Equal("ProjectBrowser.cs", named.File);
        Assert.Equal("Scan()", named.Symbol);
        Assert.Null(ParseDocument("x.md", ["`ProjectBrowser.cs` (`:1167`)"]).Single().Symbol);

        // A blockquote paragraph carries; a bare `>` ends it like a blank line.
        Assert.Equal("Menus.cs", ParseDocument("x.md", ["> (`Menus.cs:12`),", "> `:20` too"])[1].File);

        // ⚠ The other-type rule (#1422) reads across the wrap as well.
        var across = ParseDocument("x.md", ["`EditorProject.cs:56` and the", "`EditorApplication.scene` (`:108`)"])[1];
        Assert.Equal("EditorApplication.scene", across.Across);

        // And a paragraph's end is where a continuation stops reaching: a blank line, a bare `>`, a
        // heading, a table row, a list item, a fence. Each of those is refused and not skipped.
        string[][] ends = [
            ["(`Menus.cs:12`).", "", "`:20` begins"],
            ["> (`Menus.cs:12`).", ">", "> `:20` begins"],
            ["(`Menus.cs:12`).", "## Next", "`:20` begins"],
            ["| `Menus.cs:12` |", "`:20` begins"],
            ["(`Menus.cs:12`).", "| `:20` |"],
            ["- (`Menus.cs:12`).", "- `:20` begins"],
            ["(`Menus.cs:12`).", "```", "`:20` begins"]
        ];

        foreach (var lines in ends) {
            var orphan = ParseDocument("x.md", lines).Single(cited => cited.Text == ":20");

            Assert.Equal("", orphan.File);
            Assert.Contains("#1455", Resolve(orphan, index));
        }
    }

    /// <summary>
    ///     Doc 49 § 1.5's <c>`:123-124`</c>, the continuation of a pin that nothing read: moved onto a
    ///     blank line of <c>Focus.cs</c> at <c>10523d70f</c>, it goes red
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1455">#1455</a>).
    /// </summary>
    [Fact]
    public void Doc_49s_continuation_of_a_pin_is_held_to_the_pin() {
        if (!History.Has("10523d70f")) {
            Assert.Skip("this checkout has no history to hold a pin to (a shallow clone)");
        }

        var (_, index, _) = Sweep();

        const string Opens = "nothing else (`Focus.cs:91-93@10523d70f`), writes `Focused` at `:100`, and raises two `FocusEvent`s at";
        var right = ParseDocument("docs/plan/49-responder-chain-and-appkit-parity.md", [Opens, "`:123-124` — *after* the change is committed."])[^1];
        var moved = ParseDocument("docs/plan/49-responder-chain-and-appkit-parity.md", [Opens, "`:122` — *after* the change is committed."])[^1];

        Assert.Equal(("Focus.cs", "10523d70f"), (right.File, right.Commit));
        Assert.Null(Resolve(right, index));
        Assert.Contains(Targets(right, index), target => Substantial(right, target.Lines));

        Assert.Null(Resolve(moved, index));
        Assert.DoesNotContain(Targets(moved, index), target => Substantial(moved, target.Lines));
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

    /// <summary>
    ///     Doc 50 § F9's App row as it stood on master at <c>c40c2ea93</c>: the sweep passed all of its
    ///     ranges, and each is refused now — and the row as re-read is not
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1495">#1495</a>).
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Fed in and pinned, so neither half moves.</b> The drifted ranges are read at
    ///     <c>c40c2ea93</c>, the master the issue was filed against, rather than at HEAD, where the
    ///     lanes editing <c>EditorApplication</c> would turn this into a test of their line shifts.
    ///     The document is primed into the line cache under a path no checkout has, because the prose
    ///     a range is held to is read from its document.
    /// </remarks>
    [Fact]
    public void Doc_50s_F9_ranges_are_refused_where_they_say_nothing_the_row_claims() {
        if (!History.Has("c40c2ea93") || !History.Has("5f03c9b80")) {
            Assert.Skip("this checkout has no history to hold a pin to (a shallow clone)");
        }

        var (_, index, _) = Sweep();

        Cited Read(string document, string row, string text) {
            lock (LineCache) {
                LineCache[document] = [row];
            }

            return ParseDocument(document, [row]).Single(cited => cited.Text == text);
        }

        const string Drifted =
            "| `Vixen.Editor.App` | 48 | `is StandardFrameDocument`, `is ShaderGraphDocument`, `is MaterialView`, "
            + "`is TextureImportView` (`EditorApplication.cs:792-832, 2966-2973@c40c2ea93`); "
            + "`ITerrainScene`/`IVegetationScene`/`IWaterScene` — one property per feature (`:1076-1094`) |";

        var types = Read("fixture/f9-drifted.md", Drifted, "EditorApplication.cs:792-832, 2966-2973@c40c2ea93");
        var scenes = Read("fixture/f9-drifted.md", Drifted, ":1076-1094");

        // Both resolve, which is all the old sweep asked of a range with no symbol before it.
        Assert.Null(Resolve(types, index));
        Assert.Null(Resolve(scenes, index));

        // ⚠ Two ranges, where the old reading was one range and two numbers nobody checked.
        Assert.Equal([(792, 832), (2966, 2973)], types.Spans);

        // 832 is blank, so the first is refused on its ends alone…
        Assert.DoesNotContain(Targets(types, index), target => Substantial(types, target.Lines));

        // …and neither range uses a name its clause does. `:1076-1094` has words at both ends —
        // `Extensions` and its summary — so only the names can refuse it.
        Assert.NotNull(Unnamed(types, index));
        Assert.Contains(Targets(scenes, index), target => Substantial(scenes, target.Lines));
        Assert.Contains("ITerrainScene", Unnamed(scenes, index));

        // The row as re-read: the type tests pinned to the tree #1297 removed them from, and the
        // scene seams where they are.
        const string Read_ =
            "| `Vixen.Editor.App` | 48 | `is StandardFrameDocument`, `is ShaderGraphDocument`, `is MaterialView`, "
            + "`is TextureImportView` (`EditorApplication.cs:772-778, 2912-2920@5f03c9b80`, removed by #1297); "
            + "`ITerrainScene`/`IVegetationScene`/`IWaterScene` — one property per feature (`ScenePresenter.cs:255-298@c40c2ea93`) |";

        var pinned = Read("fixture/f9-read.md", Read_, "EditorApplication.cs:772-778, 2912-2920@5f03c9b80");
        var seams = Read("fixture/f9-read.md", Read_, "ScenePresenter.cs:255-298@c40c2ea93");

        foreach (var cited in new[] { pinned, seams }) {
            Assert.Null(Resolve(cited, index));
            Assert.Contains(Targets(cited, index), target => Substantial(cited, target.Lines));
            Assert.Null(Unnamed(cited, index));
        }
    }

    /// <summary>A pin is checked against its commit and not against HEAD, in both directions.</summary>
    /// <remarks>
    ///     <para>
    ///         Doc 49's § 1.5 as the audit had it: at <c>10523d70f</c> line 90 opened <c>Focus</c> and
    ///         line 91 was its <c>Focusable</c> gate. By <c>6de5ac0f2</c> neither line was either, so a
    ///         pin read against a later tree fails the first and a pin not read at all passes the second.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here reads the live <c>Focus.cs</c>.</b> It did, asserting that HEAD's line 90
    ///         was not a <c>Focus</c> line — and <c>Focus</c> is a whole word on sixteen of that hot
    ///         file's lines, so an unrelated edit could land one on line 90 and turn this red for nothing.
    ///         The later tree is a commit, which cannot move. And what catches a <see cref="Targets" />
    ///         that reads HEAD for a pin is a file HEAD does not have: the Ui test kit's own
    ///         <c>PngCodec</c>, deleted in <c>8ce3c54da</c>, so a pin to it holds at its parent and
    ///         finds nothing at all at HEAD whatever anybody edits.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pinned_citation_is_held_to_its_commit() {
        if (!History.Has("10523d70f") || !History.Has("6de5ac0f2") || !History.Has("5ac755981")) {
            Assert.Skip("this checkout has no history to hold a pin to (a shallow clone)");
        }

        var (_, index, _) = Sweep();
        var right = Parse("x.md", 1, "`Focus` (`Focus.cs:90@10523d70f`)").Single();
        var wrong = Parse("x.md", 2, "`Focus` (`Focus.cs:91@10523d70f`)").Single();

        Assert.Null(Resolve(right, index));
        Assert.Contains(Targets(right, index), target => Holds(right, target.Lines));
        Assert.DoesNotContain(Targets(right, index), target => Holds(right, History.Lines("6de5ac0f2", target.Path) ?? []));
        Assert.DoesNotContain(Targets(wrong, index), target => Holds(wrong, target.Lines));

        // A pin to a file that is gone: it holds at its commit, and HEAD has nothing it could be read against.
        var gone = Parse("x.md", 3, "`PngCodec` (`Vixen.Ui.Testing/Visual/PngCodec.cs:38@5ac755981`)").Single();

        Assert.Empty(Candidates(gone, index));
        Assert.Null(Resolve(gone, index));
        Assert.Contains(Targets(gone, index), target => Holds(gone, target.Lines));
    }

    /// <summary>
    ///     The walk reads <c>references/README.md</c> and nothing a reference clone brings with it, nor
    ///     the Sdk README <c>./build.sh</c> unpacks under <c>.nuke/temp/</c>, and still enters a
    ///     directory that is only <i>named</i> like either.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A scratch repository rather than the checkout, because the clones and the unpacked Sdk
    ///         are gitignored and neither CI nor a fresh worktree has one: asked of the real tree, "no
    ///         clone is swept" is a predicate that cannot be false there. ⚠ And it carries this
    ///         checkout's own <c>.gitignore</c>, copied, so the file under test is the rule and not a
    ///         restatement of it.
    ///     </para>
    ///     <para>
    ///         ⚠ The <c>.nuke/temp/</c> row is the one that went red on master (9200ae53a): the stale
    ///         README cited a line the real one had re-pointed. It is planted with a citation that
    ///         cannot resolve, so a walk that read it would fail the sweep and not only this list.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_walk_stops_at_the_reference_clones() {
        var root = Directory.CreateTempSubdirectory("vixen-citation-walk-").FullName;

        try {
            File.Copy(Path.Combine(Root, ".gitignore"), Path.Combine(root, ".gitignore"));
            Git(root, "init", "--quiet");

            string[] tree = [
                "references/README.md",
                "references/godot/README.md",
                "references/godot/core/Node.cs",
                ".nuke/temp/vixen-sdk-tools/README.md",
                "Samples/03-Demo/Build/README.md",
                "Core/Vixen.Demo/obj/README.md",
                "docs/plan/01.md",
                "Platform/Golden.Tests/References/README.md",
                "Tools/Nuke.Temp/README.md"
            ];

            foreach (var relative in tree) {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "`BuiltInImporters.cs:174`");
            }

            Dictionary<string, List<string>> index = new(StringComparer.Ordinal);
            Walk(root, index);
            var walked = index.Values.SelectMany(paths => paths).Order(StringComparer.Ordinal).ToArray();

            string[] expected = [
                ".gitignore",
                "Platform/Golden.Tests/References/README.md",
                "Tools/Nuke.Temp/README.md",
                "docs/plan/01.md",
                "references/README.md"
            ];

            Assert.Equal(expected, walked);
            Assert.Equal(expected.Where(path => path.EndsWith(".md", StringComparison.Ordinal)), Documents(index));
        } finally {
            // git marks its object files read-only, which Directory.Delete refuses on Windows.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Runs git in a scratch repository and fails the test on a non-zero exit.</summary>
    static void Git(string directory, params string[] arguments) {
        var start = new System.Diagnostics.ProcessStartInfo("git") {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var git = System.Diagnostics.Process.Start(start)!;
        var error = git.StandardError.ReadToEndAsync();
        git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} exited {git.ExitCode}: {error.Result}");
    }

    /// <summary>Why a citation does not resolve, or <see langword="null" /> when it does.</summary>
    static string? Resolve(Cited cited, Dictionary<string, List<string>> index) {
        if (cited.File.Length == 0) {
            return "a bare continuation with no file named before it in its paragraph — a blank line, a heading, a "
                   + "table row or a list item ends what a continuation can reach — so which file it cites is a guess; "
                   + "name the file (#1455)";
        }

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
        var cover = Cover(cited, lines);

        if (cited.Symbol is { } symbol) {
            var name = Regex.Replace(symbol, @"<.*$|\(.*\)$", "").Split('.')[^1];

            return cover.Any(line => Regex.IsMatch(line, $@"\b{Regex.Escape(name)}\b"));
        }

        var code = Collapse(cited.Code!);

        return cover.Any(line => Collapse(line).Contains(code, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Whether the cited lines say anything: every single line carries a word, and every range
    ///     opens on a word and closes on something that is not blank.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A range asked only that one of its lines carry a word</b>, and a range of forty lines in
    ///     a file of four thousand always has one — so doc 50's <c>`EditorApplication.cs:792-832`</c>,
    ///     which ended on a blank line, passed (<a href="https://github.com/Rikarin/Vixen/issues/1495">#1495</a>).
    ///     Its ends are where a drifted range lands wrong, so its ends are what is held: the first line
    ///     to anything but a blank or a closing brace — a range may open on <c>&lt;!--</c> or <c>{</c>
    ///     — and the last to anything but a blank, because a range legitimately closes on the brace
    ///     that ends what it spans.
    /// </remarks>
    static bool Substantial(Cited cited, string[] lines) {
        static bool Worded(string line) => Regex.IsMatch(line, @"\w");

        // A candidate too short for the citation says nothing; resolution has already found one that is not.
        if (!cited.Lines.All(line => line >= 1 && line <= lines.Length)) {
            return false;
        }

        return cited.Spans.All(span => span.From == span.To
            ? Worded(lines[span.From - 1])
            : Starts(lines[span.From - 1]) && lines[span.To - 1].Trim().Length > 0);

        // Anything but a blank or a closer: a range may open on `<!--` or `{`, never on the `}` that
        // ends the thing above it.
        static bool Starts(string line) => line.Trim() is { Length: > 0 } text && !Regex.IsMatch(text, @"^[})\]]+[;,]?$");
    }

    /// <summary>Every line the citation covers: each single line, and every line of each range.</summary>
    static IEnumerable<string> Cover(Cited cited, string[] lines) =>
        cited.Spans.SelectMany(span => lines[Math.Min(span.From - 1, lines.Length)..Math.Min(span.To, lines.Length)]);

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
        Walk(Root, index);

        List<Cited> citations = [];

        foreach (var relative in Documents(index)) {
            citations.AddRange(ParseDocument(relative, File.ReadLines(Path.Combine(Root, relative))));
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

    /// <summary>
    ///     The file a paragraph named last, carried onto its next line, with the text written since —
    ///     what a bare continuation opening that line continues
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1455">#1455</a>).
    /// </summary>
    /// <param name="File">The file, as written.</param>
    /// <param name="Pinned">The commit its naming citation was pinned to, which its continuations inherit.</param>
    /// <param name="Since">Everything after the naming, which the #1422 other-type rule reads across the wrap.</param>
    sealed record Carry(string File, string? Pinned, string Since);

    /// <summary>A backticked file named with no line: <c>`ProjectBrowser.cs`</c>, which a later <c>`:1167`</c> continues.</summary>
    static readonly Regex NamedFile = new(
        @"`(?:…/|\.\.\./|(?<root>\./))?(?<file>(?:[\w.-]+/)*[\w.-]+\.(?:cs|vxml|vcss|rvn|md|csproj|props|targets|txt|json|tsv|yml|yaml|py|sh|cmd|xml))`",
        RegexOptions.Compiled
    );

    /// <summary>A list item's marker, after any quote markers: <c>- </c>, <c>* </c>, <c>1. </c>.</summary>
    static readonly Regex ListItem = new(@"^(?:[-*+]|\d+[.)])\s", RegexOptions.Compiled);

    /// <summary>A Markdown line with its blockquote markers and indentation taken off.</summary>
    static string Unquoted(string line) {
        var text = line.TrimStart();

        while (text.StartsWith('>')) {
            text = text[1..].TrimStart();
        }

        return text;
    }

    /// <summary>
    ///     Whether this line starts something new, so a file named above it does not carry onto it: a
    ///     blank line (a lone <c>&gt;</c> too), a heading, a table row, a fence, a list item.
    /// </summary>
    static bool Opens(string line) {
        var text = Unquoted(line);

        return text.Length == 0
               || Regex.IsMatch(text, @"^#{1,6}\s")
               || text.StartsWith('|')
               || text.StartsWith("```", StringComparison.Ordinal)
               || ListItem.IsMatch(text);
    }

    /// <summary>
    ///     The citations of a whole document, a file named on one line of a paragraph carried onto the
    ///     next (<a href="https://github.com/Rikarin/Vixen/issues/1455">#1455</a>).
    /// </summary>
    /// <remarks>
    ///     A table row carries nothing in or out: each row is its own line, and a row's cells are about
    ///     that row.
    /// </remarks>
    static List<Cited> ParseDocument(string relative, IEnumerable<string> lines) {
        List<Cited> citations = [];
        string? previous = null;
        Carry? carry = null;
        var number = 0;

        foreach (var line in lines) {
            if (Opens(line)) {
                carry = null;
            }

            citations.AddRange(Parse(relative, ++number, line, previous, carry, out var next));
            carry = Unquoted(line).StartsWith('|') ? null : next;
            previous = line;
        }

        return citations;
    }

    /// <summary>The citations on one line read alone, with nothing carried onto it.</summary>
    static List<Cited> Parse(string relative, int number, string line, string? previous = null) =>
        Parse(relative, number, line, previous, null, out _);

    /// <summary>The citations on one line of a document, each with what it is bound to.</summary>
    /// <param name="relative">The document.</param>
    /// <param name="number">The line's number in it.</param>
    /// <param name="line">The line.</param>
    /// <param name="previous">The line before it, which a citation opening this one may be bound across.</param>
    /// <param name="carried">The file the paragraph named last on an earlier line, if any.</param>
    /// <param name="carry">What this line hands the next line of its paragraph.</param>
    static List<Cited> Parse(string relative, int number, string line, string? previous, Carry? carried, out Carry? carry) {
        List<Cited> citations = [];
        var file = carried?.File;
        var pinned = carried?.Pinned;
        var namedHere = false;
        var namedEnd = 0;

        // A file named with no line is named all the same — `ProjectBrowser.cs` 2 — … (`:1167-1175`)
        // — so the two shapes are read in the order they are written.
        var tokens = Citation.Matches(line).Concat(NamedFile.Matches(line))
            .OrderBy(match => match.Index)
            .ToList();

        foreach (var match in tokens) {
            var named = match.Groups["file"];
            string? across = null;

            if (!match.Groups["lines"].Success) {
                file = match.Groups["root"].Success ? "./" + named.Value : named.Value;
                pinned = null;
                namedHere = true;
                namedEnd = match.Index + match.Length;

                continue;
            }

            if (named.Success) {
                file = match.Groups["root"].Success ? "./" + named.Value : named.Value;
                pinned = match.Groups["commit"] is { Success: true } own ? own.Value : null;
                namedHere = true;
                namedEnd = match.Index + match.Length;
            } else if (file is not null) {
                // ⚠ And one with a symbol of another type between it and that file is a guess too
                // (#1422): `EditorProject.cs:56` … `EditorApplication.scene` (`:108`) meant
                // EditorApplication.cs and was checked against EditorProject.cs. Across a wrap the
                // text between is the rest of the naming line and every line since.
                var stem = Path.GetFileNameWithoutExtension(file);
                var between = namedHere ? line[namedEnd..match.Index] : carried!.Since + "\n" + line[..match.Index];
                across = TypedSymbol.Matches(between)
                    .Where(symbol => symbol.Groups["type"].Value != stem)
                    .Select(symbol => symbol.Groups["symbol"].Value)
                    .FirstOrDefault();
            }

            // ⚠ A continuation with no file named before it in its paragraph is no longer skipped
            // (#1455): it was the one class of citation nothing read at all, and doc 49's
            // `:123-124` continuing a pin was in it. It is read with no file and refused by Resolve.

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
                // And not when the "symbol" is the file itself, `ProjectBrowser.cs` (`:12`), which
                // names where and not what.
                if (!Regex.IsMatch(before, @"(`,\s*|`\s+and\s+|`\s+or\s+|`\s*·\s*|\bno\s+)$")
                    && !NamedFile.IsMatch($"`{bound.Groups["symbol"].Value}`")) {
                    symbol = bound.Groups["symbol"].Value;
                }
            }

            var code = BoundCode.Match(rest) is { Success: true } dash ? dash.Groups["code"].Value : null;
            var commit = match.Groups["commit"] is { Success: true } pin ? pin.Value : pinned;

            citations.Add(new(relative, number, match.Value.Trim('`'), file ?? "", lines, range, symbol, code, across, commit, match.Index));
        }

        carry = file is null ? null
            : namedHere ? new Carry(file, pinned, line[namedEnd..])
            : new Carry(file, pinned, carried!.Since + "\n" + line);

        return citations;
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
                           && (path.StartsWith("docs/", StringComparison.Ordinal)
                               || Path.GetFileName(path) == "README.md"
                               || path == "CONTRIBUTING.md"))
            .Order(StringComparer.Ordinal);

    /// <summary>Every file of the checkout at <paramref name="root" />, indexed by file name.</summary>
    /// <remarks>
    ///     ⚠ <b>What git calls the tree, not what the disk holds</b> (#1424). This was a walk pruned by
    ///     a hand-kept list of names plus a second list of directories entered one level deep, and
    ///     both were answers to a question <c>.gitignore</c> already answers: <c>.claude/worktrees/</c>
    ///     is a checkout per agent, <c>.nuke/temp/</c> is where <c>./build.sh</c> unpacks
    ///     <c>Vixen.Sdk</c> — a stale copy of its README went red on master and not on the branch
    ///     (9200ae53a) — and <c>references/*</c> holds clones of other engines beside the one tracked
    ///     README. The lists were grown one incident at a time, which is how <c>.nuke</c> reached
    ///     three of eighteen copies.
    /// </remarks>
    static void Walk(string root, Dictionary<string, List<string>> index) {
        foreach (var relative in RepositoryFiles.Listed(root)) {
            var name = relative[(relative.LastIndexOf('/') + 1)..];

            if (!index.TryGetValue(name, out var paths)) {
                index[name] = paths = [];
            }

            paths.Add(relative);
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
