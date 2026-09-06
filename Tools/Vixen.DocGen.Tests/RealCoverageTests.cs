// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     The two halves of <c>CheckDocs</c> that need no compiled graph, run over <em>this
///     repository's own</em> <c>docs/</c> — the production passes, not a re-implementation of them.
/// </summary>
/// <remarks>
///     <para>
///         <c>CheckDocs</c> reports three kinds of problem and only one of them needs the solution.
///         "<c>X</c> has no guide page and no line in <c>DocsExempt.txt</c>" is a claim about the
///         public surface, so it costs a Release build of everything and eleven minutes. But
///         "<c>X</c> is documented by <c>page</c> now — delete this line" and "nothing links to
///         <c>page</c>" are claims about committed text, and the code that makes them
///         (<see cref="Coverage.Check" />'s second loop and <see cref="PageLinks" />'s inbound
///         count) reads pages and a text file.
///     </para>
///     <para>
///         ⚠ <b>That distinction is why #480 stayed open for weeks.</b> Nobody runs the gate per
///         branch — CLAUDE.md tells them not to — so a page that documents an exempted type leaves
///         the stale line behind, and the next merge inherits it. Master's `checks` leg carried
///         twelve such lines and six orphaned pages in run <c>34003375702</c>; every one of them was
///         visible in the tree the whole time to anything that bothered to look.
///     </para>
///     <para>
///         ⚠ <b>What this deliberately does not claim.</b> A green run here is not a green
///         <c>CheckDocs</c> — the uncovered-type half is untouched and still needs the graph. This is
///         the same relationship <see cref="RealGuideTests" /> has to the page contract, one pass
///         over.
///     </para>
/// </remarks>
public class RealCoverageTests {
    /// <summary>The checkout this assembly was compiled in — the nearest root, never the outermost.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per parallel agent, so a walk that kept
    ///     going would leave a worktree's test asserting about the main tree's docs — a directory the
    ///     run cannot change, while missing the one it can.
    /// </remarks>
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

    /// <summary>
    ///     Both inputs are the real ones and both are large, so a green run below is a run over
    ///     something.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Ask what the two cases print on the day the walk stops finding the tree.</b> Without
    ///     this, the answer is "success" twice over: an empty page list orphans nothing and an
    ///     unreadable exemption file names no documented type. The floors are far below the real
    ///     counts on purpose — this is an instrument check and not a census, and a census would be an
    ///     exact-equality claim over two lists that every slice grows.
    /// </remarks>
    [Fact]
    public void The_walk_reaches_the_guide_and_the_exemption_list() {
        var (entries, errors) = Coverage.Read(Root);

        Assert.Empty(errors);
        Assert.True(entries.Count > 100, $"{Coverage.RelativePath} yielded {entries.Count} lines, which is too few to be it.");
        Assert.True(Pages().Count > 40, $"{Root}/docs/guide yielded too few pages to be the guide.");
    }

    /// <summary>
    ///     No line in <c>docs/DocsExempt.txt</c> names an id that a guide page's <c>api:</c> list
    ///     already claims. The exemption list may only shrink, and this is the direction it shrinks
    ///     in.
    /// </summary>
    [Fact]
    public void No_exemption_line_names_an_id_a_page_documents() {
        var claimed = Pages()
            .SelectMany(page => page.Front.Api.Select(id => (Id: id, page.Front.Slug)))
            .ToLookup(claim => claim.Id, claim => claim.Slug, StringComparer.Ordinal);

        var stale = Coverage.Read(Root).Entries
            .Where(entry => claimed.Contains(entry.Id))
            .Select(entry => $"{Coverage.RelativePath}:{entry.Line}: `{entry.Id}` is documented by "
                + $"`{string.Join("`, `", claimed[entry.Id])}` now — delete this line")
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} exemption line(s) name a type that already has a page:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, stale)
        );
    }

    /// <summary>
    ///     Every guide page that is not an index is linked from somewhere — prose or another page's
    ///     <c>related:</c> list. Prose nothing reaches was written and then lost.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="PageLinks.Check" /> is given an empty set of graph slugs, because the graph is
    ///     the expensive half. That makes its <c>/docs/api/…</c> resolution answer "names nothing the
    ///     graph has" for links this run cannot judge, so only the orphan problems are read back —
    ///     matched on the sentence <see cref="PageLinks" /> writes rather than by re-deriving the
    ///     inbound count here, which would be a second implementation to keep in step.
    /// </remarks>
    [Fact]
    public void Every_guide_page_is_linked_from_somewhere() {
        var orphans = PageLinks
            .Check(Pages(), new HashSet<string>(StringComparer.Ordinal))
            .Where(problem => problem.Contains("nothing links to", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            orphans.Count == 0,
            $"{orphans.Count} guide page(s) are reachable from nothing:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, orphans)
        );
    }
}
