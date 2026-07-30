// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's A5, driven the way a person drives it.</summary>
public class KeyBindingsPanelTests {
    [Fact]
    public void The_grid_lists_every_command_with_where_its_chord_came_from() {
        using var fixture = Open(out var view);

        Assert.Equal(fixture.Shell.Commands.Commands.Count, view.Grid.Items.Count);

        var row = Row(view, "file.save");

        Assert.Equal("Save Scene", row.Title);
        Assert.Equal(BindingSource.Default, fixture.Shell.Keys.SourceOf("file.save"));
    }

    [Fact]
    public void The_filter_narrows_it_by_id_title_and_category() {
        using var fixture = Open(out var view);

        view.Search.Value = "orbit";
        fixture.Settle();

        Assert.NotEmpty(view.Grid.Items);
        Assert.All(view.Grid.Items, item => Assert.Contains("orbit", ((KeyBindingRow) item).Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     ⚠ Capture is a mode, and while it is on the pressed chord is a binding rather than a
    ///     shortcut — otherwise recording Ctrl+S would save the scene.
    /// </summary>
    [Fact]
    public void Recording_a_chord_binds_it_rather_than_running_what_it_is_bound_to() {
        using var fixture = Open(out var view);

        Select(fixture, view, "scene.frame-all");
        view.Capture(true);

        Assert.True(view.IsCapturing);

        Press(fixture, InputKey.G, ModifierKeys.Alt);

        Assert.False(view.IsCapturing);
        Assert.Equal(new KeyChord(InputKey.G, ModifierKeys.Alt), fixture.Shell.Keys.ChordFor("scene.frame-all"));
        Assert.Equal(BindingSource.User, fixture.Shell.Keys.SourceOf("scene.frame-all"));
    }

    [Fact]
    public void Escape_leaves_capture_without_binding_anything() {
        using var fixture = Open(out var view);

        Select(fixture, view, "scene.frame-all");

        var was = fixture.Shell.Keys.ChordFor("scene.frame-all");

        view.Capture(true);
        Press(fixture, InputKey.Escape, ModifierKeys.None);

        Assert.False(view.IsCapturing);
        Assert.Equal(was, fixture.Shell.Keys.ChordFor("scene.frame-all"));
    }

    /// <summary>
    ///     ⚠ A conflict is reported and not applied, and pressing the same chord again is what takes
    ///     it. Silently displacing another command is how somebody loses Ctrl+S without being told;
    ///     refusing outright would make a swap impossible to express at all.
    /// </summary>
    [Fact]
    public void A_taken_chord_is_reported_and_the_second_press_takes_it() {
        using var fixture = Open(out var view);

        Select(fixture, view, "scene.frame-all");
        view.Capture(true);

        Press(fixture, InputKey.S, ModifierKeys.Control);

        Assert.Equal("file.save", view.Conflict);
        Assert.True(view.IsCapturing);
        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control), fixture.Shell.Keys.ChordFor("file.save"));

        Press(fixture, InputKey.S, ModifierKeys.Control);

        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control), fixture.Shell.Keys.ChordFor("scene.frame-all"));
        Assert.False(fixture.Shell.Keys.ChordFor("file.save").IsBound);
    }

    [Fact]
    public void The_users_bindings_and_the_preset_survive_a_restart() {
        using var fixture = Open(out var view);

        view.Presets.Value = KeyMapPresets.Unreal;
        fixture.Settle();

        Select(fixture, view, "scene.frame-all");
        view.Capture(true);
        Press(fixture, InputKey.G, ModifierKeys.Alt);

        fixture.Restart();

        Assert.Equal(KeyMapPresets.Unreal, fixture.Shell.Keys.PresetName);
        Assert.Equal(new KeyChord(InputKey.G, ModifierKeys.Alt), fixture.Shell.Keys.ChordFor("scene.frame-all"));

        // And the rest still follows the preset rather than having been copied into the user's file.
        Assert.Equal(BindingSource.Preset, fixture.Shell.Keys.SourceOf("play.play"));
    }

    static EditorSession Open(out KeyBindingsView view) {
        var fixture = EditorSession.Start();

        fixture.Open(EditorShell.KeyBindingsPanel);
        view = fixture.Shell.Keyboard!;

        return fixture;
    }

    static KeyBindingRow Row(KeyBindingsView view, string id) =>
        view.Grid.Items.OfType<KeyBindingRow>().FirstOrDefault(row => row.Id == id)
        ?? throw new InvalidOperationException($"no row for '{id}'");

    static void Select(EditorSession fixture, KeyBindingsView view, string id) {
        var index = view.Grid.Items
            .Select((item, at) => (Row: item as KeyBindingRow, At: at))
            .First(entry => entry.Row?.Id == id)
            .At;

        view.Grid.Select(index);
        fixture.Settle();

        Assert.Equal(id, view.Selected);
    }

    /// <summary>Presses a chord into the panel, the way a keyboard does.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the document rather than by calling <c>Rebind</c>, which is the whole point.</b>
    ///     The dispatcher is attached to the same document and would run whatever the chord is
    ///     already bound to; capture mode handles the event on the tunnel leg so that it never gets
    ///     there, and only a real dispatch proves that.
    /// </remarks>
    static void Press(EditorSession fixture, InputKey key, ModifierKeys modifiers) {
        var chord = new KeyChord(key, modifiers).ForPlatform();

        fixture.Document.Dispatch(
            new KeyEvent { Action = KeyAction.Pressed, Key = chord.Key, Modifiers = chord.Modifiers }
        );

        fixture.Settle();
    }
}
