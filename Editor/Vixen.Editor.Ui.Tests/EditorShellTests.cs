// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The whole shell, driven the way doc 11's headless editor host would drive it.</summary>
public class EditorShellTests {
    static StringId Title(string text) => new("test." + text, text);

    static EditorShell Built() {
        var shell = new EditorShell(1280f, 800f);

        shell.RegisterPanel("hierarchy", Title("Hierarchy"), panel => panel.Add<TextBlock>().Text = "tree");
        shell.RegisterPanel("scene", Title("Scene"), panel => panel.Add<TextBlock>().Text = "viewport");
        shell.RegisterPanel("inspector", Title("Inspector"), panel => panel.Add<TextBlock>().Text = "grid");

        shell.RegisterLayout(
            "Default",
            Title("Default"),
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"])
        );

        shell.Workspace.Reset();
        return shell;
    }

    [Fact]
    public void A_shell_lays_out_and_draws_with_no_platform_underneath_it() {
        using var shell = Built();

        shell.Tick(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(16));
        shell.Document.Update();
        shell.Document.Draw();

        // Doc 11's editor UI automation needs this class to be constructible without a GPU. It is.
        Assert.True(shell.Document.Drawing.Commands.Count > 0);
    }

    [Fact]
    public void Registering_a_panel_gives_it_a_command_and_a_place_in_the_View_menu() {
        using var shell = Built();

        var id = EditorShell.PanelCommand("scene");
        Assert.True(shell.Commands.TryGet(id, out var command));
        Assert.True(command!.IsChecked);

        shell.Commands.Execute(id);
        Assert.False(shell.Workspace.IsOpen("scene"));

        shell.Commands.Execute(id);
        Assert.True(shell.Workspace.IsOpen("scene"));
    }

    [Fact]
    public void The_palette_opens_on_its_shortcut() {
        using var shell = Built();

        Assert.False(shell.Palette.IsOpen);

        shell.Document.Dispatch(
            new KeyEvent { Key = InputKey.P, Action = KeyAction.Pressed, Modifiers = ModifierKeys.Control }
        );

        Assert.True(shell.Palette.IsOpen);
    }

    [Fact]
    public void Everything_registered_is_reachable_from_the_palette() {
        using var shell = Built();

        shell.Palette.OpenPalette();
        shell.Palette.Field.Value = "inspector";
        shell.Palette.Refresh();

        Assert.Contains(shell.Palette.Results, item => item.Title == "Inspector");
    }

    [Fact]
    public void Resetting_the_layout_says_so() {
        using var shell = Built();

        Assert.True(shell.Commands.Execute("view.reset-layout"));

        var notice = Assert.Single(shell.Notifications.History);
        Assert.Equal(EditorStrings.LayoutReset.Source, notice.Message);
        Assert.Equal(NotificationSeverity.Success, notice.Severity);
    }

    [Fact]
    public void Toggling_the_theme_is_a_command_and_reports_which_way_it_is() {
        using var shell = Built();

        Assert.True(shell.Commands["view.toggle-theme"]!.IsChecked);

        shell.Commands.Execute("view.toggle-theme");

        Assert.Equal(ThemeMode.Light, shell.Theme.Mode);
        Assert.False(shell.Commands["view.toggle-theme"]!.IsChecked);
    }

    [Fact]
    public void A_command_registered_by_the_application_lands_in_the_menu_the_shell_described() {
        using var shell = Built();

        // The shell's default model names `file.save` and skips it while nothing has registered it.
        var file = shell.MenuBar.Bar.Items[0].Menu;
        Assert.DoesNotContain(file.Items, item => item.Label == "Save");

        shell.Commands.Add("file.save", Title("Save"), () => { });
        shell.Keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

        file = shell.MenuBar.Bar.Items[0].Menu;

        var item = Assert.Single(file.Items, entry => entry.Label == "Save");
        Assert.Equal(InputKey.S, item.Shortcut?.Key);
    }

    [Fact]
    public void The_status_bar_says_what_is_running_and_goes_quiet_when_nothing_is() {
        using var shell = Built();

        var task = shell.Tasks.Begin("Importing textures");
        task.Report(0.25f);

        shell.Tick(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(16));

        Assert.Contains("(1)", Label(shell), StringComparison.Ordinal);

        shell.Tasks.Complete(task);
        shell.Tick(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(16));

        Assert.DoesNotContain("(", Label(shell), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_task_becomes_a_notification_that_does_not_expire() {
        using var shell = Built();

        var task = shell.Tasks.Begin("Building content");
        shell.Tasks.Fail(task, new InvalidOperationException("no compiler"));

        shell.Tick(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(16));
        shell.Tick(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(16));

        var notice = Assert.Single(shell.Notifications.History);

        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Equal("no compiler", notice.Detail);
        Assert.Single(shell.Toasts.Live);
    }

    [Fact]
    public void Switching_language_relabels_the_menus() {
        using var shell = Built();

        try {
            Strings.Use(new StringCatalog("cs").Set(EditorStrings.MenuFile.Id, "Soubor"));
            Assert.Equal("Soubor", shell.MenuBar.Bar.Items[0].Label);
        } finally {
            Strings.Use(null);
        }

        Assert.Equal("File", shell.MenuBar.Bar.Items[0].Label);
    }

    [Fact]
    public void A_disposed_shell_is_not_relabelled_by_a_later_language_change() {
        var shell = Built();
        shell.Dispose();

        try {
            // The subscription is to a static event, so this is the failure that would otherwise
            // arrive in whatever test happened to switch language next.
            Strings.Use(new StringCatalog("cs").Set(EditorStrings.MenuFile.Id, "Soubor"));
        } finally {
            Strings.Use(null);
        }
    }

    static string? Label(EditorShell shell) {
        foreach (var child in shell.StatusBar.Children) {
            if (child is Button button) {
                return button.Label;
            }
        }

        return null;
    }
}
