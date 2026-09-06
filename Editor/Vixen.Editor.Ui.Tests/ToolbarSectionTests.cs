// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
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

    void Mode(string id, string label, Func<bool> on, Action? run = null) =>
        commands.Add(
            new EditorCommand(id, Title(label), run ?? (() => { })) {
                Checked = on,
                RadioGroup = "gizmo"
            }
        );

    [Fact]
    public void A_group_is_one_segmented_control_with_the_members_inside_it() {
        var mode = "translate";

        Mode("scene.translate", "Translate", () => mode == "translate");
        Mode("scene.rotate", "Rotate", () => mode == "rotate");
        Mode("scene.scale", "Scale", () => mode == "scale");

        var toolbar = Toolbar();

        toolbar.Show(
            new ToolbarButton("scene.translate"),
            new ToolbarGroup("scene.translate", "scene.rotate", "scene.scale")
        );

        var group = Assert.Single(toolbar.Strip.Children.OfType<SegmentedControl>());
        Assert.Equal(3, group.Segments.Count);

        // ⚠ One question with three answers, which is the whole of why this is a control rather than
        // a class on a box. Three toggle buttons are announced as three independent pressed-or-not
        // buttons and say nothing about being alternatives.
        Assert.Equal(AccessibleRole.RadioGroup, group.Role);
        Assert.Equal(AccessibleRole.Radio, group.Segments[0].Role);

        // The value the control draws its chosen member from, refreshed on the tick rather than
        // pushed, so a mode changed anywhere shows here.
        toolbar.Refresh();
        Assert.Equal("scene.translate", group.Value);
        Assert.True(group.Segments[0].IsChecked);
        Assert.False(group.Segments[1].IsChecked);

        mode = "rotate";
        toolbar.Refresh();

        Assert.Equal("scene.rotate", group.Value);
        Assert.False(group.Segments[0].IsChecked);
        Assert.True(group.Segments[1].IsChecked);
    }

    [Fact]
    public void Choosing_a_segment_runs_its_command_and_a_refresh_does_not() {
        var mode = "translate";
        var ran = 0;

        Mode("scene.translate", "Translate", () => mode == "translate", () => { mode = "translate"; ran++; });
        Mode("scene.rotate", "Rotate", () => mode == "rotate", () => { mode = "rotate"; ran++; });

        var toolbar = Toolbar();
        toolbar.Show(new ToolbarGroup("scene.translate", "scene.rotate"));

        var group = Assert.Single(toolbar.Strip.Children.OfType<SegmentedControl>());

        toolbar.Refresh();
        Assert.Equal("scene.translate", group.Value);

        // ⚠ Zero, and this is the assertion the guard in `Choose` exists for. `Refresh` assigns
        // `Value` from whichever command reports itself checked, and that assignment raises the same
        // event a click does — an ungated handler would re-run the current mode every tick.
        Assert.Equal(0, ran);

        group.Segments[1].Activate();

        Assert.Equal(1, ran);
        Assert.Equal("rotate", mode);

        // And the route is what ran it: the segments carry no `Command` of their own, because a
        // bound button writes `:checked` straight into the element and the control writes it from
        // `Value` — two writers on one appearance.
        Assert.Null(group.Segments[1].Command);
    }

    [Fact]
    public void The_arrows_move_between_the_members_and_wrap() {
        var mode = "translate";

        Mode("scene.translate", "Translate", () => mode == "translate", () => mode = "translate");
        Mode("scene.rotate", "Rotate", () => mode == "rotate", () => mode = "rotate");
        Mode("scene.scale", "Scale", () => mode == "scale", () => mode = "scale");

        var toolbar = Toolbar();
        toolbar.Show(new ToolbarGroup("scene.translate", "scene.rotate", "scene.scale"));

        var group = Assert.Single(toolbar.Strip.Children.OfType<SegmentedControl>());
        toolbar.Refresh();

        Key(group, InputKey.Right);
        Assert.Equal("rotate", mode);

        Key(group, InputKey.Right);
        Assert.Equal("scale", mode);

        // Wrapping, which is the half a row of independent buttons could never have: the last
        // member's Right is the first member and not the end of the strip.
        Key(group, InputKey.Right);
        Assert.Equal("translate", mode);

        Key(group, InputKey.Left);
        Assert.Equal("scale", mode);
    }

    [Fact]
    public void A_group_whose_commands_have_all_gone_is_not_an_empty_box() {
        var toolbar = Toolbar();
        toolbar.Show(new ToolbarGroup("plugin.gone", "plugin.also-gone"));

        Assert.Empty(toolbar.Strip.Children.OfType<SegmentedControl>());
    }

    [Fact]
    public void A_box_is_the_run_of_ordinary_buttons_a_group_used_to_be() {
        commands.Add("play.play", Title("Play"), () => { });
        commands.Add("play.stop", Title("Stop"), () => { });

        var toolbar = Toolbar();
        toolbar.Show(new ToolbarBox("play.play", "play.stop"));

        // ⚠ Not a segmented control, and the transport is why the two records are separate. Play,
        // Pause, Step and Stop want the box because a transport is one object; they are four verbs
        // and not four alternatives, so announcing them as a question with one answer — and letting
        // an arrow key "choose" Stop — would be a lie the box does not tell.
        var box = Assert.Single(toolbar.Strip.Children, child => child.Tag == "toolbar-group");

        Assert.Empty(toolbar.Strip.Children.OfType<SegmentedControl>());
        Assert.Equal(2, box.Children.Count);
        Assert.All(box.Children, child => Assert.IsAssignableFrom<ButtonBase>(child));
    }

    static void Key(UiElement element, InputKey key) =>
        element.Raise(new KeyEvent { Key = key, Action = KeyAction.Pressed, Modifiers = ModifierKeys.None });

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
