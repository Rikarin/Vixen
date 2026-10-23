// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;
using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>#1505: source control is a published service, and Revert, Show History and the revisions panel a module over it.</summary>
/// <remarks>
///     Over a provider written here, on <c>SourceControlColumnTests</c>' terms: what is proved is where the
///     sweep is taken and who owns the verbs, not what this machine's git says.
/// </remarks>
public class SourceControlModuleTests {
    /// <summary>
    ///     The verbs know whether they are available with the content browser closed — the sweep follows the
    ///     session's rescan and the editor's start, not the panel.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The application's sweep returned early unless the browser was open</b>, so an editor whose
    ///     Project tab had never been shown greyed Revert and Show History for a project that was in git.
    ///     Both halves: the startup sweep has looked for a provider before anybody asked, and a rescan with
    ///     the panel closed makes the provider's answer known.
    /// </remarks>
    [Fact]
    public void Enablement_is_known_without_the_browser_panel_open() {
        using var editor = EditorSession.Start();

        editor.Close("project");
        Assert.False(editor.Shell.Workspace.IsOpen("project"));

        var sourceControl = SourceControlHarness.Of(editor);

        // The startup sweep: the harness project is not in a working tree, so what it leaves is "looked,
        // and found nothing" — which is a different answer from "never looked".
        for (var frame = 0; frame < 2000 && !sourceControl.IsSought; frame++) {
            editor.Frame();
        }

        Assert.True(sourceControl.IsSought, "nothing looked for a provider without the browser open");

        sourceControl.Use(new Fixed());
        Assert.False(sourceControl.IsKnown);

        Select(editor);
        Assert.False(editor.CanRun("assets.history"), "history is offered before any sweep has answered");

        // ⚠ The session's rescan, which is what the watcher and Refresh raise — not `Sweep()` by hand.
        editor.ProjectSession.Rescan();

        // A hang check and not a bound: the answer comes back through the deferred queue.
        for (var frame = 0; frame < 2000 && !sourceControl.IsKnown; frame++) {
            editor.Frame();
        }

        Assert.False(editor.Shell.Workspace.IsOpen("project"));
        Assert.True(sourceControl.IsKnown, "a rescan with the browser closed took no sweep");
        Assert.Equal(SourceControlStatus.Modified, sourceControl.Status(Fixed.Path));
        Assert.True(editor.CanRun("assets.history"));
        Assert.True(editor.CanRun("assets.revert"));
    }

    /// <summary>Show History opens the module's revisions panel on the selection, and the verbs leave with the module.</summary>
    /// <remarks>
    ///     ⚠ <b>The off half is what proves ownership.</b> A registration left behind in the application would
    ///     keep the first half green; only an editor whose commands and panel go when the module is switched
    ///     off has one place they come from.
    /// </remarks>
    [Fact]
    public void History_opens_the_revisions_panel_from_the_module_and_leaves_with_it() {
        using var editor = EditorSession.Start();

        Assert.Equal(PluginState.Active, editor.Plugins.Find(SourceControlModule.ModuleId)!.State);

        var path = Select(editor);
        var sourceControl = SourceControlHarness.Of(editor);

        sourceControl.Use(new Fixed(path));
        Swept(editor, sourceControl);

        Assert.False(editor.Shell.Workspace.IsOpen(AssetRevisions.PanelId));

        editor.Run("assets.history").Settle();

        Assert.True(editor.Shell.Workspace.IsOpen(AssetRevisions.PanelId));

        var view = SourceControlHarness.Revisions(editor).View;

        Assert.NotNull(view);
        Assert.Equal(path, view!.Path);
        Assert.Equal(1, view.Count);

        editor.Plugins.Disable(SourceControlModule.ModuleId);

        Assert.Null(editor.Shell.Commands["assets.history"]);
        Assert.Null(editor.Shell.Commands["assets.revert"]);
        Assert.DoesNotContain(editor.Shell.Workspace.Panels, panel => panel.Id == AssetRevisions.PanelId);

        // ⚠ The service is the application's and outlives the module: a column still has its answer.
        Assert.True(editor.Plugins.Services.Require<ISourceControl>().IsKnown);

        editor.Plugins.Enable(SourceControlModule.ModuleId);

        Assert.NotNull(editor.Shell.Commands["assets.history"]);
        Assert.NotNull(editor.Shell.Commands["assets.revert"]);
    }

    /// <summary>Revert asks first, and then reverts through the published service, which rescans once.</summary>
    [Fact]
    public void Revert_asks_and_then_reverts_through_the_published_service() {
        using var editor = EditorSession.Start();

        var path = Select(editor);
        var provider = new Fixed(path);
        var sourceControl = SourceControlHarness.Of(editor);

        sourceControl.Use(provider);
        Swept(editor, sourceControl);

        var reverted = 0;
        var rescans = 0;

        editor.Plugins.Services.Require<ISourceControl>().Reverted += () => reverted++;
        editor.ProjectSession.Rescanned += _ => rescans++;

        editor.Run("assets.revert").Settle();

        Assert.True(editor.IsAsking, "Revert destroys uncommitted work and must ask first");
        Assert.Empty(provider.Reverts);

        editor.Answer("Revert").Settle();

        Assert.Equal([path], provider.Reverts);
        Assert.Equal(1, reverted);
        Assert.Equal(1, rescans);
    }

