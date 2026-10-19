// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The Scene menu, the Scenes panel and the multi-scene verbs are <c>SceneModule</c>'s (#1290).</summary>
/// <remarks>
///     <para>
///         <b>The fifth slice of #1290.</b> Opening a scene beside the others and closing one were
///         application methods the Scenes panel called by index; they are
///         <see cref="ISceneEditing.OpenAdditively" /> and <see cref="ISceneEditing.Close" /> now, and the
///         panel, the two menu lines and the Scene menu itself are registered by the module through
///         <c>PluginContext</c> — so they leave with it.
///     </para>
///     <para>
///         Every test reaches by effect, through an <see cref="EditorSession" /> opened the way
///         <c>EditorHost</c> opens one: the service a plugin would <c>Require</c>, the command a menu
///         line names, the row a person clicks.
///     </para>
/// </remarks>
public class ScenesPanelTests {
    /// <summary>A plugin's open and close are the editor's: one list, and the entities leave the world.</summary>
    [Fact]
    public void A_scene_a_plugin_opens_and_closes_through_the_service_leaves_nothing_in_the_world() {
        using var fixture = EditorSession.Start();

        var service = fixture.Plugins.Services.Require<ISceneEditing>();
        var changed = 0;

        service.ScenesChanged += _ => changed++;

        var opened = service.OpenAdditively(Write(fixture)) ?? throw fixture.Fail("the second scene did not open");

        fixture.Settle();

        Assert.Equal(1, changed);
        Assert.Equal([fixture.Scene, opened], service.Scenes);
        Assert.Equal(2, fixture.Editor.Scenes.Count);

        // Not activated: opening beside is not switching to.
        Assert.Same(fixture.Scene, service.Active);

        var prop = Assert.Single(opened.Roots);

        // ⚠ Unsaved, and closed anyway: the service does not ask, the menu line and the panel do.
        opened.Create("Unsaved", LocalTransform.Identity);

        Assert.True(service.Close(opened));
        fixture.Settle();

        Assert.False(fixture.IsAsking);
        Assert.Equal(2, changed);
        Assert.False(opened.IsOpen);
        Assert.False(fixture.Scene.World.IsAlive(prop));
        Assert.Single(fixture.Editor.Scenes);

        // The first scene is not closable, by anybody.
        Assert.False(service.Close(fixture.Scene));
    }

    /// <summary>Scene ▸ Close Scene closes the active scene and asks first when it has changes.</summary>
    [Fact]
    public void Close_scene_asks_before_discarding_and_then_closes_the_active_scene() {
        using var fixture = EditorSession.Start();

        var service = fixture.Plugins.Services.Require<ISceneEditing>();
        var opened = service.OpenAdditively(Write(fixture)) ?? throw fixture.Fail("the second scene did not open");

        fixture.Settle();

        // The first scene is active, and it is the one scene this line never closes.
        Assert.False(fixture.CanRun(ScenesPanel.CloseCommand));

        Assert.True(service.Activate(opened));
        fixture.Settle();

        opened.Create("Unsaved", LocalTransform.Identity);

        fixture.Menu("Scene", "Close Scene");

        Assert.True(fixture.IsAsking, "Close Scene did not ask before discarding unsaved changes");
        Assert.True(opened.IsOpen);

        fixture.Answer("Discard");

        Assert.False(opened.IsOpen);
        Assert.Same(fixture.Scene, service.Active);
        Assert.Equal([fixture.Scene], service.Scenes);
    }

    /// <summary>The panel's rows are the open scenes, and a row's Close is the same close.</summary>
    [Fact]
    public void The_scenes_panel_lists_the_open_scenes_and_closes_one_from_its_row() {
        using var fixture = EditorSession.Start();

        var service = fixture.Plugins.Services.Require<ISceneEditing>();

        fixture.Open(ScenesPanel.PanelId).Settle();

        Assert.Equal(["Main"], Names(fixture));

        var opened = service.OpenAdditively(Write(fixture)) ?? throw fixture.Fail("the second scene did not open");

        fixture.Settle();

        // Redrawn by the service's event, not by the application that opened it.
        Assert.Equal(["Main", "Second"], Names(fixture));

        // ⚠ An edit puts the unsaved mark on the row at the next frame — the rows follow the stack,
        // not only the opens and closes.
        opened.Create("Unsaved", LocalTransform.Identity);
        fixture.Settle();

        Assert.Equal(["Main", "Second *"], Names(fixture));

        var close = Rows(fixture)[1].Children.OfType<Button>().Single();

        fixture.Click(close);

        Assert.True(fixture.IsAsking);

        fixture.Answer("Discard");

        Assert.False(opened.IsOpen);
        Assert.Equal(["Main"], Names(fixture));
    }

