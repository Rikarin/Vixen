// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     <see cref="GuideReader" /> run over <em>this repository's own</em> <c>docs/guide/**</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every other test in this file's neighbourhood writes its subject into a temp directory,
///         which means the contract is thoroughly asserted and the guide is not.</b> The page contract
///         is checked for real only by <c>./build.sh CheckDocs</c>, and that target compiles the whole
///         solution in Release — so no agent runs it per branch, CLAUDE.md tells them not to, and a
///         page that breaks the contract is discovered on master by whoever merges next.
///     </para>
///     <para>
///         ⚠ <b>It has been discovered that way twice in a week</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/741">#741</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/793">#793</a>), and eight pages from two
///         unrelated workstreams were broken at once. Nothing about the contract needs a compilation:
///         it is five headings, in order, with something under each, plus the front matter and the
///         fences. This runs that half in under a second, on the tree it was compiled in.
///     </para>
///     <para>
///         ⚠ <b>What this does <em>not</em> replace.</b> <c>CheckDocs</c> also resolves every
///         <c>api:</c> id against the graph, compiles every <c>compile</c> fence, and fails on a page
///         nothing links to — all of which need the solution. A green run here is not a claim that
///         <c>CheckDocs</c> is green; it is a claim that the commonest way to red it is not present.
///         That is <c>CheckDocsCoverage</c>'s relationship to <c>CheckDocs</c>, one layer over.
///     </para>
/// </remarks>
public class RealGuideTests {
    /// <summary>
    ///     Pages known to break the contract, with the issue that will fix them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A tripwire, not a suppression.</b> Each entry names the issue that removes it, and
    ///         <see cref="Every_excused_page_still_breaks_the_contract" /> requires the page to still
    ///         be broken — so fixing one of these turns the list red rather than leaving a stale
    ///         excuse behind. That is the shape this workstream has settled on for a claim another
    ///         branch can invalidate: derive it, or excuse it by name with a reason and an owner.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It reached empty on the merge after it was written, and the mechanism stays.</b>
    ///         All six entries named #793 and master fixed every one of those pages while this branch
    ///         was in flight, so the tripwire fired six times at once — which is the list working
    ///         rather than the list being wrong. Deleting the machinery along with the entries would
    ///         mean the next excuse somebody needs has nowhere to be recorded and no test requiring
    ///         it to expire, so the empty list is the honest state and the theory below simply has no
    ///         cases while it holds.
    ///     </para>
    /// </remarks>
    static readonly (string Page, string Issue)[] Excused = [];

    /// <summary>
    ///     The checkout this assembly was compiled in.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The nearest root and never the outermost one.</b> <c>.claude/worktrees/</c> holds a
    ///     whole checkout per agent, so a walk that kept going would leave a worktree's test reading
    ///     the main tree's guide — asserting about a directory the run cannot change and missing the
    ///     one it can.
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

    static (IReadOnlyList<GuidePage> Pages, IReadOnlyList<string> Errors) Read() =>
        GuideReader.Read(Root, new SourceLinks(Root, "https://github.com/Rikarin/Vixen", commit: null));

    /// <summary>The walk found the guide, so a green run below is not a run over nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what this file prints on the day it stops reading the guide.</b> Without this,
    ///     the answer is "success": <see cref="GuideReader.Read" /> returns no pages and no errors
    ///     for a directory it cannot find, and every assertion under it would hold vacuously. The
    ///     floor is deliberately far below the real count — this is an instrument check, not a
    ///     census, and a census would be an exact-equality claim over a surface every slice grows.
    /// </remarks>
    [Fact]
    public void The_walk_reaches_the_guide() {
        var (pages, errors) = Read();

        Assert.True(
            pages.Count + errors.Count > 40,
            $"{Root} yielded {pages.Count} pages and {errors.Count} errors, which is too few to be "
            + "the guide. The walk has stopped reaching it, and every assertion here would pass."
        );
    }

    /// <summary>Every guide page satisfies the five-heading contract, the front matter and the fences.</summary>
    [Fact]
    public void Every_guide_page_keeps_the_page_contract() {
        var excused = Excused.Select(entry => entry.Page).ToHashSet(StringComparer.Ordinal);

        var unexpected = Read().Errors
            .Where(error => !excused.Any(page => error.StartsWith(page + ":", StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            "docs/guide pages that break the contract CheckDocs gates on:\n  "
            + string.Join("\n  ", unexpected)
            + "\n\nThe contract is GuideReader.RequiredHeadings — What it is · What it is for · Using "
            + "it · Examples · See also — present, in order, each with something under it."
        );
    }

    /// <summary>
    ///     ⚠ And every excused page is still broken, so the excuse cannot outlive the defect.
    /// </summary>
    /// <remarks>
    ///     Without this the list above is a suppression that survives its own fix, and the next page
    ///     added under one of those names inherits an exemption nobody granted it. Fixing a page
    ///     fails here, with the line to delete.
    ///     <para>
    ///         ⚠ <b>With the list empty this asserts nothing, and that is stated rather than hidden.</b>
    ///         All of its value is on the day somebody adds an entry — which is why it survives the
    ///         list reaching empty instead of being deleted with it. It was a theory over the entries
    ///         until the list emptied; xunit refuses a theory with no cases, so the same guard is a
    ///         fact over the collection.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_excused_page_still_breaks_the_contract() {
        var errors = Read().Errors;

        Assert.All(
            Excused,
            entry => Assert.True(
                errors.Any(error => error.StartsWith(entry.Page + ":", StringComparison.Ordinal)),
                $"{entry.Page} is excused from the page contract on #{entry.Issue} and now keeps it. Delete its "
                + "line from RealGuideTests.Excused — an exemption that outlives its defect is how the next page "
                + "written under that name inherits one nobody granted it."
            )
        );
    }

}
