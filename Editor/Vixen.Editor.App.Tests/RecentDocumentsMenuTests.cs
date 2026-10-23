// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Frame;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui.Documents;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     File ▸ Recent Documents (#656): <c>Vixen.Ui</c>'s list, keyed on <c>IEditableDocument.Location</c>,
///     reached the way a person reaches it — a menu line that reopens the tab.
/// </summary>
public sealed class RecentDocumentsMenuTests {
    const string Knobs = """
        version: 2
        game: !StandardFrame
          quality: High

        """;

    [Fact]
    public void An_asset_opened_in_a_tab_is_on_the_menu_and_the_line_reopens_it() {
        using var session = EditorSession.Start();

        var (name, tab) = OpenFrame(session);

        Assert.True(session.Shell.Workspace.Close(tab));
        session.Settle();
        Assert.False(session.Shell.Workspace.IsOpen(tab));

        session.Menu("File", "Recent Documents", name);

        Assert.True(session.Shell.Workspace.IsOpen(tab), "the Recent Documents line did not reopen the tab");
    }

    /// <summary>
    ///     The editor's own scene is no tab's document, and is on the list under its file — the one
    ///     Save As moved it to, which is #1493's location followed rather than read once.
    /// </summary>
    [Fact]
    public void The_editors_scene_is_on_the_list_under_the_file_save_as_moved_it_to() {
        using var session = EditorSession.Start();

        var other = Path.Combine(session.Project.Paths.Assets, "Elsewhere.vxscene");

        session.Plugins.Services.Require<SceneFiles>().SaveAs(other);
        session.Settle();

        var recent = session.Plugins.Services.Require<IWorkspace>().Recent;

        Assert.Equal(Path.GetFullPath(other), recent.Locations[0]);
    }

    /// <summary>The list is the user's and outlives the editor: kept in the store, read back on the next start.</summary>
    /// <remarks>
    ///     ⚠ The tab is closed <i>before</i> the restart. Left open, the restored arrangement reopens
    ///     it, showing it notes it again, and the test is green with nothing kept at all — which is how
    ///     its first version passed with the restore sabotaged.
    /// </remarks>
    [Fact]
    public void The_list_survives_a_restart() {
        using var session = EditorSession.Start();

        var (name, tab) = OpenFrame(session);

        Assert.True(session.Shell.Workspace.Close(tab));
        session.Settle();

        session.Restart();
        session.Settle();

        Assert.False(session.Shell.Workspace.IsOpen(tab));

        session.Menu("File", "Recent Documents", name);

        Assert.True(session.Shell.Workspace.IsOpen(tab));
    }

    static (string Name, string Tab) OpenFrame(EditorSession session) {
        var name = "Recent" + StandardFrameDocument.Extension;
        var path = Path.Combine(session.Project.Paths.Assets, name);

        Directory.CreateDirectory(session.Project.Paths.Assets);
        File.WriteAllText(path, Knobs);
        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath("Assets/" + name, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Settle();

        return (name, EditorWorkspace.PanelOf(entry.Guid));
    }
}
