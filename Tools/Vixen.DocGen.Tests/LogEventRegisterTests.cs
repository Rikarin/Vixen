// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     The log-event register, checked against the checkout it registers.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a test and not only a gate.</b> On 2026-08-11 three branches each allocated 13026,
///         each obeyed both of the register's rules — pick the next free id, add the row in the same
///         commit — and each was internally consistent. Only the union was wrong, which no per-branch
///         review and no hand-maintained markdown table can see. <c>CheckDocs</c> gates the same check
///         and is where the docs graph is built, but it needs a design-time build of the solution;
///         this runs on every push and in seconds, which is what a guard against a merge race has to
///         do to be worth having.
///     </para>
///     <para>
///         ⚠ <b>These read the real repository, not a fixture.</b> That is the point — the defect was
///         a property of the tree as a whole. The fixtures below are for the shapes the tree does not
///         currently contain, so that a rule still has a test after the tree stops violating it.
///     </para>
/// </remarks>
public sealed class LogEventRegisterTests {
    /// <summary>The checkout, found by walking up to the solution file.</summary>
    static string Root {
        get {
            var directory = AppContext.BaseDirectory;

            while (directory is not null && !File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                directory = Path.GetDirectoryName(directory);
            }

            return directory ?? throw new InvalidOperationException("no Vixen.slnx above the test binary");
        }
    }

    static IReadOnlyList<DocNode> Rows() =>
        [.. NonCSharpNodes.LogEvents(Root, new SourceLinks(Root, "https://github.com/rikarin/Vixen", null))];

    static IReadOnlyList<(string Id, string Page)> Claims() {
        var (pages, _) = GuideReader.Read(Root, new SourceLinks(Root, "https://github.com/rikarin/Vixen", null));

        return [.. pages.SelectMany(page => page.Front.Api.Select(id => (id, page.Front.Slug)))];
    }

