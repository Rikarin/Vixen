// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's E3 exit criteria, one test each, plus what its Part F asks for.</summary>
/// <remarks>
///     ⚠ <b>The exit is three sentences and they are the three <c>[Fact]</c>s at the top.</b> "A
///     fresh install can be driven to a Unity-shaped or Unreal-shaped keymap in one dropdown"; "the
///     editor restores its full arrangement including open documents"; "a plugin can be enabled,
///     disabled and reloaded from a panel". The rest of the file is the lines of Part C that stopped
///     being declared-and-disabled.
/// </remarks>
public class MilestoneE3Tests {
    /// <summary>E3's first exit sentence, through the dropdown rather than through the model.</summary>
    [Theory]
    [InlineData(KeyMapPresets.Unity)]
    [InlineData(KeyMapPresets.Unreal)]
    public void A_fresh_install_reaches_another_editors_keymap_in_one_dropdown(string preset) {
        using var fixture = EditorSession.Start();

        fixture.Open(EditorShell.KeyBindingsPanel);

        var view = fixture.Shell.Keyboard!;

        Assert.Equal(KeyMapPresets.Vixen, fixture.Shell.Keys.PresetName);

        view.Presets.Value = preset;
        fixture.Settle();

        Assert.Equal(preset, fixture.Shell.Keys.PresetName);

        // The preset has actually moved something, rather than being selected and inert.
        var moved = KeyMapPresets.Find(preset)!.Bindings.First(entry => entry.Value.IsBound);

        Assert.Equal(moved.Value, fixture.Shell.Keys.ChordFor(moved.Key));
    }

    /// <summary>
    ///     Doc 20's Part F: "each preset file is asserted to bind only registered commands and to
    ///     raise no conflict".
    /// </summary>
    [Theory]
    [InlineData(KeyMapPresets.Unity)]
    [InlineData(KeyMapPresets.Unreal)]
    public void A_preset_binds_only_commands_that_exist_and_drops_nothing(string name) {
        using var fixture = EditorSession.Start();

        var preset = KeyMapPresets.Find(name)!;
        var commands = fixture.Shell.Commands;

        foreach (var (id, chord) in preset.Bindings) {
            Assert.True(commands.TryGet(id, out _), $"{name} binds '{id}', which nothing registers");
        }

        fixture.Shell.Keys.UsePreset(preset);

        // ⚠ "A preset that silently drops a binding is worse than no preset." Every entry has to
        // survive composition, which it only does if nothing else in the same context holds its
        // chord — so this is the conflict assertion, made against the whole real registry.
        foreach (var (id, chord) in preset.Bindings) {
            Assert.Equal(chord, fixture.Shell.Keys.ChordFor(id));
        }
    }

    /// <summary>E3's second exit sentence: the arrangement comes back <i>including open documents</i>.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 20's A6 is precise about what was missing.</b> <c>current.vxlayout</c> recorded
    ///     the panels and an asset editor's panel is registered on demand — so the id was written to
    ///     the file, nothing could build it on the way back, and the tab came back missing while the
    ///     id stayed in the arrangement. A restart is the only way to prove it: an in-process reload
    ///     would pass for a layout that was never written.
    /// </remarks>
    [Fact]
    public void A_restored_arrangement_reopens_the_documents_that_were_open() {
        using var fixture = EditorSession.Start();

        fixture.Step("open the scene as an asset document")
            .Open("project");

        var scene = fixture.Project.Assets.Entries.First(entry => entry.Path.EndsWith(".vxscene", StringComparison.Ordinal));
        var panel = "asset." + scene.Guid;

        fixture.Project.Selection.Set([scene.Guid]);
        fixture.Run("assets.open");

        Assert.True(fixture.Shell.Workspace.IsOpen(panel));

        fixture.Step("close and reopen the editor").Restart();

        Assert.True(fixture.Shell.Workspace.IsOpen(panel), "the document's panel did not come back");
        Assert.Contains(fixture.Panels, open => open.Id == panel);
    }

