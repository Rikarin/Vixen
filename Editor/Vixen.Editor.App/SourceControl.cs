// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;

namespace Vixen.Editor.App;

/// <summary>What source control says about one path.</summary>
/// <remarks>
///     ⚠ <b>Deliberately smaller than any provider's own vocabulary.</b> Git alone distinguishes
///     staged from unstaged and both from the index, and a browser column that drew five shades of
///     "changed" would be a column nobody can read across a panel. What a person scanning a folder
///     wants to know is whether a file is theirs to worry about, which is these six answers.
/// </remarks>
enum SourceControlStatus : byte {
    /// <summary>Nothing has been asked, or there is no provider.</summary>
    Unknown,

    /// <summary>Tracked, and the same as the last commit.</summary>
    Unmodified,

    /// <summary>Tracked and changed.</summary>
    Modified,

    /// <summary>New, and staged to be committed.</summary>
    Added,

    /// <summary>Tracked, and gone from the working tree.</summary>
    Deleted,

    /// <summary>A merge left it in two minds.</summary>
    Conflicted,

    /// <summary>In the project and not in source control at all.</summary>
    Untracked
}

/// <summary>What the editor needs from a version-control system, and no more.</summary>
/// <remarks>
///     <para>
///         <b>The seam doc 20 § B7 asks for.</b> Nothing in the tree spelled <c>SourceControl</c>
///         before this, so the four verbs the row lists — status, revert, diff, history — had nowhere
///         to hang. This is the first two of them; diff and history over a real repository want a
///         viewer and a log panel and are their own work, and inventing their signatures here would
///         be designing a panel nobody has drawn.
///     </para>
///     <para>
///         ⚠ <b>Status is asked for the whole working tree at once rather than per path, and that is
///         the decision the rest depends on.</b> A provider asked per file launches a process per
///         tile; a provider asked on a timer answers from a cache that an external checkout
///         invalidates without telling it. One sweep, taken when the project is rescanned — which is
///         the moment the browser rebuilds and the moment the file watcher fires — is the answer that
///         is right as often as the tree it is drawn beside.
///     </para>
///     <para>
///         ⚠ <b>Doc 20's second bar applies hardest here: a status column that is sometimes right is
///         worse than no column.</b> So an unknown answer is <see cref="SourceControlStatus.Unknown" />
///         and draws nothing, rather than being rounded down to "unmodified".
///     </para>
/// </remarks>
interface ISourceControl {
    /// <summary>What to call it in a menu or a notification.</summary>
    string Name { get; }

    /// <summary>Reads the status of everything in the working tree.</summary>
    /// <returns>Project-relative paths with forward slashes, and what each one is.</returns>
    /// <remarks>
    ///     ⚠ Only paths the provider has something to say about. Everything else is
    ///     <see cref="SourceControlStatus.Unmodified" /> by absence, which is what keeps the answer
    ///     the size of the change rather than the size of the project.
    /// </remarks>
    ValueTask<IReadOnlyDictionary<string, SourceControlStatus>> StatusAsync();

    /// <summary>Throws a file's changes away.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <returns>Null when it worked, or what went wrong.</returns>
    ValueTask<string?> RevertAsync(string path);
}

/// <summary>Git, over the command-line client the user already has.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The porcelain format and the <c>-z</c> flag, not the human one.</b>
///         <c>git status</c>'s default output is localised and reflowed for a terminal; the porcelain
///         format is documented as stable across versions, and <c>-z</c> is what makes a file name
///         with a space, a quote or a newline in it parse — asset names come from artists and
///         <c>Concept art (final).png</c> is an ordinary one.
///     </para>
///     <para>
///         ⚠ <b>A library was not the alternative.</b> There is no git implementation in this
///         repository to reuse, and the one thing a person always has when their project is in git is
///         git — so the seam is a process, and a provider for anything else implements the same two
///         methods without the editor learning a second vocabulary.
///     </para>
/// </remarks>
sealed class GitSourceControl : ISourceControl {
    /// <summary>How long to wait for a git invocation before giving up on it.</summary>
    /// <remarks>
    ///     ⚠ <b>A hang check rather than a budget.</b> A status sweep over a large working tree is
    ///     seconds on a cold cache and this is not a measurement of it; what it stops is a prompt —
    ///     a credential helper, a lock held by another process — turning into an editor that never
    ///     draws the column and never says why. Off the frame thread either way.
    /// </remarks>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    readonly string root;

    /// <summary>Where the project sits inside the repository, with a trailing slash, or empty.</summary>
    /// <remarks>
    ///     ⚠ <b>Porcelain paths are relative to the repository root and nothing else in the editor
    ///     is.</b> A project checked in as <c>Games/Prototype/</c> would otherwise have every status
    ///     filed under a path the asset tree has never heard of — a column that is blank for every
    ///     file, in the one layout where somebody keeps more than one project in a repository.
    ///     <c>rev-parse --show-prefix</c> is git's own answer to "where am I", so the arithmetic is
    ///     not this file's.
    /// </remarks>
    readonly string prefix;

