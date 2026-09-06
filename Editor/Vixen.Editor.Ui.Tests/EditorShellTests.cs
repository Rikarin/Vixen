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

        // ⚠ The chord this machine's user presses — ⌘K on a Mac — rather than the one the keymap
        // spells it with. A test that pressed Ctrl literally would pass on a PC and assert the
        // opposite of the intended behaviour on a Mac.
        //
        // ⚠ And asked of the keymap rather than written out. This test named `Ctrl+P` and had to be
        // edited when the palette moved to `Ctrl+K` — which means it was asserting the chord rather
        // than the claim, and the claim is "whatever the palette is bound to opens it".
        var chord = shell.Keys.ChordFor("view.palette").ForPlatform();

        Assert.True(chord.IsBound, "the palette has no binding, so the shortcut cannot be pressed");

        shell.Document.Dispatch(
            new KeyEvent { Key = chord.Key, Action = KeyAction.Pressed, Modifiers = chord.Modifiers }
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

    /// <summary>
    ///     ⚠ <b>The chrome is a column and its order is its children's order</b>, so where the menu
    ///     bar sits among them is where it is on the screen. Registering a command rebuilds the bar,
    ///     and a rebuild that appended would put it after the workspace and the status bar — a menu
    ///     bar along the bottom edge of the window, arriving on whichever frame the application
    ///     registered its last command.
    /// </summary>
    [Fact]
    public void The_menu_bar_stays_at_the_top_of_the_chrome_however_often_it_is_rebuilt() {
        using var shell = Built();

        var chrome = shell.MenuBar.Bar.Parent!;
        Assert.Equal(0, shell.MenuBar.Bar.IndexInParent);

        shell.Commands.Add("file.save", Title("Save"), () => { });
        shell.Toolbar.Show("view.palette", null, "view.toggle-theme");
        shell.Commands.Add("file.open-project", Title("Open Project"), () => { });

        Assert.Equal(0, shell.MenuBar.Bar.IndexInParent);

        // ⚠ Two, not one: doc 20's frame is menu bar → mode bar → toolbar, and the mode bar's host is
        // in the chrome whether or not anything has registered a mode — it is hidden rather than
        // absent, so that registering the first mode does not reorder everything below it.
        Assert.Equal(2, shell.Toolbar.Strip.IndexInParent);

        // And the two strips that were there before either of them is still behind both.
        Assert.True(shell.StatusBar.IndexInParent > shell.Toolbar.Strip.IndexInParent);
        Assert.Equal(shell.StatusBar.IndexInParent, chrome.Children.Count - 1);
    }

    /// <summary>The two strips are the controls that were extracted from them, not lookalikes.</summary>
    /// <remarks>
    ///     ⚠ <b>What the editor was missing here is behaviour, and none of it is visible.</b> The
    ///     hand-drawn strip was a row of buttons that were each a tab stop — fifteen presses between
    ///     a keyboard user and the document — and reported no role at all, so the toolbar a screen
    ///     reader was told about was an anonymous group; the status bar was a <c>UiElement</c> with a
    ///     class, which is to say a live region that is not live. Both of those are assertions a
    ///     screenshot cannot make and a green suite did not make either, which is why they are made
    ///     here.
    /// </remarks>
    [Fact]
    public void The_toolbar_is_one_tab_stop_and_both_strips_report_what_they_are() {
        using var shell = Built();

        shell.Commands.Add("file.new", Title("New"), () => { });
        shell.Commands.Add("file.open", Title("Open"), () => { });
        shell.Commands.Add("file.save", Title("Save"), () => { });
        shell.Toolbar.Show("file.new", "file.open", null, "file.save");

        Assert.Equal(AccessibleRole.Toolbar, shell.Toolbar.Strip.Role);
        Assert.Equal(AccessibleRole.Status, shell.StatusBar.Role);

        var items = shell.Toolbar.Strip.Items;
        Assert.Equal(3, items.Count);

        // ⚠ One zero and the rest at −1. This is the assertion the old strip could not have passed
        // and the only one that distinguishes a toolbar from a row of buttons.
        Assert.Equal(1, items.Count(item => item.TabIndex == 0));
        Assert.Same(items[0], shell.Toolbar.Strip.Active);
        Assert.All(items.Skip(1), item => Assert.Equal(-1, item.TabIndex));

        // ⚠ The separator is not one of them: `Items` is the focusables, so Right on the second
        // button reaches the third rather than stopping on a hairline.
        Assert.DoesNotContain(items, item => item is Separator);
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

    /// <summary>
    ///     ⚠ <b>This was called <c>…that_does_not_expire</c> and asserted a toast still on screen
    ///     fifty-nine seconds after a twelve-second one was shown.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>NotificationCenter</c>'s own remarks say the opposite in as many words — "an error
    ///         stays longer and <i>still goes away</i>", with the history as the place somebody goes
    ///         back to it, and a paragraph about the corner of red rectangles that
    ///         <c>TimeSpan.MaxValue</c> produced. So the old assertion contradicted the design it was
    ///         written beside, and it passed anyway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Because <c>EditorShell.Tick</c> called <c>Document.Gestures.Tick</c> rather than
    ///         <c>Document.Tick</c>, so <c>UiDocument.Now</c> was zero for the life of the
    ///         editor.</b> <c>ToastHost.Show</c> stamps a toast with that clock, and
    ///         <c>ToastHost.Tick</c> treats a zero stamp as "never stamped" and re-stamps it with the
    ///         current time — a sentinel that is exactly right for a document whose clock has not
    ///         started and exactly wrong for one whose clock never will. Every toast in the editor
    ///         was therefore given a fresh lease on the first frame after it appeared, and this test
    ///         had no frame after its last one. It was measuring a frozen clock.
    ///     </para>
    ///     <para>
    ///         What it checks now is what the design says: an error outlasts an ordinary message and
    ///         then goes, and the history keeps it either way. Both halves are needed — the first
    ///         alone passes for a toast that never expires, the second alone for one that expires
    ///         immediately.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_failed_task_becomes_a_notification_that_outlasts_an_ordinary_one_and_then_goes() {
        using var shell = Built();

        var task = shell.Tasks.Begin("Building content");
        shell.Tasks.Fail(task, new InvalidOperationException("no compiler"));

        shell.Tick(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(16));

        var notice = Assert.Single(shell.Notifications.History);

        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Equal("no compiler", notice.Detail);

        // Past `Duration`, which is four seconds, and well short of `ErrorDuration`, which is twelve.
        shell.Tick(TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(16));
        Assert.Single(shell.Toasts.Live);

        // Past `ErrorDuration`. The toast goes; the history entry does not.
        shell.Tick(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(16));
        Assert.Empty(shell.Toasts.Live);
        Assert.Single(shell.Notifications.History);
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

    /// <remarks>
    ///     ⚠ <c>Trailing</c> rather than <c>Children</c>: the strip is a <c>StatusBar</c>, whose
    ///     message is a part of its own and whose cells are in the trailing part beside it.
    /// </remarks>
    static string? Label(EditorShell shell) {
        foreach (var child in shell.StatusBar.Trailing.Children) {
            if (child is Button button) {
                return button.Label;
            }
        }

        return null;
    }
}
