// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's A4: two windows over one mechanism, and the Apply that is not a keystroke.</summary>
public class SettingsWindowTests {
    [Fact]
    public void Preferences_and_project_settings_are_the_same_control_over_different_stores() {
        using var fixture = EditorSession.Start();

        var user = fixture.Control<SettingsView>("preferences");
        var project = fixture.Control<SettingsView>("project-settings");

        Assert.NotSame(user, project);

        // Doc 20's A4 names General, Appearance, Scene View, Keybindings and Plugins by name; the
        // last two are pages that open the panel rather than a second copy of it.
        Assert.Equal(
            ["general", "appearance", "scene-view", "keybindings", "plugins"],
            user.Categories.Select(page => page.Id)
        );

        Assert.Equal(["project", "content"], project.Categories.Select(page => page.Id));
    }

    /// <summary>
    ///     ⚠ Doc 20: "a setting is not saved on every keystroke". Typing marks the window dirty and
    ///     writes nothing; Apply is what reaches the disk.
    /// </summary>
    [Fact]
    public void Editing_a_setting_writes_nothing_until_Apply() {
        using var fixture = EditorSession.Start();

        var view = fixture.Control<SettingsView>("project-settings");
        var file = fixture.Project.Settings.FileFor<ProjectInfoSettings>();

        Assert.False(view.IsDirty);
        Assert.True(view.Apply.Disabled);

        Type(view, "ProductName", "Moonshot");
        fixture.Settle();

        Assert.True(view.IsDirty);
        Assert.False(File.Exists(file));

        fixture.Click(view.Apply);

        Assert.False(view.IsDirty);
        Assert.True(File.Exists(file));
        Assert.Contains("Moonshot", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Doc 20's A4: the title bar answers "which project is this window", and the project's
    ///     own name for itself outranks the directory it happens to be in.
    /// </summary>
    [Fact]
    public void The_product_name_becomes_what_the_title_bar_says() {
        using var fixture = EditorSession.Start();

        Assert.Contains(fixture.Project.Name, fixture.Shell.Title, StringComparison.Ordinal);

        fixture.Project.Settings.Get<ProjectInfoSettings>().ProductName = "Moonshot";
        fixture.Settle();

        Assert.Contains("Moonshot", fixture.Shell.Title, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Doc 20's A4 is explicit: the scene-navigation preferences stay as ticked commands and
    ///     the window shows the <i>same</i> commands. Two writers to one setting is how a preferences
    ///     window and a menu tick come to disagree.
    /// </summary>
    [Fact]
    public void The_scene_view_page_drives_the_commands_rather_than_a_copy_of_their_state() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");

        var view = fixture.Control<SettingsView>("preferences");

        Assert.True(view.Select("scene-view"));
        fixture.Settle();

        var command = fixture.Shell.Commands["scene.zoom-to-cursor"]!;
        var toggle = Toggle(view.Pane, command.Title.Text);

        var before = command.IsChecked;

        fixture.Click(toggle);

        Assert.NotEqual(before, command.IsChecked);
        Assert.Equal(command.IsChecked, toggle.IsChecked);
    }

    [Fact]
    public void The_search_finds_a_page_by_a_setting_that_is_on_it() {
        using var fixture = EditorSession.Start();

        var view = fixture.Control<SettingsView>("preferences");

        view.Search.Value = "orbit";
        fixture.Settle();

        // ⚠ Doc 20 asks for "a search box over every setting in every category", so matching only
        // the five category names would answer the easy half. "Orbit" is on no heading.
        Assert.Equal("scene-view", view.Current);
    }

    [Fact]
    public void Resetting_a_page_puts_its_settings_back_and_marks_the_window_dirty() {
        using var fixture = EditorSession.Start();

        fixture.Project.Settings.Get<ProjectInfoSettings>().Company = "Rikarin";
        var view = fixture.Control<SettingsView>("project-settings");

        Assert.True(view.Select("project"));
        fixture.Settle();

        fixture.Click(view.ResetPage);

        Assert.True(view.IsDirty);
        Assert.Equal(string.Empty, fixture.Project.Settings.Get<ProjectInfoSettings>().Company);
    }

    /// <summary>The undo depth is a preference with a reader, which is the bar a shipped setting clears.</summary>
    [Fact]
    public void The_undo_depth_preference_reaches_every_stack() {
        using var scope = new Scratch();
        using var fixture = EditorSession.Start(new EditorSessionOptions { DataDirectory = scope.Directory });

        File.WriteAllText(Path.Combine(scope.Directory, EditorUserStore.PreferencesFile), "undoDepth: 9\n");
        fixture.Restart();

        Assert.Equal(9, fixture.Scene.Stack.Capacity);
        Assert.Equal(9, fixture.Project.GlobalStack.Capacity);
    }

    /// <summary>Types into the inspector row for a member, the way a person does.</summary>
    static void Type(SettingsView view, string member, string value) {
        foreach (var row in Descendants(view.Pane).OfType<InspectorRow>()) {
            if (string.Equals(row.Field.Member.Name, member, StringComparison.Ordinal)) {
                row.Field.Write(value);
                return;
            }
        }

        throw new InvalidOperationException($"no row for '{member}'");
    }

    static Vixen.Ui.Controls.ToggleButton Toggle(UiElement pane, string label) =>
        Descendants(pane)
            .OfType<Vixen.Ui.Controls.ToggleButton>()
            .FirstOrDefault(button => button.Label == label)
        ?? throw new InvalidOperationException($"no toggle labelled '{label}'");

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

}
