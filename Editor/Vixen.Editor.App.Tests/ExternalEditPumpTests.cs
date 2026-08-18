// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Editor.AssetEditors.Frame;
using Vixen.Editor.Testing;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A file changed underneath a running editor, and the editor's own save, through the real loop.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The whole path or nothing.</b> The unit suites in <c>Vixen.Editor.Core.Tests</c> drive
///         <c>ExternalEdits</c> with a stub watcher and hand-written changes; this drives a real
///         <c>FileWatcher</c> over a real project through <c>EditorApplication.FollowDisk</c>, on the
///         frame, which is the only place the ordering — drain, rescan, route — actually exists. A
///         seam that is built and never fed is this tree's commonest defect, and the two suites
///         together are what say it is fed.
///     </para>
///     <para>
///         ⚠ <b>Waits on the assertion rather than on a clock.</b> The watcher debounces for a quarter
///         of a second and the platform reports on its own schedule — FSEvents batches with a latency
///         of its own — so a fixed sleep sized to the debounce is the shape of test that passes on a
///         developer's machine and fails on a loaded agent. These pump frames until the thing being
///         asserted is true, or until a budget generous enough to be a real failure runs out.
///     </para>
/// </remarks>
public sealed class ExternalEditPumpTests : IDisposable {
    /// <summary>Long enough that running out of it is a defect rather than a slow machine.</summary>
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>How long a claim that nothing happens is given to be wrong.</summary>
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    readonly List<IDisposable> owned = [];

    public void Dispose() {
        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }
    }

    const string Knobs = """
        version: 2
        game: !StandardFrame
          quality: High
          shadows: Cascades
          look: !Look
            settings:
              ev100: 13

        """;

    const string Changed = """
        version: 2
        game: !StandardFrame
          quality: Low
          shadows: Off
          look: !Look
            settings:
              ev100: 7

        """;

    /// <summary>An editor with a frame document open, and the path of its file.</summary>
    (EditorSession Session, StandardFrameDocument Document, string Path) Editing() {
        var session = EditorSession.Start();

        owned.Add(session);

        var name = "Frame" + StandardFrameDocument.Extension;
        var path = Path.Combine(session.Project.Paths.Assets, name);

        Directory.CreateDirectory(session.Project.Paths.Assets);
        File.WriteAllText(path, Knobs);

        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath("Assets/" + name, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Settle();

        var document = session.Project.Documents.OfType<StandardFrameDocument>().Single();

        Assert.Equal(FrameQualityChoice.High, document.Settings.Quality);

        return (session, document, path);
    }

    /// <summary>
    ///     ⚠ <b>The defect this was filed for.</b> Before it, a <c>.vxcompositor</c> saved by a text
    ///     editor beside the running Vixen reached the asset database, the project tree and the build
    ///     panel — and did not reach the panel that had it open.
    /// </summary>
    [Fact]
    public void A_file_changed_underneath_the_editor_reaches_the_document_open_on_it() {
        var (session, document, path) = Editing();

        File.WriteAllText(path, Changed);

        Assert.True(
            Pump(session, () => document.Settings.Quality == FrameQualityChoice.Low, Budget),
            "the change on disk never reached the open document"
        );

        Assert.Equal(ShadowMode.Off, document.Settings.Shadows);
        Assert.Equal(7f, document.Look.Ev100);
        Assert.False(document.IsDirty.Value);
        Assert.False(document.IsStale.Value);
    }

    /// <summary>
    ///     The policy, on the real loop. Unsaved work is the only copy of itself, so it is kept and
    ///     the document says the file has moved on.
    /// </summary>
    [Fact]
    public void A_document_with_unsaved_edits_keeps_them_and_says_the_file_moved_on() {
        var (session, document, path) = Editing();

        document.Settings.Antialiasing = AntialiasingMode.Off;

        Assert.True(document.Apply());

        // What an inspector's write leaves behind, which is what makes the document dirty.
        document.Stack.Execute(new Editor.Core.DelegateCommand("Turn a knob", _ => { }, _ => { }));

        Assert.True(document.IsDirty.Value);

        File.WriteAllText(path, Changed);

        Assert.True(
            Pump(session, () => document.IsStale.Value, Budget),
            "the editor never noticed the file had changed underneath an unsaved document"
        );

        // Kept, not reloaded: the knobs are still the ones on screen.
        Assert.Equal(FrameQualityChoice.High, document.Settings.Quality);
        Assert.Equal(AntialiasingMode.Off, document.Settings.Antialiasing);
        Assert.True(document.IsDirty.Value);

        // And it is said out loud rather than only flagged.
        Assert.Contains(
            session.Shell.Notifications.History,
            entry => entry.Message.Contains("changed on disk", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ <b>The hard half, and it was the built-but-unwired one.</b> <c>IFileWatcher.Suppress</c>
    ///     has existed since the coalescer was written and had no callers, so without this the
    ///     editor's own Ctrl+S arrives back as somebody else's edit — and a document is offered a
    ///     reload over the work it has just saved.
    /// </summary>
    [Fact]
    public void The_editors_own_save_does_not_come_back_as_somebody_elses_edit() {
        var (session, document, _) = Editing();

        document.Settings.Quality = FrameQualityChoice.Low;

        Assert.True(document.Apply());

        var announced = 0;

        document.Changed += _ => announced++;
        document.Save();

        // Given every chance to be wrong: frames for as long as a reload could possibly take.
        Assert.False(
            Pump(session, () => document.IsStale.Value || announced > 0, Quiet),
            "the editor's own save came back through the watcher as an external edit"
        );

        Assert.Equal(FrameQualityChoice.Low, document.Settings.Quality);
        Assert.False(document.IsDirty.Value);
    }

    /// <summary>Runs the editor's frame until something is true, or until the budget is gone.</summary>
    static bool Pump(EditorSession session, Func<bool> until, TimeSpan budget) {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < budget) {
            session.Frames(1);

            if (until()) {
                return true;
            }

            // ⚠ Real time, because the thing being waited on is a debounce measured in it. The frame
            // is what applies the change; the sleep is what lets the watcher have one to apply.
            Thread.Sleep(25);
        }

        return until();
    }
}
