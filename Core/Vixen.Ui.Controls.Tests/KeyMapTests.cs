// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The keymap, exercised from an application that is not the editor.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Which assembly this file is in is half of what it asserts.</b> <see cref="KeyMap" />,
///         <see cref="KeyMapPreset" /> and <see cref="CommandDispatcher" /> were
///         <c>Vixen.Editor.Ui</c> types until doc 49 § 4.4's move (#650), and a test project for the
///         controls library cannot reference the editor — so a regression that put them back would
///         not fail these assertions, it would fail to compile them.
///     </para>
///     <para>
///         ⚠ <b>The last thing to move was the layering, and the thing that did not move was the
///         file.</b> Four sessions recorded the blocker as "<c>KeyMap</c> reads
///         <c>Vixen.Core.Yaml</c>, which no UI assembly references". That was true and was the wrong
///         conclusion: what reads YAML is the *round trip*, not the three layers, and a control
///         library that took the round trip with it would have put a YAML parser in the dependency
///         closure of every application that has a button. <see cref="KeyMap.Overrides" /> and
///         <see cref="KeyMap.Restore" /> are the seam, and
///         <see cref="Nothing_here_can_read_or_write_a_keymap_file" /> is what keeps it one.
///     </para>
/// </remarks>
public class KeyMapTests {
    static readonly KeyChord CtrlS = new(InputKey.S, ModifierKeys.Control);
    static readonly KeyChord CtrlZ = new(InputKey.Z, ModifierKeys.Control);
    static readonly KeyChord CtrlU = new(InputKey.U, ModifierKeys.Control);

    /// <summary>The keymap ships in the same assembly as the menu that draws its chords.</summary>
    /// <remarks>
    ///     A structural claim rather than a behavioural one, and deliberately so: every other test
    ///     here would pass just as well against a copy of these types in the editor. The symptom the
    ///     move exists to end is <see cref="MenuItem.ShowShortcut" /> — the controls library could
    ///     <i>draw</i> "⌘S" while every part of the machinery behind it lived in the editor.
    /// </remarks>
    [Fact]
    public void The_keymap_is_in_the_controls_library() {
        Assert.Same(typeof(MenuItem).Assembly, typeof(KeyMap).Assembly);
        Assert.Same(typeof(MenuItem).Assembly, typeof(KeyMapPreset).Assembly);
        Assert.Same(typeof(MenuItem).Assembly, typeof(CommandDispatcher).Assembly);
    }

    /// <summary>Nothing in the keymap's own surface names a file, a stream or a format.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that says why this class could come down at all.</b> The old
    ///     <c>Save()</c>/<c>Load(string)</c> pair is what made the type need
    ///     <c>Vixen.Core.Yaml</c>; a member with either name coming back would either drag that
    ///     dependency into a controls package or, worse, invent a second format beside the editor's.
    /// </remarks>
    [Fact]
    public void Nothing_here_can_read_or_write_a_keymap_file() {
        var members = typeof(KeyMap).GetMembers().Select(member => member.Name).ToList();

        Assert.DoesNotContain("Save", members);
        Assert.DoesNotContain("Load", members);

        // And the seam that replaced them is present, so this is not satisfied by the class being
        // empty — which is the shape of a structural assertion that cannot fail.
        Assert.Contains("Overrides", members);
        Assert.Contains("Restore", members);
    }

    /// <summary>What the user moved is the layer that comes back out, and only that.</summary>
    [Fact]
    public void Overrides_report_the_users_layer_and_not_the_defaults_underneath_it() {
        var keys = new KeyMap();
        keys.SetDefault("file.save", CtrlS);
        keys.SetDefault("edit.undo", CtrlZ);

        keys.Bind("edit.undo", CtrlU);

        // The reason only this layer is ever persisted: a default that moves in a later version has
        // to reach everybody who had not deliberately rebound it.
        Assert.Equal(["edit.undo"], keys.Overrides.Keys);
        Assert.Equal(CtrlU, keys.Overrides["edit.undo"]);
    }

