// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The file behind the open scenes as a service (#1296), and the File menu's scene lines as <c>SceneModule</c>'s.</summary>
public sealed class SceneFilesTests {
    /// <summary>
    ///     ⚠ <b>Save names the file the scene writes, after Save As and a switch away and back.</b> The
    ///     application kept a <c>scenePath</c> beside the writer, and switching the active scene set it to
    ///     the path the scene was <i>opened</i> from — so the notice named <c>Main.vxscene</c> for a save
    ///     that wrote <c>Moved.vxscene</c>.
    /// </summary>
    [Fact]
    public void Save_names_the_file_the_scene_writes_after_save_as_and_a_switch_away_and_back() {
        using var session = EditorSession.Start();

        var files = session.Plugins.Services.Require<SceneFiles>();
        var editing = session.Plugins.Services.Require<ISceneEditing>();
        var main = session.Scene;

        var moved = Path.Combine(session.Project.Paths.Assets, "Moved.vxscene");
        Assert.True(files.SaveAs(moved));

        var second = Path.Combine(session.Project.Paths.Assets, "Second.vxscene");
        File.Copy(moved, second);

        var opened = editing.OpenAdditively(second);
        Assert.NotNull(opened);
        Assert.True(editing.Activate(opened));
        Assert.True(editing.Activate(main));

        Assert.True(session.Shell.Commands.Execute("scene.create-entity"));
        Assert.True(main.HasUnsavedChanges.Value);

        Assert.True(session.Shell.Commands.Execute("file.save"));

        Assert.Equal("Moved.vxscene", session.Shell.Notifications.History[0].Message);
        Assert.False(main.HasUnsavedChanges.Value);
    }

    /// <summary>New Scene, through the module, leaves the active scene one clean root with no history.</summary>
    [Fact]
    public void New_scene_through_the_module_leaves_one_clean_root() {
        using var session = EditorSession.Start();

        var scene = session.Scene;

        Assert.True(scene.Entities.Count() > 1);
        Assert.False(scene.HasUnsavedChanges.Value);

        // Clean, so nothing is asked.
        Assert.True(session.Shell.Commands.Execute("file.new-scene"));

        var root = Assert.Single(scene.Roots);

        Assert.Equal("Scene Root", scene.NameOf(root));
        Assert.Single(scene.Entities);
        Assert.False(scene.HasUnsavedChanges.Value);
        Assert.False(scene.Stack.CanUndo.Value);
    }
}
