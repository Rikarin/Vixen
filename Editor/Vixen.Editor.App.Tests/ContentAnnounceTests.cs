// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>An import run from inside the editor is told to the documents that are open.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1006">#1006</a>'s second box, and it
///         is a near miss rather than an oversight.</b> <c>ContentTasks.Pump</c> already announced a
///         finished import to four things — the project browser's rescan, the build panel, the
///         viewport's mount and two <c>Invalidate</c> calls — and not one of them reaches a document.
///         <c>ExternalEdits.Rescan</c> <em>does</em> announce, but the <c>Rescan</c> delegate
///         <c>ContentTasks</c> holds is a different one: <c>EditorApplication</c> assigns it a
///         closure over three panels.
///     </para>
///     <para>
///         ⚠ <b>And the case it exists for moves no file the watcher can see.</b> A version bump or
///         an import-settings edit rewrites <c>Library/</c> and touches nothing under <c>Assets/</c>,
///         so a document waiting to hear that a model changed waits for ever.
///     </para>
/// </remarks>
public sealed class ContentAnnounceTests {
    /// <summary>A finished import reaches every open document, with no path named.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument is checked before the claim.</b> The listener counts every
    ///         announcement it is given, and the count is read as a <em>delta</em> around the import
    ///         rather than as a total — opening a document during a session that is already draining
    ///         a watcher would otherwise make this pass on somebody else's notification.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is asserted rather than tolerated.</b> An import rewrites a database of its
    ///         own; naming one file of it would be a path documents filter on, and every one of them
    ///         would filter this out.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_finished_import_is_announced_to_the_open_documents() {
        using var session = EditorSession.Start();

        var tasks = new ContentTasks(session.Project, session.Shell);
        var listener = new Listener(session.Project);

        var before = listener.Heard;

        tasks.Import();

        // ⚠ A hang check and not a budget. The project is a fresh temporary one with nothing in it;
        // what this waits for is the pool getting to the task at all, and a machine where that takes
        // half a minute has a problem this test cannot describe.
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (tasks.IsBusy && DateTime.UtcNow < deadline) {
            tasks.Pump();
            Thread.Sleep(5);
        }

        Assert.False(tasks.IsBusy, "the import never finished.");

        // The result may still be queued when the busy flag drops — it is enqueued first — so the
        // drain that matters is this one rather than whichever loop iteration happened to be last.
        tasks.Pump();

        Assert.True(
            listener.Heard > before,
            "A finished import told the panels, the mesh source and the surface source, and told no "
            + "open document anything. `ContentTasks.Pump` is the only place that can: the file "
            + "watcher never fires for an import that rewrites Library/ and moves nothing under "
            + "Assets/."
        );

        Assert.Null(listener.LastPath);
    }

    /// <summary>A finished import redraws the open Project panel from the rescanned database.</summary>
    /// <remarks>
    ///     ⚠ <b>The browser follows the content tasks itself now (#1293)</b>, through
    ///     <c>ContentTasks.Stale</c>, where the application used to assign one delegate that called it.
    ///     Nothing else in the suite noticed when that subscription was emptied: the file is written
    ///     behind the editor's back (a test session watches nothing), so the browser's rescan after the
    ///     import is the only thing that can put its row on screen.
    /// </remarks>
    [Fact]
    public void A_finished_import_redraws_the_open_browser() {
        using var session = EditorSession.Start();

        File.WriteAllText(session.Project.Paths.Absolute("Assets/arrived.png"), "pixels");

        session.ExpandAll(session.Assets);
        Assert.DoesNotContain("arrived.png", EditorSession.Labels(session.Assets));

        session.Editor.Content.Import();

        // ⚠ A hang check and not a budget, as `BuildStepContributionTests.Until` says: a working import
        // of an empty project finishes in a few frames and a broken one never does.
        for (var frame = 0; frame < 400 && session.Editor.Content.IsBusy; frame++) {
            session.Frame();
            Thread.Sleep(1);
        }

        Assert.False(session.Editor.Content.IsBusy, "the import never finished.");
        session.Settle();

        session.ExpandAll(session.Assets);
        Assert.Contains("arrived.png", EditorSession.Labels(session.Assets));
    }

    /// <summary>A document that does nothing but count being told a project file changed.</summary>
    sealed class Listener : EditorDocument {
        public Listener(EditorProject project)
            : base(project, AssetId.Empty, "Listener") {
        }

        /// <summary>How many announcements this has been given.</summary>
        public int Heard { get; private set; }

        /// <summary>The path of the last one, which for an import must be null.</summary>
        public string? LastPath { get; private set; } = "never told";

        /// <inheritdoc />
        protected override void OnProjectFileChanged(string? path) {
            base.OnProjectFileChanged(path);

            Heard++;
            LastPath = path;
        }

        /// <inheritdoc />
        protected override void SaveCore() {
        }
    }
}
