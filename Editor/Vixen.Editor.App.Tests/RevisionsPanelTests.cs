// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Issue 1170: the panel doc 20 § B7's last two verbs needed and did not have.</summary>
/// <remarks>
///     <para>
///         <b>History and diff are one panel, which is why they land together.</b> The issue's own
///         argument: a history row's value is what a person does with it — see what that commit
///         changed, or take that version back — so a list with nothing under it is a list of
///         sentences, and a diff with no list is a diff of the version you already have.
///     </para>
///     <para>
///         ⚠ <b>Over a provider written here rather than over git</b>, on
///         <c>SourceControlColumnTests</c>' terms. What has to be proved is that a revision reaches
///         a row, that picking one asks for its diff and draws the answer, and that Restore reaches
///         the provider with the right revision. What git says is asked of a real repository in
///         <c>GitSourceControlTests</c>, and mixing the two would put this machine's git inside
///         every assertion about a panel.
///     </para>
/// </remarks>
public class RevisionsPanelTests {
    /// <summary>The claim: an asset's revisions become rows, and picking one draws its diff.</summary>
    [Fact]
    public void Picking_a_revision_asks_for_its_diff_and_draws_the_answer() {
        using var editor = EditorSession.Start();
        var asset = Select(editor, out var path);

        var provider = new Recording(path);

        editor.Editor.UseSourceControl(provider);
        Swept(editor);
        editor.Run("assets.history").Settle();

        var view = editor.Editor.Revisions;

        Assert.NotNull(view);
        Assert.Equal(path, view!.Path);
        Assert.Equal(2, view.Count);

        // Nothing is picked until somebody picks something, so the pane below is empty and the
        // Restore button has nothing to act on.
        Assert.Null(view.Chosen);
        Assert.Equal(string.Empty, view.Patch);

        view.Choose(Recording.Newest);
        editor.Settle();

        Assert.Equal(Recording.Newest, view.ChosenId);
        Assert.Equal(Recording.Newest, provider.Diffed);
        Assert.True(view.IsText);
        Assert.Contains("+after", view.Patch, StringComparison.Ordinal);

        Assert.NotEqual(default, asset);
    }

    /// <summary>
    ///     A binary asset's diff is drawn as the sentence it is and marked as one, which is what the
    ///     styling reads to stop setting a refusal in a code face.
    /// </summary>
    [Fact]
    public void A_binary_assets_diff_is_drawn_as_a_sentence_rather_than_a_patch() {
        using var editor = EditorSession.Start();

        Select(editor, out var path);

        editor.Editor.UseSourceControl(new Recording(path));
        Swept(editor);
        editor.Run("assets.history").Settle();

        var view = editor.Editor.Revisions!;

        view.Choose(Recording.Older);
        editor.Settle();

        Assert.False(view.IsText);
        Assert.Contains("Bin", view.Patch, StringComparison.Ordinal);
    }

    /// <summary>Restore reaches the provider with the revision that was picked and not another.</summary>
    /// <remarks>
    ///     ⚠ <b>The id is the assertion, not that anything was called.</b> A panel that restored the
    ///     newest revision whatever the row said would satisfy "Restore was asked for" and would be
    ///     a button that quietly does nothing, since the newest revision is usually what is already
    ///     in the working tree.
    /// </remarks>
    [Fact]
    public void Restore_asks_the_provider_for_the_picked_revision() {
        using var editor = EditorSession.Start();

        Select(editor, out var path);

        var provider = new Recording(path);

        editor.Editor.UseSourceControl(provider);
        Swept(editor);
        editor.Run("assets.history").Settle();

        var view = editor.Editor.Revisions!;

        view.Choose(Recording.Older);
        editor.Settle();

        view.Restore();
        editor.Settle();

        Assert.True(editor.IsAsking, "Restore overwrites a file on disk and must ask first");

        editor.Answer("Restore");

        Assert.Equal(Recording.Older, provider.Restored);
        Assert.Equal(path, provider.RestoredPath);
    }