    GitSourceControl(string root, string prefix) {
        this.root = root;
        this.prefix = prefix;
    }

    /// <inheritdoc />
    public string Name => "Git";

    /// <summary>Whether a directory is inside a git working tree, and a provider for it if it is.</summary>
    /// <param name="directory">The project root.</param>
    /// <returns>The provider, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked once, at startup, and a project that is not in git gets no provider at all
    ///     rather than a provider that answers nothing.</b> The difference is what the browser draws:
    ///     no provider means no column, and a column of blanks would be a feature that looks broken
    ///     to everybody who does not use git.
    /// </remarks>
    public static GitSourceControl? For(string directory) {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || !NearGit(directory)) {
            return null;
        }

        var (code, output, _) = Run(directory, "rev-parse", "--show-toplevel", "--show-prefix");

        if (code != 0) {
            return null;
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0 || lines[0].Trim().Length == 0) {
            return null;
        }

        // ⚠ The second line is empty when the project *is* the repository root, and `rev-parse`
        // prints nothing for it rather than a blank line — so the count, not the content, is what
        // says which case this is.
        var prefix = lines.Length > 1 ? lines[1].Trim().Replace('\\', '/') : string.Empty;

        if (prefix.Length > 0 && !prefix.EndsWith('/')) {
            prefix += '/';
        }

        return new GitSourceControl(directory, prefix);
    }

    /// <summary>Whether a <c>.git</c> is anywhere at or above a directory.</summary>
    /// <param name="directory">Where to start.</param>
    /// <returns>Whether it is worth asking git.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A stat walk before a process, and the cost it saves is the ordinary case.</b>
    ///         Every project that is not in git — which includes every project the test suite makes,
    ///         several hundred of them in one run — would otherwise pay a <c>git rev-parse</c> to be
    ///         told no.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A file counts as much as a directory.</b> Inside a git *worktree* — which is what
    ///         every agent in this repository works in — <c>.git</c> is a file naming the real one,
    ///         and a check for a directory would report the whole arrangement as unversioned.
    ///     </para>
    ///     <para>
    ///         The one thing this cannot see is a repository named entirely by environment
    ///         (<c>GIT_DIR</c> with no marker on disk), which is rare enough to be worth a process
    ///         per project launch to nobody.
    ///     </para>
    /// </remarks>
    static bool NearGit(string directory) {
        for (var walk = new DirectoryInfo(directory); walk is not null; walk = walk.Parent) {
            var marker = Path.Combine(walk.FullName, ".git");

            if (Directory.Exists(marker) || File.Exists(marker)) {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, SourceControlStatus>> StatusAsync() =>
        new(Task.Run(Status));

    /// <inheritdoc />
    public ValueTask<string?> RevertAsync(string path) => new(Task.Run(() => Revert(path)));

    IReadOnlyDictionary<string, SourceControlStatus> Status() {
        // ⚠ `--untracked-files=all` rather than the default, which reports a directory once and
        // stops. A folder of newly-copied textures is exactly the case the browser has to mark, and
        // the default answer for it is one line naming the folder.
        var (code, output, _) = Run(root, "status", "--porcelain", "-z", "--untracked-files=all");

        Dictionary<string, SourceControlStatus> statuses = new(StringComparer.Ordinal);

        if (code != 0) {
            return statuses;
        }

        // ⚠ Records are NUL-terminated, and a rename is TWO of them: `R  new\0old\0`. Reading the
        // second as a record of its own would report the old path with the next record's code.
        var records = output.Split('\0');

        for (var index = 0; index < records.Length; index++) {
            var record = records[index];

            if (record.Length < 4) {
                continue;
            }

            var staged = record[0];
            var worktree = record[1];
            var path = record[3..].Replace('\\', '/');

            if (staged == 'R' || staged == 'C') {
                // Skip the record holding where it came from.
                index++;
            }

            // ⚠ Everything git names is relative to the repository root, and everything the editor
            // holds is relative to the project. A repository with the project in a subdirectory —
            // several games in one checkout, or an engine and a game — would otherwise file every
            // status under a path no asset has, and the column would simply be blank.
            if (prefix.Length > 0) {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) {
                    continue;
                }

                path = path[prefix.Length..];
            }

            statuses[path] = Read(staged, worktree);
        }

        return statuses;
    }

    /// <summary>Turns git's two status letters into the one answer a column can draw.</summary>
    /// <param name="staged">The index's letter.</param>
    /// <param name="worktree">The working tree's letter.</param>
    /// <returns>What to say about it.</returns>
    /// <remarks>
    ///     ⚠ <b>Conflict first, because git spells it with letters that mean something else on their
    ///     own.</b> <c>UU</c>, <c>AA</c> and <c>DD</c> are both-sides states, and reading the first
    ///     letter of <c>AA</c> as "added" reports a merge conflict as a new file — which is the one
    ///     answer that makes a person commit it without looking.
    /// </remarks>
    static SourceControlStatus Read(char staged, char worktree) {
        if (staged == 'U' || worktree == 'U' || (staged == 'A' && worktree == 'A')
            || (staged == 'D' && worktree == 'D')) {
            return SourceControlStatus.Conflicted;
        }

        if (staged == '?' || worktree == '?') {
            return SourceControlStatus.Untracked;
        }

        if (staged == 'D' || worktree == 'D') {
            return SourceControlStatus.Deleted;
        }

        if (staged is 'A' or 'C') {
            return SourceControlStatus.Added;
        }

        return staged == ' ' && worktree == ' '
            ? SourceControlStatus.Unmodified
            : SourceControlStatus.Modified;
    }

    string? Revert(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return "There is no path to revert.";
        }

        // ⚠ `--` before the path, always. Without it a file called `main` is a revision and git
        // checks the branch out instead — which for a revert verb is the one mistake that loses
        // work rather than failing to.
        var (code, _, error) = Run(root, "checkout", "--", path);

        if (code == 0) {
            return null;
        }

        // ⚠ A path git has never heard of is not an error worth a stack trace, but it is not
        // success either: a revert that reports success having done nothing is how a person
        // concludes their changes are gone when they are not.
        return error.Trim() is { Length: > 0 } message ? message : "git could not revert that file.";
    }

    static (int Code, string Output, string Error) Run(string directory, params string[] arguments) {
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(start);

            if (process is null) {
                return (-1, string.Empty, "git could not be started.");
            }

            // ⚠ Read before waiting, and both streams. A process whose pipe fills blocks writing to
            // it, so a wait-then-read over a working tree with a few thousand changed files is a
            // deadlock rather than a slow answer.
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(Patience)) {
                process.Kill(entireProcessTree: true);
                return (-1, string.Empty, "git did not answer.");
            }

            return (process.ExitCode, output, error);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException) {
            // ⚠ A machine with no git is the ordinary case, not a failure: `Win32Exception` is what
            // "no such executable" arrives as, and a project outside a working tree must simply have
            // no provider.
            return (-1, string.Empty, failure.Message);
        }
    }
}

