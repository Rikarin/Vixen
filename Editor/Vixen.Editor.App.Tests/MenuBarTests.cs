// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The bar doc 20's Part C describes, held to being all there and all live.</summary>
/// <remarks>
///     ⚠ <b>Doc 20's Part F asks for exactly the first test here and says why: five menu lines named
///     commands nobody registered, and no test noticed.</b> A dangling id is invisible — the menu
///     builder skips it — so the File menu quietly had two entries in it for as long as that was
///     true. Fifteen lines of walk over the model is the whole cost of never having that again.
/// </remarks>
public class MenuBarTests {
    [Fact]
    public void Every_line_of_every_menu_names_a_command_that_exists() {
        using var fixture = new EditorFixture();

        var commands = fixture.Editor.Shell.Commands;
        List<string> dangling = [];

        foreach (var menu in fixture.Editor.Shell.Menus.Menus) {
            Walk(menu, commands, dangling);
        }

        Assert.Empty(dangling);
    }

    [Fact]
    public void The_bar_is_the_ten_menus_Part_C_names_in_Part_Cs_order() {
        using var fixture = new EditorFixture();

        var titles = fixture.Editor.Shell.Menus.Menus.Select(menu => menu.Title.Text).ToList();

        Assert.Equal(
            ["File", "Edit", "Assets", "Entity", "Scene", "Play", "Window", "Build", "Tools", "Help"],
            titles
        );
    }

    [Fact]
    public void No_menu_on_the_bar_opens_onto_nothing() {
        using var fixture = new EditorFixture();

        foreach (var item in fixture.Editor.Shell.MenuBar.Bar.Items) {
            Assert.NotEmpty(item.Menu.Items);
        }
    }

    /// <summary>
    ///     Every command that is declared but not built says which milestone builds it, and none of
    ///     them can be run.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The reason is the whole mechanism.</b> A greyed line with no explanation is a line
    ///     that reads as broken; doc 20's bar is that a verb which is not implemented is
    ///     <i>visibly</i> not implemented. A blank reason would satisfy the type and defeat the
    ///     point, which is why the assertion is on the text and not on the flag.
    /// </remarks>
    [Fact]
    public void A_declared_but_unbuilt_verb_is_disabled_and_says_why() {
        using var fixture = new EditorFixture();

        var planned = fixture.Editor.Shell.Commands.Commands.Where(command => command.IsUnavailable).ToList();

        // If this ever reaches zero the mechanism has been retired, which is a good day and should
        // be a deliberate edit rather than a test that silently stops asserting anything.
        Assert.NotEmpty(planned);

        foreach (var command in planned) {
            Assert.False(command.CanExecute, command.Id + " is unavailable and still says it can run");
            Assert.False(string.IsNullOrWhiteSpace(command.Unavailable.Text), command.Id + " gives no reason");
            Assert.False(fixture.Editor.Shell.Commands.Execute(command.Id), command.Id + " ran");
        }
    }

    [Fact]
    public void Every_default_binding_is_taken_without_a_conflict() {
        using var fixture = new EditorFixture();

        var keys = fixture.Editor.Shell.Keys;
        var commands = fixture.Editor.Shell.Commands;

        // ⚠ A default that collided with another default is a bug in the application rather than
        // something to ask the user about — `KeyMap.SetDefault` reports it and nothing else looks.
        // Re-binding each command to what it already has must come back Unchanged; anything else
        // means the binding did not survive registration.
        foreach (var (id, chord) in keys.Bindings) {
            Assert.True(commands.TryGet(id, out _), id + " is bound and is not registered");
            Assert.Equal(BindResult.Unchanged, keys.SetDefault(id, chord));
        }
    }

    /// <summary>Doc 20's A5, in the small: the two Deletes, and neither giving up the key.</summary>
    [Fact]
    public void The_outliner_and_the_content_browser_can_both_be_the_focused_context() {
        using var fixture = new EditorFixture();

        var shell = fixture.Editor.Shell;

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        Assert.Equal("scene", shell.Context);
        Assert.True(shell.Commands.CanExecute("edit.delete"));

        fixture.Open("project");
        fixture.Click(fixture.Project);

        Assert.Equal("project", shell.Context);

        // Out of scope rather than merely disabled: the entity is still selected, and an editor that
        // decided from the selection alone would delete it.
        Assert.False(shell.Commands.CanExecute("edit.delete"));
        Assert.False(shell.Commands.Execute("edit.delete"));
    }

    static void Walk(MenuGroup group, CommandRegistry commands, List<string> dangling) {
        foreach (var entry in group.Entries) {
            switch (entry) {
                case MenuCommand(var id) when !commands.TryGet(id, out _):
                    dangling.Add(id);
                    break;

                case MenuSubmenu(var child):
                    Walk(child, commands, dangling);
                    break;

                case MenuDynamic(var ids):
                    dangling.AddRange(ids().Where(id => !commands.TryGet(id, out _)));
                    break;

                default:
                    break;
            }
        }
    }
}
