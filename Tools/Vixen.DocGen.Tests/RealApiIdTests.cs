// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     Every <c>api:</c> id a guide page claims, resolved against id sets this repository already
///     keeps in committed text — no Release build of the solution, no graph.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A mistyped <c>api:</c> id was silently green in every <c>dotnet test</c> run</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1145">#1145</a>). The two real-tree
///         coverage tests next door cannot see one:
///         <see cref="RealCoverageTests.No_exemption_line_names_an_id_a_page_documents" /> compares
///         two sets, so a page claiming <c>T:…LibraryIrCal</c> (one <c>l</c>) is a claim that matches
///         nothing and stays quiet, and
///         <see cref="RealCoverageTests.Every_guide_page_is_linked_from_somewhere" /> is an orphan
///         check handed an empty graph on purpose. Ask what either prints on the day a page's claims
///         are wrong and the answer is "success", twice.
///     </para>
///     <para>
///         The check that catches it — <c>`api: {id}` names nothing the graph has</c> — runs only
///         inside <c>Docs</c>/<c>CheckDocs</c>, and ⚠ <c>Docs</c> is not a gate: it prints the
///         problem and exits 0. So a typo cost the type its page, left the exemption line deleted in
///         the same commit, and surfaced eleven minutes and several merges later as somebody else's
///         red.
///     </para>
///     <para>
///         <b>Two id sets, and the split is what makes this exact rather than approximate.</b> The
///         non-C# half — shaders, diagnostics, log events — is <em>already</em> derived from
///         committed text by <see cref="NonCSharpNodes" />, which the graph itself calls, so those
///         ids are checked completely and with no allowance at all. The C# half is checked against
///         the <c>PublicAPI</c> baselines, which <c>CheckApi</c> gates, <em>scoped to the namespaces
///         those baselines cover</em> — see <see cref="Every_api_id_a_baseline_could_carry_is_in_one" />
///         for why that scoping is the whole design and not a hedge.
///     </para>
///     <para>
///         ⚠ <b>What this deliberately does not claim.</b> A green run here is not a green
///         <c>CheckDocs</c>: it says every claimed id names something, never that everything is
///         claimed. That direction is <c>CheckDocsCoverage</c>'s and the graph's.
///     </para>
/// </remarks>
public class RealApiIdTests {
    /// <summary>Directories a walk of the checkout must not descend into.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per parallel agent, so a walk that kept
    ///     going would read another branch's baselines and answer about a tree this run cannot
    ///     change — the false positive that stopped <c>SharedUiShaderTests</c> reaching its own tree.
    /// </remarks>
    static readonly string[] Skipped = [".git", ".claude", ".nuke", "bin", "obj", "artifacts", "node_modules"];

    /// <summary>
    ///     The ids whose namespace a baseline covers and whose own project keeps no baseline.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One namespace, two projects, and only one of them baselined.</b>
    ///         <c>Core/Vixen.App.Hosting</c> declares most of <c>Vixen.App</c> and keeps a baseline;
    ///         <c>Tools/Vixen.App</c> declares these three and keeps none — no <c>PublicAPI</c> file
    ///         exists anywhere under <c>Tools/</c>, which is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/749">#749</a>'s subject and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/641">#641</a>'s blocker. So the
    ///         namespace scoping below reads them as "should have been baselined" and it is the
    ///         coverage that is missing, not the id.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The list can only shrink</b>, which <see cref="The_unbaselined_list_can_only_shrink" />
    ///         enforces in both directions: the day <c>Tools/Vixen.App</c> grows a baseline these
    ///         three fail as stale rather than sitting here excusing a typo in the same namespace.
    ///     </para>
    /// </remarks>
    static readonly string[] Unbaselined = [
        "T:Vixen.App.GraphicsHost",
        "T:Vixen.App.PlatformHost",
        "T:Vixen.App.VixenApp"
    ];

    /// <summary>The checkout this assembly was compiled in — the nearest root, never the outermost.</summary>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null) {
                if (Directory.Exists(Path.Combine(directory.FullName, "docs", "guide"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No docs/guide above {AppContext.BaseDirectory}. This test reads the repository it "
                + "was compiled in, so an output directory outside the checkout breaks it."
            );
        }
    }

