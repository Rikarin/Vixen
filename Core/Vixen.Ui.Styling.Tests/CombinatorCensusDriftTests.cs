// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Ui.Styling.Testing;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     The combinator censuses other suites keep, held against the sheets as they are now — in the
///     suite a sheet's author already runs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Rikarin/Vixen#1349</c>: the gate that proves a child-combinator rule reachable
///         worked, and was in the wrong assembly.</b> The domain is this suite's
///         <c>CombinatorPairs.txt</c>; the proofs are four editor censuses in
///         <c>Vixen.Editor.App.Tests</c>, a project that takes about ten minutes to build and run,
///         and two control censuses in <c>Vixen.Ui.Controls.Advanced.Tests</c>. So an <c>A &gt; B</c>
///         added to <c>ControlTheme.vcss</c> went red here, was regenerated here — which is what this
///         suite's own message tells its reader to do — and was then green everywhere its author would
///         think to look. The editor suite was red on master for weeks that way: <c>console-detail
///         &gt; scroll-content</c> and <c>message-log-detail &gt; scroll-content</c> reached the domain
///         and no editor census, and a later sweep regenerated the censuses as a by-product without
///         anybody attributing the red.
///     </para>
///     <para>
///         ⚠ <b>A join over committed files, which is exactly as strong as the live one on a green
///         tree and needs nothing built.</b> Each of the six proof censuses is held exactly and in
///         both directions against its own sweep by its own suite, so on the day those suites are
///         green each file IS its sweep. What only this suite can see is the sheets moving
///         underneath them — and it sees that against the MEASURED pairs, not the domain file, so
///         the drift is reported before the domain is regenerated as well as after.
///     </para>
///     <para>
///         ⚠ <b>What this does not replace.</b> Whether a new pairing is actually BUILT is a question
///         only the sweeps can answer; this can only say that nothing committed claims it is, and
///         send its reader to the suite that can. It fails in the direction that costs a run of the
///         slow suite rather than the one that costs master.
///     </para>
/// </remarks>
public class CombinatorCensusDriftTests {
    /// <summary>The four editor sweeps' censuses, each confined to pairings a sheet declares.</summary>
    static readonly string[] EditorCensusFiles = [
        "Editor/Vixen.Editor.App.Tests/EditorCombinatorPairs.txt",
        "Editor/Vixen.Editor.App.Tests/OpenedEditorCombinatorPairs.txt",
        "Editor/Vixen.Editor.App.Tests/DocumentEditorCombinatorPairs.txt",
        "Editor/Vixen.Editor.App.Tests/OverlayEditorCombinatorPairs.txt"
    ];

    /// <summary>The controls' two sweeps' censuses, confined the same way.</summary>
    static readonly string[] ControlCensusFiles = [
        "Core/Vixen.Ui.Controls.Advanced.Tests/LiveCombinatorPairs.txt",
        "Core/Vixen.Ui.Controls.Advanced.Tests/SeededCombinatorPairs.txt"
    ];

    /// <summary>The residue the editor's pair gate names: declared, proved by no sweep, and why.</summary>
    const string UnprovedFile = "Editor/Vixen.Editor.App.Tests/UnprovedCombinatorPairs.txt";

    /// <summary>The editor's scoped census: every type-only selector, and the shallowest depth that matched it.</summary>
    const string ScopedFile = "Editor/Vixen.Editor.App.Tests/ScopedSelectors.txt";

    /// <summary>The controls' scoped census: every type-only selector the two control sheets declare, and whether a bare control matched it.</summary>
    const string ControlScopedFile = "Core/Vixen.Ui.Controls.Advanced.Tests/ControlScopedSelectors.txt";

    /// <summary>The scoped residue: every type-only selector neither scoped census credits, its kind, and what builds it.</summary>
    const string UnjudgedFile = "Editor/Vixen.Editor.App.Tests/UnjudgedScopedSelectors.txt";

    /// <summary>The kinds a residue row may have; see the file's header for what each one means.</summary>
    static readonly string[] UnjudgedKinds = ["unreached", "api", "none"];

