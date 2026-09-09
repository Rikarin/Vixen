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

/// <summary>One commit that touched a file, as a history row shows it.</summary>
/// <param name="Id">The full revision, which is what a later command is given.</param>
/// <param name="ShortId">The abbreviation a person reads and quotes.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="When">When they did, in their own offset as the provider recorded it.</param>
/// <param name="Summary">The first line of the message, which is the row's label.</param>
/// <remarks>
///     ⚠ <b>Both ids, rather than one and an abbreviation rule.</b> How many characters are enough
///     to be unambiguous is a property of the repository — git decides it per repository and grows
///     it as the repository does — so a panel that truncated the full one itself would print an id
///     that resolves to two commits in exactly the repositories where that matters.
/// </remarks>
sealed record SourceControlRevision(string Id, string ShortId, string Author, DateTimeOffset When, string Summary);

/// <summary>What changed in one file, at one revision or since the last commit.</summary>
/// <param name="IsText">Whether <see cref="Text" /> is a patch or a sentence about one.</param>
/// <param name="Text">The unified diff, or what there is to say when there cannot be one.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The flag is the whole design decision, and it is the one the issue said had to be
///         made first.</b> Most of a game project is not text: a diff line for a <c>.png</c>, a mesh
///         or a compiled <c>.vxscene</c> is a promise the viewer breaks the first time somebody uses
///         it. So a binary asset's diff is <em>not</em> a patch with the bytes elided — it is one
///         honest sentence saying it is binary and how its size moved, which is what git's own
///         <c>--stat</c> says about one and is the most any viewer can say without a per-format
///         comparer.
///     </para>
///     <para>
///         ⚠ <b>And that is why <see cref="IsText" /> is a field rather than something a reader
///         infers.</b> A panel that decided by looking for a leading <c>@@</c> would call an empty
///         diff binary, and a text file whose only change is a trailing newline produces exactly
///         that.
///     </para>
/// </remarks>
sealed record SourceControlDiff(bool IsText, string Text);