/// <summary>What the browser draws: one status per project-relative path, folders included.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A folder takes the strongest answer under it, because a folder has no status of its
///         own.</b> A collapsed <c>Textures/</c> holding one modified file has to say so, or the
///         column only ever helps in the folder you are already standing in — which is the one place
///         you can see the files.
///     </para>
///     <para>
///         ⚠ <b>Empty until a sweep lands, and empty is <see cref="SourceControlStatus.Unknown" />
///         rather than clean.</b> A column that says "unmodified" while the answer is still in
///         flight is the version of this feature that is sometimes right.
///     </para>
/// </remarks>
sealed class SourceControlStatuses {
    readonly Dictionary<string, SourceControlStatus> statuses = new(StringComparer.Ordinal);

    /// <summary>Whether a sweep has ever landed.</summary>
    public bool IsKnown { get; private set; }

    /// <summary>What source control says about a path.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <returns>The status, or <see cref="SourceControlStatus.Unknown" /> before the first sweep.</returns>
    public SourceControlStatus Of(string path) {
        if (!IsKnown) {
            return SourceControlStatus.Unknown;
        }

        return statuses.TryGetValue(path, out var status) ? status : SourceControlStatus.Unmodified;
    }

    /// <summary>Takes a sweep's answer, folding every folder above a changed file.</summary>
    /// <param name="swept">What the provider said.</param>
    public void Accept(IReadOnlyDictionary<string, SourceControlStatus> swept) {
        ArgumentNullException.ThrowIfNull(swept);

        statuses.Clear();
        IsKnown = true;

        foreach (var (path, status) in swept) {
            statuses[path] = status;

            // ⚠ Folded upwards here rather than asked for downwards at draw time. A tile asking
            // "is anything under me changed" would walk the subtree once per tile per frame; the
            // fold is one pass over what actually changed, which is the small collection.
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1)) {
                var folder = path[..slash];

                if (!statuses.TryGetValue(folder, out var already) || Louder(status, already)) {
                    statuses[folder] = status;
                }
            }
        }
    }

    /// <summary>Which of two statuses a folder should show.</summary>
    /// <param name="status">The candidate.</param>
    /// <param name="already">What the folder says now.</param>
    /// <returns>Whether the candidate wins.</returns>
    /// <remarks>
    ///     A conflict outranks everything, because it is the one that stops a commit; an untracked
    ///     file is the quietest, because a folder full of them is the ordinary state of an
    ///     unimported import.
    /// </remarks>
    static bool Louder(SourceControlStatus status, SourceControlStatus already) => Rank(status) > Rank(already);

    static int Rank(SourceControlStatus status) => status switch {
        SourceControlStatus.Conflicted => 5,
        SourceControlStatus.Deleted => 4,
        SourceControlStatus.Modified => 3,
        SourceControlStatus.Added => 2,
        SourceControlStatus.Untracked => 1,
        _ => 0
    };
}
