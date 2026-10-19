// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Vixen.Core;
using Vixen.Core.IO.Watch;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Behaviors;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § P2d: the project's rescan and reopen request, with no window and through the plugin door.</summary>
/// <remarks>
///     ⚠ <b>The browser used to scan and save the database itself</b>, and the disk watcher did the same
///     three calls again when the browser was closed. The reach test below is the one that can tell a
///     panel doing its own scan from a panel asking the service: both put the file in the tree.
/// </remarks>
public sealed class ProjectSessionTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-project-session", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose() {
        try {
            Directory.Delete(root, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }
    }

    /// <summary>A rescan indexes, saves the index and rebuilds the references — with no window.</summary>
    [Fact]
    public void Rescan_indexes_saves_and_rebuilds_references_without_a_window() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        var project = new EditorProject(new ProjectPaths(root));
        var session = new ProjectSession(project, (_, _, _) => { }, () => { }, () => false, new ProjectHistory(Path.Combine(root, "Data")));
        List<ScanReport> heard = [];

        session.Rescanned += heard.Add;

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Leaf.yaml"), "name: leaf\n");
        session.Rescan();

        var leaf = project.Assets.Entries.Single(entry => entry.Name == "Leaf.yaml").Guid;

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Root.yaml"), "uses: " + new AssetReference(leaf) + "\n");
        File.Delete(project.Paths.GuidIndexFile);

        var report = session.Rescan();

        Assert.Equal(2, report.Assets);
        Assert.True(File.Exists(project.Paths.GuidIndexFile), "the index was not saved after the scan");
        Assert.Single(project.References.ReferrersOf(leaf));
        Assert.Equal(2, heard.Count);
        Assert.Same(report, heard[^1]);
    }

    /// <summary>New Project writes the game template and asks to reopen there — with no window.</summary>
    /// <remarks>
    ///     ⚠ <b>The request is the reopen loop's input</b>, so a scaffold that wrote the files and did not
    ///     ask would leave the user in the old project with a new one on disk and nothing on screen.
    /// </remarks>
    [Fact]
    public void Create_project_scaffolds_the_template_and_requests_it_without_a_window() {
        var (session, reported, closes) = Bare();
        var next = Path.Combine(root, "My Game");

        session.CreateProject(next);

        Assert.True(Directory.Exists(Path.Combine(next, "Assets")));
        Assert.True(ProjectMarker.TryRead(next, out var marker), "the scaffold wrote no .vxproj");
        Assert.False(ProjectMarker.IsFromTheFuture(marker));
        Assert.Single(Directory.GetFiles(next, "*.csproj"));
        Assert.Equal(Path.GetFullPath(next), session.PendingProject);
        Assert.Equal(1, closes());
        Assert.Empty(reported);

        // A second New Project over the same folder opens what is there rather than overwriting it.
        var (again, said, _) = Bare();

        again.CreateProject(next);

        Assert.Equal("Opened what was already there", Assert.Single(said));
        Assert.Equal(Path.GetFullPath(next), again.PendingProject);
    }

    /// <summary>Opening puts the project at the top of the recent list and says so when a newer engine made it.</summary>
    [Fact]
    public void Opened_records_the_project_and_warns_about_a_newer_engine_without_a_window() {
        var (session, reported, _) = Bare();

        File.WriteAllText(Path.Combine(root, "Project", "Game" + ProjectMarker.Extension), ProjectMarker.Write("999.0.0"));

        session.Opened(new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc));

        var first = Assert.Single(session.Recent);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "Project")), first.Path);
        Assert.Equal("This project was made with a newer Vixen", Assert.Single(reported));
    }

    /// <summary>The recent list a plugin reads is the one the editor writes when it opens a project.</summary>
    [Fact]
    public void The_recent_list_a_plugin_reads_is_the_one_open_recent_shows() {
        using var editor = EditorSession.Start();

        var recent = editor.Plugins.Services.Require<IProjectSession>().Recent;

        Assert.Equal(Path.GetFullPath(editor.ProjectRoot), recent[0].Path);
        Assert.Equal(editor.RecentProjects.Select(entry => entry.Path), recent.Select(entry => entry.Path));
    }

    /// <summary>The browser's rescan is the published service's, which is what the Refresh command reaches.</summary>
    [Fact]
    public void The_browser_rescans_through_the_service_a_plugin_requires() {
        using var editor = EditorSession.Start();
        editor.Open("project");

        var service = editor.Plugins.Services.Require<IProjectSession>();
        var heard = 0;

        service.Rescanned += _ => heard++;

        File.WriteAllText(Path.Combine(editor.Project.Paths.Assets, "behind-its-back.txt"), "hello");

        editor.Run("assets.refresh");

        Assert.Equal(1, heard);
        Assert.Contains(editor.Project.Assets.Entries, entry => entry.Name == "behind-its-back.txt");
        Assert.Same(editor.Project, service.Project);
    }

    /// <summary>A plugin asking for another project is what the host reads when the loop returns.</summary>
    [Fact]
    public void A_project_a_plugin_requests_is_the_one_the_host_reopens() {
        using var editor = EditorSession.Start();

        var next = Path.Combine(root, "Next");

        editor.Plugins.Services.Require<IProjectSession>().RequestProject(next);
        editor.Settle();

        Assert.True(editor.IsClosing);
        Assert.Equal(Path.GetFullPath(next), editor.PendingProject);
    }

    /// <summary>
    ///     A file written behind the editor's back reaches the published session once, after the
    ///     rescan it caused, and the panels follow without a second scan.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The watcher was the application's, in its constructor and frame loop (#1293).</b> It
    ///         is the session's now, so a module that wants to hear the disk asks
    ///         <c>Require&lt;IProjectSession&gt;()</c> rather than not being able to at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is a count; the waits are a hang check and a quiet period.</b> The
    ///         thing waited on is the watcher's debounce, which is measured in real time; what is
    ///         asserted is how many times the session spoke and how many scans it took. The quiet
    ///         period is <c>ExternalEditPumpTests.Quiet</c>'s, inside the coalescer's suppression
    ///         window, so "once" is not asserted past what the design promises.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_file_changed_on_disk_reaches_the_published_session_once() {
        using var editor = EditorSession.Start(new() { WatchAssets = true });
        editor.Open("project");

        var service = editor.Plugins.Services.Require<IProjectSession>();

        // The instrument first: a session that came up without a watcher would make the wait below
        // time out for a reason that has nothing to do with the session.
        Assert.True(service.IsWatching, "the session is not watching, so nothing below can be heard");
        Assert.True(editor.Editor.IsWatchingAssets);

        // Anything the harness wrote while starting is drained before the counting begins.
        Pump(editor, () => false, TimeSpan.FromSeconds(1));

        const string Written = "/from-outside.txt";
        var bursts = 0;
        var heard = 0;
        var scans = 0;
        var indexedWhenHeard = false;

        service.Rescanned += _ => scans++;

        service.ChangedOnDisk += (changes, _) => {
            bursts++;

            if (changes.Any(change => change.Path.ToString() == Written)) {
                heard++;
                indexedWhenHeard = editor.Project.Assets.Entries.Any(entry => entry.Name == "from-outside.txt");
            }
        };

        File.WriteAllText(Path.Combine(editor.Project.Paths.Assets, "from-outside.txt"), "hello");

        Assert.True(Pump(editor, () => heard > 0, TimeSpan.FromSeconds(20)), "the change on disk never reached the session");

        // A hang check would stop here; "once" needs the quiet period after it.
        Pump(editor, () => false, TimeSpan.FromSeconds(1.5));

        Assert.Equal(1, heard);
        Assert.True(indexedWhenHeard, "the session spoke before its rescan had indexed the file");

        // ⚠ One burst and one rescan — the application's panels follow the session's scan rather than
        // making one of their own. It was two (#1488): the scan writes the new file's `.meta` into
        // `Assets/`, and the watcher heard the database's own write as somebody else's. The session
        // now suppresses every sidecar its scan reports writing.
        Assert.Equal(1, bursts);
        Assert.Equal(1, scans);
    }

    /// <summary>The project's watcher, its routing to open documents and its code have one owner, and it is not the application.</summary>
    /// <remarks>
    ///     ⚠ <b>A move that left a copy behind is the failure this rules out.</b> The application held the
    ///     watcher and the <c>ProjectAssemblies</c> in fields of its own; a second watcher would drain
    ///     the same directory into a second rescan, and a second loader would hold a second context.
    /// </remarks>
    [Fact]
    public void The_watcher_and_the_projects_code_are_the_sessions_alone() {
        var application = Held(typeof(EditorApplication));

        Assert.DoesNotContain(application, type => typeof(IFileWatcher).IsAssignableFrom(type));
        Assert.DoesNotContain(typeof(ProjectAssemblies), application);

        // ⚠ And what routes the watcher's changes to the open documents, which moved with it — a copy
        // left in the application would apply every change to a document twice.
        Assert.DoesNotContain(typeof(ExternalEdits), application);

        var session = Held(typeof(ProjectSession));

        Assert.Contains(typeof(IFileWatcher), session);
        Assert.Contains(typeof(ProjectAssemblies), session);
        Assert.Contains(typeof(ExternalEdits), session);

        // ⚠ And a module reads the code through an interface, not the App's loader: the session is the
        // one door that takes the scene's behaviours off around a reload, so a module handed the
        // loader could unload an assembly a scene still holds instances of.
        Assert.Equal(typeof(IProjectCode), typeof(IProjectSession).GetProperty(nameof(IProjectSession.Code))!.PropertyType);
        Assert.DoesNotContain(typeof(IProjectCode).GetMethods(), method => !method.IsSpecialName);

        static List<Type> Held(Type type) => [
            .. type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(field => field.FieldType)
        ];
    }

    /// <summary>A save that could not write is reported through the session — with no window (#656, moved by #1293).</summary>
    /// <remarks>
    ///     ⚠ <b>With nobody subscribed to <c>EditorProject.SaveFailed</c> the failure is thrown on</b>, so this
    ///     is red both when the subscription is lost and when it is made somewhere a windowless session
    ///     never reaches — the application's constructor, where it was.
    /// </remarks>
    [Fact]
    public void A_failed_save_is_reported_through_the_session_without_a_window() {
        var (session, reported, _) = Bare();
        using var world = new World("Scene");

        // A file where the scene's folder would have to be, so the write fails as a disk would.
        var blocked = Path.Combine(session.Project.Paths.Assets, "not-a-folder");
        File.WriteAllText(blocked, string.Empty);

        var scene = new SceneDocument(session.Project, world, AssetId.Empty, "Level") {
            Writer = new SceneFileWriter(Path.Combine(blocked, "Level.vxscene"))
        };

        Assert.False(((IEditableDocument)scene).Save());
        Assert.Equal("Could not save Level", Assert.Single(reported));
    }

    /// <summary>A project with no code builds as nothing — with no window — and announces no reload.</summary>
    [Fact]
    public void Building_a_project_with_no_code_succeeds_and_loads_nothing_without_a_window() {
        var (session, reported, _) = Bare();
        var built = 0;

        session.CodeBuilt += () => built++;

        Assert.True(session.BuildCode());
        Assert.Null(session.Code.Loaded);
        Assert.Null(session.Code.Project);
        Assert.Equal(0, built);
        Assert.Empty(reported);
    }

    /// <summary>A project whose code does not build says so through the published session, and is still open.</summary>
    /// <remarks>
    ///     ⚠ <b>A failed build is a notification and not a refusal</b> — the remark that moved with the
    ///     method. Reached through <c>Require&lt;IProjectSession&gt;()</c>, which is what a module
    ///     wanting to rebuild the game's code after writing a script would call.
    /// </remarks>
    [Fact]
    public void A_build_that_fails_is_reported_through_the_published_session() {
        using var editor = EditorSession.Start();

        var service = editor.Plugins.Services.Require<IProjectSession>();
        var built = 0;
        List<string> said = [];

        service.CodeBuilt += () => built++;
        editor.Editor.Shell.Notifications.Shown += notification => said.Add(notification.Message);

        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Broken.csproj"), "<Project><this is not xml");

        Assert.False(service.BuildCode());
        Assert.Equal(0, built);
        Assert.Contains("The project's code did not build", said);
        Assert.Same(editor.Project, service.Project);
    }

    /// <summary>A code build that loaded nothing leaves the scene's authored behaviours where they were (#1490).</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every editor launch runs this build on its first frame</b>, and the scratch project and
    ///         every content-only project have no <c>.csproj</c>. The build used to take every behaviour off
    ///         the scene and put them back only when an assembly loaded — so a behaviour from an assembly the
    ///         editor already references was gone a frame after launch, and the next save wrote the scene
    ///         without it.
    ///     </para>
    ///     <para>
    ///         Two cases, one assertion: no code at all, and code that does not build. The values are checked
    ///         as well as the count, because a restore that attached a fresh instance would pass a count.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_build_that_loads_nothing_keeps_the_scenes_behaviours(bool broken) {
        SceneBehaviorRegistry.Register<PatrolBehavior>();

        using var editor = EditorSession.Start();

        if (broken) {
            File.WriteAllText(Path.Combine(editor.ProjectRoot, "Broken.csproj"), "<Project><this is not xml");
        }

        var scene = editor.Scene;
        var entity = scene.Entities.First();
        var authored = scene.Behaviors.Add<PatrolBehavior>(entity);

        authored.Speed = 41.5f;

        Assert.Single(scene.Behaviors.AllOn(entity).ToArray());

        Assert.Equal(!broken, editor.Plugins.Services.Require<IProjectSession>().BuildCode());

        var kept = Assert.IsType<PatrolBehavior>(Assert.Single(scene.Behaviors.AllOn(entity).ToArray()));
        Assert.Equal(41.5f, kept.Speed);

        // ⚠ And the file a save writes still names it — the half of the defect a user meets.
        Assert.Contains("!PatrolBehavior", SceneSerializer.ToYaml(scene), StringComparison.Ordinal);
    }

    /// <summary>Frames until a condition holds or the budget runs out, sleeping between so the watcher's debounce can pass.</summary>
    static bool Pump(EditorSession editor, Func<bool> until, TimeSpan budget) {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < budget) {
            editor.Frames(1);

            if (until()) {
                return true;
            }

            // ⚠ Real time, because the thing being waited on is a debounce measured in it.
            Thread.Sleep(25);
        }

        return until();
    }

    (ProjectSession Session, List<string> Reported, Func<int> Closes) Bare() {
        Directory.CreateDirectory(Path.Combine(root, "Project", "Assets"));

        var project = new EditorProject(new ProjectPaths(Path.Combine(root, "Project")));
        List<string> reported = [];
        var closes = 0;

        var session = new ProjectSession(
            project,
            (message, _, _) => reported.Add(message),
            () => closes++,
            () => true,
            new ProjectHistory(Path.Combine(root, "Data"))
        );

        return (session, reported, () => closes);
    }
}
