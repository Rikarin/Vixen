// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

/// <summary>
///     Why one agent worktree is kept, or nothing at all when it is safe to remove.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The positive branch of <c>PruneWorktrees</c> is the one that cannot be observed on a
///         real machine</b>: a removable worktree is by definition one nobody has, and every audit of
///         #561 so far has had to reach for sabotage — forcing the merged predicate true — to see the
///         table's other half at all. Sabotage proves the branch is <em>reached</em>; it does not
///         leave anything behind that would notice a later edit. Splitting the decision out from the
///         git calls and the file walk is what makes the positive case an ordinary test.
///     </para>
///     <para>
///         ⚠ <b>Each condition can be got backwards without anything failing to build</b>, and the
///         target prints a plausible table either way — which is the shape this repository keeps
///         losing to. A reversed recency comparison keeps every idle worktree and removes the busy
///         one; a merged check asked about an empty head answers about the checkout the target is
///         running in (<c>git rev-list --count "master.."</c> is not an error, and the missing
///         right-hand side means <em>this</em> HEAD); and "merged" is the answer that deletes a
///         checkout.
///     </para>
///     <para>
///         The two git questions arrive as delegates rather than as booleans on purpose: the order
///         they are asked in is part of the decision, and an empty head must mean <em>git is not
///         asked at all</em> rather than "asked and answered no".
///     </para>
///     <para>
///         Dependency-free, and linked into <c>Vixen.ApiCheck.Tests</c> as source for the reason
///         <see cref="AotProbeProjectFile" /> already records: <c>build/_build.csproj</c> is outside
///         <c>Vixen.slnx</c> and nothing in the tree tests it, so the alternative to linking is no
///         test at all.
///     </para>
/// </remarks>
static class WorktreeSafety {
    /// <summary>
    ///     Every reason to keep the worktree described by the arguments — empty when there is none,
    ///     which is the only state <c>--remove-merged</c> acts on.
    /// </summary>
    /// <param name="locked">The lock reason git reported, or <see langword="null" /> when unlocked.</param>
    /// <param name="head">The worktree's HEAD as git reported it, which can be absent.</param>
    /// <param name="isMergedIntoMaster">
    ///     Whether a non-empty head is already contained in <c>master</c>. ⚠ Not called for an empty
    ///     head, because the question would then be answered about a different subject.
    /// </param>
    /// <param name="isClean">Whether that worktree has nothing uncommitted.</param>
    /// <param name="footprintRead">
    ///     Whether the walk that produced <paramref name="newestWrite" /> completed. ⚠ A walk that
    ///     threw has no newest write to report, and "no newest write" is otherwise indistinguishable
    ///     from "nobody has touched it in weeks" — which is the answer that deletes a checkout.
    /// </param>
    /// <param name="newestWrite">The newest write anywhere under it, UTC, or <see langword="null" />.</param>
    /// <param name="now">The moment to measure that against, UTC.</param>
    /// <param name="idleMinutes">The recency window; <c>0</c> or less turns the condition off.</param>
    public static IReadOnlyList<string> KeepReasons(
        string? locked,
        string head,
        Func<string, bool> isMergedIntoMaster,
        Func<bool> isClean,
        bool footprintRead,
        DateTime? newestWrite,
        DateTime now,
        int idleMinutes
    ) {
        var reasons = new List<string>();

        if (locked is not null) {
            reasons.Add($"locked ({locked})");
        }

        if (string.IsNullOrWhiteSpace(head)) {
            // Distinct from "has commits master does not", because it is not a fact about the branch
            // — it is the parse having nothing to ask about. Saying so out loud matters: a worktree
            // kept for a reason nobody can act on is a worktree kept for ever.
            reasons.Add("git reported no HEAD for it");
        } else if (!isMergedIntoMaster(head)) {
            reasons.Add("has commits master does not");
        }

        if (!isClean()) {
            reasons.Add("has uncommitted changes");
        }

        // ⚠ Fourth, and the only one about the worker rather than the work: an agent whose runner set
        // no lock is invisible to the other three the moment its branch is merged (#770).
        if (!footprintRead && idleMinutes > 0) {
            // ⚠ And the fourth condition's own instrument, which it did not have. `Measure` reports
            // a null newest write for two different things — a directory it walked and found no
            // files in, and a directory whose walk threw — and this predicate read both as "idle".
            // So the day the safety condition cannot run, it used to answer *removable*: the
            // failure this repository keeps meeting, where an instrument that did not run reports
            // success. Unreadable is the state in which #770's whole argument applies most, since a
            // worktree an agent is actively writing to is exactly the one whose files come and go
            // under the walk.
            reasons.Add("its files could not be walked, so the idle check could not run");
        } else if (StillBeingWrittenTo(newestWrite, now, idleMinutes) is { } busy) {
            reasons.Add(busy);
        }

        return reasons;
    }

    /// <summary>
    ///     Why a worktree is still in use according to its files, or <see langword="null" /> when
    ///     nothing under it has been written inside the window.
    /// </summary>
    /// <param name="newestWrite">The newest write under it, UTC, or <see langword="null" />.</param>
    /// <param name="now">The moment to measure against, UTC.</param>
    /// <param name="idleMinutes">The window; <c>0</c> or less turns the condition off.</param>
    public static string? StillBeingWrittenTo(DateTime? newestWrite, DateTime now, int idleMinutes) {
        if (idleMinutes <= 0 || newestWrite is not { } written) {
            return null;
        }

        var age = now - written;

        return age < TimeSpan.FromMinutes(idleMinutes)
            ? $"written {Ago(age)} ago, inside --idle-minutes {idleMinutes}"
            : null;
    }

    /// <summary>How long ago something was written, worded for a log line.</summary>
    public static string Ago(TimeSpan written) =>
        written < TimeSpan.FromMinutes(1)
            ? "under a minute"
            : written < TimeSpan.FromHours(1)
                ? $"{(int)written.TotalMinutes} min"
                : written < TimeSpan.FromDays(1)
                    ? $"{written.TotalHours:0.0} h"
                    : $"{written.TotalDays:0.0} days";
}
