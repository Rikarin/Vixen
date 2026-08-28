// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.HotReload;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What <c>--hot-reload</c> turns on, and what it deliberately does not.</summary>
/// <remarks>
///     <para>
///         <b>The style channel, wired to a directory the developer names.</b> A folder of the
///         developer's own rules is loaded at <c>Author</c> origin so it beats the shipped
///         <c>UserAgent</c> sheets without out-specifying them.
///     </para>
///     <para>
///         ⚠ <b>Every editor sheet is a real <c>.vcss</c> now, which the tests below are mostly
///         about.</b> They used to be C# string constants — there was nothing on disk to point at —
///         and now the same file is both the source of the shipped sheet and the one a developer
///         edits. So the directory can be the editor's own <c>Theming/</c>, and the question stops
///         being "does a save arrive" and becomes "does it <i>replace</i>": an edit that adds a
///         second copy on top makes a deleted rule immortal.
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

    // ---------------------------------------- The editor's own sheets, replaced

    /// <summary>
    ///     ⚠ <b>The whole point, asserted on a resolved value after an edit rather than on an
    ///     event.</b> <c>EditorTheme.vcss</c> is embedded from the same file the developer edits, so
    ///     a watcher that loads it again leaves two copies in the cascade — and every value the new
    ///     text states still wins, which is why a test that changed a number would pass against both
    ///     behaviours. What only a replacement can do is make a <b>deleted</b> rule stop applying,
    ///     and the status bar's height is a rule this file is the only source of.
    /// </summary>
    [Fact]
    public async Task A_rule_deleted_from_the_editor_s_own_theme_stops_applying() {
        using var styles = new Folder();
        using var fixture = EditorSession.Start();

        var sheets = fixture.Document.Styles.SheetCount;
        var file = styles.Write("EditorTheme.vcss", EditorTheme.Css);

        Assert.Equal(1, fixture.Editor.WatchStyles(styles.Path));

        // Adopted rather than added: the document holds the sheet it already had, at its own origin.
        Assert.Equal(sheets, fixture.Document.Styles.SheetCount);

        fixture.Frame();
        Assert.Equal(24f, fixture.Shell.StatusBar.Height);

        // The edit, at its bluntest: a sheet with none of the old rules left in it.
        await File.WriteAllTextAsync(
            file,
            "hot-reload-probe { width: 33px; }",
            TestContext.Current.CancellationToken
        );

        // Polled rather than slept once: a filesystem notification has no deadline.
        for (var attempt = 0; attempt < 100 && fixture.Shell.StatusBar.Height == 24f; attempt++) {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            fixture.Frame();
        }

        Assert.NotEqual(24f, fixture.Shell.StatusBar.Height);
        Assert.Equal(sheets, fixture.Document.Styles.SheetCount);
    }

    /// <summary>
    ///     ⚠ <b>One <c>.vcss</c> in that folder is not a stylesheet, and handing it to the cascade
    ///     can only produce a diagnostic.</b> <c>vixen.ui.vcss</c> is the <c>@theme</c> token source
    ///     the utility generator reads at build time — the name is the build's own glob. Loading it
    ///     used to be harmless because nothing read the diagnostics; they drain to the log now, so it
    ///     was a warning on start-up and on every save of every other sheet beside it.
    /// </summary>
    [Fact]
    public void The_theme_token_source_beside_a_sheet_is_not_watched() {
        using var styles = new Folder();
        using var fixture = EditorSession.Start();

        var sheets = fixture.Document.Styles.SheetCount;

        styles.Write("vixen.ui.vcss", "@theme {\n  --spacing: 4px;\n}\n");
        styles.Write("dev.vcss", "hot-reload-probe { width: 33px; }");

        // One of the two, and it is the one that can do something.
        Assert.Equal(1, fixture.Editor.WatchStyles(styles.Path));
        Assert.Equal(sheets + 1, fixture.Document.Styles.SheetCount);
    }

    /// <summary>
    ///     ⚠ <b>And the generated sheet under <c>obj/</c> is not watched either, which is the same
    ///     rule one level further out and was the one place in the tree that read a file nothing is
    ///     supposed to read.</b> The utility build step writes
    ///     <c>obj/&lt;config&gt;/&lt;tfm&gt;/Vixen/&lt;Assembly&gt;.g.vcss</c>; point
    ///     <c>--style-directory</c> at a source tree that has been built and it used to be picked up,
    ///     because the only filter was on the file <i>name</i> and this file's name is the assembly's.
    ///     <c>DesktopHotReload</c> has always excluded <c>obj</c> and <c>bin</c>, and its remarks say
    ///     why binding to a build artefact is a trap rather than a bonus: it is rewritten on every
    ///     build, so every rebuild fires a reload of a file nobody edited, and with one copy per
    ///     configuration the <c>obj/Release</c> one binds a sheet the running process is not using.
    ///     <para>
    ///         Two configurations are written here on purpose. One would pass against a fix that
    ///         happened to skip the first match; the failure this pins is a sheet from the
    ///         configuration you are not running.
    ///     </para>
    /// </summary>
    [Fact]
    public void The_generated_sheet_under_obj_is_not_watched() {
        using var styles = new Folder();
        using var fixture = EditorSession.Start();

        var sheets = fixture.Document.Styles.SheetCount;

        styles.Write(Path.Combine("obj", "Debug", "net10.0", "Vixen", "Probe.g.vcss"), "hot-reload-probe { width: 11px; }");
        styles.Write(Path.Combine("obj", "Release", "net10.0", "Vixen", "Probe.g.vcss"), "hot-reload-probe { width: 22px; }");
        styles.Write(Path.Combine("bin", "Debug", "net10.0", "Copied.vcss"), "hot-reload-probe { width: 44px; }");
        styles.Write("dev.vcss", "hot-reload-probe { width: 33px; }");

        // Only the hand-written one, out of four files that all end in .vcss.
        Assert.Equal(1, fixture.Editor.WatchStyles(styles.Path));
        Assert.Equal(sheets + 1, fixture.Document.Styles.SheetCount);

        var probe = fixture.Document.Root.Add<UiElement>("hot-reload-probe");
        fixture.Frame();

        Assert.Equal(33f, probe.Width);
    }

    /// <summary>
    ///     ⚠ <b>The shell's one markup panel is mounted through the reload host, which is an
    ///     ordering fix rather than a markup one.</b> Only a component the host tracks is rebuilt
    ///     when the runtime replaces its <c>Build</c>, and <c>EditorShell</c> builds the task centre
    ///     inside the constructor that makes the document the host is built over — so it could not
    ///     have been tracked at the time, and nothing came back for it afterwards. The editor's only
    ///     <c>.vxml</c> panel was the only panel a <c>dotnet watch</c> could not reach.
    /// </summary>
    [Fact]
    public void The_task_centre_is_mounted_through_the_reload_host() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Plugins.Services.TryGet<HotReloadHost>(out var host));
        Assert.Contains(
            host.Components,
            component => component.GetType().Name == "TaskCenter"
        );
    }

    /// <summary>
    ///     And exactly one of them, because a remount that left the first build in the popover would
    ///     be two task centres over one manager rather than a reloadable one.
    /// </summary>
    [Fact]
    public void And_the_popover_holds_one_of_them() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Plugins.Services.TryGet<HotReloadHost>(out var host));

        var centres = host.Components.Where(component => component.GetType().Name == "TaskCenter").ToList();
        var roots = Assert.Single(centres).Root.Parent;

        Assert.NotNull(roots);
        Assert.Single(roots.Children);
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

        /// <summary>Writes a sheet, where <paramref name="name" /> may name a subdirectory.</summary>
        /// <remarks>
        ///     The directory is created, because the build artefact the exclusion test needs lives
        ///     several levels down an <c>obj/</c> the temporary folder has no reason to have.
        /// </remarks>
        public string Write(string name, string css) {
            var file = System.IO.Path.Combine(Path, name);

            if (System.IO.Path.GetDirectoryName(file) is { Length: > 0 } directory) {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(file, css);
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