    /// <summary>
    ///     ⚠ The instrument check. A panel that showed the first of a multi-selection would be right
    ///     by accident, and it has a Restore button on it — so more than one selected asset is a
    ///     sentence and an empty list rather than a guess.
    /// </summary>
    [Fact]
    public void A_multi_selection_shows_no_history_at_all() {
        using var editor = EditorSession.Start();

        Select(editor, out var path);

        editor.Editor.UseSourceControl(new Recording(path));
        Swept(editor);
        editor.Run("assets.history").Settle();

        var view = editor.Editor.Revisions!;

        Assert.Equal(2, view.Count);

        // A second file, because a project with one has no multi-selection to make.
        File.WriteAllText(Path.Combine(editor.Project.Paths.Assets, "second.txt"), "another");
        editor.Project.Assets.Scan();
        editor.Settle();

        var files = editor.Project.Assets.Entries.Where(entry => !entry.IsFolder).Take(2).ToList();

        Assert.Equal(2, files.Count);

        editor.Project.Selection.Set(files.Select(entry => entry.Guid));
        editor.Editor.ShowRevisions();
        editor.Settle();

        Assert.Equal(0, view.Count);
        Assert.Equal(string.Empty, view.Path);
    }

    /// <summary>The command is registered and greyed until a provider has answered.</summary>
    [Fact]
    public void Show_history_is_registered_and_disabled_without_a_provider() {
        using var editor = EditorSession.Start();

        Assert.NotNull(editor.Shell.Commands["assets.history"]);
        Assert.False(editor.CanRun("assets.history"));
    }

    /// <summary>
    ///     Takes the status sweep, because the command's enablement asks whether one has landed —
    ///     a provider set and never asked is an editor whose menu line is still greyed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A condition rather than a frame count.</b> The sweep hands its answer to the
    ///     deferred queue behind a <c>Task.Run</c>, so a fixed number of settles asserts "the thread
    ///     pool got round to it in time", which is a wall-clock budget wearing a frame counter's
    ///     clothes. The ceiling below is a hang check and not a bound.
    /// </remarks>
    static void Swept(EditorSession editor) {
        editor.Editor.Sweep();

        for (var frame = 0; frame < 2000 && !editor.Editor.SourceControl.IsKnown; frame++) {
            editor.Frame();
        }

        editor.Settle();

        Assert.True(editor.Editor.SourceControl.IsKnown, "the sweep never landed");
    }

    /// <summary>Selects exactly one file in the project, and says which.</summary>
    static Vixen.Core.AssetId Select(EditorSession editor, out string path) {
        editor.Open("project");
        editor.Settle();

        var files = editor.Project.Assets.Entries.Where(candidate => !candidate.IsFolder).ToList();

        if (files.Count == 0) {
            throw editor.Fail("the fixture project has no file to show a history for");
        }

        var entry = files[0];

        path = entry.Path;
        editor.Project.Selection.Set([entry.Guid]);

        return entry.Guid;
    }

    /// <summary>
    ///     A provider that answers two fixed revisions and records what it was asked, so an
    ///     assertion is about the panel rather than about git.
    /// </summary>
    sealed class Recording(string path) : ISourceControl {
        public const string Newest = "1111111111111111111111111111111111111111";
        public const string Older = "2222222222222222222222222222222222222222";

        /// <summary>Which revision the panel last asked for a diff of.</summary>
        public string? Diffed { get; private set; }

        /// <summary>Which revision the panel last asked to restore.</summary>
        public string? Restored { get; private set; }

        /// <summary>And which file it asked to restore it into.</summary>
        public string? RestoredPath { get; private set; }

        public string Name => "Recording";

        public ValueTask<IReadOnlyDictionary<string, SourceControlStatus>> StatusAsync() =>
            new((IReadOnlyDictionary<string, SourceControlStatus>)new Dictionary<string, SourceControlStatus>(
                StringComparer.Ordinal
            ) {
                [path] = SourceControlStatus.Modified
            });

        public ValueTask<string?> RevertAsync(string requested) => new((string?)null);

        public ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string requested, int limit) =>
            new(
                string.Equals(requested, path, StringComparison.Ordinal)
                    ? (IReadOnlyList<SourceControlRevision>)[
                        new SourceControlRevision(
                            Newest,
                            "1111111",
                            "Suite",
                            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
                            "second"
                        ),
                        new SourceControlRevision(
                            Older,
                            "2222222",
                            "Suite",
                            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
                            "first"
                        )
                    ]
                    : []
            );

        public ValueTask<SourceControlDiff> DiffAsync(string requested, string revision) {
            Diffed = revision;

            return new(
                string.Equals(revision, Newest, StringComparison.Ordinal)
                    ? new SourceControlDiff(true, "@@ -1 +1 @@\n-before\n+after\n")
                    : new SourceControlDiff(false, $"{requested} | Bin 4 -> 8 bytes")
            );
        }

        public ValueTask<string?> RestoreAsync(string requested, string revision) {
            Restored = revision;
            RestoredPath = requested;

            return new((string?)null);
        }
    }
}
