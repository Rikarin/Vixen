// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>Resolution and orphans — docs/plan/25 § Part 5, read in both directions.</summary>
public class PageLinkTests {
    static GuidePage Page(string slug, string body = "", params string[] related) => Page(slug, body, related, []);

    static GuidePage Page(string slug, string body, string[] related, IReadOnlyList<DocHeading> headings) => new() {
        Front = new FrontMatter(
            Title: slug,
            Slug: slug,
            Kind: "guide",
            Area: "ECS",
            Summary: "One sentence.",
            Api: ["T:Vixen.Ecs.World"],
            Tags: [],
            Since: null,
            Status: "stable",
            Related: related,
            Breaking: []),
        Path = $"docs/guide/{slug}.md",
        Body = body,
        Headings = headings,
        Examples = []
    };

    static IReadOnlyList<string> Check(IReadOnlyList<GuidePage> pages, params string[] slugs) =>
        PageLinks.Check(pages, slugs.ToHashSet(StringComparer.Ordinal));

    // ── Resolution ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASiteLinkToAPageResolves() {
        Assert.Empty(Check([
            Page("ecs/index", "See [queries](/docs/guide/ecs/queries)."),
            Page("ecs/queries")
        ]));
    }

    [Fact]
    public void ASiteLinkToNoPageIsReported() {
        var problems = Check([Page("ecs/index", "See [chunks](/docs/guide/ecs/chunks).")]);

        Assert.Contains(problems, problem => problem.Contains("names no guide page", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Relative markdown links are how a page reads on GitHub, and therefore how most of them get
    ///     written — resolved against the page's real path rather than pattern-matched on the slug.
    /// </summary>
    [Fact]
    public void ARelativeMarkdownLinkResolves() {
        Assert.Empty(Check([
            Page("ecs/index", "See [queries](queries.md) and [materials](../rendering/materials.md)."),
            Page("ecs/queries"),
            Page("rendering/materials")
        ]));
    }

    [Fact]
    public void ARelativeMarkdownLinkToNothingIsReported() {
        var problems = Check([Page("ecs/index", "See [chunks](chunks.md).")]);

        Assert.Contains(problems, problem => problem.Contains("chunks.md", StringComparison.Ordinal));
    }

    [Fact]
    public void AnApiLinkResolvesAgainstTheGraph() {
        Assert.Empty(Check([Page("ecs/index", "See [World](/docs/api/vixen.ecs/world).")], "vixen.ecs/world"));
    }

    [Fact]
    public void AnApiLinkToNoSymbolIsReported() {
        var problems = Check([Page("ecs/index", "See [Realm](/docs/api/vixen.ecs/realm).")], "vixen.ecs/world");

        Assert.Contains(problems, problem => problem.Contains("nothing the graph has", StringComparison.Ordinal));
    }

    [Fact]
    public void ARouteTheSiteDoesNotServeIsReported() {
        var problems = Check([Page("ecs/index", "See [the tour](/docs/tour).")]);

        Assert.Contains(problems, problem => problem.Contains("not a route", StringComparison.Ordinal));
    }

    /// <summary>External links rot on someone else's schedule; a gate that watched them would be flaky.</summary>
    [Fact]
    public void AnExternalLinkIsPassedOver() {
        Assert.Empty(Check([Page("ecs/index", "See [spec](https://example.org/spec).")]));
    }

    /// <summary>A slug written whole, and a sibling named by its last segment. Both are in the tree.</summary>
    [Theory]
    [InlineData("rendering/materials")]
    [InlineData("../rendering/materials")]
    public void ASlugWithoutAnExtensionResolves(string href) {
        Assert.Empty(Check([Page("ecs/index", $"See [materials]({href})."), Page("rendering/materials")]));
    }

    [Fact]
    public void ASiblingNamedByItsLastSegmentResolves() {
        Assert.Empty(Check([Page("ecs/index", "See [queries](queries)."), Page("ecs/queries")]));
    }

    [Fact]
    public void ASlugThatNamesNoPageIsReported() {
        var problems = Check([Page("ecs/index", "See [chunks](ecs/chunks).")]);

        Assert.Contains(problems, problem => problem.Contains("names no guide page", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A heading is renamed far more often than a page is, and a dead anchor lands the reader at
    ///     the top of the right page — which reads as having worked.
    /// </summary>
    [Fact]
    public void AnAnchorThatNamesNoHeadingIsReported() {
        var problems = Check([
            Page("ecs/index", "See [that bit](queries#chunks)."),
            Page("ecs/queries", "", [], [new DocHeading("what-it-is", "What it is", 2)])
        ]);

        Assert.Contains(problems, problem => problem.Contains("names no heading", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnchorThatNamesAHeadingResolves() {
        Assert.Empty(Check([
            Page("ecs/index", "See [that bit](queries#what-it-is)."),
            Page("ecs/queries", "", [], [new DocHeading("what-it-is", "What it is", 2)])
        ]));
    }

    // ── Rewriting — the same resolution, written into the body ──────────────────────────────────

    /// <summary>
    ///     ⚠ The three relative shapes are all 404s in a browser as written, and each was in the tree
    ///     in the hundreds.
    /// </summary>
    [Theory]
    [InlineData("[queries](queries.md)", "[queries](/docs/guide/ecs/queries)")]
    [InlineData("[materials](../rendering/materials.md)", "[materials](/docs/guide/rendering/materials)")]
    [InlineData("[materials](rendering/materials)", "[materials](/docs/guide/rendering/materials)")]
    [InlineData("[queries](queries)", "[queries](/docs/guide/ecs/queries)")]
    [InlineData("[queries](queries.md#what-it-is)", "[queries](/docs/guide/ecs/queries#what-it-is)")]
    public void AnInTreeLinkIsRewrittenToTheRouteTheSiteServes(string written, string served) {
        var pages = PageLinks.WithSiteLinks([
            Page("ecs/index", $"See {written}."),
            Page("ecs/queries", "", [], [new DocHeading("what-it-is", "What it is", 2)]),
            Page("rendering/materials")
        ]);

        Assert.Equal($"See {served}.", pages[0].Body);
    }

    /// <summary>
    ///     ⚠ A bare `#anchor` is the one that looks fine and is not: the application ships
    ///     <c>&lt;base href="/"&gt;</c>, so it resolves to the site root.
    /// </summary>
    [Fact]
    public void ABareAnchorIsRewrittenToHangOffThePageItIsOn() {
        var pages = PageLinks.WithSiteLinks([
            Page("ecs/queries", "See [below](#what-it-is).", [], [new DocHeading("what-it-is", "What it is", 2)])
        ]);

        Assert.Equal("See [below](/docs/guide/ecs/queries#what-it-is).", pages[0].Body);
    }

    [Theory]
    [InlineData("[spec](https://example.org/spec)")]
    [InlineData("[World](/docs/api/vixen.ecs/world)")]
    [InlineData("[the shaders](/docs/shaders)")]
    public void ALinkThisPassDoesNotOwnIsLeftAsWritten(string link) {
        var pages = PageLinks.WithSiteLinks([Page("ecs/index", $"See {link}.")]);

        Assert.Equal($"See {link}.", pages[0].Body);
    }

    /// <summary>An unresolved link keeps what its author wrote, so the message can name that string.</summary>
    [Fact]
    public void AnUnresolvedLinkIsLeftAsWritten() {
        var pages = PageLinks.WithSiteLinks([Page("ecs/index", "See [chunks](chunks.md).")]);

        Assert.Equal("See [chunks](chunks.md).", pages[0].Body);
    }

    [Fact]
    public void ALinkTitleSurvivesTheRewrite() {
        var pages = PageLinks.WithSiteLinks([
            Page("ecs/index", """See [queries](queries.md "Entity queries")."""),
            Page("ecs/queries")
        ]);

        Assert.Equal("""See [queries](/docs/guide/ecs/queries "Entity queries").""", pages[0].Body);
    }

    [Fact]
    public void ALinkTitleIsNotPartOfTheTarget() {
        Assert.Empty(Check([
            Page("ecs/index", """See [queries](/docs/guide/ecs/queries "Entity queries")."""),
            Page("ecs/queries")
        ]));
    }

    // ── Orphans ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APageNothingLinksToIsReported() {
        var problems = Check([Page("ecs/index"), Page("ecs/queries")]);

        Assert.Contains(problems, problem => problem.Contains("nothing links to `ecs/queries`", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIndexIsARootAndNeedsNoInboundLink() {
        Assert.Empty(Check([Page("index"), Page("ecs/index")]));
    }

    /// <summary>`related:` is rendered as navigation, so it is a link.</summary>
    [Fact]
    public void ARelatedEntryCountsAsALink() {
        Assert.Empty(Check([Page("ecs/index"), Page("ecs/queries", related: "ecs/components"), Page("ecs/components", related: "ecs/queries")]));
    }

    [Fact]
    public void APageThatOnlyLinksToItselfIsStillAnOrphan() {
        var problems = Check([Page("ecs/queries", "See [this page](/docs/guide/ecs/queries).")]);

        Assert.Contains(problems, problem => problem.Contains("nothing links to", StringComparison.Ordinal));
    }
}
