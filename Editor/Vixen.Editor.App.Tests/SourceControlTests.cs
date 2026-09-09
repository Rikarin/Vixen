// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20 § B7: the provider seam, the git implementation, and the browser's column.</summary>
/// <remarks>
///     ⚠ <b>Split in three deliberately.</b> What git says is a question about git and is asked of a
///     real repository; what a folder shows is arithmetic and is asked of the fold on its own; and
///     what the grid draws is asked over a provider this file writes, because a column test that
///     shelled out would be a test of whichever git this machine has.
/// </remarks>
public class SourceControlStatusTests {
    /// <summary>A folder has no status of its own and takes the loudest one under it.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the column only helps in the folder you are already standing in</b> — the
    ///     one place you can see the files themselves — and a collapsed <c>Textures/</c> holding a
    ///     modified file would look clean.
    /// </remarks>
    [Fact]
    public void A_folder_speaks_for_the_loudest_thing_under_it() {
        SourceControlStatuses statuses = new();

        statuses.Accept(
            new Dictionary<string, SourceControlStatus> {
                ["Assets/Textures/wood.png"] = SourceControlStatus.Untracked,
                ["Assets/Textures/Detail/fine.png"] = SourceControlStatus.Conflicted,
                ["Assets/Scenes/Main.vxscene"] = SourceControlStatus.Modified
            }
        );

        Assert.Equal(SourceControlStatus.Conflicted, statuses.Of("Assets/Textures"));
        Assert.Equal(SourceControlStatus.Conflicted, statuses.Of("Assets/Textures/Detail"));
        Assert.Equal(SourceControlStatus.Modified, statuses.Of("Assets/Scenes"));

        // ⚠ The root folds too, and it is the one a person sees first.
        Assert.Equal(SourceControlStatus.Conflicted, statuses.Of("Assets"));

        // A file keeps its own answer whatever its folder came to.
        Assert.Equal(SourceControlStatus.Untracked, statuses.Of("Assets/Textures/wood.png"));
    }

    /// <summary>Unknown and unmodified are different answers, and drawing them alike is the defect.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 20's second bar: a status column that is sometimes right is worse than no
    ///     column.</b> Before a sweep lands there is no answer, and "clean" is a claim.
    /// </remarks>
    [Fact]
    public void Nothing_is_known_until_a_sweep_lands() {
        SourceControlStatuses statuses = new();

        Assert.False(statuses.IsKnown);
        Assert.Equal(SourceControlStatus.Unknown, statuses.Of("Assets/Scenes/Main.vxscene"));

        statuses.Accept(new Dictionary<string, SourceControlStatus>());

        Assert.True(statuses.IsKnown);
        Assert.Equal(SourceControlStatus.Unmodified, statuses.Of("Assets/Scenes/Main.vxscene"));
    }

    /// <summary>A second sweep replaces the first rather than accumulating.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the "cache it does not invalidate" the issue warns about, in miniature.</b> A
    ///     fold that merged into what was there would keep a file marked modified for the rest of the
    ///     session after somebody reverted it.
    /// </remarks>
    [Fact]
    public void A_later_sweep_forgets_what_the_last_one_said() {
        SourceControlStatuses statuses = new();

        statuses.Accept(
            new Dictionary<string, SourceControlStatus> { ["Assets/Scenes/Main.vxscene"] = SourceControlStatus.Modified }
        );

        Assert.Equal(SourceControlStatus.Modified, statuses.Of("Assets/Scenes"));

        statuses.Accept(new Dictionary<string, SourceControlStatus>());

        Assert.Equal(SourceControlStatus.Unmodified, statuses.Of("Assets/Scenes"));
        Assert.Equal(SourceControlStatus.Unmodified, statuses.Of("Assets/Scenes/Main.vxscene"));
    }
}

