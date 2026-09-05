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
///         with no way back. So the safety conditions are all three of: the worktree's HEAD is
///         contained in <c>master</c>, its <c>git status --porcelain</c> is empty, and it is not
///         locked. Anything that fails one of them, and every directory that is not a registered
///         worktree, is reported and left alone whatever the flags say.
///     </para>
///     <para>
///         ⚠ <b>Of those three, only two are properties of the work; the lock is a property of the
///         runner, and the runner does not always set it.</b> Read on 2026-09-05, 2 of 13 live agent
///         worktrees carried no lock at all (#770). Both were unmerged, so nothing was at risk — but
///         the window this target is meant to be run in is exactly the one between the orchestrator
///         merging a branch and the agent's process ending, and in that window a missing lock leaves
///         "merged" and "clean" doing the whole job. <c>git worktree remove</c> is not a second line
///         of defence here either: a merged, clean worktree passes its dirtiness check too.
///     </para>
///     <para>
///         ⚠ <b>The obvious fourth condition — refusing anything whose files were touched recently —
///         is refused, and not on this repository's general dislike of wall-clock predicates.</b> It
///         fails on its own terms: an agent's last act is to commit, so at the moment its branch is
///         merged its worktree is the <i>most</i> recently touched thing on the disk. A recency guard
///         would therefore refuse every worktree in precisely the merge-then-prune window the target
///         exists to serve, and would only start allowing removals once the checkouts had been idle
///         long enough to have been forgotten about. It cannot be repaired by reading the git index
///         instead, either: <see cref="IsClean" /> runs <c>git status</c> against each worktree
///         moments before the decision, and that refreshes the index — the instrument would be
///         reading its own footprint.
///     </para>
///     <para>
///         <b>So the decision is that merge implies the agent has finished</b>, and it is written here
///         rather than left to be re-derived: a branch reaches master only after the orchestrator has
///         read the agent's report, so a merged and clean worktree is one whose owner has said it is
///         done. The lock remains the intended signal and is honoured when present; it is not the one
///         being relied on. Removal stays behind an explicit <c>--remove-merged</c> typed on purpose,
///         which is the actual guard.
///     </para>
/// </remarks>
partial class Build {
    [Parameter("Actually remove the agent worktrees PruneWorktrees reports as safe, rather than only listing them")]
    readonly bool RemoveMerged;

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

    static string Gigabytes(AbsolutePath directory) {
        try {
            var bytes = Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => {
                    try {
                        return new FileInfo(file).Length;
                    } catch (IOException) {
                        return 0L;
                    }
                }
                );

            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB";
        } catch (IOException) {
            return "size unknown";
        } catch (UnauthorizedAccessException) {
            return "size unknown";
        }
    }

    Target PruneWorktrees => definition => definition
        .Description("Reports the agent worktrees under .claude/worktrees that are merged, clean and unlocked — and removes them with --remove-merged")
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
                        Gigabytes(entry)
                    );

                    continue;
                }

                // Never the checkout this target is running in, whatever its state says. Nuke's
                // own assemblies are loaded out of it.
                if (entry == RootDirectory) {
                    Log.Information("  in use          {Name} — this is the checkout PruneWorktrees is running in.", entry.Name);

                    continue;
                }

                var reasons = new List<string>();

                if (worktree.Locked is not null) {
                    reasons.Add($"locked ({worktree.Locked})");
                }

                if (string.IsNullOrWhiteSpace(worktree.Head)) {
                    // Distinct from "has commits master does not", because it is not a fact about the
                    // branch — it is the parse having nothing to ask about. `IsMergedIntoMaster`
                    // refuses the same input for the same reason; this says so out loud, because a
                    // worktree kept for a reason nobody can act on is a worktree kept for ever.
                    reasons.Add("git reported no HEAD for it");
                } else if (!IsMergedIntoMaster(worktree.Head)) {
                    reasons.Add("has commits master does not");
                }

                if (!IsClean(entry)) {
                    reasons.Add("has uncommitted changes");
                }

                if (reasons.Count > 0) {
                    Log.Information("  keep            {Name}  {Size} — {Reasons}.", entry.Name, Gigabytes(entry), string.Join("; ", reasons));

                    continue;
                }

                Log.Information("  removable       {Name}  {Size} — merged into master, clean, unlocked.", entry.Name, Gigabytes(entry));
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
