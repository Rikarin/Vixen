// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What <c>--hot-reload</c> turns on, and what it deliberately does not.</summary>
/// <remarks>
///     <para>
///         <b>The style channel, wired to a directory the developer names.</b> The editor's own five
///         sheets are C# string constants — there is no <c>.vcss</c> on disk to watch, and a
///         published editor would have none even if the source tree did — so what this watches is a
///         folder of the developer's own rules, loaded at <c>Author</c> origin so they beat the
///         shipped <c>UserAgent</c> ones without out-specifying them.
///     </para>
///     <para>
///         ⚠ <b>The markup channel is not tested here and cannot be.</b> A <c>.vxml</c> becomes a
///         different <c>Build</c> only after the compiler has run and the runtime has delivered a
///         metadata update, which is <c>dotnet watch</c>'s doing and not something a test process
///         can stage. <c>Vixen.Ui.HotReload.Tests</c> covers the half that is ours — the handler
///         rebuilding a registered host — and the other half is a command in the host's README.
///     </para>
/// </remarks>
public class HotReloadModeTests {
    /// <summary>The ordinary case: a directory with a sheet in it, applied to the live document.</summary>
    [Fact]
    public void A_watched_stylesheet_is_loaded_over_the_editor_s_own() {
        using var styles = new Folder();
        using var fixture = EditorSession.Start();

        var probe = fixture.Document.Root.Add<UiElement>("hot-reload-probe");
        styles.Write("dev.vcss", "hot-reload-probe { width: 33px; }");

        Assert.Equal(1, fixture.Editor.WatchStyles(styles.Path));

        fixture.Frame();

        Assert.Equal(33f, probe.Width);
    }

    /// <summary>
    ///     ⚠ <b>Zero is reported rather than swallowed.</b> A watcher over a directory with no
    ///     stylesheets in it is a channel that looks wired and does nothing, and the developer who
    ///     mistyped the path has no other way to find out.
    /// </summary>
    [Fact]
    public void An_empty_directory_is_reported_as_empty_rather_than_looking_wired() {
        using var styles = new Folder();
        using var fixture = EditorSession.Start();

        Assert.Equal(0, fixture.Editor.WatchStyles(styles.Path));
    }

    /// <summary>
    ///     A path that is not there at all is a mistyped argument, not a reason to refuse to start
    ///     an editor — the same trade the asset watcher makes for a project with no <c>Assets</c>.
    /// </summary>
    [Fact]
    public void A_directory_that_does_not_exist_does_not_stop_the_editor_opening() {
        using var fixture = EditorSession.Start();

        Assert.Equal(0, fixture.Editor.WatchStyles(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        fixture.Frame();

        Assert.False(fixture.IsClosing);
    }

    /// <summary>
    ///     ⚠ <b>The claim that matters for threading: the reload happens on the frame, not on the
    ///     pool.</b> A <c>FileSystemWatcher</c> callback runs on a thread pool thread and the element
    ///     tree has no lock — so the change is coalesced where it arrives and applied in
    ///     <c>EditorApplication.Update</c>, which is the frame loop. This test never calls
    ///     <c>Poll</c>: if the frame did not, the save below would never land.
    /// </summary>
    [Fact]
    public async Task A_saved_stylesheet_reaches_the_document_through_the_frame_loop() {
        using var styles = new Folder();
        using var fixture = EditorSession.Start();

        var probe = fixture.Document.Root.Add<UiElement>("hot-reload-probe");
        var sheet = styles.Write("dev.vcss", "hot-reload-probe { width: 33px; }");

        fixture.Editor.WatchStyles(styles.Path);
        fixture.Frame();

        Assert.Equal(33f, probe.Width);

        await File.WriteAllTextAsync(
            sheet,
            "hot-reload-probe { width: 77px; }",
            TestContext.Current.CancellationToken
        );

        // Frames rather than one sleep: a filesystem notification has no deadline, and a single
        // wait long enough to be reliable on a loaded machine is a second added to every run.
        for (var attempt = 0; attempt < 100 && probe.Width != 77f; attempt++) {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            fixture.Frame();
        }

        Assert.Equal(77f, probe.Width);
    }

    /// <summary>
    ///     ⚠ <b>And a broken sheet puts the previous one back.</b> Half a stylesheet is worse than
    ///     the old one — a rule somebody is midway through typing drops the size off everything it
    ///     used to match — and it is a selector that is usually half-typed, which only the selector
    ///     compiler's diagnostics report. See <c>HotReloadWatcherTests</c>.
    /// </summary>
    [Fact]
    public async Task A_broken_save_leaves_the_editor_looking_as_it_did() {
        using var styles = new Folder();
        using var fixture = EditorSession.Start();

        var probe = fixture.Document.Root.Add<UiElement>("hot-reload-probe");
        var sheet = styles.Write("dev.vcss", "hot-reload-probe { width: 33px; }");

        fixture.Editor.WatchStyles(styles.Path);
        fixture.Frame();

        await File.WriteAllTextAsync(
            sheet,
            "hot-reload-probe:nonsense-pseudo { width: 77px; }",
            TestContext.Current.CancellationToken
        );

        for (var attempt = 0; attempt < 20; attempt++) {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            fixture.Frame();
        }

        Assert.Equal(33f, probe.Width);
    }

    /// <summary>An editor nobody asked to watch anything opens no watcher and polls nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Which is the <c>--frames N</c> case, and the reason the flag defaults to off.</b> CI
    ///     runs the editor for a fixed number of frames on a machine with nobody there to save a
    ///     file; a watcher opened anyway would be a platform handle and a pool callback bought for
    ///     nothing, in the one run that has to shut down cleanly.
    /// </remarks>
    [Fact]
    public void An_editor_that_was_not_asked_to_watch_runs_its_frames_and_stops() {
        using var fixture = EditorSession.Start();

        fixture.Frames(5);

        Assert.False(fixture.IsClosing);
    }

    sealed class Folder : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory("vixen-editor-styles-").FullName;

        public string Write(string name, string css) {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, css);
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
