// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>Where a declared line lands, which submenu two declarations share, and what unloading takes back.</summary>
/// <remarks>
///     <see cref="DeclaredMenus" /> is the half of the declaration path both readers share — the scan a
///     disk plugin gets and the generated <c>Declared.Register</c> an in-tree module calls — so these
///     are the placement rules for both, asserted once.
/// </remarks>
public class DeclaredMenuTests {
    sealed class Module(Action<PluginContext> activate) : IEditorPlugin {
        public void Activate(PluginContext context) => activate(context);
    }

    static StringId Named(string id) => new(id, id);

    /// <summary>A Scene menu shaped like the application's: two submenus, the second being Measure.</summary>
    static MenuGroup Scene(EditorShell shell) {
        var scene = shell.Menus.AddMenu(new StringId("editor.menu.scene", "Scene"));

        scene.Add("scene.create-entity");
        scene.AddSubmenu(new StringId("editor.menu.snap", "Snap")).Add("scene.toggle-snap");
        scene.AddSubmenu(new StringId("editor.menu.precision", "Measure")).Add("scene.measure");
        scene.AddSubmenu(new StringId("editor.menu.camera", "Camera")).Add("scene.view-front");

        return scene;
    }

    static string Render(MenuGroup group) => string.Join(" ", group.Entries.Select(Render));

    static string Render(MenuEntry entry) => entry switch {
        MenuCommand(var id) => id,
        MenuSeparator => "|",
        MenuSubmenu(var group) => group.Title.Id + "[" + Render(group) + "]",
        _ => "?"
    };

    /// <summary>
    ///     ⚠ <b>The claim the scan's own remark made and did not keep.</b> It said two declarations
    ///     naming one menu "land in it together"; it gave every declaration its own submenu, with an id
    ///     made from the <i>command's</i> id — so <c>Tools/My Thing/A</c> and <c>Tools/My Thing/B</c>
    ///     were two <i>My Thing</i> lines.
    /// </summary>
    [Fact]
    public void Two_lines_under_one_undeclared_submenu_share_it() {
        using var shell = new EditorShell(1280f, 800f);
        var tools = shell.Menus.AddMenu(new StringId("editor.menu.tools", "Tools"));
        var host = new PluginHost(shell);

        host.Activate(
            "mine",
            "Mine",
            new Module(
                context => {
                    var menus = new DeclaredMenus(context);

                    menus.Command("Tools/My Thing/A", static () => { });
                    menus.Command("Tools/My Thing/B", static () => { });
                    menus.Apply();
                }
            )
        );

        Assert.Equal("editor.menu.tools.my-thing[scripts.tools.my-thing.a scripts.tools.my-thing.b]", Render(tools));

        // ⚠ And a second plugin naming the same path joins it too, which is the case a cache of this
        // pass's own paths cannot answer: it has to find the submenu the first one made.
        host.Activate(
            "theirs",
            "Theirs",
            new Module(
                context => {
                    var menus = new DeclaredMenus(context);

                    menus.Command("Tools/My Thing/C", static () => { });
                    menus.Apply();
                }
            )
        );

        Assert.Equal(
            "editor.menu.tools.my-thing[scripts.tools.my-thing.a scripts.tools.my-thing.b scripts.tools.my-thing.c]",
            Render(tools)
        );
    }

    [Fact]
    public void A_declared_submenu_goes_after_the_sibling_it_names_with_its_own_title() {
        using var shell = new EditorShell(1280f, 800f);
        var scene = Scene(shell);
        var host = new PluginHost(shell);

        host.Activate(
            "mine",
            "Mine",
            new Module(
                context => {
                    var menus = new DeclaredMenus(context);

                    // ⚠ Declared in the reverse of the order they end up in, and with the second
                    // placed after the first: the priorities are what order them, and the second's
                    // `after` names a submenu this same pass creates.
                    menus.Submenu("Scene/Two", Named("mine.two"), after: "mine.one", priority: 2);
                    menus.Submenu("Scene/One", Named("mine.one"), after: "editor.menu.precision", priority: 1);
                    menus.Line("Scene/Two", "mine.b");
                    menus.Line("Scene/One", "mine.a");
                    menus.Apply();
                }
            )
        );

        Assert.Equal(
            "scene.create-entity editor.menu.snap[scene.toggle-snap] editor.menu.precision[scene.measure] "
            + "mine.one[mine.a] mine.two[mine.b] editor.menu.camera[scene.view-front]",
            Render(scene)
        );
    }

