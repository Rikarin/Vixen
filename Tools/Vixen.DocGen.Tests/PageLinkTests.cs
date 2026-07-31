// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>Resolution and orphans — docs/plan/25 § Part 5, read in both directions.</summary>
public class PageLinkTests {
    static GuidePage Page(string slug, string body = "", params string[] related) => new() {
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
        Headings = [],
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
    [Theory]
    [InlineData("[spec](https://example.org/spec)")]
    [InlineData("[the heading](#what-it-is)")]
    public void LinksThisPassDoesNotOwnArePassedOver(string link) {
        Assert.Empty(Check([Page("ecs/index", $"See {link}.")]));
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