/// <summary>What git says about a real working tree, asked of a real one.</summary>
/// <remarks>
///     ⚠ <b>A repository made here rather than this one.</b> The engine's own checkout has whatever
///     the agent running the suite has left in it, so a test asserting "one file is modified" against
///     it would pass or fail on the state of somebody's desk.
/// </remarks>
public class GitSourceControlTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-git-" + Guid.NewGuid().ToString("N"));

    public GitSourceControlTests() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        // ⚠ The instrument first. Every assertion below is about git's answer, so a machine without
        // git has to fail loudly here rather than leave five tests quietly green about nothing.
        Assert.True(Git("init", "--quiet"), "git could not initialise a repository in a temp directory");

        Git("config", "user.email", "suite@example.invalid");
        Git("config", "user.name", "Suite");
        Git("config", "commit.gpgsign", "false");
    }

    public void Dispose() {
        GC.SuppressFinalize(this);

        try {
            Directory.Delete(root, recursive: true);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }
    }

    /// <summary>The three answers a browser column is made of, from one working tree.</summary>
    [Fact]
    public async Task It_reads_modified_added_and_untracked_out_of_a_working_tree() {
        Write("Assets/Committed.txt", "one");
        Write("Assets/Deleted.txt", "gone");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        var provider = GitSourceControl.For(root);

        Assert.NotNull(provider);

        // Nothing has changed yet, so the sweep is empty and every path is unmodified by absence.
        Assert.Empty(await provider!.StatusAsync());

        Write("Assets/Committed.txt", "two");
        Write("Assets/New.txt", "new");
        File.Delete(Path.Combine(root, "Assets", "Deleted.txt"));
        Write("Assets/Staged.txt", "staged");

        Assert.True(Git("add", "Assets/Staged.txt"));

        var swept = await provider!.StatusAsync();

        Assert.Equal(SourceControlStatus.Modified, swept["Assets/Committed.txt"]);
        Assert.Equal(SourceControlStatus.Untracked, swept["Assets/New.txt"]);
        Assert.Equal(SourceControlStatus.Deleted, swept["Assets/Deleted.txt"]);
        Assert.Equal(SourceControlStatus.Added, swept["Assets/Staged.txt"]);

        // ⚠ The count is part of the claim: a sweep that returned everything would satisfy every
        // lookup above and would be a column that marks a clean project as changed.
        Assert.Equal(4, swept.Count);
    }

    /// <summary>An untracked directory is reported file by file, which is what a grid draws.</summary>
    /// <remarks>
    ///     ⚠ <b>git's default answer for a new folder is the folder</b>, one line, and a browser
    ///     showing that folder's contents would mark none of them — which is exactly the case a drop
    ///     from the desktop creates.
    /// </remarks>
    [Fact]
    public async Task A_new_folder_is_reported_by_its_files_rather_than_by_itself() {
        Write("Assets/Committed.txt", "one");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Write("Assets/Bark/bark.png", "bark");
        Write("Assets/Bark/Detail/fine.png", "fine");

        var swept = await GitSourceControl.For(root)!.StatusAsync();

        Assert.Equal(SourceControlStatus.Untracked, swept["Assets/Bark/bark.png"]);
        Assert.Equal(SourceControlStatus.Untracked, swept["Assets/Bark/Detail/fine.png"]);
    }

    /// <summary>A name with a space in it survives the parse, because asset names have spaces in them.</summary>
    /// <remarks>
    ///     ⚠ <b>git quotes such a path in its default porcelain output and does not with <c>-z</c>.</b>
    ///     A parser reading the quoted form would file the status under a path with quotation marks
    ///     in it, which matches no asset and shows as a blank column for exactly the files artists
    ///     name that way.
    /// </remarks>
    [Fact]
    public async Task A_path_with_a_space_is_read_whole() {
        Write("Assets/Concept art (final).png", "art");

        var swept = await GitSourceControl.For(root)!.StatusAsync();

        Assert.Equal(SourceControlStatus.Untracked, swept["Assets/Concept art (final).png"]);
    }

    /// <summary>Revert is the second verb the row asks for, and it puts the bytes back.</summary>
    [Fact]
    public async Task Revert_puts_a_file_back_as_it_was_committed() {
        Write("Assets/Committed.txt", "one");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Write("Assets/Committed.txt", "two");

        var provider = GitSourceControl.For(root)!;

        var before = await provider.StatusAsync();

        Assert.Equal(SourceControlStatus.Modified, before["Assets/Committed.txt"]);

        var message = await provider.RevertAsync("Assets/Committed.txt");

        Assert.Null(message);
        Assert.Equal("one", File.ReadAllText(Path.Combine(root, "Assets", "Committed.txt")));
        Assert.Empty(await provider.StatusAsync());
    }

    /// <summary>A project in a subdirectory of the repository gets paths it can use.</summary>
    /// <remarks>
    ///     ⚠ <b>Porcelain paths are relative to the repository root and every path in the editor is
    ///     relative to the project</b>, and the two are the same string only when the project is the
    ///     repository. Two games in one checkout, or an engine with a sample project in it, is the
    ///     ordinary case where they are not — and the symptom is not a crash but a column that is
    ///     blank for every file, which reads as "nothing has changed".
    /// </remarks>
    [Fact]
    public async Task A_project_below_the_repository_root_is_reported_relative_to_itself() {
        var game = Path.Combine(root, "Game");

        Write("Game/Assets/wood.png", "wood");

        var provider = GitSourceControl.For(game);

        Assert.NotNull(provider);

        var swept = await provider!.StatusAsync();

        Assert.Equal(SourceControlStatus.Untracked, swept["Assets/wood.png"]);
        Assert.DoesNotContain("Game/Assets/wood.png", swept.Keys);
    }

    /// <summary>The third verb: what has been committed to one asset, newest first.</summary>
    [Fact]
    public async Task History_lists_the_commits_that_touched_one_file_newest_first() {
        Write("Assets/Notes.txt", "one");
        Write("Assets/Other.txt", "unrelated");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Write("Assets/Notes.txt", "two");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "second"));

        Write("Assets/Other.txt", "still unrelated");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "third"));

        var history = await GitSourceControl.For(root)!.HistoryAsync("Assets/Notes.txt", 50);

        // ⚠ Two and not three. A `git log` with no pathspec would answer every commit in the
        // repository, which is what a panel showing an asset's history must not do — and it would
        // satisfy an assertion that only looked for "second" somewhere in the list.
        Assert.Equal(2, history.Count);
        Assert.Equal("second", history[0].Summary);
        Assert.Equal("first", history[1].Summary);

        Assert.Equal("Suite", history[0].Author);
        Assert.NotEqual(default, history[0].When);
        Assert.Equal(40, history[0].Id.Length);
        Assert.True(history[0].ShortId.Length is > 0 and < 40);
        Assert.StartsWith(history[0].ShortId, history[0].Id, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A renamed asset keeps its history, which is the whole reason this is worth asking per
    ///     asset.</b> Every asset is renamed the first time somebody tidies a folder, and without
    ///     <c>--follow</c> the log stops at the rename — so a file with three years behind it reads
    ///     as having been created last Tuesday, which is worse than no panel.
    /// </summary>
    [Fact]
    public async Task History_follows_a_file_across_a_rename() {
        Write("Assets/Before.txt", "one");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Assert.True(Git("mv", "Assets/Before.txt", "Assets/After.txt"));
        Assert.True(Git("commit", "--quiet", "-m", "renamed"));

        var history = await GitSourceControl.For(root)!.HistoryAsync("Assets/After.txt", 50);

        Assert.Equal(2, history.Count);
        Assert.Equal("renamed", history[0].Summary);
        Assert.Equal("first", history[1].Summary);
    }

    /// <summary>A history bounded is a history bounded, because a panel is not an archive.</summary>
    [Fact]
    public async Task History_stops_at_the_limit_it_was_given() {
        for (var index = 0; index < 4; index++) {
            Write("Assets/Notes.txt", "version " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.True(Git("add", "."));
            Assert.True(Git("commit", "--quiet", "-m", "commit " + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.Equal(2, (await GitSourceControl.For(root)!.HistoryAsync("Assets/Notes.txt", 2)).Count);
        Assert.Equal(4, (await GitSourceControl.For(root)!.HistoryAsync("Assets/Notes.txt", 50)).Count);
    }

    /// <summary>
    ///     The fourth verb, and the design decision the issue said had to be made before the viewer:
    ///     a text asset's diff is a patch and a binary asset's is one honest sentence.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Most of a game project is not text.</b> A unified diff for a <c>.png</c> or a mesh
    ///     is a promise the viewer breaks the first time somebody uses it, so the answer for one is
    ///     not a patch with the bytes elided — it is git's own <c>--stat</c> sentence, which says it
    ///     is binary and how its size moved. Both halves are asserted here because a flag that was
    ///     always <c>true</c> would pass the first half and a flag that was always <c>false</c>
    ///     would pass the second.
    /// </remarks>
    [Fact]
    public async Task A_text_diff_is_a_patch_and_a_binary_one_is_a_sentence_about_bytes() {
        Write("Assets/Notes.txt", "before\n");
        WriteBytes("Assets/Blob.bin", [0, 1, 2, 3]);

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Write("Assets/Notes.txt", "after\n");
        WriteBytes("Assets/Blob.bin", [0, 1, 2, 3, 4, 5, 6, 7]);

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "second"));

        var provider = GitSourceControl.For(root)!;
        var newest = (await provider.HistoryAsync("Assets/Notes.txt", 50))[0].Id;

        var text = await provider.DiffAsync("Assets/Notes.txt", newest);

        Assert.True(text.IsText);
        Assert.Contains("-before", text.Text, StringComparison.Ordinal);
        Assert.Contains("+after", text.Text, StringComparison.Ordinal);

        var binary = await provider.DiffAsync("Assets/Blob.bin", newest);

        Assert.False(binary.IsText);
        Assert.Contains("Bin", binary.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@@", binary.Text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A revision's diff is what <em>that commit</em> did to the file, not everything since. The
    ///     two are the same string only for the newest commit, which is why the assertion is on the
    ///     older one.
    /// </summary>
    [Fact]
    public async Task An_older_revisions_diff_is_what_that_commit_changed() {
        Write("Assets/Notes.txt", "one\n");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Write("Assets/Notes.txt", "two\n");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "second"));

        Write("Assets/Notes.txt", "three\n");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "third"));

        var provider = GitSourceControl.For(root)!;
        var history = await provider.HistoryAsync("Assets/Notes.txt", 50);

        // The middle commit: it turned "one" into "two" and knows nothing about "three".
        var middle = await provider.DiffAsync("Assets/Notes.txt", history[1].Id);

        Assert.Contains("-one", middle.Text, StringComparison.Ordinal);
        Assert.Contains("+two", middle.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("three", middle.Text, StringComparison.Ordinal);
    }

    /// <summary>The working tree's own changes are the diff with no revision named.</summary>
    [Fact]
    public async Task An_empty_revision_diffs_the_working_tree_against_the_last_commit() {
        Write("Assets/Notes.txt", "committed\n");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        var provider = GitSourceControl.For(root)!;

        // Nothing has changed, so there is no patch — and that is not the same as a binary refusal.
        var clean = await provider.DiffAsync("Assets/Notes.txt", string.Empty);

        Assert.True(clean.IsText);
        Assert.Equal(string.Empty, clean.Text);

        Write("Assets/Notes.txt", "edited\n");

        var dirty = await provider.DiffAsync("Assets/Notes.txt", string.Empty);

        Assert.True(dirty.IsText);
        Assert.Contains("-committed", dirty.Text, StringComparison.Ordinal);
        Assert.Contains("+edited", dirty.Text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Restore writes one old version into the working tree and leaves everything else alone,
    ///     which is what a row in an asset's history can honestly offer.
    /// </summary>
    [Fact]
    public async Task Restore_writes_one_old_version_into_the_working_tree() {
        Write("Assets/Notes.txt", "one");
        Write("Assets/Other.txt", "untouched");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Write("Assets/Notes.txt", "two");
        Write("Assets/Other.txt", "moved on");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "second"));

        var provider = GitSourceControl.For(root)!;
        var history = await provider.HistoryAsync("Assets/Notes.txt", 50);

        Assert.Null(await provider.RestoreAsync("Assets/Notes.txt", history[1].Id));

        Assert.Equal("one", File.ReadAllText(Path.Combine(root, "Assets", "Notes.txt")));

        // ⚠ The other file is the assertion that this was not a checkout of the whole revision,
        // which is the thing an editor must not do behind a row in an asset panel.
        Assert.Equal("moved on", File.ReadAllText(Path.Combine(root, "Assets", "Other.txt")));

        // ⚠ And the result is an ordinary uncommitted change, which is what makes the button safe to
        // offer at all: the file is Modified, so the browser marks it and Revert to Source Control
        // undoes it. (`checkout <rev> -- <path>` stages what it writes, so this is git's `M ` rather
        // than its ` M` — one status either way, which is the point of the column being six answers
        // and not git's whole vocabulary.)
        Assert.Equal(SourceControlStatus.Modified, (await provider.StatusAsync())["Assets/Notes.txt"]);
    }

    /// <summary>A file git has never heard of has no history, rather than the repository's.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument check on every assertion above.</b> A <c>log</c> that lost its
    ///     pathspec would answer the whole repository for this too, and the panel would show a
    ///     freshly dropped texture as having twelve commits behind it.
    /// </remarks>
    [Fact]
    public async Task An_uncommitted_file_has_no_history() {
        Write("Assets/Committed.txt", "one");

        Assert.True(Git("add", "."));
        Assert.True(Git("commit", "--quiet", "-m", "first"));

        Write("Assets/Dropped.png", "just arrived");

        Assert.Empty(await GitSourceControl.For(root)!.HistoryAsync("Assets/Dropped.png", 50));
    }

    /// <summary>A directory that is not a working tree has no provider, rather than a silent one.</summary>
    /// <remarks>
    ///     ⚠ <b>The difference is what the browser draws.</b> No provider means no column; a provider
    ///     that answered nothing would mark every file in a project that is not in git as clean.
    /// </remarks>
    [Fact]
    public void A_directory_outside_a_repository_has_no_provider() {
        var outside = Path.Combine(Path.GetTempPath(), "vixen-nogit-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        try {
            Assert.Null(GitSourceControl.For(outside));
        } finally {
            Directory.Delete(outside, recursive: true);
        }
    }

    void Write(string relative, string text) {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A file git will call binary, which is a file with a NUL near the front of it.</summary>
    void WriteBytes(string relative, byte[] bytes) {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    bool Git(params string[] arguments) {
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ⚠ A repository of its own, with the user's config kept out of it: a machine whose global
        // config sets `core.autocrlf`, a hooks path or a template directory would otherwise be
        // deciding what this suite asserts.
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["HOME"] = root;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(start);

            if (process is null) {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0;
        } catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception) {
            return false;
        }
    }
}

/// <summary>The column itself: what the grid draws when a provider has answered.</summary>
/// <remarks>
///     ⚠ <b>Over a provider written here rather than over git.</b> What has to be proved is that a
///     status reaches a tile — the seam, the sweep, the fold and the bind — and a test that made a
///     repository would prove that and also whether this machine's git behaves, which is two claims
///     in one assertion.
/// </remarks>
public class SourceControlColumnTests {
    /// <summary>A tile shows the letter its asset's status carries, and nothing when it is clean.</summary>
    [Fact]
    public void The_grid_marks_a_modified_asset_and_leaves_a_clean_one_alone() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var scenes = editor.Project.Assets.Entries
            .Where(entry => entry.Path.EndsWith(".vxscene", StringComparison.Ordinal))
            .ToList();

        if (scenes.Count == 0) {
            throw editor.Fail("the fixture project has no scene to mark");
        }

        var scene = scenes[0];

        // ⚠ The scene is in a folder, so what the root grid draws is the FOLD — which is the half of
        // this that a test over a file in the root could not see.
        var folder = scene.Path[..scene.Path.LastIndexOf('/')];

        Assert.True(folder.Contains('/'), "the fixture's scene is at the root, so this proves no fold");

        editor.Editor.UseSourceControl(new Fake(new Dictionary<string, SourceControlStatus> {
            [scene.Path] = SourceControlStatus.Modified
        }));

        editor.Editor.Sweep();
        Swept(editor);

        var marked = Tiles(editor).Where(tile => !tile.Status.HasClass("hidden")).ToList();

        // ⚠ The count is the assertion. A grid that marked everything would contain the one tile
        // this looks for, and a column that marks a clean project is the version of this feature
        // that is worse than nothing.
        Assert.Single(marked);
        Assert.Equal("M", marked[0].Status.Text);
        Assert.True(marked[0].Status.HasClass("modified"));
        Assert.Equal(folder, marked[0].Node?.Path);
    }

    /// <summary>A project with no provider draws no column at all.</summary>
    [Fact]
    public void A_project_that_is_not_under_source_control_marks_nothing() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.Settle();

        Assert.False(editor.Editor.SourceControl.IsKnown);

        var tiles = Tiles(editor);

        Assert.NotEmpty(tiles);
        Assert.All(tiles, tile => Assert.True(tile.Status.HasClass("hidden")));
    }

    /// <summary>The verb is greyed until a provider has said so, and named all the same.</summary>
    /// <remarks>
    ///     Doc 20's first bar: a verb that is not available is <i>visibly</i> unavailable rather than
    ///     absent, so Revert is in the Assets menu of an editor whose project is not in git.
    /// </remarks>
    [Fact]
    public void Revert_is_registered_and_disabled_without_a_provider() {
        using var editor = EditorSession.Start();

        Assert.NotNull(editor.Shell.Commands["assets.revert"]);
        Assert.False(editor.CanRun("assets.revert"));
    }

    /// <summary>
    ///     The list view carries the same column, at the trailing edge of a row.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The claim that a tree row had nowhere to put one is refuted by the outliner.</b> A
    ///     <c>TreeRow</c> is a control with children and <c>tree-label</c> grows, so an element added
    ///     after it lands at the trailing edge — which is what the eye and the padlock have been
    ///     doing since doc 20 § Part D landed, and <c>TreeView.RowBound</c>'s own remarks name a
    ///     source-control mark as the second case it exists for. No control change was needed.
    /// </remarks>
    [Fact]
    public void The_list_marks_a_modified_asset_and_leaves_a_clean_one_alone() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var scenes = editor.Project.Assets.Entries
            .Where(entry => entry.Path.EndsWith(".vxscene", StringComparison.Ordinal))
            .ToList();

        if (scenes.Count == 0) {
            throw editor.Fail("the fixture project has no scene to mark");
        }

        var scene = scenes[0];

        editor.Editor.UseSourceControl(new Fake(new Dictionary<string, SourceControlStatus> {
            [scene.Path] = SourceControlStatus.Modified
        }));

        editor.Editor.Sweep();
        Swept(editor);

        // ⚠ `editor.Assets` puts the panel in front of the tree, which is what this asks about. The
        // panel opens on the grid.
        var tree = editor.Assets;

        editor.Settle();

        var rows = Marks(tree);

        // ⚠ The count of *rows* is asserted first. A tree showing nothing would satisfy every
        // assertion below by having no unmarked row either, which is the vacuous shape a loop that
        // asserts inside itself takes on an empty collection.
        Assert.NotEmpty(rows);

        var marked = rows.Where(mark => !mark.HasClass("hidden")).ToList();

        Assert.NotEmpty(marked);
        Assert.All(marked, mark => Assert.Equal("M", mark.Text));
        Assert.All(marked, mark => Assert.True(mark.HasClass("modified")));

        // And the folds do not mark the whole project: something under the tree is clean.
        Assert.True(marked.Count < rows.Count, "every row in the list is marked, so the column says nothing");
    }

    /// <summary>A project with no provider draws no column on the list either.</summary>
    [Fact]
    public void The_list_marks_nothing_without_a_provider() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var tree = editor.Assets;

        editor.Settle();

        Assert.False(editor.Editor.SourceControl.IsKnown);

        var rows = Marks(tree);

        Assert.NotEmpty(rows);
        Assert.All(rows, mark => Assert.True(mark.HasClass("hidden")));
    }

    /// <summary>Pumps frames until the sweep has landed, and says so if it never does.</summary>
    /// <remarks>
    ///     ⚠ <b>A condition rather than a frame count, and the difference is not theoretical.</b>
    ///     <c>EditorSourceControl.Sweep</c> hands its answer to the deferred queue behind a
    ///     <c>Task.Run</c>, so a fixed <c>Settle()</c> asserts "the thread pool got round to it
    ///     within a dozen frames" — a wall-clock budget wearing a frame counter's clothes. It failed
    ///     here, twice, on a machine running another project's suite, and the two tests it failed
    ///     were different ones each time. The ceiling below is a hang check and not a bound; what
    ///     the deferred queue is actually waiting on is
    ///     <see href="https://github.com/Rikarin/Vixen/issues/1179">#1179</see>.
    /// </remarks>
    static void Swept(EditorSession editor) {
        for (var frame = 0; frame < 2000 && !editor.Editor.SourceControl.IsKnown; frame++) {
            editor.Frame();
        }

        editor.Settle();

        Assert.True(editor.Editor.SourceControl.IsKnown, "the sweep never landed");
    }

    /// <summary>Every live row's status mark, which excludes the pool's parked rows.</summary>
    static IReadOnlyList<UiElement> Marks(TreeView tree) {
        List<UiElement> marks = [];

        foreach (var row in Descendants(tree).OfType<TreeRow>()) {
            if (row.Node is null || row.HasClass("parked")) {
                continue;
            }

            foreach (var child in row.Children) {
                if (string.Equals(child.Tag, "row-status", StringComparison.Ordinal)) {
                    marks.Add(child);
                }
            }
        }

        return marks;
    }

    /// <summary>The grid's own live tiles, which excludes the pool's parked ones.</summary>
    static IReadOnlyList<AssetTile> Tiles(EditorSession editor) {
        var grid = Descendants(editor.Panel("project")).OfType<AssetGrid>().FirstOrDefault()
            ?? throw editor.Fail("the browser has no grid");

        return [.. grid.Tiles.Where(tile => tile.Node is not null)];
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    sealed class Fake(IReadOnlyDictionary<string, SourceControlStatus> statuses) : ISourceControl {
        public string Name => "Fake";

        public ValueTask<IReadOnlyDictionary<string, SourceControlStatus>> StatusAsync() => new(statuses);

        public ValueTask<string?> RevertAsync(string path) => new((string?)null);

        public ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string path, int limit) =>
            new((IReadOnlyList<SourceControlRevision>)[]);

        public ValueTask<SourceControlDiff> DiffAsync(string path, string revision) =>
            new(new SourceControlDiff(true, string.Empty));

        public ValueTask<string?> RestoreAsync(string path, string revision) => new((string?)null);
    }
}