    [Fact]
    public void Turning_the_preference_off_leaves_the_documents_closed() {
        using var scope = new Scratch();
        using var fixture = EditorSession.Start(new EditorSessionOptions { DataDirectory = scope.Directory });

        fixture.Open("project");

        var scene = fixture.Project.Assets.Entries.First(entry => entry.Path.EndsWith(".vxscene", StringComparison.Ordinal));
        var panel = "asset." + scene.Guid;

        fixture.Project.Selection.Set([scene.Guid]);
        fixture.Run("assets.open");

        File.WriteAllText(
            Path.Combine(scope.Directory, EditorUserStore.PreferencesFile),
            "restoreOpenDocuments: false\n"
        );

        fixture.Restart();

        Assert.False(fixture.Shell.Workspace.IsOpen(panel));
    }

    /// <summary>E3's third exit sentence, as far as a project with no plugins in it can go.</summary>
    /// <remarks>
    ///     ⚠ <b>The panel and its verbs, not a plugin.</b> Loading a real plugin needs an assembly on
    ///     disk built against this editor, which is <c>Vixen.Editor.Plugin.Tests</c>' fixture and its
    ///     business — what this asserts is that the manager is reachable, is a view over the host,
    ///     and offers the three verbs disabled rather than absent when there is nothing to act on.
    ///     The enable/disable path itself is asserted against a real plugin in that suite.
    ///     <para>
    ///         ⚠ <b>The detail line is read through its children rather than off its own
    ///         <c>Text</c>.</b> Doc 36 § F7 wave 1b moved this panel into <c>.vxml</c>, and a markup
    ///         interpolation emits a <c>text</c> child rather than setting the parent's string — so
    ///         <c>Detail.Text</c> is null on a line that is showing a sentence. Every markup panel in
    ///         the tree behaves this way and <c>NetworkViewTests</c> has the same walker for the same
    ///         reason; this is the first place it reached an assertion.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_plugin_manager_is_a_panel_with_the_three_verbs_on_it() {
        using var fixture = EditorSession.Start();

        fixture.Run("tools.plugins");

        var view = fixture.Control<PluginManagerView>("plugins");