    static IReadOnlyList<GuidePage> Pages() =>
        GuideReader.Read(Root, new SourceLinks(Root, "https://github.com/Rikarin/Vixen", commit: null)).Pages;

    /// <summary>Every id every page claims, in page order.</summary>
    static List<(string Id, string Path)> Claims() => [
        .. Pages().SelectMany(page => page.Front.Api.Select(id => (Id: id, page.Path)))
    ];

    /// <summary>Every <c>PublicAPI</c> baseline in this checkout.</summary>
    static List<string> Baselines() {
        var found = new List<string>();

        Walk(Root, found);
        found.Sort(StringComparer.Ordinal);
        return found;

        static void Walk(string directory, List<string> into) {
            foreach (var file in Directory.GetFiles(directory)) {
                if (Path.GetFileName(file) is "PublicAPI.Shipped.txt" or "PublicAPI.Unshipped.txt") {
                    into.Add(file);
                }
            }

            foreach (var child in Directory.GetDirectories(directory)) {
                if (!Skipped.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                    Walk(child, into);
                }
            }
        }
    }

    /// <summary>The type ids the baselines declare, with the <c>T:</c> a page writes.</summary>
    static HashSet<string> BaselinedIds() => [
        .. Baselines()
            .SelectMany(baseline => PublicApiTypeNames.BaselinedIds(File.ReadAllLines(baseline)))
            .Select(id => "T:" + id)
    ];

    /// <summary>Everything before an id's last dot — the namespace, or a namespace and an outer type.</summary>
    static string Owner(string id) {
        var dot = id.LastIndexOf('.');
        return dot < 0 ? id : id[..dot];
    }

    /// <summary>
    ///     Every input is the real one and every one of them is large, so a green run below is a run
    ///     over something.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Ask what the three cases print on the day the walk stops finding the tree.</b> Without
    ///     this the answer is "success" three times: no pages claim no ids, an unread baseline
    ///     declares no types, and an unread register offers no nodes — and every assertion below is
    ///     over an empty set. The floors are far below the real counts on purpose; this is an
    ///     instrument check and not a census.
    /// </remarks>
    [Fact]
    public void The_walk_reaches_the_guide_the_baselines_and_the_registers() {
        var links = new SourceLinks(Root, "https://github.com/Rikarin/Vixen", commit: null);

        Assert.True(Baselines().Count > 100, $"{Root} yielded too few PublicAPI baselines to be this repository.");
        Assert.True(BaselinedIds().Count > 3500, "The baselines yielded too few types — the format has moved.");
        Assert.True(Claims().Count > 1000, $"{Root}/docs/guide yielded too few `api:` claims to be the guide.");
        Assert.True(NonCSharpNodes.Read(Root, links).Count > 50, "The shader and register walk found too little.");
    }

