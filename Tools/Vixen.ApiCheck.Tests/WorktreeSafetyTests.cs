// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ The half of <c>PruneWorktrees</c> that no machine can show you: the verdict that deletes a
///     checkout.
/// </summary>
/// <remarks>
///     <para>
///         A removable agent worktree is by definition one nobody has, so the target's positive
///         branch is unreachable in every real run — and four audits of #561 in a row could only
///         reach it by forcing a predicate true and reading the log. That proves the branch is
///         <em>reached</em>. It leaves nothing behind that would notice the next edit, and each of
///         these four conditions can be reversed without anything failing to build.
///     </para>
///     <para>
///         So the decision is a pure function over five answers and these are its tests. The
///         negative cases each fail on their own — that is the property that matters, because the
///         guard that is silently doing nothing is the one that looks identical to the guard that
///         is working.
///     </para>
/// </remarks>
public sealed class WorktreeSafetyTests {
    static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     The verdict `--remove-merged` acts on, which is the one no live machine produces.
    /// </summary>
    [Fact]
    public void MergedCleanUnlockedAndIdleIsTheOnlyRemovableState() =>
        Assert.Empty(Reasons(locked: null, head: "abc", merged: true, clean: true, written: Now.AddHours(-4)));

    /// <summary>Each condition refuses on its own, with a reason somebody can act on.</summary>
    [Theory]
    [InlineData("claude agent wf_1 (pid 3)", "abc", true, true, -4.0, "locked")]
    [InlineData(null, "abc", false, true, -4.0, "has commits master does not")]
    [InlineData(null, "abc", true, false, -4.0, "has uncommitted changes")]
    [InlineData(null, "abc", true, true, -0.1, "written")]
    [InlineData(null, "", true, true, -4.0, "git reported no HEAD for it")]
    public void EveryConditionRefusesOnItsOwn(
        string? locked,
        string head,
        bool merged,
        bool clean,
        double writtenHoursAgo,
        string expected
    ) {
        var reasons = Reasons(locked, head, merged, clean, Now.AddHours(writtenHoursAgo));

        Assert.Contains(reasons, reason => reason.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ An empty HEAD must mean git is never asked, and not "asked and answered no".
    /// </summary>
    /// <remarks>
    ///     <c>git rev-list --count "master.."</c> is not an error: the missing right-hand side means
    ///     <c>HEAD</c>, and the HEAD it means belongs to the checkout the target is running in. It
    ///     answers, it exits zero, and it says nothing about having changed the subject — the same
    ///     defect as #561's own 3.8 GB stray directory answering about its parent repository, one
    ///     level in. The two reasons are also kept distinct on purpose: "no HEAD" is the parse
    ///     having nothing to ask about, which a person can act on, and "has commits master does
    ///     not" is a fact about a branch, which they cannot.
    /// </remarks>
    [Fact]
    public void AnEmptyHeadIsRefusedRatherThanAskedAbout() {
        var asked = false;

        var reasons = WorktreeSafety.KeepReasons(
            locked: null,
            head: "   ",
            isMergedIntoMaster: _ => {
                asked = true;

                return true;
            },
            isClean: () => true,
            footprintRead: true,
            newestWrite: Now.AddHours(-4),
            now: Now,
            idleMinutes: 30
        );

        Assert.False(asked, "an empty head was passed to git, which would answer about a different worktree.");
        Assert.Equal(["git reported no HEAD for it"], reasons);
    }

    /// <summary>
    ///     ⚠ The recency window refuses the merge-then-prune sweep, which is a described consequence
    ///     rather than a regression.
    /// </summary>
    /// <remarks>
    ///     An agent's last act is a commit, so at the moment its branch is merged its worktree is the
    ///     most recently written thing on the disk. A prune run straight after a batch merge
    ///     therefore reports that batch as <c>keep … written N min ago</c> — and reads as a failure
    ///     of the merged predicate when it is not. <c>--idle-minutes 0</c> is the way past it, and
    ///     restores the three-condition behaviour exactly.
    /// </remarks>
    [Fact]
    public void TheBatchThatWasJustMergedIsKeptUntilIdleMinutesIsTurnedOff() {
        var justMerged = Now.AddMinutes(-2);

        Assert.Equal(
            ["written 2 min ago, inside --idle-minutes 30"],
            Reasons(locked: null, head: "abc", merged: true, clean: true, written: justMerged)
        );

        Assert.Empty(
            WorktreeSafety.KeepReasons(null, "abc", _ => true, () => true, true, justMerged, Now, idleMinutes: 0)
        );
    }

    /// <summary>
    ///     ⚠ The comparison is not reversed, which is the one arithmetic here and the one that
    ///     builds cleanly either way.
    /// </summary>
    /// <remarks>
    ///     Reversed, the target keeps every idle worktree — recovering nothing, for ever — and offers
    ///     up the busy one, which is the checkout an agent is compiling in. Both directions print a
    ///     plausible table.
    /// </remarks>
    [Fact]
    public void RecencyIsAboutHowRecentlyItWasWrittenAndNotHowLongAgo() {
        Assert.NotNull(WorktreeSafety.StillBeingWrittenTo(Now.AddMinutes(-29), Now, 30));
        Assert.Null(WorktreeSafety.StillBeingWrittenTo(Now.AddMinutes(-31), Now, 30));
        Assert.Null(WorktreeSafety.StillBeingWrittenTo(Now.AddMinutes(-1), Now, 0));
        Assert.Null(WorktreeSafety.StillBeingWrittenTo(null, Now, 30));
    }

    /// <summary>Two failed conditions are both reported, so one fix does not reveal the next.</summary>
    [Fact]
    public void EveryFailedConditionIsNamedRatherThanTheFirst() {
        var reasons = Reasons("claude agent (pid 3)", "abc", merged: false, clean: false, written: Now.AddMinutes(-1));

        Assert.Equal(4, reasons.Count);
    }

    /// <summary>
    ///     ⚠ A worktree whose files could not be walked is kept, and it used to be offered up.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Measure</c> answers <see langword="null" /> for a newest write in two unrelated
    ///         cases — a directory with no files in it, and a directory whose walk threw
    ///         <c>IOException</c> or <c>UnauthorizedAccessException</c> partway — and the idle
    ///         condition read both as "nothing has touched this". So on the day the fourth safety
    ///         condition could not run, its verdict was <i>removable</i>: an instrument that did not
    ///         run reporting success, which is the failure this repository has met in a comparator, a
    ///         parity test and eighteen goldens.
    ///     </para>
    ///     <para>
    ///         ⚠ And it is not a hypothetical shape here: a walk of ~100 000 files under a worktree
    ///         an agent is compiling in is exactly the walk whose entries vanish underneath it, which
    ///         is #770's case — the live agent with no lock whose branch is already merged.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AWorktreeWhoseFilesCouldNotBeWalkedIsKeptRatherThanCalledIdle() {
        var unreadable = WorktreeSafety.KeepReasons(
            locked: null,
            head: "abc",
            isMergedIntoMaster: _ => true,
            isClean: () => true,
            footprintRead: false,
            newestWrite: null,
            now: Now,
            idleMinutes: 30
        );

        Assert.Equal(["its files could not be walked, so the idle check could not run"], unreadable);

        // The other half, so the reason is about the failed walk and not about the null: a walk that
        // completed and found nothing is a real answer, and `--idle-minutes 0` still turns the whole
        // condition off rather than being overridden by it.
        Assert.Empty(
            WorktreeSafety.KeepReasons(null, "abc", _ => true, () => true, true, null, Now, idleMinutes: 30)
        );

        Assert.Empty(
            WorktreeSafety.KeepReasons(null, "abc", _ => true, () => true, false, null, Now, idleMinutes: 0)
        );
    }

    static IReadOnlyList<string> Reasons(string? locked, string head, bool merged, bool clean, DateTime written) =>
        WorktreeSafety.KeepReasons(locked, head, _ => merged, () => clean, true, written, Now, idleMinutes: 30);
}