    /// <summary>A restore puts back what <see cref="KeyMap.Overrides" /> reported, and nothing else.</summary>
    [Fact]
    public void A_restore_replaces_the_users_layer_and_leaves_the_defaults_alone() {
        var saved = new KeyMap();
        saved.SetDefault("file.save", CtrlS);
        saved.SetDefault("edit.undo", CtrlZ);
        saved.Bind("edit.undo", CtrlU);

        var restored = new KeyMap();
        restored.SetDefault("file.save", CtrlS);
        restored.SetDefault("edit.undo", CtrlZ);
        restored.Restore(null, saved.Overrides);

        Assert.Equal(CtrlU, restored.ChordFor("edit.undo"));
        Assert.Equal(CtrlS, restored.ChordFor("file.save"));
        Assert.True(restored.IsCustomised("edit.undo"));
        Assert.False(restored.IsCustomised("file.save"));
    }

    /// <summary>A command deliberately unbound comes back unbound rather than back to its default.</summary>
    /// <remarks>
    ///     It travels as <see cref="KeyChord.None" /> rather than as an absence, because an absence
    ///     means "use the layer underneath" and the user said the opposite.
    /// </remarks>
    [Fact]
    public void A_deliberate_unbind_survives_the_round_trip() {
        var saved = new KeyMap();
        saved.SetDefault("file.save", CtrlS);
        saved.Bind("file.save", KeyChord.None);

        var restored = new KeyMap();
        restored.SetDefault("file.save", CtrlS);
        restored.Restore(null, saved.Overrides);

        Assert.False(restored.ChordFor("file.save").IsBound);
        Assert.True(restored.IsCustomised("file.save"));
    }

    /// <summary>One <see cref="KeyMap.Changed" /> for a whole restore, not one per binding.</summary>
    /// <remarks>
    ///     A keymap is loaded while the menus are already listening, so an event per entry would
    ///     re-label every shortcut in the application a few hundred times on startup. Counted as
    ///     work rather than timed, which is what makes it an assertion and not a budget.
    /// </remarks>
    [Fact]
    public void A_restore_raises_one_change_however_many_bindings_it_carries() {
        var keys = new KeyMap();
        keys.SetDefault("file.save", CtrlS);
        keys.SetDefault("edit.undo", CtrlZ);

        var changes = 0;
        keys.Changed += _ => changes++;

        keys.Restore(
            null,
            [new KeyValuePair<string, KeyChord>("edit.undo", CtrlU), new KeyValuePair<string, KeyChord>("file.save", KeyChord.None)]
        );

        Assert.Equal(1, changes);
        Assert.Equal(CtrlU, keys.ChordFor("edit.undo"));
    }

    /// <summary>A preset name is resolved through the application's source, and there is no default one.</summary>
    /// <remarks>
    ///     ⚠ <b>Left unset, <see cref="KeyMap.PresetSource" /> finds nothing, and that is not a
    ///     stub.</b> The presets an application ships are the application's — the editor points this
    ///     at its own three in <c>EditorShell</c> — so a default that reached for a particular set
    ///     would make a control library know one application's names.
    /// </remarks>
    [Fact]
    public void A_preset_is_resolved_by_the_application_and_by_nothing_here() {
        var keys = new KeyMap();
        keys.SetDefault("file.save", CtrlS);

        Assert.False(keys.UsePreset("Studio"));
        Assert.Equal(KeyMap.NoPreset, keys.PresetName);

        keys.PresetSource = name =>
            name == "Studio" ? KeyMapPreset.Of(name, [new KeyValuePair<string, KeyChord>("file.save", CtrlU)]) : null;

        Assert.True(keys.UsePreset("Studio"));
        Assert.Equal("Studio", keys.PresetName);
        Assert.Equal(CtrlU, keys.ChordFor("file.save"));

        // And the default underneath is untouched, which is what makes a preset a layer.
        Assert.Equal(CtrlS, keys.Defaults["file.save"]);
        Assert.Equal(BindingSource.Preset, keys.SourceOf("file.save"));
    }

    /// <summary>A restore naming a preset the source has not got keeps the bindings and drops the name.</summary>
    /// <remarks>What happens to a team preset on a machine that has not got it.</remarks>
    [Fact]
    public void A_restore_naming_an_unknown_preset_keeps_what_it_can() {
        var keys = new KeyMap();
        keys.SetDefault("file.save", CtrlS);

        keys.Restore("Studio", [new KeyValuePair<string, KeyChord>("file.save", CtrlU)]);

        Assert.Equal(KeyMap.NoPreset, keys.PresetName);
        Assert.Equal(CtrlU, keys.ChordFor("file.save"));
    }
}
