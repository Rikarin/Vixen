// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Menus and toolbars as views over the registry.</summary>
public class MenuTests : IDisposable {
    readonly UiDocument document = new(1280f, 800f);
    readonly CommandRegistry commands = new();
    readonly KeyMap keys = new();
    readonly List<MenuPresenter> presenters = [];

    public MenuTests() => ControlTheme.Install(document);

    public void Dispose() {
        // ⚠ Every presenter, and this is not tidiness. A presenter is subscribed to the static
        // `Strings.Changed`, so one left alive rebuilds a menu bar into this disposed document the
        // next time any other test switches language — a failure in a test that has nothing to do
        // with menus.
        foreach (var presenter in presenters) {
            presenter.Dispose();
        }

        document.Dispose();
        GC.SuppressFinalize(this);
    }

    static StringId Title(string text) => new("test." + text, text);

    MenuPresenter Present(MenuModel model) {
        var presenter = new MenuPresenter(document.Root, model, commands, keys);
        presenters.Add(presenter);

        return presenter;
    }

    [Fact]
    public void A_menu_shows_what_the_registry_calls_a_command_and_what_the_keymap_binds_it_to() {
        commands.Add("file.save", Title("Save"), () => { });
        keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

        var model = new MenuModel();
        model.AddMenu(Title("File")).Add("file.save");

        var presenter = Present(model);
        var menu = presenter.Bar.Items[0].Menu;

        var item = Assert.Single(menu.Items);
        Assert.Equal("Save", item.Label);
        Assert.Equal(InputKey.S, item.Shortcut?.Key);

        // ⚠ What this machine's user presses, not what the keymap stores. The two are the same on a
        // PC and differ on a Mac, where the bar has to read ⌘S — see `KeyChord.ForPlatform`.
        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control).ForPlatform().Modifiers, item.Shortcut?.Modifiers);
    }

    [Fact]
    public void A_command_registered_afterwards_appears_without_anybody_rebuilding() {
        var model = new MenuModel();
        model.AddMenu(Title("File")).Add("file.save");

        var presenter = Present(model);
        Assert.Empty(presenter.Bar.Items[0].Menu.Items);

        commands.Add("file.save", Title("Save"), () => { });

        // This is the whole claim: an action added once appears everywhere it belongs.
        Assert.Single(presenter.Bar.Items[0].Menu.Items);
    }

    [Fact]
    public void An_id_nothing_registered_is_skipped_and_takes_its_separator_with_it() {
        commands.Add("file.save", Title("Save"), () => { });

        var model = new MenuModel();
        model.AddMenu(Title("File"))
            .Add("plugin.gone")
            .AddSeparator()
            .Add("file.save")
            .AddSeparator()
            .Add("plugin.also-gone");

        var presenter = Present(model);
        var menu = presenter.Bar.Items[0].Menu;

        Assert.Single(menu.Items);

        // A rule at the top, two in a row in the middle and one hanging off the bottom is what a
        // menu built from a model that has lost entries otherwise looks like.
        Assert.DoesNotContain(menu.Children, child => child is Separator);
    }

    [Fact]
    public void Opening_a_menu_asks_every_line_whether_it_can_run() {
        var enabled = false;
        commands.Add(new EditorCommand("edit.undo", Title("Undo"), () => { }) { Enablement = () => enabled });

        var model = new MenuModel();
        model.AddMenu(Title("Edit")).Add("edit.undo");

        var presenter = Present(model);
        var menu = presenter.Bar.Items[0].Menu;

        menu.Open();
        Assert.True(menu.Items[0].Disabled);

        menu.Close();
        enabled = true;
        menu.Open();

        // Asked as it opens, so it cannot be stale — there is no event for "the selection changed
        // in a way that makes Undo meaningful".
        Assert.False(menu.Items[0].Disabled);
    }

    [Fact]
    public void Choosing_a_line_runs_its_command() {
        var ran = 0;
        commands.Add("file.save", Title("Save"), () => ran++);

        var model = new MenuModel();
        model.AddMenu(Title("File")).Add("file.save");

        var presenter = Present(model);
        // ⚠ `Activate` rather than a raised `ClickEvent`, and the change is the wiring rather than
        // the test. A bound command runs from the activation and the click is the *notification*
        // that it did — see `ButtonBase.Activate` — so a synthesised click is now the report of a
        // thing that did not happen. This is the path a real press takes.
        presenter.Bar.Items[0].Menu.Items[0].Activate();

        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_dynamic_group_is_asked_every_time_the_menu_is_built() {
        var panels = new List<string>();

        var model = new MenuModel();
        model.AddMenu(Title("View")).AddDynamic(() => panels);

        var presenter = Present(model);
        Assert.Empty(presenter.Bar.Items[0].Menu.Items);

        commands.Add("view.panel.console", Title("Console"), () => { });
        panels.Add("view.panel.console");
        presenter.Rebuild();

        Assert.Single(presenter.Bar.Items[0].Menu.Items);
    }

    [Fact]
    public void A_rebuild_leaves_no_orphaned_menu_behind() {
        commands.Add("file.save", Title("Save"), () => { });

        var model = new MenuModel();
        model.AddMenu(Title("File")).Add("file.save");

        var presenter = Present(model);
        var before = document.Root.Children.Count;

        presenter.Rebuild();
        presenter.Rebuild();

        // A menu is a child of the root rather than of the bar, so a presenter that edited the bar
        // in place would leak one overlay per rebuild — invisible, and still listening for pointer
        // events.
        Assert.Equal(before, document.Root.Children.Count);
    }

    [Fact]
    public void A_toolbar_button_follows_its_commands_enablement() {
        var enabled = true;
        commands.Add(new EditorCommand("file.save", Title("Save"), () => { }) { Enablement = () => enabled });

        var toolbar = new ToolbarPresenter(document.Root, commands, keys);
        toolbar.Show("file.save");

        var button = Assert.IsType<Button>(toolbar.Strip.Children[0]);
        Assert.False(button.Disabled);

        enabled = false;
        toolbar.Refresh();

        Assert.True(button.Disabled);
    }

    [Fact]
    public void A_toolbar_click_runs_the_command() {
        var ran = 0;
        commands.Add("file.save", Title("Save"), () => ran++);

        var toolbar = new ToolbarPresenter(document.Root, commands, keys);
        toolbar.Show("file.save");

        Assert.IsAssignableFrom<ButtonBase>(toolbar.Strip.Children[0]).Activate();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_context_menu_is_built_from_ids_the_same_way() {
        var ran = 0;
        commands.Add("edit.copy", Title("Copy"), () => ran++);
        commands.Add("edit.paste", Title("Paste"), () => { });

        var menu = MenuPresenter.Context(document, commands, keys, "edit.copy", null, "edit.paste", "plugin.gone");

        Assert.Equal(2, menu.Items.Count);

        menu.Items[0].Activate();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_context_menu_can_be_built_from_a_group_and_has_submenus() {
        var ran = 0;

        commands.Add("scene.create-entity", Title("Create Empty"), () => { });
        commands.Add("scene.create-cube", Title("Cube"), () => ran++);
        commands.Add("scene.create-sphere", Title("Sphere"), () => { });
        commands.Add("scene.delete-entity", Title("Delete"), () => { });

        var group = new MenuGroup(Title("Hierarchy"));
        group.Add("scene.create-entity");

        group.AddSubmenu(Title("3D Object"))
            .Add("scene.create-cube")
            .Add("scene.create-sphere");

        group.AddSeparator();
        group.Add("scene.delete-entity");

        var menu = MenuPresenter.Context(document, group, commands, keys);

        // Create Empty, the submenu's own line, and Delete. The separator is not an item.
        Assert.Equal(3, menu.Items.Count);
        Assert.Equal("3D Object", menu.Items[1].Label);

        var shapes = Assert.IsType<Menu>(menu.Items[1].Submenu, exactMatch: false);
        Assert.Equal(2, shapes.Items.Count);

        // ⚠ On the submenu's own item, which is the case the flat overload cannot express: a
        // submenu is a sibling of its parent rather than a child of it, so a line inside one is
        // reached and bound in its own right.
        shapes.Items[0].Activate();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_context_menus_lines_grey_themselves_out_as_it_opens() {
        var enabled = false;

        commands.Add(
            new EditorCommand("scene.delete-entity", Title("Delete"), () => { }) { Enablement = () => enabled }
        );

        var group = new MenuGroup(Title("Hierarchy"));
        group.Add("scene.delete-entity");

        var menu = MenuPresenter.Context(document, group, commands, keys);
        menu.Open();

        Assert.True(menu.Items[0].Disabled);

        menu.Close(CloseReason.Code);
        enabled = true;
        menu.Open();

        // Opening is the last moment before the user reads it, which is why enablement is applied
        // there rather than when the menu was built — a menu built at start-up would show whatever
        // was true then, for ever.
        Assert.False(menu.Items[0].Disabled);
    }

    /// <summary>
    ///     ⚠ <b>A hundred commands are one bar, not a hundred.</b> The presenter rebuilt the whole
    ///     bar synchronously from <c>CommandRegistry.Changed</c>, and the registry raises that once
    ///     per command — so standing an editor up threw the bar away and built it again about two
    ///     hundred times before the window appeared, and unloading one plugin did it once per command
    ///     withdrawn. It is asserted as a count of rebuilds rather than as a duration on purpose: the
    ///     claim is about how much work a registration causes, and a millisecond budget for the same
    ///     claim is calibrated on whichever machine ran it.
    /// </summary>
    [Fact]
    public void Registering_a_hundred_commands_rebuilds_the_bar_once_rather_than_a_hundred_times() {
        var model = new MenuModel();
        var file = model.AddMenu(Title("File"));

        var presenter = Present(model);
        var built = presenter.Rebuilds;

        for (var index = 0; index < 100; index++) {
            var id = "file.command-" + index;

            file.Add(id);
            commands.Add(id, Title("Command " + index), () => { });
        }

        // Nothing yet: a hundred registrations have marked the bar stale a hundred times and built
        // nothing at all.
        Assert.Equal(built, presenter.Rebuilds);
        Assert.True(presenter.IsPending);

        document.Tick(TimeSpan.FromSeconds(1));

        Assert.Equal(built + 1, presenter.Rebuilds);
        Assert.False(presenter.IsPending);
        Assert.Equal(100, presenter.Bar.Items[0].Menu.Items.Count);
    }

    /// <summary>
    ///     ⚠ <b>A reader settles it, which is what keeps the deferral invisible.</b> Callers all over
    ///     this repository register a command and read the bar on the next line with no frame in
    ///     between — the shell's own tests included — and a bar that answered them with the
    ///     arrangement from before the registration would be a coalescing that changed what the class
    ///     means rather than when it does its work.
    /// </summary>
    [Fact]
    public void Reading_the_bar_settles_a_registration_no_frame_has_reached_yet() {
        var model = new MenuModel();
        model.AddMenu(Title("File")).Add("file.save");

        var presenter = Present(model);

        commands.Add("file.save", Title("Save"), () => { });

        var item = Assert.Single(presenter.Bar.Items[0].Menu.Items);

        Assert.Equal("Save", item.Label);
        Assert.False(presenter.IsPending);
    }
}
