// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
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