        Assert.Null(view.Selected);
        Assert.True(view.Toggle.Disabled);
        Assert.True(view.Reload.Disabled);
        Assert.Equal(EditorStrings.PluginsNone.Text, Shown(view.Detail));
    }

    /// <summary>What an element is showing, its markup <c>text</c> children included.</summary>
    static string Shown(UiElement element) {
        var text = element.Text ?? string.Empty;

        foreach (var child in element.Children) {
            text += Shown(child);
        }

        return text;
    }

    /// <summary>Every E3 line of Part C runs, rather than being greyed with a milestone on it.</summary>
    [Theory]
    [InlineData("edit.preferences")]
    [InlineData("edit.keybindings")]
    [InlineData("edit.undo-history")]
    [InlineData("file.project-settings")]
    [InlineData("tools.plugins")]
    [InlineData("edit.search-everywhere")]
    public void The_verbs_this_milestone_owed_are_built_rather_than_declared(string id) {
        using var fixture = EditorSession.Start();

        var command = fixture.Shell.Commands[id];

        Assert.NotNull(command);
        Assert.False(command.IsUnavailable, id + " still says it is not built");
        Assert.True(fixture.CanRun(id), id + " is registered and disabled");
    }

    [Fact]
    public void Open_project_is_reachable_and_no_longer_says_it_is_a_milestone() {
        using var fixture = EditorSession.Start();

        var open = fixture.Shell.Commands["file.open-project"]!;

        Assert.False(open.IsUnavailable);
        Assert.True(open.CanExecute);

        // ⚠ New Project needs a folder picker and a headless session has none, so it greys itself
        // out with an enablement rather than with a milestone — which is the distinction doc 20's
        // `Unavailable` draws and the reason the two are different mechanisms.
        var create = fixture.Shell.Commands["file.new-project"]!;

        Assert.False(create.IsUnavailable);
        Assert.False(create.CanExecute);
    }

    /// <summary>Doc 20's A8: the same machinery over content rather than over commands.</summary>
    [Fact]
    public void Search_everywhere_finds_an_asset_and_shows_it_in_the_browser() {
        using var fixture = EditorSession.Start();

        fixture.Run("edit.search-everywhere");

        var search = fixture.Shell.Search;

        Assert.True(search.IsOpen);

        // ⚠ Nothing before a word is typed, which is what tells this apart from the palette: an
        // empty search-everywhere listing twenty commands pushes out the first asset that matches.
        Assert.Empty(search.Results);

        search.Field.Value = "Main";
        fixture.Settle();

        var found = Assert.Single(search.Results, item => item.Category == "Asset");

        Assert.Contains("Main", found.Title, StringComparison.Ordinal);
        Assert.NotNull(found.Preview);

        found.Run();
        fixture.Settle();

        Assert.Single(fixture.Project.Selection);
        Assert.Equal("project", fixture.Shell.Context);
    }

    [Fact]
    public void Search_everywhere_finds_an_entity_by_name() {
        using var fixture = EditorSession.Start();

        fixture.Run("edit.search-everywhere");

        fixture.Shell.Search.Field.Value = "Crate";
        fixture.Settle();

        var found = Assert.Single(fixture.Shell.Search.Results, item => item.Category == "Entity");

        found.Run();
        fixture.Settle();

        var selected = Assert.Single(fixture.Scene.Selection);

        Assert.Equal("Crate", fixture.Scene.NameOf(selected));
    }

    /// <summary>Doc 20's A8 again: Find References is the same query, and it answers.</summary>
    [Fact]
    public void Find_references_selects_what_points_at_the_selection() {
        using var fixture = EditorSession.Start();

        fixture.Open("project");

        var scene = fixture.Project.Assets.Entries.First(entry => entry.Path.EndsWith(".vxscene", StringComparison.Ordinal));

        fixture.Project.Selection.Set([scene.Guid]);
        fixture.Settle();

        Assert.True(fixture.CanRun("assets.find-references"));
        fixture.Run("assets.find-references");

        // Nothing references the seeded scene, so the honest answer is a message and a selection
        // left alone — replacing it with an empty one reads exactly like the command having failed.
        Assert.Single(fixture.Project.Selection);
        Assert.Contains(fixture.Shell.Notifications.History, entry => entry.Message.Contains("references", StringComparison.Ordinal));
    }

    /// <summary>Doc 20's A7: the place the toasts accumulate once they have gone.</summary>
    [Fact]
    public void The_message_log_shows_what_the_editor_has_said() {
        using var fixture = EditorSession.Start();

        fixture.Shell.Notifications.Error("Could not import", "wood.png");
        fixture.Shell.Notifications.Success("Saved");

        fixture.Open(EditorShell.MessageLogPanel);

        var log = fixture.Shell.Messages!;

        Assert.True(log.Count >= 2);

        log.Levels.Value = nameof(NotificationSeverity.Error);
        fixture.Settle();

        var only = Assert.Single(log.Shown);

        Assert.Equal("Could not import", only.Message);
        Assert.Equal("wood.png", only.Detail);

        log.Search.Value = "nothing says this";
        fixture.Settle();

        Assert.Empty(log.Shown);
    }

    /// <summary>
    ///     An edit made while the panel is open reaches it, and nothing asked it to look.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The point of the panel being markup rather than a control.</b> It used to compare
    ///     the stack, its count and its depth once a frame and rewrite every row when any of them
    ///     moved — the panel's own remarks called the polling out and blamed it on nothing in the
    ///     editor's loop flushing the reactive scheduler. The shell flushes now, <c>CommandStack</c>
    ///     was always signal-backed, and this asserts the consequence: a row appears because a
    ///     signal changed, not because a frame went looking.
    /// </remarks>
    [Fact]
    public void An_edit_made_while_the_history_is_open_appears_in_it() {
        using var fixture = EditorSession.Start();

        fixture.Run("edit.undo-history");
        var view = fixture.Component<UndoHistory>("history");

        // Just the row for where the document started.
        Assert.Equal(1, view.Count);

        fixture.Run("scene.create-entity");
        fixture.Settle();

        Assert.Equal(2, view.Count);
    }

    /// <summary>
    ///     ⚠ <b>And the panel stops following it the moment it leaves the document.</b>
    ///     <c>UndoHistory</c> writes its <c>@for</c> inside the <c>&lt;ScrollView&gt;</c>, so the
    ///     loop's region hangs off the scroll view rather than off the component's host — which used
    ///     to mean that closing the panel left a row per edit still subscribed to the command stack,
    ///     assigning to elements that had left the tree.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Asserted on the scheduler, because assigning to a removed element does not
    ///     complain.</b> A panel that looks right after it has been closed proves nothing; what a
    ///     live binding does is queue.
    ///
    ///     ⚠ <b>And the edit is made against the stack rather than through <c>Run</c>, because
    ///     <c>Run</c> settles.</b> The first version of this test ran a command and asserted an
    ///     empty queue, which passes with the whole fix deleted: settling flushes, and a queue that
    ///     has been drained says nothing about what was in it.
    /// </remarks>
    [Fact]
    public void Closing_the_undo_history_stops_it_following_the_stack() {
        using var fixture = EditorSession.Start();

        fixture.Run("scene.create-entity");
        fixture.Run("edit.undo-history");

        var view = fixture.Component<UndoHistory>("history");
        Assert.Equal(2, view.Count);

        view.Root.Remove();
        fixture.Document.Effects.Flush();

        Assert.True(fixture.Scene.Stack.Undo());
        Assert.Equal(0, fixture.Document.Effects.PendingCount);
    }

    /// <summary>Part C's <b>Undo History⋯</b>, and the one operation an undo stack actually supports.</summary>
    /// <remarks>
    ///     ⚠ <b>Clicking an entry undoes back to it rather than undoing that one.</b> Removing the
    ///     third of ten edits would need every later command rebased against a world that no longer
    ///     matches, which `CommandStack` calls a research project — and going back to a point is what
    ///     people mean anyway.
    /// </remarks>
    [Fact]
    public void The_undo_history_lists_the_edits_and_a_click_goes_back_to_one() {
        using var fixture = EditorSession.Start();

        fixture.Run("scene.create-entity");
        fixture.Run("scene.create-entity");
        fixture.Run("scene.create-entity");

        fixture.Run("edit.undo-history");

        var view = fixture.Component<UndoHistory>("history");

        // Three edits, plus the row for where the document was before any of them.
        Assert.Equal(4, view.Count);
        Assert.Equal(3, fixture.Scene.Stack.Depth.Value);

        view.Rewind(1);
        fixture.Settle();

        Assert.Equal(1, fixture.Scene.Stack.Depth.Value);
        Assert.True(fixture.Scene.Stack.CanRedo.Value);

        // And back the other way, because a history that could only undo would be a one-way trip
        // through the list it is showing.
        view.Rewind(3);
        fixture.Settle();

        Assert.Equal(3, fixture.Scene.Stack.Depth.Value);
    }

    /// <summary>Part F's panel-lifecycle row, over every panel the shell and the editor register.</summary>
    /// <remarks>
    ///     ⚠ <b>"A panel's factory runs again when it is reopened" is a documented hazard and nothing
    ///     proved a given panel survives it.</b> The four this milestone adds are exactly the shape
    ///     that gets it wrong — each holds a model the application owns and each subscribes to
    ///     something — so closing and reopening every registered panel is the cheapest proof that
    ///     none of them left a subscription behind or came back empty.
    /// </remarks>
    [Fact]
    public void Every_registered_panel_survives_being_closed_and_reopened() {
        using var fixture = EditorSession.Start();

        foreach (var descriptor in fixture.Shell.Workspace.Panels.ToList()) {
            fixture.Open(descriptor.Id);
            Assert.True(fixture.Shell.Workspace.IsOpen(descriptor.Id), descriptor.Id + " would not open");

            fixture.Close(descriptor.Id);
            Assert.False(fixture.Shell.Workspace.IsOpen(descriptor.Id), descriptor.Id + " would not close");

            fixture.Open(descriptor.Id);
            Assert.NotEmpty(fixture.Panel(descriptor.Id).Children);
        }
    }


}
