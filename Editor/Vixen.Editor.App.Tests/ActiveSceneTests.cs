// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Which scene is active is the scene-editing service's, and the editor follows it.</summary>
/// <remarks>
///     <para>
///         <b>#1290: the <c>scene</c> field as "the active scene" owned by a service.</b> It was an
///         assignment inside the application's multi-scene verbs, so a plugin could read
///         <see cref="ISceneEditing.Active" /> and not change it. The open scenes and the active one are
///         <see cref="ISceneEditing" />'s now; the application follows <c>ISceneEditing.ActiveChanged</c>
///         with the arbitration only it can do — the picker, the probe, the panes, the document focus.
///     </para>
///     <para>
///         ⚠ Not <c>EditorProject.ActiveDocument</c>, which the first slice refuted: an asset editor's tab
///         activates its own document, and a Group pressed then must still group entities.
///     </para>
/// </remarks>
public class ActiveSceneTests {
    /// <remarks>
    ///     ⚠ <b>Closing the active additive scene left the editor on the closed document.</b> The close
    ///     moved the index back and then called <c>Activate(index)</c>, which returns early when the index
    ///     is already the active one — so nothing was ever pointed at the scene that remained, and every
    ///     command after it acted on a document that had deleted its entities and closed.
    /// </remarks>
    [Fact]
    public void Closing_the_active_additive_scene_leaves_the_editor_on_one_that_is_open() {
        using var fixture = EditorSession.Start();

        var main = fixture.Scene;
        var opened = Second(fixture);

        fixture.Editor.Activate(1);
        fixture.Settle();

        Assert.Same(opened, fixture.Scene);

        fixture.Editor.CloseScene(1);
        fixture.Settle();

        Assert.False(opened.IsOpen);
        Assert.Same(main, fixture.Scene);
        Assert.Same(main, fixture.Plugins.Services.Require<ISceneEditing>().Active);
    }

    [Fact]
    public void A_scene_activated_through_the_service_is_the_one_the_editor_edits() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(1);

        var opened = Second(fixture);
        var service = fixture.Plugins.Services.Require<ISceneEditing>();

        Assert.Equal([fixture.Scene, opened], service.Scenes);

        Assert.True(service.Activate(opened));
        fixture.Settle();

        // The editor follows: its scene, the panes and the focus all moved with the service.
        Assert.Same(opened, fixture.Scene);
        Assert.Same(opened, fixture.Viewport!.Document);
        Assert.Same(opened, fixture.Project.ActiveDocument.Peek());

        // And a command run after it creates into the scene the service named.
        var before = opened.Entities.Count();

        fixture.Run("scene.create-entity");

        Assert.Equal(before + 1, opened.Entities.Count());
    }

    /// <summary>Writes a second scene beside Main and opens it additively.</summary>
    static SceneDocument Second(EditorSession fixture) {
        var path = Path.Combine(fixture.Project.Paths.Assets, "Scenes", "Second.vxscene");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var other = new SceneDocument(fixture.Project, fixture.Scene.World, Vixen.Core.AssetId.Empty, "Second") {
            Writer = new SceneFileWriter(path)
        };

        other.Add("Prop", LocalTransform.At(new(3f, 0f, 0f)));
        other.Save();
        other.Close();

        return fixture.Editor.OpenSceneAdditively(path) ?? throw fixture.Fail("the second scene did not open");
    }
}