    [Fact]
    public void Lines_come_in_priority_order_with_separators_where_declared() {
        using var shell = new EditorShell(1280f, 800f);
        var scene = Scene(shell);
        var host = new PluginHost(shell);

        host.Activate(
            "mine",
            "Mine",
            new Module(
                context => {
                    var menus = new DeclaredMenus(context);

                    menus.Submenu("Scene/Mine", Named("mine.menu"));
                    menus.Line("Scene/Mine", "mine.c", priority: 3, separator: true);
                    menus.Line("Scene/Mine", "mine.a", priority: 1, separator: true);
                    menus.Lines("Scene/Mine", ["mine.b1", "mine.b2"], priority: 2);
                    menus.Apply();
                }
            )
        );

        var mine = Assert.Single(
            scene.Entries.OfType<MenuSubmenu>(),
            entry => entry.Group.Title.Id == "mine.menu"
        );

        // The first line's separator is dropped — it has nothing above it to be separated from.
        Assert.Equal("mine.a mine.b1 mine.b2 | mine.c", Render(mine.Group));
    }

    /// <summary>A declared submenu with a title the menu already has joins it rather than doubling it.</summary>
    [Fact]
    public void A_declared_submenu_joins_one_with_its_title_id() {
        using var shell = new EditorShell(1280f, 800f);
        var scene = Scene(shell);
        var host = new PluginHost(shell);

        host.Activate(
            "mine",
            "Mine",
            new Module(
                context => {
                    var menus = new DeclaredMenus(context);

                    menus.Submenu("Scene/Measure", new StringId("editor.menu.precision", "Measure"));
                    menus.Line("Scene/Measure", "mine.ruler", separator: true);
                    menus.Apply();
                }
            )
        );

        Assert.Equal(
            "scene.create-entity editor.menu.snap[scene.toggle-snap] editor.menu.precision[scene.measure | mine.ruler] "
            + "editor.menu.camera[scene.view-front]",
            Render(scene)
        );

        // ⚠ And unloading takes the line and its separator out of the menu it joined, and leaves the
        // menu — which is the application's, not the plugin's.
        host.Unload("mine");

        Assert.Equal(
            "scene.create-entity editor.menu.snap[scene.toggle-snap] editor.menu.precision[scene.measure] "
            + "editor.menu.camera[scene.view-front]",
            Render(scene)
        );
    }

    [Fact]
    public void Unloading_takes_back_every_submenu_line_and_command_the_pass_added() {
        using var shell = new EditorShell(1280f, 800f);
        var scene = Scene(shell);
        var before = Render(scene);
        var bar = shell.Menus.Menus.Count;
        var host = new PluginHost(shell);

        host.Activate(
            "mine",
            "Mine",
            new Module(
                context => {
                    var menus = new DeclaredMenus(context);

                    menus.Submenu("Scene/Mine", Named("mine.menu"), after: "editor.menu.snap");
                    menus.Submenu("Scene/Mine/Deeper", Named("mine.deeper"));
                    menus.Line("Scene/Mine", "mine.a");
                    menus.Line("Scene/Mine/Deeper", "mine.b", separator: true);
                    menus.Command("Mine Menu/Go", static () => { }, id: "mine.go");
                    menus.Apply();
                }
            )
        );

        Assert.NotEqual(before, Render(scene));
        Assert.NotNull(shell.Commands["mine.go"]);
        Assert.Equal(bar + 1, shell.Menus.Menus.Count);

        host.Unload("mine");

        Assert.Equal(before, Render(scene));
        Assert.Null(shell.Commands["mine.go"]);
        Assert.Equal(bar, shell.Menus.Menus.Count);
    }

    [Fact]
    public void A_path_with_too_few_parts_is_refused_naming_the_path() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        var loaded = host.Activate(
            "mine",
            "Mine",
            new Module(context => new DeclaredMenus(context).Command("Tools", static () => { }))
        );

        Assert.Equal(PluginState.Failed, loaded.State);
        Assert.Contains("'Tools'", Assert.Single(host.Diagnostics).Message, StringComparison.Ordinal);
    }
}