    /// <summary>A <c>file:line</c> in a residue reason: a file name or path, a colon, a line.</summary>
    /// <remarks>
    ///     A bare <c>:NNN</c> — one no word character or slash runs into — continues the file named
    ///     last before it in the same reason, as in <c>ToolbarPresenter.cs:416, :427, :435</c>. ⚠ It
    ///     was not read at all at first, so every continuation in the file was taken on trust while
    ///     the rule claimed each cited line had to be there.
    /// </remarks>
    static readonly Regex Cited = new(@"(?:(?<file>(?:[\w.-]+/)*[\w.-]+\.(?:cs|vxml|vcss))|(?<![\w/])):(?<line>\d+)", RegexOptions.Compiled);

    /// <summary>The two sheets whose selectors that census answers for, as <c>LiveCombinatorPairTests</c> names them.</summary>
    static readonly string[] ControlSheets = [
        "Core/Vixen.Ui.Controls/ControlTheme.vcss",
        "Core/Vixen.Ui.Controls.Advanced/AdvancedTheme.vcss"
    ];

    /// <summary>Where to send somebody whose sheet edit this suite has just refused.</summary>
    const string Remedy = """
        Run the suite that owns the census rather than editing it by hand:

          dotnet test Core/Vixen.Ui.Controls.Advanced.Tests --filter "FullyQualifiedName~LiveCombinatorPairTests"
          dotnet test Editor/Vixen.Editor.App.Tests --filter "FullyQualifiedName~EditorCombinatorPairTests"

        with VIXEN_REGENERATE=1 once you have read why they fail. A pairing no sweep builds is named in
        Editor/Vixen.Editor.App.Tests/UnprovedCombinatorPairs.txt with the file:line that builds it —
        or, if nothing builds it, the rule is dead and wants correcting, as `palette-row > text` did.
        """;

    /// <summary>
    ///     Every pairing the sheets declare now is proved by a committed census or named in the
    ///     residue, and the residue names nothing a census proves.
    /// </summary>
    /// <remarks>
    ///     The editor's own pair gate asks this of its live sweeps and the committed domain file; this
    ///     asks it of the committed proofs and the sheets themselves. Both directions, for the pair
    ///     gate's reason: a residue row that became proved has expired, and a list nobody prunes stops
    ///     describing anything.
    /// </remarks>
    [Fact]
    public void Every_pairing_the_sheets_declare_is_proved_by_a_committed_census_or_named() {
        var root = RepositoryScan.Root();
        var declared = Declared();

        var proved = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EditorCensusFiles.Concat(ControlCensusFiles)) {
            proved.UnionWith(Rows(root, file).Select(static row => row.Split('\t')[0].Trim()));
        }

        var named = Reasons(root);
        var residue = declared.Where(pair => !proved.Contains(pair)).ToHashSet(StringComparer.Ordinal);

