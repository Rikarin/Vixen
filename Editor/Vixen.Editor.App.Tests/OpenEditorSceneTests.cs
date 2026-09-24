// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Opening, from the project, the scene file the editor already has open.</summary>
/// <remarks>
///     ⚠ <b>#1395.</b> The editor's own scene is a <c>SceneDocument</c> with <c>AssetId.Empty</c> and
///     the file held by its writer, and <c>AssetEditorRegistry.TryOpen</c>'s guard against two
///     documents over one file is keyed on the id. So opening <c>Main.vxscene</c> from the project
///     built a second document over it — its own world, its own undo history, its own writer on the
///     same path — and its Compiled tab compiled a copy the Scene view's edits never reached.
/// </remarks>
public sealed class OpenEditorSceneTests {
    [Fact]
    public void Opening_the_editors_own_scene_file_brings_that_scene_forward_rather_than_a_second_document() {
        using var session = EditorSession.Start();

        var entry = MainScene(session);
        var scenes = session.Project.Documents.OfType<SceneDocument>().Count();

        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        Assert.Equal(scenes, session.Project.Documents.OfType<SceneDocument>().Count());
        Assert.Same(session.Scene, session.Project.ActiveDocument.Peek());

        // And a second double-click is the same answer, not a third document.
        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        Assert.Equal(scenes, session.Project.Documents.OfType<SceneDocument>().Count());
    }

    /// <summary>
    ///     The document tab's Compiled pane compiles what the Scene view edits — entities that exist
    ///     only in memory, which a second document loaded from the file cannot have.
    /// </summary>
    [Fact]
    public void The_opened_scenes_compiled_pane_sees_edits_that_were_never_saved() {
        using var session = EditorSession.Start();

        var entry = MainScene(session);

        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        var view = session.Control<CompiledSceneView>("asset." + entry.Guid);

        Assert.True(view.Refresh(), "the scene did not compile");

        var before = view.Content!.Count;

        for (var index = 0; index < 3; index++) {
            session.Scene.Create($"Unsaved {index}", LocalTransform.Identity);
        }

        Assert.True(view.Refresh(), "the scene did not compile");
        Assert.Equal(before + 3, view.Content!.Count);
    }

    static Vixen.Editor.Core.AssetEntry MainScene(EditorSession session) {
        var writer = Assert.IsType<SceneFileWriter>(session.Scene.Writer);
        var relative = session.Project.Paths.Relative(writer.Path);

        session.Project.Assets.Scan();

        Assert.True(
            session.Project.Assets.TryGetByPath(relative, out var entry),
            $"'{relative}', the file the editor's scene writes, is not an asset of the project."
        );

        return entry;
    }
}
