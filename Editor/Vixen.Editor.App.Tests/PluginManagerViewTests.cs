// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Blockout;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Terrain;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Editor.Water;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     The plugin manager doc 36 § F7 wave 1b moved into <c>.vxml</c>, asserted through the elements
///     it built.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The reactive model here is a snapshot, not a live list, and that was a finding.</b>
///         <c>PluginHost.Changed</c> is already raised by every path that changes anything the panel
///         reads, the grid is filled by <c>SetItems</c> which no attribute can bind, and what is left
///         on screen is a derived sentence about the one selected plugin — so <c>PluginNote</c> is one
///         value written by one reading, exactly as <c>PrefabBanner</c> is. <c>PluginHost</c> itself
///         needed no change, which is what these tests are here to hold.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-verified:</b> <c>note</c> as a plain <c>PluginNote</c> field rather than a
///         <c>Signal</c> fails <see cref="Choosing_a_row_ungreys_the_verbs_and_names_the_plugin" />
///         and <c>MilestoneE3Tests.The_plugin_manager_is_a_panel_with_the_three_verbs_on_it</c>, and
///         nothing else. The second one is the interesting half: a host calls <c>Show</c> after
///         <c>panel.Add&lt;PluginManagerView&gt;()</c> has returned, so even the panel's <i>first</i>
///         reading arrives after its bindings have run once.
///     </para>
/// </remarks>
public class PluginManagerViewTests {
    /// <summary>The tag the stylesheet names, and the three parts under it.</summary>
    [Fact]
    public void The_panel_answers_to_the_tag_its_stylesheet_names() {
        using var fixture = EditorSession.Start();

        var view = Open(fixture);

        Assert.Equal("plugin-manager", view.Tag);
        Assert.Equal("plugin-toolbar", view.Children[0].Tag);
        Assert.Equal("plugin-detail", view.Children[^1].Tag);
    }

    /// <summary>
    ///     ⚠ The assertion that <c>note</c> is a signal. Nothing calls a <c>Restate</c> from outside
    ///     the panel any more: a click on a row raises <c>DataGrid.SelectionChanged</c>, the panel
    ///     takes one reading of the host, and three elements follow it.
    /// </summary>
    [Fact]
    public void Choosing_a_row_ungreys_the_verbs_and_names_the_plugin() {
        using var fixture = EditorSession.Start();

        var view = Open(fixture);

        Assert.True(view.Toggle.Disabled);
        Assert.True(view.Reload.Disabled);
        Assert.Equal(EditorStrings.PluginsNone.Text, Shown(view.Detail));

        view.Grid.Select(0);
        fixture.Settle();

        var plugin = Assert.IsType<LoadedPlugin>(view.Grid.Items[0]);

        Assert.NotNull(view.Selected);
        Assert.False(view.Toggle.Disabled);
        Assert.False(view.Reload.Disabled);

        // The line is now about that plugin rather than about the absence of any. It can legitimately
        // be empty — a built-in module has neither a description nor a directory, which is what the
        // C# put there too — so what is asserted is that the reading moved, not that it says a lot.
        Assert.NotEqual(EditorStrings.PluginsNone.Text, Shown(view.Detail));

        // And the switch names what pressing it would do, which is the other half of the same reading.
        Assert.Equal(EditorStrings.PluginsDisable.Text, view.Toggle.Label);
        Assert.Equal(plugin.Failure is not null, view.Detail.HasClass("failed"));
    }

    /// <summary>The filter narrows the grid and the panel keeps working after it does.</summary>
    [Fact]
    public void The_filter_narrows_the_grid() {
        using var fixture = EditorSession.Start();

        var view = Open(fixture);
        var all = view.Grid.Items.Count;

        Assert.True(all > 0, "a session with no plugins in it still lists the editor's own modules");

        view.Search.Value = "there-is-no-plugin-called-this";
        fixture.Settle();

        Assert.Empty(view.Grid.Items);

        view.Search.Value = string.Empty;
        fixture.Settle();

        Assert.Equal(all, view.Grid.Items.Count);
    }

    /// <summary>
    ///     #1288, by effect: Water switched off in the manager is absent from the next session's mode
    ///     bar, still listed so it can be switched back on, and back in its place when it is.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Red before the fix, twice over.</b> The built-ins were activated inside
    ///     <c>Commands()</c>, before the user's list was read, and <c>PluginHost.Activate</c> never
    ///     consulted it anyway — so the restarted editor had Water on the bar whatever the file said.
    /// </remarks>
    [Fact]
    public void A_built_in_switched_off_in_the_manager_stays_off_in_the_next_session() {
        using var fixture = EditorSession.Start();

        Toggle(fixture, WaterModule.ModuleId);

        Assert.True(fixture.Plugins.IsSuppressed(WaterModule.ModuleId));

        fixture.Restart();

        Assert.DoesNotContain(WaterMode.ModeId, fixture.Shell.Modes.Modes.Select(mode => mode.Id));
        Assert.Equal(PluginState.Disabled, fixture.Plugins.Find(WaterModule.ModuleId)!.State);

        // And the manager can switch it back on, which it could not do for a module it never listed.
        Toggle(fixture, WaterModule.ModuleId);

        Assert.Equal(PluginState.Active, fixture.Plugins.Find(WaterModule.ModuleId)!.State);
        Assert.Equal(
            [SelectMode.ModeId, BlockoutMode.ModeId, TerrainMode.ModeId, FoliageMode.ModeId, WaterMode.ModeId],
            fixture.Shell.Modes.Modes.Select(mode => mode.Id)
        );
    }

    /// <summary>
    ///     ⚠ Every built-in can now actually be off at start-up, which no session had ever been in —
    ///     two of them (<c>AssetEditors</c>, <c>Diagnostics</c>) are held by typed fields on the
    ///     application. The editor still has to open, and to open the plugin manager to undo it.
    /// </summary>
    [Fact]
    public void The_editor_starts_with_any_one_built_in_switched_off() {
        using var fixture = EditorSession.Start();

        var ids = fixture.Plugins.Plugins.Where(plugin => plugin.IsBuiltIn).Select(plugin => plugin.Id).ToList();

        Assert.True(ids.Count >= 7, "the standard modules are listed");

        foreach (var id in ids) {
            Toggle(fixture, id);
            fixture.Restart();

            Assert.Equal(PluginState.Disabled, fixture.Plugins.Find(id)!.State);

            Toggle(fixture, id);

            Assert.Equal(PluginState.Active, fixture.Plugins.Find(id)!.State);
        }
    }

    static void Toggle(EditorSession fixture, string id) {
        var view = Open(fixture);
        var index = -1;

        for (var row = 0; row < view.Grid.Items.Count; row++) {
            if (view.Grid.Items[row] is LoadedPlugin { Id: var each } && each == id) {
                index = row;
            }
        }

        Assert.True(index >= 0, $"'{id}' is not a row in the plugin manager");

        view.Grid.Select(index);
        fixture.Settle();
        fixture.Click(view.Toggle);
        fixture.Settle();
    }

    static PluginManagerView Open(EditorSession fixture) {
        fixture.Run("tools.plugins");

        return fixture.Control<PluginManagerView>("plugins");
    }

    /// <summary>
    ///     What an element is showing, its markup <c>text</c> children included — an interpolation
    ///     emits one rather than setting the parent's own string.
    /// </summary>
    static string Shown(UiElement element) {
        var text = element.Text ?? string.Empty;

        foreach (var child in element.Children) {
            text += Shown(child);
        }

        return text;
    }
}
