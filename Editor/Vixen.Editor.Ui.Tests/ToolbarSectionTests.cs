// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The toolbar as sections rather than as a flat strip, and the marks a menu draws.</summary>
/// <remarks>
///     Doc 20's objection to the old strip is precise: Translate, Rotate and Scale drawn as three
///     adjacent buttons say nothing about being one choice, and three ticks in a menu is not how a
///     choice reads. Both halves of the fix are here because they are the same claim seen from two
///     views over the one registry.
/// </remarks>
public class ToolbarSectionTests : IDisposable {
    readonly UiDocument document = new(1280f, 800f);
    readonly CommandRegistry commands = new();
    readonly KeyMap keys = new();
    readonly List<MenuPresenter> presenters = [];

    public ToolbarSectionTests() => ControlTheme.Install(document);

    public void Dispose() {
        foreach (var presenter in presenters) {
            presenter.Dispose();
        }

        document.Dispose();
        GC.SuppressFinalize(this);
    }

    static StringId Title(string text) => new("test." + text, text);

    ToolbarPresenter Toolbar() => new(document.Root, commands, keys);

    void Mode(string id, string label, Func<bool> on) =>
        commands.Add(
            new EditorCommand(id, Title(label), () => { }) {
                Checked = on,
                RadioGroup = "gizmo"
            }
        );

    [Fact]
    public void A_group_is_one_box_with_the_members_inside_it() {
        var mode = "translate";

        Mode("scene.translate", "Translate", () => mode == "translate");
        Mode("scene.rotate", "Rotate", () => mode == "rotate");
        Mode("scene.scale", "Scale", () => mode == "scale");

        var toolbar = Toolbar();

        toolbar.Show(
            new ToolbarButton("scene.translate"),
            new ToolbarGroup("scene.translate", "scene.rotate", "scene.scale")
        );

        var group = Assert.Single(toolbar.Strip.Children, child => child.Tag == "toolbar-group");
        Assert.Equal(3, group.Children.Count);

        // The state the theme's `:checked` reads, which is what draws the current mode pressed. It
        // is refreshed on the tick rather than pushed, so a mode changed anywhere shows here.
        toolbar.Refresh();
        Assert.True(group.Children[0].State.HasFlag(ElementState.Checked));
        Assert.False(group.Children[1].State.HasFlag(ElementState.Checked));

        mode = "rotate";
        toolbar.Refresh();

        Assert.False(group.Children[0].State.HasFlag(ElementState.Checked));
        Assert.True(group.Children[1].State.HasFlag(ElementState.Checked));
    }

    [Fact]
    public void A_group_whose_commands_have_all_gone_is_not_an_empty_box() {
        var toolbar = Toolbar();
        toolbar.Show(new ToolbarGroup("plugin.gone", "plugin.also-gone"));

        Assert.DoesNotContain(toolbar.Strip.Children, child => child.Tag == "toolbar-group");
    }

    [Fact]
    public void A_dropdown_opens_a_menu_over_the_same_registry() {
        var ran = 0;

        commands.Add("scene.toggle-grid", Title("Grid"), () => ran++);
        commands.Add("scene.toggle-snap", Title("Snapping"), () => { });

        var toolbar = Toolbar();

        toolbar.Show(
            new ToolbarDropdown(Title("Gizmo"), null, "scene.toggle-grid", null, "scene.toggle-snap")
        );

        var button = Assert.Single(toolbar.Strip.Children.OfType<Button>());
        Assert.True(button.HasClass("toolbar-dropdown"));

        var menu = Assert.Single(document.Root.Children.OfType<ContextMenu>());
        Assert.Equal(2, menu.Items.Count);

        menu.Items[0].Activate();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_rebuild_takes_the_dropdowns_menus_with_it() {
        commands.Add("scene.toggle-grid", Title("Grid"), () => { });

        var toolbar = Toolbar();
        toolbar.Show(new ToolbarDropdown(Title("Gizmo"), null, "scene.toggle-grid"));

        Assert.Single(document.Root.Children.OfType<ContextMenu>());

        // ⚠ A menu hangs off the document root so it can float over everything, so a rebuild that
        // removed only the strip would leave one invisible, attached to nothing, per rebuild.
        toolbar.Rebuild();
        toolbar.Rebuild();

        Assert.Single(document.Root.Children.OfType<ContextMenu>());
    }

    [Fact]
    public void The_flat_form_still_describes_a_strip_of_buttons_and_rules() {
        commands.Add("file.save", Title("Save"), () => { });

        var toolbar = Toolbar();
        toolbar.Show("file.save", null, "plugin.gone");

        Assert.Equal(["file.save", null, "plugin.gone"], toolbar.Items);
        Assert.Single(toolbar.Strip.Children.OfType<Button>());
        Assert.Single(toolbar.Strip.Children.OfType<Separator>());
    }

    [Fact]
    public void A_toolbar_button_greys_itself_out_when_its_command_is_out_of_scope() {
        string? context = "scene";

        commands.FocusedContext = () => context;
        commands.Add(new EditorCommand("edit.delete", Title("Delete"), () => { }) { Context = "scene" });

        var toolbar = Toolbar();
        toolbar.Show("edit.delete");

        var button = Assert.Single(toolbar.Strip.Children.OfType<Button>());
        Assert.False(button.Disabled);

        context = "project";
        toolbar.Refresh();

        // A button that stayed live for a command the registry would refuse is the toolbar lying
        // about what a click does.
        Assert.True(button.Disabled);
    }

    [Fact]
    public void A_toggle_gets_a_tick_and_a_radio_member_gets_a_dot() {
        commands.Add(new EditorCommand("view.grid", Title("Grid"), () => { }) { Checked = () => true });
        commands.Add("file.save", Title("Save"), () => { });

        Mode("scene.translate", "Translate", () => true);

        var model = new MenuModel();
        model.AddMenu(Title("View")).Add("view.grid", "file.save", "scene.translate");

        var presenter = new MenuPresenter(document.Root, model, commands, keys);
        presenters.Add(presenter);

        var menu = presenter.Bar.Items[0].Menu;

        // ⚠ The geometry, not just the visibility. An `Icon` with no geometry draws nothing, so a
        // menu that only toggled the mark's display showed a checked command with an empty gutter.
        Assert.Same(ControlIcons.Check, menu.Items[0].Mark.Geometry);
        Assert.Same(EditorIcons.RadioMark, menu.Items[2].Mark.Geometry);

        // And a command that is not a toggle never grows a mark at all, which is what keeps the
        // ordinary menu from being indented by a column of empty ticks.
        Assert.DoesNotContain(menu.Items[1].Children, child => child is Icon);
    }
}