    /// <summary>
    ///     The whole gate, over the real tree. One assertion rather than five, because the message is
    ///     the list and a reader wants every problem at once rather than the first one alphabetically.
    /// </summary>
    [Fact]
    public void TheRegisterAgreesWithTheTree() {
        var problems = LogEventRegister.Check(
            Rows(),
            LogEventRegister.Sites(Root),
            LogEventRegister.Ranges(Root),
            Claims());

        Assert.True(
            problems.Count == 0,
            $"{problems.Count} problem(s) between docs/manual/log-events.md, the [LoggerMessage] "
            + $"attributes and the guide pages:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", problems));
    }

    /// <summary>The register is not empty, so a green run cannot mean the parser stopped matching.</summary>
    /// <remarks>
    ///     The rows and the sites are read by two different parsers of two different languages, and a
    ///     regex that silently stops matching turns every check above into a tautology. Numbers rather
    ///     than exact counts: the point is "the parsers still see the tree", not a value to update on
    ///     every commit that adds a log line.
    /// </remarks>
    [Fact]
    public void BothHalvesAreRead() {
        Assert.True(Rows().Count > 50, "the register parser found almost no rows");
        Assert.True(LogEventRegister.Sites(Root).Count > 50, "the call-site scanner found almost no attributes");
        Assert.True(LogEventRegister.Ranges(Root).Count > 10, "the range table parser found almost no ranges");
        Assert.Contains(Claims(), claim => claim.Id.StartsWith("L:", StringComparison.Ordinal));
    }

    /// <summary>A test project's ids are its own, and are not the register's business.</summary>
    [Fact]
    public void TestProjectsAreOutOfScope() {
        var sites = LogEventRegister.Sites(Root);

        Assert.DoesNotContain(sites, site => site.Path.Contains(".Tests/", StringComparison.Ordinal));

        // Vixen.Core.Diagnostics.Tests logs under 1–5 and under 2001, which is Vulkan's. Both would be
        // failures if the scanner counted them, and neither is a defect.
        Assert.Single(sites, site => site.Id == 2001);
    }

    // ── The shapes the tree does not currently have ─────────────────────────────────────────────

    static DocNode Row(string id, string since = "0.1.0") => new() {
        Id = $"L:{id}",
        Kind = DocKind.LogEvent,
        Name = id,
        QualifiedName = id,
        Namespace = "LogEvents",
        Assembly = "Vixen.App",
        Area = "Core",
        Slug = $"log-events/{id}",
        Signature = [],
        Facets = new DocFacets { Level = "Information", Since = since }
    };

    static LogEventSite Site(int id, string project = "Vixen.App.Hosting", string area = "Core", int line = 1) =>
        new(id, "Information", "message", project, area, $"{area}/{project}/Log.cs", line);

    static readonly LogEventRange[] AppRange = [new(13000, 13999, ["Vixen.App"], true)];

    [Fact]
    public void TwoCallSitesOnOneIdFail() {
        var problems = LogEventRegister.Check(
            [Row("13026")],
            [Site(13026, line: 10), Site(13026, line: 20)],
            AppRange,
            []);

        Assert.Contains(problems, problem => problem.Contains("13026 is claimed by 2 call sites", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdWithNoRowFails() {
        var problems = LogEventRegister.Check([], [Site(13026)], AppRange, []);

        Assert.Contains(problems, problem => problem.Contains("has no row in docs/manual/log-events.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ARowWithNoCallSiteFails() {
        var problems = LogEventRegister.Check([Row("13026")], [], AppRange, []);

        Assert.Contains(problems, problem => problem.Contains("has a row and no [LoggerMessage]", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The rules keep a retired id's row so an old log still decodes, so "no call site" is only a
    ///     failure when the row does not say the line is gone.
    /// </summary>
    [Fact]
    public void ARetiredRowWithNoCallSiteIsFine() {
        var problems = LogEventRegister.Check(
            [Row("13026", "0.1.0, retired in 0.2.0")],
            [],
            AppRange,
            []);

        Assert.DoesNotContain(problems, problem => problem.Contains("13026", StringComparison.Ordinal));
    }

    /// <summary>The other half of permanence: a retired id is never given to a new call site.</summary>
    [Fact]
    public void ARetiredIdThatIsLoggedAgainFails() {
        var problems = LogEventRegister.Check(
            [Row("13026", "0.1.0, retired in 0.2.0")],
            [Site(13026)],
            AppRange,
            []);

        Assert.Contains(problems, problem => problem.Contains("is marked retired and is", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoPagesClaimingOneEventFail() {
        var problems = LogEventRegister.Check(
            [Row("13027")],
            [Site(13027)],
            AppRange,
            [("L:13027", "rendering/capturing-a-frame"), ("L:13027", "rendering/diagnostic-overlays")]);

        Assert.Contains(problems, problem =>
            problem.Contains("`api: L:13027` is claimed by", StringComparison.Ordinal)
            && problem.Contains("capturing-a-frame", StringComparison.Ordinal)
            && problem.Contains("diagnostic-overlays", StringComparison.Ordinal));
    }

    [Fact]
    public void APageClaimingAnUnregisteredEventFails() {
        var problems = LogEventRegister.Check([], [], AppRange, [("L:13030", "rendering/capturing-a-frame")]);

        Assert.Contains(problems, problem =>
            problem.Contains("`api: L:13030` names no row", StringComparison.Ordinal));
    }

    /// <summary>A `T:` id is the graph's business and not this gate's.</summary>
    [Fact]
    public void SymbolClaimsAreIgnored() {
        var problems = LogEventRegister.Check([], [], [], [
            ("T:Vixen.App.VixenApp", "engine/one"), ("T:Vixen.App.VixenApp", "engine/two")
        ]);

        Assert.Empty(problems);
    }

    [Fact]
    public void AnIdOutsideEveryRangeFails() {
        var problems = LogEventRegister.Check([Row("27001")], [Site(27001, "Vixen.Live.Orchestrator", "Live")], AppRange, []);

        Assert.Contains(problems, problem =>
            problem.Contains("is in no range the register allocates", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdLoggedFromTheWrongAssemblyFails() {
        var problems = LogEventRegister.Check(
            [Row("13026")],
            [Site(13026, "Vixen.Rendering", "Core")],
            AppRange,
            []);

        Assert.Contains(problems, problem =>
            problem.Contains("is logged from `Vixen.Rendering`", StringComparison.Ordinal));
    }

    /// <summary>
    ///     `Vixen.App` owns `Vixen.App.Hosting`, and does not own `Vixen.Application`.
    /// </summary>
    [Fact]
    public void ARangeOwnsItsPrefixAndNotAWordThatStartsTheSame() {
        Assert.True(AppRange[0].Owns(Site(13026, "Vixen.App.Hosting")));
        Assert.True(AppRange[0].Owns(Site(13026, "Vixen.App")));
        Assert.False(AppRange[0].Owns(Site(13026, "Vixen.Application")));
    }

    /// <summary>`Samples/*` is a folder, so it matches the area rather than the project name.</summary>
    [Fact]
    public void AFolderRangeMatchesTheArea() {
        LogEventRange samples = new(14000, 14999, ["Samples/"], true);

        Assert.True(samples.Owns(Site(14001, "11-VideoPlayback", "Samples")));
        Assert.False(samples.Owns(Site(14001, "Vixen.App.Hosting", "Core")));
    }

    [Fact]
    public void ARangeMarkedReservedThatIsLoggedFromFails() {
        var problems = LogEventRegister.Check(
            [Row("13026")],
            [Site(13026)],
            [new LogEventRange(13000, 13999, ["Vixen.App"], false)],
            []);

        Assert.Contains(problems, problem => problem.Contains("is marked reserved and is", StringComparison.Ordinal));
    }

    [Fact]
    public void ARangeMarkedInUseThatNothingLogsInFails() {
        var problems = LogEventRegister.Check([], [], AppRange, []);

        Assert.Contains(problems, problem => problem.Contains("is marked **in use** and", StringComparison.Ordinal));
    }
}