    /// <summary>
    ///     Every <c>R:</c>, <c>D:</c> and <c>L:</c> id a page claims is a node the shader reflection
    ///     and the two registers actually produce.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This half is exact and has no allowance, because its id set is the graph's own.</b>
    ///     <see cref="NonCSharpNodes" /> reads <c>Raven/Library/**/*.reflect.json</c>,
    ///     <c>docs/manual/diagnostic-codes.md</c> and the log-event register — committed text, no
    ///     compilation — and <c>Program</c> puts exactly these nodes in the graph the
    ///     <c>names nothing the graph has</c> check runs against. So for a shader, a diagnostic code
    ///     or a log event, this test and <c>CheckDocs</c> ask the identical question, one in
    ///     milliseconds and the other in eleven minutes.
    /// </remarks>
    [Fact]
    public void Every_shader_diagnostic_and_log_event_id_names_a_real_node() {
        var links = new SourceLinks(Root, "https://github.com/Rikarin/Vixen", commit: null);
        var nodes = NonCSharpNodes.Read(Root, links).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        var claimed = Claims()
            .Where(claim => claim.Id.StartsWith("R:", StringComparison.Ordinal)
                || claim.Id.StartsWith("D:", StringComparison.Ordinal)
                || claim.Id.StartsWith("L:", StringComparison.Ordinal))
            .ToList();

        // A loop that asserts inside itself passes vacuously on an empty collection, so the count is
        // part of what is asserted: the guide claims shaders, diagnostics and log events today.
        Assert.True(claimed.Count > 50, $"Only {claimed.Count} non-C# id(s) claimed, which is too few to be the guide.");

        var wrong = claimed
            .Where(claim => !nodes.Contains(claim.Id))
            .Select(claim => $"{claim.Path}: `api: {claim.Id}` names nothing the registers have")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} `api:` id(s) name no shader, diagnostic or log event:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, wrong)
        );
    }

    /// <summary>
    ///     Every <c>T:</c> id whose namespace a <c>PublicAPI</c> baseline covers is itself in one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Scoped to the namespaces a baseline reaches, and that scoping is the design.</b>
    ///         Only 132 of the 421 projects keep a baseline, so "every claimed type is baselined"
    ///         would report 443 perfectly good ids from <c>Vixen.Editor.*</c>, <c>Vixen.Cli</c> and
    ///         the rest — a rule with that many false positives is one nobody reads. But a namespace
    ///         a baseline covers is covered <em>completely</em>: <c>CheckApi</c>'s analyzer refuses a
    ///         public type with no line, so within such a namespace an id that is not baselined names
    ///         nothing. That is exactly the typo, and it is where the typos are — 1 965 of the
    ///         2 422 claimed ids fall inside a covered namespace.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The one thing the scoping cannot see is a namespace split across a baselined and
    ///         an unbaselined project</b>, which is real and is <see cref="Unbaselined" />: three
    ///         <c>Tools/Vixen.App</c> types inside <c>Core/Vixen.App.Hosting</c>'s namespace. Three
    ///         named lines, shrink-only, rather than a rule loosened until it says nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_api_id_a_baseline_could_carry_is_in_one() {
        var baselined = BaselinedIds();
        var covered = baselined.Select(Owner).ToHashSet(StringComparer.Ordinal);

        var claimed = Claims()
            .Where(claim => claim.Id.StartsWith("T:", StringComparison.Ordinal))
            .Where(claim => covered.Contains(Owner(claim.Id)))
            .ToList();

        Assert.True(claimed.Count > 1000, $"Only {claimed.Count} claimed type id(s) fall in a baselined namespace.");

        var wrong = claimed
            .Where(claim => !baselined.Contains(claim.Id))
            .Where(claim => !Unbaselined.Contains(claim.Id, StringComparer.Ordinal))
            .Select(claim => $"{claim.Path}: `api: {claim.Id}` names no type in any PublicAPI baseline, "
                + $"and `{Owner(claim.Id)}` is a namespace the baselines cover — so it is a typo, a "
                + "type that was renamed, or one that stopped being public")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} `api:` id(s) name nothing:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, wrong)
        );
    }

    /// <summary>
    ///     ⚠ Both directions, which is what makes <see cref="Unbaselined" /> only ever shrink. A line
    ///     that has done its job — the project grew a baseline, or the page stopped claiming the id —
    ///     cannot sit here excusing the next typo in the same namespace, which is the property
    ///     <c>docs/WhitespaceExempt.txt</c> and <c>docs/DocsExempt.txt</c> both have.
    /// </summary>
    [Fact]
    public void The_unbaselined_list_can_only_shrink() {
        var baselined = BaselinedIds();
        var covered = baselined.Select(Owner).ToHashSet(StringComparer.Ordinal);
        var claimed = Claims().Select(claim => claim.Id).ToHashSet(StringComparer.Ordinal);

        var stale = Unbaselined
            .Where(id => baselined.Contains(id) || !claimed.Contains(id) || !covered.Contains(Owner(id)))
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} line(s) in Unbaselined no longer need to be there — the type is baselined now, "
            + $"no page claims it, or its namespace left the baselines: {string.Join(", ", stale)}"
        );
    }
}