        var unexplained = residue.Where(pair => !named.Contains(pair)).Order(StringComparer.Ordinal).ToList();
        var expired = named.Where(pair => !residue.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unexplained.Count == 0 && expired.Count == 0,
            $"""
             A sheet declares a child-combinator pairing that no committed census proves and nobody has named:
             {Lines(unexplained)}

             Named in {UnprovedFile} and proved by a committed census, or declared by no sheet any more:
             {Lines(expired)}

             {Remedy}
             """
        );
    }

    /// <summary>Every row of every proof census is a pairing some sheet still declares.</summary>
    /// <remarks>
    ///     ⚠ <b>The removal half.</b> A rule deleted or renamed in a sheet leaves a proof behind in
    ///     whichever census had recorded it — each is confined to declared pairings, so its own suite
    ///     goes red the next time somebody runs it, and nobody editing a sheet does. This is the
    ///     direction <c>Every_pairing_the_sheets_declare_is_proved_by_a_committed_census_or_named</c>
    ///     cannot see, because a stale proof only ever makes that join easier to satisfy.
    /// </remarks>
    [Fact]
    public void Every_proof_census_row_is_a_pairing_a_sheet_still_declares() {
        var root = RepositoryScan.Root();
        var declared = Declared();

        var stale = EditorCensusFiles.Concat(ControlCensusFiles)
            .SelectMany(file => Rows(root, file).Select(row => (File: file, Pair: row.Split('\t')[0].Trim())))
            .Where(row => !declared.Contains(row.Pair))
            .Select(static row => $"{row.Pair}  — in {row.File}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"""
             A census still proves a pairing no sheet declares any more:
             {Lines(stale)}

             {Remedy}
             """
        );
    }

    /// <summary>The scoped census names exactly the type-only selectors the sheets declare.</summary>
    /// <remarks>
    ///     ⚠ <b>The selector column only.</b> The verdict beside each row is what a running editor's
    ///     matcher said, and only the editor suite can re-ask it; which selectors are in the file is a
    ///     property of the sheets, and <see cref="TypeOnlySelectors" /> is the same reader the editor
    ///     suite compiles — linked, not copied — so the two cannot disagree about the question. This
    ///     is the second shape #1349 records: <c>key-value-value level-indicator</c> reached the sheets
    ///     and not this file, in the same sweep as the two pairings above.
    /// </remarks>
    [Fact]
    public void The_scoped_census_names_exactly_the_type_only_selectors_the_sheets_declare() {
        var root = RepositoryScan.Root();
        var declared = TypeOnlySelectors.Read(root);

        Assert.True(declared.Count >= 150, $"the sheets declare only {declared.Count} type-only selectors, against 195 measured.");

        var census = Rows(root, ScopedFile).Select(static row => row.Split('\t')[0].Trim()).ToHashSet(StringComparer.Ordinal);

        var arrived = declared.Keys.Where(selector => !census.Contains(selector))
            .Order(StringComparer.Ordinal)
            .Select(selector => $"{selector}  — in {declared[selector]}")
            .ToList();

        var departed = census.Where(selector => !declared.ContainsKey(selector)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             {ScopedFile} is out of step with the sheets.

             Declared by a sheet and not in the scoped census:
             {Lines(arrived)}

             In the scoped census and declared by no sheet any more:
             {Lines(departed)}

             Only a running editor can say which depth matches a selector, so regenerate it there:

               dotnet test Editor/Vixen.Editor.App.Tests --filter "FullyQualifiedName~EditorCombinatorPairTests"

             with VIXEN_REGENERATE=1, and read the verdicts it writes before committing them.
             """
        );
    }

    /// <summary>
    ///     The controls' scoped census names exactly the type-only selectors the two control sheets
    ///     declare.
    /// </summary>
    /// <remarks>
    ///     The same question as the editor's scoped census above, for the file
    ///     <c>LiveCombinatorPairTests</c> keeps in <c>Vixen.Ui.Controls.Advanced.Tests</c>: its
    ///     verdicts need the controls built, and its selector column needs only the sheets — which is
    ///     the half a <c>ControlTheme.vcss</c> author reaches from here.
    /// </remarks>
    [Fact]
    public void The_control_scoped_census_names_exactly_the_type_only_selectors_the_control_sheets_declare() {
        var root = RepositoryScan.Root();

        var declared = TypeOnlySelectors.Read(root)
            .Where(static row => ControlSheets.Contains(row.Value, StringComparer.Ordinal))
            .ToDictionary(static row => row.Key, static row => row.Value, StringComparer.Ordinal);

        Assert.True(declared.Count >= 30, $"the control sheets declare only {declared.Count} type-only selectors, against 55 measured.");

        var census = Rows(root, ControlScopedFile).Select(static row => row.Split('\t')[0].Trim()).ToHashSet(StringComparer.Ordinal);

        var arrived = declared.Keys.Where(selector => !census.Contains(selector))
            .Order(StringComparer.Ordinal)
            .Select(selector => $"{selector}  — in {declared[selector]}")
            .ToList();

        var departed = census.Where(selector => !declared.ContainsKey(selector)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             {ControlScopedFile} is out of step with the control sheets.

             Declared by a control sheet and not in the census:
             {Lines(arrived)}

             In the census and declared by no control sheet any more:
             {Lines(departed)}

             Only the built controls can say whether a selector matches, so regenerate it there:

               dotnet test Core/Vixen.Ui.Controls.Advanced.Tests --filter "FullyQualifiedName~LiveCombinatorPairTests"

             with VIXEN_REGENERATE=1, and read the verdicts it writes before committing them.
             """
        );
    }

    /// <summary>
    ///     Every type-only selector that neither scoped census credits is named in the residue with
    ///     its kind and what builds it, and the residue names nothing a census credits.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The negative half of the scoped question, which both scoped censuses' headers said
    ///         nobody had made</b> (<c>Rikarin/Vixen#531</c>). A <c>-</c> in <c>ScopedSelectors.txt</c>
    ///         meant "unjudged" and nothing more, and with no file saying why, a rule that had died
    ///         read exactly like one waiting for content. The residue is the editor's <c>-</c> rows
    ///         less those <c>ControlScopedSelectors.txt</c> reads <c>Bare</c> — the controls' sweep
    ///         credits ten the editor cannot reach — and it is held exactly, as the pair residue is,
    ///         so a row a sweep starts crediting expires and a new uncredited selector is refused.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every reason cites a line, and the line has to be there.</b> A reason is the whole
    ///         value of a row and the one thing a regeneration could not write, so a row whose reason
    ///         names no <c>file:line</c>, or one the tree does not have, is refused rather than taken
    ///         on trust — the same resolution rule <c>RealPlanCitationTests</c> holds the documents to.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_selector_no_scoped_census_credits_is_named_here() {
        var root = RepositoryScan.Root();

        var editor = Verdicts(root, ScopedFile);
        var control = Verdicts(root, ControlScopedFile);

        var residue = editor.Where(row => row.Value == "-" && control.GetValueOrDefault(row.Key) != "Bare")
            .Select(static row => row.Key)
            .ToHashSet(StringComparer.Ordinal);

        var named = Unjudged(root);

        var unexplained = residue.Where(selector => !named.ContainsKey(selector)).Order(StringComparer.Ordinal).ToList();
        var expired = named.Keys.Where(selector => !residue.Contains(selector)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unexplained.Count == 0 && expired.Count == 0,
            $"""
             A type-only selector that neither scoped census credits, and that {UnjudgedFile} does not name:
             {Lines(unexplained)}

             Named in {UnjudgedFile} and credited by a scoped census now, or declared by no sheet any more:
             {Lines(expired)}

             Chase each new one to what builds it and add a row with its kind and a file:line; delete an
             expired one. A selector nothing can build is a `none` row and a finding, not a reason.
             """
        );

        var index = RepositoryScan.Files("*.cs")
            .Concat(RepositoryScan.Files("*.vxml"))
            .Concat(RepositoryScan.Files("*.vcss"))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToLookup(static path => Path.GetFileName(path), StringComparer.Ordinal);

        List<string> unfounded = [];

        foreach (var (selector, (kind, reason)) in named) {
            var citations = Cited.Matches(reason);

            if (!UnjudgedKinds.Contains(kind, StringComparer.Ordinal)) {
                unfounded.Add($"{selector} — '{kind}' is not one of {string.Join(", ", UnjudgedKinds)}");
            } else if (citations.Count == 0) {
                unfounded.Add($"{selector} — the reason cites no file:line");
            }

            string? file = null;

            foreach (Match citation in citations) {
                if (citation.Groups["file"].Success) {
                    file = citation.Groups["file"].Value;
                } else if (file is null) {
                    unfounded.Add($"{selector} — {citation.Value} continues no file named before it");

                    continue;
                }

                var line = int.Parse(citation.Groups["line"].Value);

                var resolves = index[Path.GetFileName(file)]
                    .Where(path => path == file || path.EndsWith("/" + file, StringComparison.Ordinal))
                    .Any(path => File.ReadLines(Path.Combine(root, path)).Count() >= line);

                if (!resolves) {
                    unfounded.Add($"{selector} — {file}:{line} is not a line in the tree");
                }
            }
        }

        Assert.True(unfounded.Count == 0, $"{UnjudgedFile} has rows whose reason is not evidence:\n{Lines(unfounded)}");
    }

    /// <summary>The joins above are over the real files, and the measured pairs are the real table.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this every test above passes loudest on the day it stops running.</b> Every set
    ///     difference is empty against an empty domain, and a census read as a header is an empty set
    ///     of proofs — which the residue test would report, but the stale-row test would call clean.
    ///     So each file is asserted to hold rows, and a pairing each file is known to prove is named.
    /// </remarks>
    [Fact]
    public void The_joins_read_the_real_censuses() {
        var root = RepositoryScan.Root();

        Assert.True(Declared().Count >= 60, $"the sheets declare only {Declared().Count} pairings, which is not the real table.");

        Assert.Contains("editor-shell > menu-bar", Rows(root, EditorCensusFiles[0]));
        Assert.Contains("split-view > split-bar", Rows(root, ControlCensusFiles[0]));
        Assert.Contains("tab-panels > tab-panel", Rows(root, ControlCensusFiles[1]));
        Assert.Contains("radial-item > icon", Reasons(root));

        // The scoped residue: the editor's census has `-` rows, the controls' credits some of them,
        // and the whole-selector row only this file can hold is named.
        Assert.True(Verdicts(root, ScopedFile).Count(static row => row.Value == "-") >= 20, $"{ScopedFile} has too few `-` rows to be the real census.");
        Assert.True(Verdicts(root, ControlScopedFile).Count(static row => row.Value == "Bare") >= 20, $"{ControlScopedFile} credits too few selectors to be the real census.");
        Assert.Equal("unreached", Unjudged(root)["shape-fields fact-value > numeric-input"].Kind);

        foreach (var file in EditorCensusFiles) {
            Assert.True(Rows(root, file).Count >= 10, $"{file} holds fewer than ten proofs, so it was not read.");
        }
    }

    /// <summary>The pairings the sheets declare now, measured rather than read off the domain file.</summary>
    static HashSet<string> Declared() =>
        CombinatorPairTests.Declared.Select(static pair => pair.Text).ToHashSet(StringComparer.Ordinal);

    /// <summary>The residue file's pairings, refusing a row with no reason for the pair gate's reason.</summary>
    static HashSet<string> Reasons(string root) {
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Rows(root, UnprovedFile)) {
            var columns = row.Split('\t');

            Assert.True(
                columns.Length >= 2 && columns[1].Trim().Length > 0,
                $"'{columns[0]}' in {UnprovedFile} has no reason beside it."
            );

            pairs.Add(columns[0].Trim());
        }

        return pairs;
    }

    /// <summary>A scoped census's verdicts: selector to the depth, <c>Bare</c> or <c>-</c> beside it.</summary>
    static Dictionary<string, string> Verdicts(string root, string file) =>
        Rows(root, file)
            .Select(static row => row.Split('\t'))
            .ToDictionary(static columns => columns[0].Trim(), static columns => columns.Length > 1 ? columns[1].Trim() : "", StringComparer.Ordinal);

    /// <summary>The scoped residue's rows: selector to its kind and its reason.</summary>
    static Dictionary<string, (string Kind, string Reason)> Unjudged(string root) {
        var rows = new Dictionary<string, (string Kind, string Reason)>(StringComparer.Ordinal);

        foreach (var row in Rows(root, UnjudgedFile)) {
            var columns = row.Split('\t');

            Assert.True(
                columns.Length == 3 && columns[2].Trim().Length > 0,
                $"'{columns[0]}' in {UnjudgedFile} is not <selector><TAB><kind><TAB><reason>."
            );

            rows.Add(columns[0].Trim(), (columns[1].Trim(), columns[2].Trim()));
        }

        return rows;
    }

    /// <summary>A committed census's rows, refusing one that has lost its header.</summary>
    static List<string> Rows(string root, string file) {
        var lines = File.ReadAllLines(Path.Combine(root, file));

        Assert.True(
            lines.Count(static line => line.StartsWith('#')) >= 5,
            $"{file} has lost its header, so it was emptied rather than answered."
        );

        return lines.Select(static line => line.Trim())
            .Where(static line => line.Length != 0 && !line.StartsWith('#'))
            .ToList();
    }

    static string Lines(IEnumerable<string> rows) {
        var joined = string.Join("\n", rows.Select(static row => $"  {row}"));

        return joined.Length == 0 ? "  (none)" : joined;
    }
}
