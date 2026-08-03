// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>Doc 36 § D2: a feature the editor ships uses the door a third party comes through.</summary>
/// <remarks>
///     ⚠ <b>F2's finding is that no built-in ever did.</b> Twelve feature assemblies referenced by
///     hand meant the plugin surface never had to be sufficient — an API whose own authors bypass it
///     is a guess. These tests are about the seam that lets a compiled-in module stop bypassing it:
///     the same <see cref="PluginContext" />, the same registration scope, the same rollback, and the
///     same unload as an assembly loaded off a disk.
/// </remarks>
public class ModuleTests {
    sealed class Module(Action<PluginContext> activate) : IEditorPlugin {
        public int Deactivations { get; private set; }

        public void Activate(PluginContext context) => activate(context);

        public void Deactivate() => Deactivations++;
    }

    static StringId Named(string id) => new(id, id);

    [Fact]
    public void A_module_registers_through_the_same_context_a_plugin_does() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        var module = new Module(
            context => context.AddCommand("terrain.sculpt", Named("terrain.sculpt"), static () => { })
        );

        var loaded = host.Activate("terrain", "Terrain", module);

        Assert.Equal(PluginState.Active, loaded.State);
        Assert.NotNull(shell.Commands["terrain.sculpt"]);

        // It is listed beside whatever came off a disk, because a plugin manager showing only the
        // third-party half would be one where "what is running in my editor" has two answers.
        Assert.Same(loaded, Assert.Single(host.Plugins));
    }

    [Fact]
    public void Unloading_a_module_takes_out_what_it_registered_and_tells_it() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        var module = new Module(
            context => context.AddCommand("terrain.sculpt", Named("terrain.sculpt"), static () => { })
        );

        host.Activate("terrain", "Terrain", module);

        Assert.True(host.Unload("terrain"));
        Assert.Null(shell.Commands["terrain.sculpt"]);
        Assert.Equal(1, module.Deactivations);
        Assert.Equal(PluginState.Unloaded, host.Find("terrain")!.State);
    }

    [Fact]
    public void A_module_that_throws_halfway_leaves_nothing_behind() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        var module = new Module(
            context => {
                context.AddCommand("terrain.sculpt", Named("terrain.sculpt"), static () => { });

                throw new InvalidOperationException("this host has no terrain renderer");
            }
        );

        var loaded = host.Activate("terrain", "Terrain", module);

        // ⚠ Half a built-in is harder to spot than half a plugin, because nobody thinks to look.
        Assert.Equal(PluginState.Failed, loaded.State);
        Assert.Null(shell.Commands["terrain.sculpt"]);
        Assert.Contains(host.Diagnostics, entry => entry.PluginId == "terrain" && entry.Severity == PluginSeverity.Error);
    }

    [Fact]
    public void Two_things_cannot_share_an_id() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        host.Activate("terrain", "Terrain", new Module(static _ => { }));

        Assert.Throws<ArgumentException>(() => host.Activate("terrain", "Terrain Again", new Module(static _ => { })));
    }

    [Fact]
    public void A_module_puts_its_verbs_in_the_menu_the_thing_they_act_on_already_has() {
        using var shell = new EditorShell(1280f, 800f);
        var scene = shell.Menus.AddMenu(new StringId("editor.menu.scene", "Scene"));
        var host = new PluginHost(shell);

        var module = new Module(
            context => {
                context.AddCommand("blockout.extrude", Named("blockout.extrude"), static () => { });

                // ⚠ Not a menu of its own. A top-level heading per feature is a menu bar that grows
                // one for every plugin somebody installs.
                var found = context.FindMenu("editor.menu.scene");

                Assert.NotNull(found);
                context.AddSubmenu(found, new StringId("editor.menu.geometry", "Geometry")).Add("blockout.extrude");
            }
        );

        host.Activate("blockout", "Blockout", module);

        var geometry = Assert.Single(scene.Entries.OfType<MenuSubmenu>());

        Assert.Equal("editor.menu.geometry", geometry.Group.Title.Id);

        // And it goes away again, or the Scene menu keeps a Geometry submenu naming commands that
        // are no longer registered.
        host.Unload("blockout");
        Assert.Empty(scene.Entries);
    }

    [Fact]
    public void Per_frame_work_runs_until_the_module_is_unloaded() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);
        var frames = 0;

        host.Activate("terrain", "Terrain", new Module(context => context.OnUpdate(_ => frames++)));

        host.Update(TimeSpan.FromMilliseconds(16));
        host.Update(TimeSpan.FromMilliseconds(16));

        Assert.Equal(2, frames);

        // ⚠ A callback left behind is not merely a wasted call: it is a delegate over the plugin's
        // own state held by the editor's loop, which is the reference that stops its assembly being
        // collected.
        host.Unload("terrain");
        host.Update(TimeSpan.FromMilliseconds(16));

        Assert.Equal(2, frames);
    }

    [Fact]
    public void A_module_that_throws_once_a_frame_is_unloaded_rather_than_left_throwing() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        var module = new Module(
            context => {
                context.AddCommand("terrain.sculpt", Named("terrain.sculpt"), static () => { });
                context.OnUpdate(static _ => throw new InvalidOperationException("no terrain renderer"));
            }
        );

        host.Activate("terrain", "Terrain", module);
        host.Update(TimeSpan.FromMilliseconds(16));

        // Sixty exceptions a second from one panel is an editor that has stopped, and the first one
        // is the interesting one.
        Assert.Equal(PluginState.Failed, host.Find("terrain")!.State);
        Assert.Null(shell.Commands["terrain.sculpt"]);
        Assert.Contains(host.Diagnostics, entry => entry.Message.Contains("per-frame"));

        // And it stays down rather than being retried every frame.
        host.Update(TimeSpan.FromMilliseconds(16));
        Assert.Equal(PluginState.Failed, host.Find("terrain")!.State);
    }

    [Fact]
    public void A_menu_this_host_has_not_got_is_null_rather_than_a_second_one() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        host.Activate(
            "blockout",
            "Blockout",
            new Module(context => Assert.Null(context.FindMenu("editor.menu.scene")))
        );

        // ⚠ Creating one would put the plugin's entries in a second "Scene" menu, which is somewhere
        // nobody looks — and a misspelt id would be silent rather than reported. The shell's own
        // File/Edit/View/Help are still there; what must not have appeared is a Scene.
        Assert.DoesNotContain(shell.Menus.Menus, menu => menu.Title.Id == "editor.menu.scene");
    }
}