    /// <summary>Switching the module off takes the Scene menu, the panel and the two verbs with it.</summary>
    /// <remarks>
    ///     ⚠ <b>And Blockout's and Water's submenus stay reachable</b>, because each finds the Scene menu
    ///     or — with the module off — makes one of its own through <c>[EditorMenu]</c>.
    /// </remarks>
    [Fact]
    public void Switching_the_scene_module_off_takes_the_scene_menu_and_the_scenes_panel_with_it() {
        using var editor = EditorSession.Start();

        var bar = editor.Shell.Menus.Menus.Select(menu => menu.Title.Id).ToList();

        // On: Scene sits after Entity, where doc 20's Part C puts it, and Blockout landed in it.
        Assert.Equal(bar.IndexOf(EditorStrings.MenuEntity.Id) + 1, bar.IndexOf(EditorStrings.MenuScene.Id));
        Assert.Contains(SceneMenu(editor)!.Entries.OfType<MenuSubmenu>(), entry => entry.Group.Title.Id == "editor.menu.blockout-handoff");

        editor.Plugins.Disable(SceneModule.ModuleId);
        editor.Settle();

        Assert.DoesNotContain(editor.Shell.Workspace.Panels, panel => panel.Id == ScenesPanel.PanelId);
        Assert.Null(editor.Shell.Commands["scene.scenes"]);
        Assert.Null(editor.Shell.Commands["scene.open-additive"]);
        Assert.Null(editor.Shell.Commands[ScenesPanel.CloseCommand]);

        // The menu went with the module; the application's viewport verbs it named did not.
        Assert.DoesNotContain(
            SceneMenu(editor)?.Entries.OfType<MenuCommand>() ?? [],
            line => line.CommandId == "scene.focus"
        );
        Assert.NotNull(editor.Shell.Commands["scene.focus"]);
    }

    /// <summary>After the module is switched off, nothing the editor holds reaches its Scenes panel.</summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="WeakReference" /> on the panel object, because the module lives in the
    ///     application's assembly</b> and <c>WaitForCollection</c> answers true for a built-in (#740). The
    ///     panel subscribes to the service, which outlives every module, so an unload that left one
    ///     handler behind would keep it — and every scene it ever marked — alive.
    /// </remarks>
    [Fact]
    public void Nothing_the_editor_holds_reaches_the_scenes_panel_after_the_module_is_switched_off() {
        using var editor = EditorSession.Start();

        editor.Open(ScenesPanel.PanelId).Settle();

        var reference = Probe(editor);

        editor.Plugins.Disable(SceneModule.ModuleId);
        editor.Settle();

        for (var attempt = 0; attempt < 8 && reference.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.IsAlive, "something the editor still holds is pointing at the Scenes panel.");
    }

    /// <summary>The panel object, found through the descriptor its module registered.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference Probe(EditorSession editor) {
        var descriptor = Assert.Single(editor.Shell.Workspace.Panels, panel => panel.Id == ScenesPanel.PanelId);

        return new WeakReference(Assert.IsType<ScenesPanel>(descriptor.Build.Target));
    }

    static MenuGroup? SceneMenu(EditorSession editor) =>
        editor.Shell.Menus.Menus.SingleOrDefault(menu => menu.Title.Id == EditorStrings.MenuScene.Id);

    static List<UiElement> Rows(EditorSession fixture) =>
        [.. Descendants(fixture.Panel(ScenesPanel.PanelId)).Where(element => element.Tag == "scene-row")];

    static List<string?> Names(EditorSession fixture) =>
        [.. Rows(fixture).Select(row => row.Children.Single(child => child.Tag == "scene-name").Text)];

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var below in Descendants(child)) {
                yield return below;
            }
        }
    }

    /// <summary>Writes a scene called Second beside Main, with one entity in it.</summary>
    static string Write(EditorSession fixture) {
        var path = Path.Combine(fixture.Project.Paths.Assets, "Scenes", "Second.vxscene");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var other = new SceneDocument(fixture.Project, fixture.Scene.World, Vixen.Core.AssetId.Empty, "Second") {
            Writer = new SceneFileWriter(path)
        };

        other.Add("Prop", LocalTransform.At(new(3f, 0f, 0f)));
        other.Save();
        other.Close();

        return path;
    }
}
