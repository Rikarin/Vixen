// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.Git;
using Serilog;

/// <summary>
///     Reports, and on request removes, the agent worktrees under <c>.claude/worktrees</c> that are
///     finished with.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>They are never cleaned up by anything, and on 2026-09-04 they were 105 GB of a
///         132 GB tree</b> (#561). Each one carries its own <c>bin</c> and <c>obj</c> — about 25 GB
///         apiece once the solution has been built in both configurations — and three of the six
///         were on branches already merged into master, with clean working trees, and had been for
///         days. Removing them by hand recovered 79 GB.
///     </para>
///     <para>
///         ⚠ <b>And one 3.8 GB directory in there was not a registered worktree at all.</b> It had
///         no <c>.git</c> file and did not appear in <c>git worktree list</c>, so
///         <c>git worktree prune</c> would never have touched it. Because it sits inside the main
///         working tree, running git from inside it silently answered about the <em>parent</em>
///         repository — it reported itself clean and on master while being neither. Anything that
///         decides what to delete by asking git from inside the directory gets a wrong answer for
///         that shape, which is why this target reads the registered set from the repository root
///         and lists the directory entries separately, rather than assuming the two agree.
///     </para>
///     <para>
///         <b>Reports by default and removes only when asked.</b> Two of those six worktrees had
///         unmerged commits and uncommitted edits — three commits and fifteen modified files in one
///         of them, including shaders and a doc page — and deleting either would have destroyed work
///         with no way back. So the safety conditions are all four of: the worktree's HEAD is
///         contained in <c>master</c>, its <c>git status --porcelain</c> is empty, it is not
///         locked, and nothing under it has been written in the last <c>--idle-minutes</c>. Anything
///         that fails one of them, and every directory that is not a registered worktree, is
///         reported and left alone whatever the flags say.
///     </para>
///     <para>
///         ⚠ <b>The fourth condition is here because the third is not the runner's promise, only its
///         habit</b> (#770). The lock is what is read as "somebody is still using this", and the
///         agent runner does set it — but on 2026-09-05, with two batches running, two of thirteen
///         live agent worktrees carried none. The other two conditions are properties of the
///         <i>work</i> rather than of the <i>worker</i>: merged and clean is exactly the state an
///         agent is in for the window between the orchestrator merging its branch and the agent's
///         process ending, and in that window a missing lock is the only thing between
///         <c>--remove-merged</c> and a live checkout. ⚠ The failure would not be loud, either:
///         <c>git worktree remove</c> re-checks dirtiness and a merged, clean worktree passes that
///         check too.
///     </para>
///     <para>
///         <b>Recency and not the lock's pid.</b> The pid is right there in the lock reason, but a
///         recycled pid is a worse oracle than a missing lock — and it answers nothing at all for
///         the worktrees this is about, which are the ones with no lock to read a pid out of. So the
///         signal is the newest write anywhere under the worktree, which costs nothing: this target
///         already walks every file of every candidate to report its size, and the walk now returns
///         both numbers.
///     </para>
///     <para>
///         ⚠ <b>The ordering hazard, named rather than left to be re-derived.</b> Merge-then-prune is
///         the intended workflow — <c>./build.sh PruneWorktrees --remove-merged</c> straight after a
///         batch merge — and against a window this now reports "keep, written N minutes ago" for the
///         worktrees that batch just produced. That is the point rather than a regression: the disk
///         #561 was about had been held for *days*, so reclaiming it in the next sweep instead of
///         this one costs nothing, and the sweep that would have deleted a live checkout is the one
///         it refuses. <c>--idle-minutes 0</c> disables the condition and restores the three-way
///         behaviour exactly, for an operator who knows what else is running.
///     </para>
///     <para>
///         ⚠ <b>And what it cannot see</b>: an agent that has only been <i>reading</i> for longer
///         than the window writes nothing, so it looks idle — and an agent that made no commits has
///         a HEAD master already contains, which is merged and clean. That case is precisely why
///         this is a fourth condition and not a replacement for the lock: the two are blind in
///         different directions, and removal needs all of them.
///     </para>
///     <para>
///         ⚠ <b>This block used to end by <em>refusing</em> the fourth condition, and that refusal has
///         expired rather than been overruled.</b> Its argument was that a recency guard fails on its
///         own terms — an agent's last act is to commit, so at the moment its branch is merged its
///         worktree is the <i>most</i> recently touched thing on the disk, and the guard would refuse
///         every worktree in precisely the merge-then-prune window the target exists to serve. That
///         is still true, and it is now a <em>described consequence</em> rather than a reason not to
///         have the condition: it refuses that window, the paragraph above says so, and
///         <c>--idle-minutes 0</c> is the way past it. The half of the old argument that did not
///         survive is the conclusion, because the risk on the other side is deleting a live checkout
///         and the cost on this side is waiting half an hour for disk that had been held for days.
///     </para>
///     <para>
///         ⚠ Its second half stands and is why the condition reads file timestamps rather than the
///         git index: <see cref="IsClean" /> runs <c>git status</c> against each worktree moments
///         before the decision, and that refreshes the index — an index-mtime instrument would be
///         reading its own footprint.
///     </para>
///     <para>
///         <b>Underneath all four, the decision is that merge implies the agent has finished</b>: a
///         branch reaches master only after the orchestrator has read the agent's report, so a merged
///         and clean worktree is one whose owner has said it is done. The lock remains the intended
///         signal and is honoured when present; it is not the one being relied on. Removal stays
///         behind an explicit <c>--remove-merged</c> typed on purpose, which is the actual guard.
///     </para>
///     <para>
///         ⚠ <b>The four conditions themselves are in <see cref="WorktreeSafety" />, not here, and
///         that is a testability split rather than a tidiness one.</b> The removable verdict is
///         unreachable on any real machine — a removable worktree is one nobody has — so every audit
///         of this target has had to sabotage a predicate to see the branch at all, and sabotage
///         leaves nothing behind that would notice the next edit. As a pure function over five
///         answers it is an ordinary test, including the case that has to hold: an empty HEAD must
///         mean git is never asked, because <c>rev-list --count "master.."</c> would answer about
///         the checkout the target is running in.
///     </para>
/// </remarks>
partial class Build {
    [Parameter("Actually remove the agent worktrees PruneWorktrees reports as safe, rather than only listing them")]
    readonly bool RemoveMerged;

