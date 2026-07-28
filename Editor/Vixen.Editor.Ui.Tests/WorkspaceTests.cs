// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The panel registry, the presets, and what survives a save.</summary>
public class WorkspaceTests : IDisposable {
    readonly UiDocument document = new(1280f, 800f);
    readonly DockingWorkspace workspace;

    public WorkspaceTests() {
        ControlTheme.Install(document);
        AdvancedTheme.Install(document);

        workspace = new DockingWorkspace(document.Root);
    }

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    static StringId Title(string text) => new("test.panel." + text, text);

    void Three() {
        workspace.Register("hierarchy", Title("Hierarchy"), panel => panel.Add<TextBlock>().Text = "tree");
        workspace.Register("scene", Title("Scene"), panel => panel.Add<TextBlock>().Text = "viewport");
        workspace.Register("inspector", Title("Inspector"), panel => panel.Add<TextBlock>().Text = "grid");
    }

    [Fact]
    public void A_panel_is_built_the_first_time_it_is_shown_and_not_before() {
        var built = 0;
        workspace.Register("console", Title("Console"), _ => built++);

        Assert.Equal(0, built);
        Assert.False(workspace.IsOpen("console"));

        Assert.NotNull(workspace.Open("console"));
        Assert.Equal(1, built);
        Assert.True(workspace.IsOpen("console"));

        // Already open: brought to the front rather than rebuilt, which is what "show me the
        // console" means when the console is behind another tab.
        workspace.Open("console");
        Assert.Equal(1, built);
    }

    [Fact]
    public void An_unregistered_id_opens_nothing() => Assert.Null(workspace.Open("nope"));

    [Fact]
    public void Toggling_closes_and_reopens() {
        var built = 0;
        workspace.Register("console", Title("Console"), _ => built++);

        workspace.Toggle("console");
        Assert.True(workspace.IsOpen("console"));

        workspace.Toggle("console");
        Assert.False(workspace.IsOpen("console"));

        // The elements went with it, so reopening runs the factory again — which is what closing a
        // panel has to mean if a scene view's render target is ever to be released.
        workspace.Toggle("console");
        Assert.Equal(2, built);
    }

    [Fact]
    public void A_preset_opens_every_panel_it_names() {
        Three();

        workspace.AddPreset(
            "Default",
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"])
        );

        Assert.True(workspace.Apply("Default"));

        Assert.True(workspace.IsOpen("hierarchy"));
        Assert.True(workspace.IsOpen("scene"));
        Assert.True(workspace.IsOpen("inspector"));

        var split = Assert.IsType<DockSplitNode>(workspace.Host.Layout.Root);
        Assert.Equal("hierarchy", Assert.IsType<DockGroupNode>(split.First).Panels[0]);
    }

    [Fact]
    public void Reset_goes_back_to_the_preset_rather_than_to_what_was_dragged() {
        Three();

        workspace.AddPreset("Default", () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"]));
        workspace.Apply("Default");

        var preset = Assert.IsType<DockSplitNode>(workspace.Host.Layout.Root);
        preset.Ratio = 0.9f;

        workspace.Reset();

        // A preset handed out as an object would be the object the drag just edited.
        Assert.Equal(0.2f, Assert.IsType<DockSplitNode>(workspace.Host.Layout.Root).Ratio, 0.001f);
    }

    [Fact]
    public void An_arrangement_round_trips_and_brings_its_panels_back() {
        Three();

        workspace.AddPreset("Default", () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"]));
        workspace.Apply("Default");

        var saved = workspace.Save();

        var second = new DockingWorkspace(document.Root);
        second.Register("hierarchy", Title("Hierarchy"), _ => { });
        second.Register("scene", Title("Scene"), _ => { });
        second.Register("inspector", Title("Inspector"), _ => { });
        second.AddPreset("Default", () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"]));

        second.Load(saved);

        Assert.Equal(saved, second.Save());
        Assert.True(second.IsOpen("inspector"));
    }

    [Fact]
    public void An_arrangement_naming_nothing_this_editor_knows_falls_back_to_the_default() {
        Three();
        workspace.AddPreset("Default", () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"]));

        workspace.Load(LayoutPresets.Single("some.plugin.panel").Save());

        // An empty window is worse than a wrong one.
        Assert.True(workspace.IsOpen("scene"));
    }

    [Fact]
    public void An_arrangement_naming_a_panel_that_is_gone_keeps_the_rest() {
        Three();

        var stale = new DockLayout {
            Root = new DockSplitNode(
                Orientation.Horizontal,
                new DockGroupNode("hierarchy"),
                new DockGroupNode("plugin.gone"),
                0.3f
            )
        };

        workspace.AddPreset("Default", () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"]));
        workspace.Load(stale.Save());

        Assert.True(workspace.IsOpen("hierarchy"));
        Assert.False(workspace.IsOpen("plugin.gone"));
    }

    [Fact]
    public void Registering_a_panel_twice_throws() {
        workspace.Register("scene", Title("Scene"), _ => { });
        Assert.Throws<ArgumentException>(() => workspace.Register("scene", Title("Scene Again"), _ => { }));
    }

    [Fact]
    public void The_standard_preset_puts_the_console_under_the_middle_rather_than_the_window() {
        var layout = LayoutPresets.Standard(["browser"], ["scene"], ["inspector"], ["console"]);

        var outer = Assert.IsType<DockSplitNode>(layout.Root);
        Assert.Equal("browser", Assert.IsType<DockGroupNode>(outer.First).Panels[0]);

        // A log along the whole bottom edge pushes the inspector up and leaves it half the height
        // it needs.
        var centre = Assert.IsType<DockSplitNode>(outer.Second);
        Assert.Equal("inspector", Assert.IsType<DockGroupNode>(centre.Second).Panels[0]);

        var stack = Assert.IsType<DockSplitNode>(centre.First);
        Assert.Equal(Orientation.Vertical, stack.Orientation);
        Assert.Equal("console", Assert.IsType<DockGroupNode>(stack.Second).Panels[0]);
    }

    [Fact]
    public void A_preset_with_nothing_in_a_column_does_not_leave_an_empty_group() {
        var layout = LayoutPresets.Standard([], ["scene"], []);

        // An empty group is a tab strip taking up half the window with nothing in it.
        Assert.Equal("scene", Assert.IsType<DockGroupNode>(layout.Root).Panels[0]);
    }

    [Fact]
    public void A_restored_arrangement_keeps_the_tab_that_was_in_front() {
        Three();
        workspace.Register("project", Title("Project"), _ => { });

        workspace.AddPreset(
            "Default",
            () => LayoutPresets.Standard(["hierarchy", "project"], ["scene"], ["inspector"])
        );

        workspace.Apply("Default");

        var browser = workspace.Host.Layout.Groups()[0];
        browser.Selected = 0;

        var second = new DockingWorkspace(document.Root);
        second.Register("hierarchy", Title("Hierarchy"), _ => { });
        second.Register("project", Title("Project"), _ => { });
        second.Register("scene", Title("Scene"), _ => { });
        second.Register("inspector", Title("Inspector"), _ => { });
        second.AddPreset("Default", () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"]));

        second.Load(workspace.Save());

        // Opening a panel brings it to the front, so restoring a two-tab group would otherwise
        // always come back showing whichever panel was built last.
        Assert.Equal(0, second.Host.Layout.Groups()[0].Selected);
    }
}