    /// <summary>Selects exactly one file in the project, and says which.</summary>
    static string Select(EditorSession editor) {
        var files = editor.Project.Assets.Entries.Where(candidate => !candidate.IsFolder).ToList();

        if (files.Count == 0) {
            throw editor.Fail("the fixture project has no file to select");
        }

        var entry = files[0];

        editor.Project.Selection.Set([entry.Guid]);

        return entry.Path;
    }

    /// <summary>A sweep that comes back cancelled releases the latch, so the next sweep is taken (#1512).</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Deferred.When</c> runs neither of its handlers for a cancelled call</b>, and the sweep's
    ///         latch was cleared only by them — so one cancelled status left <c>sweeping</c> set for good and
    ///         every later sweep folded into <c>again</c>, which is the column that never drew that the
    ///         status-read race produced, by the other door.
    ///     </para>
    ///     <para>
    ///         Both of <c>When</c>'s paths: a status already cancelled when it is asked for, and one cancelled
    ///         later from the pool. The ceiling is a hang check.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cancelled_sweep_does_not_stop_the_next_one(bool later) {
        using var editor = EditorSession.Start();

        var sourceControl = SourceControlHarness.Of(editor);

        for (var frame = 0; frame < 2000 && !sourceControl.IsSought; frame++) {
            editor.Frame();
        }

        var provider = new Cancelling(later);

        sourceControl.Use(provider);
        sourceControl.Sweep();

        for (var frame = 0; frame < 2000 && provider.Asked == 0; frame++) {
            editor.Frame();
        }

        Assert.Equal(1, provider.Asked);
        editor.Settle();
        Assert.False(sourceControl.IsKnown);

        // The provider answers from now on; a latch the cancellation left set would take no sweep at all.
        Swept(editor, sourceControl);
        Assert.Equal(SourceControlStatus.Modified, sourceControl.Status(Fixed.Path));
    }

    /// <summary>A revert that comes back cancelled still counts, so the rescan after the last answer runs (#1512).</summary>
    [Fact]
    public void A_cancelled_revert_still_lets_the_rescan_run() {
        using var editor = EditorSession.Start();

        var sourceControl = SourceControlHarness.Of(editor);
        var reverted = 0;

        sourceControl.Reverted += () => reverted++;
        sourceControl.Use(new Cancelling(later: false, cancelsStatus: false));

        sourceControl.Revert([Fixed.Path, "Assets/Other.txt"]);

        for (var frame = 0; frame < 2000 && reverted == 0; frame++) {
            editor.Frame();
        }

        Assert.Equal(1, reverted);
    }

    /// <summary>A provider whose first status, and every revert, comes back cancelled.</summary>
    sealed class Cancelling(bool later, bool cancelsStatus = true) : ISourceControlProvider {
        public int Asked;

        public string Name => "Cancelling";

        public ValueTask<IReadOnlyDictionary<string, SourceControlStatus>> StatusAsync() {
            if (Interlocked.Increment(ref Asked) == 1 && cancelsStatus) {
                return Cancelled<IReadOnlyDictionary<string, SourceControlStatus>>();
            }

            return new((IReadOnlyDictionary<string, SourceControlStatus>)new Dictionary<string, SourceControlStatus>(StringComparer.Ordinal) {
                [Fixed.Path] = SourceControlStatus.Modified
            });
        }

        public ValueTask<string?> RevertAsync(string requested) => Cancelled<string?>();

        public ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string requested, int limit) =>
            new((IReadOnlyList<SourceControlRevision>)[]);

        public ValueTask<SourceControlDiff> DiffAsync(string requested, string revision) =>
            new(new SourceControlDiff(true, string.Empty));

        public ValueTask<string?> RestoreAsync(string requested, string revision) => new((string?)null);

        ValueTask<T> Cancelled<T>() {
            if (!later) {
                return ValueTask.FromCanceled<T>(new CancellationToken(canceled: true));
            }

            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(async () => {
                await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
                source.SetCanceled(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            return new(source.Task);
        }
    }

    static void Swept(EditorSession editor, ProjectSourceControl sourceControl) {
        sourceControl.Sweep();

        // A hang check and not a bound: the answer comes back through the deferred queue.
        for (var frame = 0; frame < 2000 && !sourceControl.IsKnown; frame++) {
            editor.Frame();
        }

        editor.Settle();

        Assert.True(sourceControl.IsKnown, "the sweep never landed");
    }

    /// <summary>A provider that says one file is modified, has one revision of it, and records reverts.</summary>
    sealed class Fixed(string path = Fixed.Path) : ISourceControlProvider {
        public const string Path = "Assets/Changed.txt";

        public List<string> Reverts { get; } = [];

        public string Name => "Fixed";

        public ValueTask<IReadOnlyDictionary<string, SourceControlStatus>> StatusAsync() =>
            new((IReadOnlyDictionary<string, SourceControlStatus>)new Dictionary<string, SourceControlStatus>(StringComparer.Ordinal) {
                [path] = SourceControlStatus.Modified
            });

        public ValueTask<string?> RevertAsync(string requested) {
            Reverts.Add(requested);
            return new((string?)null);
        }

        public ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string requested, int limit) =>
            new(
                string.Equals(requested, path, StringComparison.Ordinal)
                    ? (IReadOnlyList<SourceControlRevision>)[
                        new SourceControlRevision(new string('1', 40), "1111111", "Suite", DateTimeOffset.UnixEpoch, "only")
                    ]
                    : []
            );

        public ValueTask<SourceControlDiff> DiffAsync(string requested, string revision) =>
            new(new SourceControlDiff(true, string.Empty));

        public ValueTask<string?> RestoreAsync(string requested, string revision) => new((string?)null);
    }
}
