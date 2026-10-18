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
        var session = new ProjectSession(project, (_, _, _) => { }, () => { }, () => false);
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
}
