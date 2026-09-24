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

    /// <summary>
    ///     ⚠ <b>#1416: a document tab is bound to the file it was opened on, so the editor's scene
    ///     leaving that file closes it.</b> The tab is registered once under the asset's GUID with the
    ///     title that file had; its builder decided what to show again on every rebuild by matching
    ///     the file the scene writes <i>now</i>. After Save As a tab called <c>Main</c> either kept
    ///     showing a scene that writes <c>Other</c> or, rebuilt, lost it — which one depended on
    ///     when the panel was last built. Closing it is the one answer that does not.
    /// </summary>
    [Fact]
    public void Save_as_closes_the_tab_named_after_the_file_the_scene_left() {
        using var session = EditorSession.Start();

        var main = MainScene(session);
        var tab = "asset." + main.Guid;

        session.Editor.OpenAsset(main.Guid);
        session.Frames(2);
        Assert.True(session.Shell.Workspace.IsOpen(tab));

        var other = Path.Combine(session.Project.Paths.Assets, "Other.vxscene");
        session.Editor.SaveSceneAs(other);
        session.Frames(2);

        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(Writer(session).Path));
        Assert.False(session.Shell.Workspace.IsOpen(tab), $"the tab named after {main.Path} still shows a scene that writes Other.vxscene");

        // The file the scene left is nobody's now, so opening it is a document of its own…
        session.Editor.OpenAsset(main.Guid);
        session.Frames(2);

        var reopened = Assert.IsType<SceneDocument>(session.Project.ActiveDocument.Peek());
        Assert.NotSame(session.Scene, reopened);
        Assert.Equal(main.Guid, reopened.Asset);

        // …and the file it moved to is the editor's scene, brought forward rather than doubled.
        Assert.True(session.Project.Assets.TryGetByPath(session.Project.Paths.Relative(other), out var moved));
        session.Editor.OpenAsset(moved.Guid);
        session.Frames(2);

        Assert.Same(session.Scene, session.Project.ActiveDocument.Peek());
    }

    /// <summary>Open Scene moves the scene's writer too, and leaves the same tab behind.</summary>
    [Fact]
    public void Open_scene_closes_the_tab_named_after_the_file_the_scene_left() {
        using var session = EditorSession.Start();

        var main = MainScene(session);
        var tab = "asset." + main.Guid;
        var other = CopyOf(session, main, "Other.vxscene");

        session.Editor.OpenAsset(main.Guid);
        session.Frames(2);
        Assert.True(session.Shell.Workspace.IsOpen(tab));

        session.Editor.LoadScene(other);
        session.Frames(2);

        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(Writer(session).Path));
        Assert.False(session.Shell.Workspace.IsOpen(tab));
    }

    /// <summary>
    ///     ⚠ <b>The question #1416 left open: Save As onto a file another tab is editing made two
    ///     documents over one file again</b>, the defect #1395 removed — two undo histories, two
    ///     writers, whichever saved last winning. It is refused, and the scene keeps writing where it
    ///     did.
    /// </summary>
    [Fact]
    public void Save_as_onto_a_file_another_tab_is_editing_is_refused() {
        using var session = EditorSession.Start();

        var main = MainScene(session);
        var before = Writer(session).Path;
        var other = CopyOf(session, main, "Other.vxscene");
        Assert.True(session.Project.Assets.TryGetByPath(session.Project.Paths.Relative(other), out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        var theirs = Assert.IsType<SceneDocument>(session.Project.ActiveDocument.Peek());
        Assert.NotSame(session.Scene, theirs);

        session.Editor.SaveSceneAs(other);
        session.Frames(2);

        Assert.Equal(before, Writer(session).Path);
        Assert.Contains(
            session.Shell.Notifications.History,
            message => (message.Detail ?? string.Empty).Contains("Other.vxscene", StringComparison.Ordinal)
        );

        // And Open Scene onto it is the same second document by another route.
        session.Editor.LoadScene(other);
        session.Frames(2);

        Assert.Equal(before, Writer(session).Path);
        Assert.True(theirs.IsOpen);
    }

    /// <summary>
    ///     Opening a scene additively matched the scene's <i>recorded</i> path, which Save As leaves
    ///     behind — so after the main scene moved, adding its old file activated the main scene, and
    ///     adding its new one loaded a second document over the file it writes.
    /// </summary>
    [Fact]
    public void Opening_additively_after_save_as_matches_the_file_the_scene_writes_now() {
        using var session = EditorSession.Start();

        var main = MainScene(session);
        var left = Writer(session).Path;
        var other = Path.Combine(session.Project.Paths.Assets, "Other.vxscene");

        session.Editor.SaveSceneAs(other);
        session.Frames(2);

        Assert.Same(session.Scene, session.Editor.OpenSceneAdditively(other));

        var added = session.Editor.OpenSceneAdditively(left);
        Assert.NotNull(added);
        Assert.NotSame(session.Scene, added);
        Assert.Equal(main.Path, session.Project.Paths.Relative(Assert.IsType<SceneFileWriter>(added!.Writer).Path));
    }

    static SceneFileWriter Writer(EditorSession session) => Assert.IsType<SceneFileWriter>(session.Scene.Writer);

    static string CopyOf(EditorSession session, Vixen.Editor.Core.AssetEntry entry, string name) {
        var path = Path.Combine(session.Project.Paths.Assets, name);
        File.Copy(session.Project.Paths.Absolute(entry.Path), path);
        session.Project.Assets.Scan();

        return path;
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
