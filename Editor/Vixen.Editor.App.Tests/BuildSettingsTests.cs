// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Debugger;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's B7: build settings, Build and Run, and what Deploy means for a device.</summary>
/// <remarks>
///     ⚠ <b>Nothing here runs <c>dotnet publish</c>.</b> What a build <i>does</i> is
///     <see cref="PlayerBuild" />'s and is tested where the decisions are; what an editor adds is a
///     window, a menu that is a second view of the same setting, and a refusal that arrives before a
///     minute is spent — and all three are assertable in milliseconds. A suite that shelled out would
///     be one nobody could run on a machine without the SDK's workloads.
/// </remarks>
public class BuildSettingsTests {
    [Fact]
    public void The_build_settings_panel_opens_and_survives_being_closed_and_reopened() {
        using var session = EditorSession.Start();

        session.Open("build-settings");
        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == "build-settings");

        session.Close("build-settings");
        session.Frames(2);

        session.Open("build-settings");
        session.Frames(2);

        Assert.NotEmpty(session.Panel("build-settings").Children);
    }

    /// <summary>
    ///     ⚠ Doc 20's rule is that a verb is either implemented or <i>visibly</i> not, and these two
    ///     were declared-and-disabled with a sentence about milestone E6 until this one.
    /// </summary>
    [Theory]
    [InlineData("build.settings")]
    [InlineData("build.run")]
    public void The_two_build_verbs_are_no_longer_declared_and_disabled(string id) {
        using var session = EditorSession.Start();

        Assert.False(session.Shell.Commands[id]!.IsUnavailable, $"'{id}' is still declared-and-disabled.");
    }

    /// <summary>
    ///     ⚠ Doc 20's A4: one setting, two views. The menu tick and the window's picker write the
    ///     same field, because a menu that remembered a target of its own is how the two come to
    ///     disagree.
    /// </summary>
    [Fact]
    public void Choosing_a_target_on_the_menu_writes_the_project_setting() {
        using var session = EditorSession.Start();

        var settings = session.Project.Settings.Get<PlayerBuildSettings>();
        var file = session.Project.Settings.FileFor<PlayerBuildSettings>();

        Assert.Equal(string.Empty, settings.Target);

        session.Run("build.target-linux");

        Assert.Equal("Linux", settings.Target);

        // ⚠ Written, not merely marked. A menu tick that forgot itself on the next launch is a tick
        // that lies, which is why this surface has no Apply — see BuildSettingsView.
        Assert.True(File.Exists(file));
        Assert.Contains("Linux", File.ReadAllText(file), StringComparison.Ordinal);

        // And back to the machine the editor is on, which is the seventh line Part C does not name
        // and the one a fresh project is on.
        session.Run("build.target-host");
        Assert.Equal(string.Empty, settings.Target);
    }

    /// <summary>Exactly one target is ticked at a time, which is what makes it one choice.</summary>
    [Fact]
    public void The_target_menu_is_one_choice_rather_than_seven_toggles() {
        using var session = EditorSession.Start();

        session.Run("build.target-macos");

        var ticked = EditorApplication.BuildIds.Targets
            .Where(id => session.Shell.Commands[id]!.IsChecked)
            .ToList();

        Assert.Equal(["build.target-macos"], ticked);

        foreach (var id in EditorApplication.BuildIds.Targets) {
            Assert.Equal("build.target", session.Shell.Commands[id]!.RadioGroup);
        }
    }

    /// <summary>And the configuration submenu is the four variants doc 17 names, as one choice.</summary>
    [Fact]
    public void The_configuration_menu_is_the_four_variants() {
        using var session = EditorSession.Start();

        Assert.Equal(4, EditorApplication.BuildIds.Variants.Length);

        session.Run("build.configuration-release");

        Assert.Equal("Release", session.Project.Settings.Get<PlayerBuildSettings>().Variant);

        var ticked = EditorApplication.BuildIds.Variants
            .Where(id => session.Shell.Commands[id]!.IsChecked)
            .ToList();

        Assert.Equal(["build.configuration-release"], ticked);
    }

    /// <summary>
    ///     ⚠ Web is on the menu because it is a platform this engine ships on, and greyed because it
    ///     is not one <c>dotnet publish</c> produces an application from. Absent would read as an
    ///     engine that has never heard of it.
    /// </summary>
    [Fact]
    public void Web_is_on_the_menu_and_greyed_with_the_reason() {
        using var session = EditorSession.Start();

        var web = session.Shell.Commands["build.target-web"];

        Assert.NotNull(web);
        Assert.True(web.IsUnavailable);
        Assert.False(session.CanRun("build.target-web"));
    }

    /// <summary>
    ///     ⚠ The gate is checked before anything is started, and the sentence names the thing that is
    ///     missing. A scratch project has no <c>.csproj</c>, which is the ordinary first-run state and
    ///     the one where a minute in <c>dotnet</c> would be a minute wasted.
    /// </summary>
    [Fact]
    public void Build_and_Run_is_refused_until_the_project_has_something_to_publish() {
        using var session = EditorSession.Start();

        Assert.False(session.CanRun("build.run"));

        var view = session.Control<BuildSettingsView>("build-settings");

        Assert.True(view.BuildButton.Disabled);
        Assert.Contains(".csproj", view.Status.Text, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(session.ProjectRoot, "Game.csproj"), "<Project />");
        session.Frames(2);

        Assert.True(session.CanRun("build.run"));

        // ⚠ The window is asked again rather than told, which is what makes it right after a change
        // nothing in the editor made — a project file arriving from a checkout, say.
        view.Rebuild();

        Assert.False(view.BuildButton.Disabled);
        Assert.DoesNotContain(".csproj", view.Status.Text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The panel is derived from state that changes for reasons that are not edits to it, and
    ///     nothing was asking it again — so it stayed as it was when it opened. The menu never had
    ///     this because <c>MenuPresenter</c> asks <c>CanExecute</c> every time it draws.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Driven from the menu rather than from a build, deliberately.</b> The other thing that
    ///     changes the answer is a task starting and stopping, and asserting on that means racing a
    ///     pool thread — a content build over an empty project can finish between two frames, so the
    ///     transition a test watched for might never be observable. The menu is the same wiring with
    ///     no timing in it: one setting, two views, and the one that is not being touched has to
    ///     follow.
    /// </remarks>
    [Fact]
    public void A_target_chosen_on_the_menu_reaches_a_panel_that_is_already_open() {
        using var session = EditorSession.Start();

        File.WriteAllText(Path.Combine(session.ProjectRoot, "Game.csproj"), "<Project />");

        var view = session.Control<BuildSettingsView>("build-settings");

        view.Rebuild();

        Assert.False(view.BuildButton.Disabled);
        Assert.False(view.RunButton.Disabled);

        session.Run("build.target-android");

        // The picker followed the menu, rather than the two disagreeing about one field.
        Assert.Equal("Android", view.TargetPicker.Value);

        // And so did the buttons: an APK is buildable from here and is not something this machine
        // can launch, which is the distinction `TargetShape.Runnable` exists to draw.
        Assert.False(view.BuildButton.Disabled);
        Assert.True(view.RunButton.Disabled);
    }

    /// <summary>
    ///     The scenes-in-build list: what is offered, what order means, and what a build would say
    ///     about an entry that no longer resolves.
    /// </summary>
    [Fact]
    public void The_scene_list_offers_the_project_and_marks_the_first_entry() {
        using var session = EditorSession.Start();

        var view = session.Control<BuildSettingsView>("build-settings");
        var settings = session.Project.Settings.Get<PlayerBuildSettings>();

        // The scene every project starts with, which `EditorApplication` writes and rescans for.
        var scene = Assert.Single(view.ScenePicker.Options).Value;

        Assert.NotNull(scene);
        Assert.EndsWith(".vxscene", scene, StringComparison.Ordinal);

        view.ScenePicker.Value = scene;
        session.Click(view.AddScene);

        Assert.Equal([scene], settings.Scenes);

        // Offered once: a scene already in the build is not in the picker.
        Assert.Empty(view.ScenePicker.Options);
        Assert.True(view.AddScene.Disabled);

        Assert.Equal(1, view.Scenes.RowCount);

        // ⚠ Persisted like every other field on this window, and for the same reason: Build reads
        // the list, so an edit behind an Apply would build something other than what is on screen.
        Assert.Contains(scene, File.ReadAllText(session.Project.Settings.FileFor<PlayerBuildSettings>()), StringComparison.Ordinal);

        // And an entry that names nothing says so, which is the one thing this list can be wrong
        // about — somebody else's rename arriving in a checkout.
        settings.Scenes.Add("Assets/Scenes/Gone.vxscene");
        view.Rebuild();

        Assert.Equal(2, view.Scenes.RowCount);
        Assert.Contains(view.Scenes.Items.Cast<object>(), row => row.ToString()!.Contains("Missing", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ Doc 20's B7 asks Deploy to list, deploy, launch and attach. What this asserts is the part
    ///     that is a decision: which devices the editor can reach, and that the ones it cannot say
    ///     which tool is missing rather than failing when pressed.
    /// </summary>
    [Fact]
    public void Deploy_is_offered_for_this_machine_and_refused_with_a_reason_for_the_rest() {
        using var session = EditorSession.Start();

        var view = session.Control<DeviceManagerView>("devices");

        Assert.NotNull(view.CanDeploy);

        var phone = new DeviceEntry("phone", "A Phone", DeviceKind.Mobile, "Android 14");
        var refusal = view.CanDeploy(phone);

        Assert.NotNull(refusal);
        Assert.Contains("adb", refusal, StringComparison.Ordinal);

        // This machine's refusal is the *build's* — there is nothing to publish yet — rather than
        // one about the device, which is the whole point of the two being one method.
        var local = Assert.Single(view.Devices.Items.Cast<DeviceEntry>(), device => device.Kind is DeviceKind.Local);

        Assert.Contains(".csproj", view.CanDeploy(local)!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <c>Deploying</c> and <c>Running</c> are states no provider can report, so a manager that
    ///     could not be told about them would be a panel whose row read Available while a build was
    ///     on its way.
    /// </summary>
    [Fact]
    public void A_device_can_be_told_what_it_is_doing() {
        var devices = new DeviceManager();

        devices.Add(new LocalDeviceProvider());
        devices.Discover();

        devices.Selected = devices.Devices[0];

        Assert.True(devices.Mark("local", DeviceStatus.Deploying));
        Assert.Equal(DeviceStatus.Deploying, devices.Devices[0].Status);

        // The selection follows the identity rather than the record, which every other path in this
        // class already does — a row that deselected itself the moment a deploy started would be a
        // row nobody could then attach to.
        Assert.Equal(DeviceStatus.Deploying, devices.Selected?.Status);

        Assert.False(devices.Mark("phone", DeviceStatus.Running));
    }

    /// <summary>
    ///     Doc 36 § F7 wave 2: the panel is <c>BuildSettingsView.vxml</c>, and this is what the port
    ///     had to leave alone.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The order is the assertion.</b> A markup port's one real risk is a tree that is the
    ///     same set of elements in a different arrangement — every property test above would still
    ///     pass and the panel would be laid out wrongly, because <c>EditorTheme.vcss</c> styles these
    ///     by tag and <c>build-actions</c>'s top border only reads as a footer while it is last but
    ///     one. The seven tags in this order are the file.
    /// </remarks>
    [Fact]
    public void The_panel_is_the_seven_parts_the_markup_declares_in_the_order_it_declares_them() {
        using var session = EditorSession.Start();

        var view = session.Control<BuildSettingsView>("build-settings");

        Assert.Equal(
            ["build-form", "build-note", "build-heading", "build-scene-bar", "data-grid", "build-actions", "build-status"],
            view.Children.Select(child => child.Tag)
        );

        // Each `ref` in the file names one of them, and a `ref` that never got assigned is a null
        // the panel would only fault on when somebody pressed something.
        Assert.Same(view.Children[0], view.Form);
        Assert.Same(view.Children[1], view.Note);
        Assert.Same(view.Children[2], view.Heading);
        Assert.Same(view.Children[4], view.Scenes);
        Assert.Same(view.Children[6], view.Status);

        // ⚠ The labelled rows are the helper the port deleted: `Field<T>` built a `build-row`, put a
        // `TextBlock` in it and returned the editor. Written as tags that shape is the file, so what
        // is worth pinning is that it is still the shape.
        var row = Assert.IsType<UiElement>(view.Form.Children[0], exactMatch: false);

        Assert.Equal("build-row", row.Tag);
        Assert.Equal("Target", row.Children[0].Text);
        Assert.Same(view.TargetPicker, row.Children[1]);
    }

    /// <summary>
    ///     ⚠ <b>Why nothing on this panel is bound</b>, pinned so that a later wave reaching for a
    ///     <c>Signal&lt;T&gt;</c> here fails rather than quietly regressing.
    /// </summary>
    /// <remarks>
    ///     An effect runs at the frame's flush and not at the write — <c>EffectScheduler</c>'s whole
    ///     argument — so a bound <c>Disabled</c> is right on the next frame. This panel is read back
    ///     on the line after the call, with no frame in between, which is the shape a binding cannot
    ///     have.
    /// </remarks>
    [Fact]
    public void The_buttons_are_right_on_the_line_after_Rebuild_rather_than_on_the_next_frame() {
        using var session = EditorSession.Start();

        var view = session.Control<BuildSettingsView>("build-settings");
        var settings = session.Project.Settings.Get<PlayerBuildSettings>();

        Assert.True(view.RemoveScene.Disabled);

        settings.Scenes.Add("Assets/Scenes/Main.vxscene");
        view.Rebuild();
        view.Scenes.Select(0);

        // No `session.Frames(...)` here, deliberately: that is the whole point of the assertion.
        Assert.False(view.RemoveScene.Disabled);
        Assert.True(view.MoveUp.Disabled);
        Assert.True(view.MoveDown.Disabled);
    }
}