    /// <summary>
    ///     How recently a worktree has to have been written to for <see cref="PruneWorktrees" /> to
    ///     keep it whatever its other three conditions say.
    /// </summary>
    /// <remarks>
    ///     Thirty minutes because the window it covers — an agent's process outliving the merge of
    ///     its branch — is seconds to a couple of minutes, and because the disk this reclaims had
    ///     been held for days when it was worth reclaiming. <c>0</c> turns the condition off.
    /// </remarks>
    [Parameter("Keep any agent worktree written to within this many minutes, whatever else says (0 disables)")]
    readonly int IdleMinutes = 30;

    /// <summary>One line of <c>git worktree list --porcelain</c>, parsed.</summary>
    sealed record Worktree(AbsolutePath Path, string Head, string? Locked);

    /// <summary>
    ///     Every worktree git knows about, main tree first.
    /// </summary>
    /// <remarks>
    ///     ⚠ The first entry is the main working tree, and it rather than
    ///     <see cref="NukeBuild.RootDirectory" /> is where <c>.claude/worktrees</c> lives: this
    ///     target is very likely being run from inside one of the worktrees it is reporting on.
    /// </remarks>
    IReadOnlyList<Worktree> RegisteredWorktrees() {
        var lines = GitTasks
            .Git("worktree list --porcelain", RootDirectory, logOutput: false, logInvocation: false)
            .Select(output => output.Text.Trim())
            .ToList();

        var worktrees = new List<Worktree>();
        AbsolutePath? path = null;
        string head = string.Empty;
        string? locked = null;

        void Flush() {
            if (path is not null) {
                worktrees.Add(new Worktree(path, head, locked));
            }

            path = null;
            head = string.Empty;
            locked = null;
        }

        foreach (var line in lines) {
            if (line.Length == 0) {
                Flush();
            } else if (line.StartsWith("worktree ", StringComparison.Ordinal)) {
                Flush();
                path = (AbsolutePath)line["worktree ".Length..];
            } else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) {
                head = line["HEAD ".Length..];
            } else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal)) {
                locked = line.Length > "locked".Length ? line["locked ".Length..] : "no reason given";
            }
        }

        Flush();

        // The instrument: git always reports at least the main worktree, so an empty list means the
        // parse stopped matching and not that there is nothing to do — and "nothing to do" is
        // exactly what this target would then print for ever.
        Assert.True(worktrees.Count > 0, "Parsed no worktrees at all out of `git worktree list --porcelain`.");

        return worktrees;
    }

    /// <summary>
    ///     Whether every commit in <paramref name="head" /> is already in <c>master</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The commit graph is the only honest merged test.</b> A branch name says nothing,
    ///         and a hash an agent reported for its own branch is stale the moment anything rebases.
    ///         This is <c>git merge-base --is-ancestor</c> written as a count so that "not merged" is
    ///         an answer rather than a non-zero exit code to be caught:
    ///         <c>rev-list --count master..HEAD</c> is zero exactly when HEAD is an ancestor of
    ///         master.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty head is refused rather than asked about, because git would answer — and
    ///         about the wrong tree.</b> <c>rev-list --count "master.."</c> is not an error: the
    ///         missing right-hand side means <c>HEAD</c>, and the <c>HEAD</c> it means is the one of
    ///         the checkout this target is running in. So a worktree whose <c>HEAD</c> line the parse
    ///         did not see would be judged by <i>this</i> agent's branch, exit 0, and say nothing
    ///         about having done so — and "merged" is the answer that deletes a checkout. It is the
    ///         same class of defect as the stray directory that answered about its parent repository,
    ///         one level in: the subject is silently substituted rather than the question refused.
    ///     </para>
    /// </remarks>
    bool IsMergedIntoMaster(string head) {
        if (string.IsNullOrWhiteSpace(head)) {
            return false;
        }

        var count = GitTasks
            .Git($"rev-list --count master..{head}", RootDirectory, logOutput: false, logInvocation: false)
            .Select(output => output.Text.Trim())
            .FirstOrDefault(line => line.Length > 0);

        return count == "0";
    }

    /// <summary>
    ///     Whether the working tree at <paramref name="worktree" /> has nothing uncommitted.
    /// </summary>
    /// <remarks>
    ///     ⚠ Asked with <c>-C</c> from here rather than by running git inside the directory, and
    ///     that is the whole lesson of #561's 3.8 GB stray: a directory that is not a worktree
    ///     answers about the repository that contains it. <c>-C</c> into a real worktree is answered
    ///     by that worktree; this is only ever called for paths git itself just listed.
    /// </remarks>
    bool IsClean(AbsolutePath worktree) =>
        GitTasks
            .Git($"-C \"{worktree}\" status --porcelain", RootDirectory, logOutput: false, logInvocation: false)
            .All(output => output.Text.Trim().Length == 0);

    /// <summary>What one walk of a directory learns about it.</summary>
    /// <param name="Size">Its size, already worded for a log line.</param>
    /// <param name="NewestWrite">
    ///     The newest last-write time under it, or <see langword="null" /> when the walk could not
    ///     read the directory at all.
    /// </param>
    /// <remarks>
    ///     ⚠ Two numbers out of one walk deliberately. The size is what a person reads to decide
    ///     whether a sweep was worth running; the newest write is a safety condition, and a second
    ///     traversal of ~100 000 files to get it would have made the condition look expensive
    ///     enough to turn off.
    /// </remarks>
    sealed record Footprint(string Size, DateTime? NewestWrite);

    /// <summary>
    ///     Walks <paramref name="directory" /> once for its size and its newest write.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every file, <c>bin</c> and <c>obj</c> included, and that is the useful half rather than
    ///     noise: an agent that is compiling or testing is writing there constantly and nowhere
    ///     else, so excluding them would hide the busiest worker there is.
    /// </remarks>
    static Footprint Measure(AbsolutePath directory) {
        try {
            var bytes = 0L;
            DateTime? newest = null;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
                try {
                    var info = new FileInfo(file);
                    bytes += info.Length;
                    var written = info.LastWriteTimeUtc;

                    if (newest is null || written > newest) {
                        newest = written;
                    }
                } catch (IOException) {
                    // A file that vanished between the enumeration and the stat — which is what a
                    // worktree somebody is still building in looks like.
                }
            }

            return new Footprint($"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB", newest);
        } catch (IOException) {
            return new Footprint("size unknown", null);
        } catch (UnauthorizedAccessException) {
            return new Footprint("size unknown", null);
        }
    }

    Target PruneWorktrees => definition => definition
        .Description("Reports the agent worktrees under .claude/worktrees that are merged, clean, unlocked and idle — and removes them with --remove-merged")
        .Executes(() => {
            var registered = RegisteredWorktrees();
            var main = registered[0].Path;
            var directory = main / ".claude" / "worktrees";

            if (!directory.DirectoryExists()) {
                Log.Information("{Directory} does not exist; nothing to prune.", directory);

                return;
            }

            var byPath = registered.ToDictionary(worktree => worktree.Path.ToString(), StringComparer.Ordinal);
            var removable = new List<Worktree>();

            // Every directory entry, not every registered worktree, because the two disagreeing
            // is the failure this target exists for.
            foreach (var entry in directory.GlobDirectories("*").OrderBy(path => path.ToString(), StringComparer.Ordinal)) {
                if (!byPath.TryGetValue(entry.ToString(), out var worktree)) {
                    Log.Warning(
                        "  ⚠ not a worktree  {Name}  {Size} — git does not know about this directory, so "
                        + "`git worktree prune` will never touch it and git run from inside it answers about "
                        + "the parent repository. Look at it by hand.",
                        entry.Name,
                        Measure(entry).Size
                    );

                    continue;
                }

                // Never the checkout this target is running in, whatever its state says. Nuke's
                // own assemblies are loaded out of it.
                if (entry == RootDirectory) {
                    Log.Information("  in use          {Name} — this is the checkout PruneWorktrees is running in.", entry.Name);

                    continue;
                }

                // One walk, and before the conditions rather than inside them: both branches below
                // print the size, the fourth condition reads the same walk's other number, and
                // measuring first means no write this target itself provokes can be mistaken for a
                // worker's.
                var footprint = Measure(entry);

                // ⚠ The four conditions live in WorktreeSafety and not here, and the split is the
                // only way the removable verdict is ever exercised: a removable worktree is by
                // definition one nobody has, so on a real machine this loop's other branch is
                // unreachable and every audit of it so far has had to sabotage a predicate to see
                // it. Compared against UTC — the newest write is a file's UTC timestamp, not the
                // local time a log line shows.
                var reasons = WorktreeSafety.KeepReasons(
                    worktree.Locked,
                    worktree.Head,
                    IsMergedIntoMaster,
                    () => IsClean(entry),
                    footprint.NewestWrite,
                    DateTime.UtcNow,
                    IdleMinutes
                );

                if (reasons.Count > 0) {
                    Log.Information("  keep            {Name}  {Size} — {Reasons}.", entry.Name, footprint.Size, string.Join("; ", reasons));

                    continue;
                }

                Log.Information(
                    "  removable       {Name}  {Size} — merged into master, clean, unlocked, {Idle}.",
                    entry.Name,
                    footprint.Size,
                    IdleMinutes > 0 ? $"and unwritten for {IdleMinutes} min" : "and the idle check is off"
                );
                removable.Add(worktree);
            }

            if (removable.Count == 0) {
                Log.Information("Nothing is safe to remove.");

                return;
            }

            if (!RemoveMerged) {
                Log.Information(
                    "{Count} worktree(s) are safe to remove. Rerun with --remove-merged to remove them.",
                    removable.Count
                );

                return;
            }

            foreach (var worktree in removable) {
                // ⚠ `git worktree remove` and not a directory delete: it re-checks dirtiness
                // itself and refuses, so the decision above is a filter in front of git's own
                // guard rather than a replacement for it. It also unregisters the worktree,
                // which a delete would leave for `prune` to notice later.
                GitTasks.Git($"worktree remove \"{worktree.Path}\"", RootDirectory);
                Log.Information("Removed {Name}.", worktree.Path.Name);
            }
        }
        );
}