/// <summary>What the editor needs from a version-control system, and no more.</summary>
/// <remarks>
///     <para>
///         <b>The seam doc 20 § B7 asks for.</b> Nothing in the tree spelled <c>SourceControl</c>
///         before this, so the four verbs the row lists — status, revert, diff, history — had nowhere
///         to hang. All four are here now, and the last two arrived together because they are one
///         panel: <c>RevisionsView</c> lists what <see cref="HistoryAsync" /> answered and draws
///         what <see cref="DiffAsync" /> says about whichever row is picked.
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

    /// <summary>The commits that touched a file, newest first.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <param name="limit">At most this many.</param>
    /// <returns>The revisions, or empty for a file the provider has never seen.</returns>
    /// <remarks>
    ///     ⚠ <b>Bounded, and the bound is not a performance nicety.</b> An engine's own repository
    ///     has files with four figures of commits behind them, and a panel that asked for all of
    ///     them would spend the time and then draw a list nobody scrolls to the end of. What a
    ///     person is looking for is nearly always in the last few dozen.
    /// </remarks>
    ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string path, int limit);

    /// <summary>What changed in a file, at a revision or since the last commit.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <param name="revision">
    ///     A <see cref="SourceControlRevision.Id" />, or empty for the working tree's own changes.
    /// </param>
    /// <returns>The patch, or the sentence that stands in for one.</returns>
    ValueTask<SourceControlDiff> DiffAsync(string path, string revision);

    /// <summary>Puts a file back as it was at a revision.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <param name="revision">A <see cref="SourceControlRevision.Id" />.</param>
    /// <returns>Null when it worked, or what went wrong.</returns>
    /// <remarks>
    ///     ⚠ <b>Into the working tree, not a checkout of the whole revision.</b> What a history row
    ///     offers is "give me back this version of this asset", which leaves everything else alone
    ///     and leaves the result as an ordinary uncommitted change somebody can look at and revert.
    ///     Moving the whole project to an old commit is a thing git clients do and an editor should
    ///     not do behind a row in an asset panel.
    /// </remarks>
    ValueTask<string?> RestoreAsync(string path, string revision);
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

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string path, int limit) =>
        new(Task.Run(() => History(path, limit)));

    /// <inheritdoc />
    public ValueTask<SourceControlDiff> DiffAsync(string path, string revision) =>
        new(Task.Run(() => Diff(path, revision)));

    /// <inheritdoc />
    public ValueTask<string?> RestoreAsync(string path, string revision) =>
        new(Task.Run(() => Restore(path, revision)));

    /// <summary>What a history row is made of, as one record git prints without quoting anything.</summary>
    /// <remarks>
    ///     ⚠ <b>A NUL between the fields and a NUL between the records, which is what <c>%x00</c>
    ///     buys.</b> A commit message is arbitrary text — it has newlines and tabs in it by
    ///     construction — so every separator that occurs in ordinary prose splits a record in the
    ///     wrong place, and the author line is the one that then reads as a subject.
    /// </remarks>
    const string LogFormat = "%H%x00%h%x00%an%x00%aI%x00%s%x00";

    IReadOnlyList<SourceControlRevision> History(string path, int limit) {
        if (string.IsNullOrWhiteSpace(path) || limit <= 0) {
            return [];
        }

        // ⚠ `--follow`, which is the whole reason this is worth doing over an asset rather than over
        // a repository. An asset that was renamed — which every asset is, the first time somebody
        // tidies a folder — has its history end at the rename without it, and the panel would say a
        // file with three years behind it was created last Tuesday.
        var (code, output, _) = Run(
            root,
            "log",
            "--follow",
            "--max-count=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--format=" + LogFormat,
            "--",
            path
        );

        if (code != 0) {
            return [];
        }

        List<SourceControlRevision> revisions = [];
        var fields = output.Split('\0');

        // Five fields per record, and the trailing NUL leaves a final empty element the stride steps
        // straight past.
        for (var index = 0; index + 4 < fields.Length; index += 5) {
            var id = fields[index].Trim('\n', '\r');

            if (id.Length == 0) {
                continue;
            }

            revisions.Add(
                new SourceControlRevision(
                    id,
                    fields[index + 1],
                    fields[index + 2],
                    DateTimeOffset.TryParse(
                        fields[index + 3],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var when
                    )
                        ? when
                        : default,
                    fields[index + 4]
                )
            );
        }

        return revisions;
    }

    /// <summary>The one git invocation a diff needs, with the revision deciding which verb it is.</summary>
    /// <param name="path">The file.</param>
    /// <param name="revision">The commit, or empty for the working tree.</param>
    /// <param name="extra">A flag to add before the path separator, or empty for the patch itself.</param>
    /// <returns>What git said, and whether it said it.</returns>
    /// <remarks>
    ///     ⚠ <b><c>show</c> and not <c>diff &lt;rev&gt;</c>, and they answer different questions.</b>
    ///     <c>git diff &lt;rev&gt; -- path</c> is "how does the file differ from that commit *now*",
    ///     which for a row three years back is every change since. What a history row means is "what
    ///     did this commit do to this file", which is <c>show</c> — and <c>--format=</c> is what
    ///     drops the commit header so the answer is the patch and not the patch under a letter.
    /// </remarks>
    (int Code, string Output) Patch(string path, string revision, string extra) {
        List<string> arguments = revision.Length > 0 ? ["show", "--format="] : ["diff"];

        // ⚠ Before the revision, not after it. Options that follow a positional argument are read
        // as more of them by several git verbs, and the failure is an "ambiguous argument" that
        // reads like the path being wrong.
        if (extra.Length > 0) {
            arguments.Add(extra);
        }

        arguments.Add(revision.Length > 0 ? revision : "HEAD");
        arguments.Add("--");
        arguments.Add(path);

        var (code, output, _) = Run(root, [.. arguments]);
        return (code, output);
    }

    SourceControlDiff Diff(string path, string revision) {
        if (string.IsNullOrWhiteSpace(path)) {
            return new(true, string.Empty);
        }

        // ⚠ Asked before the patch rather than sniffed out of it. `--numstat` answers "-\t-" for a
        // binary file and a pair of counts for a text one, which is git's own decision about which
        // this is — made with the same attributes and the same heuristics it will use a moment later
        // when it either writes a patch or refuses to.
        var (code, counts) = Patch(path, revision, "--numstat");

        if (code != 0) {
            return new(true, string.Empty);
        }

        if (counts.StartsWith("-\t-", StringComparison.Ordinal)) {
            // git's own sentence about a binary file, which is `<path> | Bin 1234 -> 5678 bytes`.
            // Nothing here computes it: the sizes are the blobs' and git already has them.
            var (statCode, stat) = Patch(path, revision, "--stat");

            return new(
                false,
                statCode == 0 && Line(stat) is { Length: > 0 } summary
                    ? summary
                    : $"{path} is binary, and there is no line-by-line diff for it."
            );
        }

        var (patchCode, patch) = Patch(path, revision, string.Empty);

        return new(true, patchCode == 0 ? patch : string.Empty);
    }

    /// <summary>The first line of <c>--stat</c> that names the file, without its total line.</summary>
    static string Line(string stat) {
        foreach (var line in stat.Split('\n')) {
            var trimmed = line.Trim();

            if (trimmed.Length > 0 && trimmed.Contains('|', StringComparison.Ordinal)) {
                return trimmed;
            }
        }

        return string.Empty;
    }

    string? Restore(string path, string revision) {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(revision)) {
            return "There is no revision to restore from.";
        }

        // ⚠ `--` again, and here it matters twice over: the revision is already spelled out as its
        // own argument, so a path that also names a branch would otherwise be read as a second
        // revision and git would complain about an ambiguous argument rather than doing the work.
        var (code, _, error) = Run(root, "checkout", revision, "--", path);

        if (code == 0) {
            return null;
        }

        return error.Trim() is { Length: > 0 } message
            ? message
            : "git could not restore that version of the file.";
    }

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
