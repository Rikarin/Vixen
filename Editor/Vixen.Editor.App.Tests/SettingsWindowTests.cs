// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
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

    /// <summary>
    ///     ⚠ The page's toggles carry no read-back of their own any more (#1140), so this is the
    ///     assertion that the binding reaches the registry at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A click cannot show this and the click test above does not.</b> A command that
    ///         follows agrees with the toggle's own guess, so <c>IsChecked</c> is right after a click
    ///         whether the binding wrote it or the flip did. What only the binding can do is follow a
    ///         change nobody made through this page — the same command run from a menu, a keystroke
    ///         or the palette, which is the case doc 20's "two writers to one setting" rule exists
    ///         for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two resolutions are not the same walk and that is what had to be checked.</b>
    ///         <c>EditorSettingsPanels.Toggles</c> reads <see cref="Vixen.Ui.Controls.CommandRegistry" />
    ///         directly to draw the label, while <c>ButtonBase.RefreshCommand</c> goes through
    ///         <c>CommandRoute.Resolve(Document, id)</c> and reaches that registry only because
    ///         <c>EditorShell</c> installs it as the document's <c>ApplicationCommandResponder</c>.
    ///         Deleting the read-back on the strength of #1046 alone would have rested on the two
    ///         meeting; this is the test that says they do.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_settings_toggle_follows_its_command_when_something_else_runs_it() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");

        var view = fixture.Control<SettingsView>("preferences");

        Assert.True(view.Select("scene-view"));
        fixture.Settle();

        var command = fixture.Shell.Commands["scene.zoom-to-cursor"]!;
        var toggle = Toggle(view.Pane, command.Title.Text);

        Assert.Equal(command.IsChecked, toggle.IsChecked);

        // Run from outside the window, the way a keystroke or the palette would.
        var before = command.IsChecked;

        fixture.Run("scene.zoom-to-cursor");
        fixture.Document.InvalidateCommands();
        fixture.Settle();

        Assert.NotEqual(before, command.IsChecked);
        Assert.Equal(command.IsChecked, toggle.IsChecked);
    }

    /// <summary>
    ///     ⚠ Doc 20: a command is entitled to refuse, and the toggle then stays where it was. With
    ///     the page's read-back gone (#1140) this is <see cref="Vixen.Ui.Controls.ToggleBase" />'s
    ///     job, and nothing in the editor asserted it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A command bound to nothing rather than one of the page's five, because none of the
    ///     five can refuse.</b> The scene toggles all write the flag their own predicate reads, so a
    ///     click on one is always followed; the refusal doc 20 asks about is a command whose check
    ///     state is decided elsewhere — a "wireframe" a 2D viewport will not turn on. This is that
    ///     command, drawn as a bound toggle in the preferences pane so that it resolves through the
    ///     same route, the same document and the same registry the page's own toggles do.
    /// </remarks>
    [Fact]
    public void A_bound_toggle_the_command_refused_to_follow_goes_back() {
        using var fixture = EditorSession.Start();

        var view = fixture.Control<SettingsView>("preferences");

        Assert.True(view.Select("appearance"));
        fixture.Settle();

        var ran = 0;

        fixture.Shell.Commands.Add(
            new EditorCommand(
                "test.refuses",
                new StringId("editor.command.test.refuses", "Refuses"),
                () => ran++
            ) {
                // Runs, changes nothing, and keeps saying no — which is exactly a command whose
                // check state is not the click's to decide.
                Checked = () => false
            }
        );

        var toggle = view.Pane.Add<ToggleButton>();

        toggle.Label = "Refuses";
        toggle.Command = "test.refuses";
        fixture.Settle();

        Assert.False(toggle.Disabled);
        Assert.False(toggle.IsChecked);

        fixture.Click(toggle);
        fixture.Settle();

        // It ran, so this is a refusal rather than a command that never got the click.
        Assert.Equal(1, ran);
        Assert.False(toggle.IsChecked);
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
