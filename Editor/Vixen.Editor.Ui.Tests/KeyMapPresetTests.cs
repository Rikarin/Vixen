// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The third layer doc 20's A5 calls "the work", and the dropdown it is under.</summary>
/// <remarks>
///     ⚠ <b>Doc 20 is precise about why a preset cannot be an edit:</b> "choosing Unreal and then
///     rebinding one key has to leave the other two hundred following the preset — otherwise the next
///     preset update reaches nobody who has ever rebound anything". Two of the tests here are that
///     sentence turned into assertions, and the rest are the compositions that fall out of it.
/// </remarks>
public class KeyMapPresetTests {
    static KeyChord Save => new(InputKey.S, ModifierKeys.Control);
    static KeyChord SaveAll => new(InputKey.S, ModifierKeys.Control | ModifierKeys.Alt);
    static KeyChord Play => new(InputKey.P, ModifierKeys.Control);

    static KeyMap Shipped() {
        var keys = new KeyMap();

        keys.SetDefault("file.save", Save);
        keys.SetDefault("file.save-all", SaveAll);
        keys.SetDefault("view.palette", Play);

        return keys;
    }

    [Fact]
    public void A_preset_moves_a_binding_and_the_default_underneath_it_is_untouched() {
        var keys = Shipped();

        keys.UsePreset(KeyMapPreset.Parse("Test", "bindings:\n  file.save: \"Ctrl+W\"\n"));

        Assert.Equal(new KeyChord(InputKey.W, ModifierKeys.Control), keys.ChordFor("file.save"));
        Assert.Equal(BindingSource.Preset, keys.SourceOf("file.save"));

        // The shipped default is still what the application declared, which is what makes taking the
        // preset off a restoration rather than a guess.
        Assert.Equal(Save, keys.Defaults["file.save"]);

        keys.UsePreset((KeyMapPreset?) null);
        Assert.Equal(Save, keys.ChordFor("file.save"));
    }

    /// <summary>Doc 20's sentence, as an assertion.</summary>
    [Fact]
    public void Rebinding_one_key_leaves_the_rest_following_the_preset() {
        var keys = Shipped();

        keys.UsePreset(KeyMapPreset.Parse("Test", "bindings:\n  file.save: \"Ctrl+W\"\n  file.save-all: \"Ctrl+Shift+S\"\n"));
        keys.Bind("file.save", new KeyChord(InputKey.K, ModifierKeys.Control));

        Assert.Equal(BindingSource.User, keys.SourceOf("file.save"));

        // The other one is still the preset's, and — the half that matters — it is still *marked* as
        // the preset's, so a later preset update reaches it.
        Assert.Equal(BindingSource.Preset, keys.SourceOf("file.save-all"));
        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift), keys.ChordFor("file.save-all"));
    }

    [Fact]
    public void Choosing_a_preset_does_not_mark_two_hundred_commands_as_customised() {
        var keys = Shipped();

        keys.UsePreset(KeyMapPresets.Find(KeyMapPresets.Unreal));

        // ⚠ Otherwise "what have I changed" is unanswerable the moment a preset is chosen, and the
        // keybinding editor's Source column says "Yours" against every row the user never touched.
        Assert.All(keys.Ids(), id => Assert.False(keys.IsCustomised(id), id + " is marked as the user's"));
    }

    /// <summary>
    ///     A preset that claims a chord a default holds takes it, and the displaced command ends up
    ///     unbound rather than sharing it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is what lets a preset be twenty lines rather than two hundred.</b> Unity puts
    ///     Play on <c>Ctrl+P</c>, which is this editor's palette; the preset says where the palette
    ///     goes and says nothing about every other command that might have wanted the key, because
    ///     the composition works it out.
    /// </remarks>
    [Fact]
    public void A_preset_takes_a_chord_off_the_default_that_had_it() {
        var keys = Shipped();

        keys.UsePreset(KeyMapPreset.Parse("Test", "bindings:\n  play.play: \"Ctrl+P\"\n"));

        Assert.Equal("play.play", keys.CommandFor(Play));
        Assert.False(keys.ChordFor("view.palette").IsBound);
    }

    [Fact]
    public void Reset_throws_away_the_users_overrides_and_keeps_the_preset() {
        var keys = Shipped();

        keys.UsePreset(KeyMapPreset.Parse("Test", "bindings:\n  file.save: \"Ctrl+W\"\n"));
        keys.Bind("file.save", new KeyChord(InputKey.K, ModifierKeys.Control));

        keys.Reset();

        // ⚠ Not the shipped default. "Reset" means "undo what I changed", and somebody who chose a
        // preset and then made a mess of three keys is asking for the preset back rather than for a
        // keymap they have never used.
        Assert.Equal(new KeyChord(InputKey.W, ModifierKeys.Control), keys.ChordFor("file.save"));
        Assert.Equal("Test", keys.PresetName);
        Assert.False(keys.IsCustomised("file.save"));
    }

    [Fact]
    public void The_chosen_preset_and_the_users_overrides_survive_a_save() {
        var keys = Shipped();

        keys.UsePreset(KeyMapPresets.Find(KeyMapPresets.Unity));

        // ⚠ Not Ctrl+K, which the Unity preset gives to the palette — binding it would be refused,
        // which is the conflict rule working and would make this test about the wrong thing.
        Assert.Equal(BindResult.Bound, keys.Bind("file.save", new KeyChord(InputKey.J, ModifierKeys.Control)));

        var text = keys.Save();

        Assert.Contains("Unity", text, StringComparison.Ordinal);
        Assert.Contains("file.save", text, StringComparison.Ordinal);

        // Only the one the user moved: a file holding the preset's bindings as well would freeze
        // them at this version, which is the whole reason the layer exists.
        Assert.DoesNotContain("view.palette", text, StringComparison.Ordinal);

        var reloaded = Shipped();
        reloaded.Load(text);

        Assert.Equal(KeyMapPresets.Unity, reloaded.PresetName);
        Assert.Equal(new KeyChord(InputKey.J, ModifierKeys.Control), reloaded.ChordFor("file.save"));

        // And the preset is still in force underneath, which is what the name in the file is for.
        Assert.Equal(new KeyChord(InputKey.K, ModifierKeys.Control), reloaded.ChordFor("view.palette"));
    }

    [Fact]
    public void A_keymap_naming_a_preset_this_editor_has_not_got_loads_without_it() {
        var keys = Shipped();

        keys.Load("preset: \"Studio\"\nbindings:\n  file.save: \"Ctrl+K\"\n");

        // ⚠ Dropped rather than fatal, for the reason a stale chord is: a team preset on a machine
        // that has not got it must not be an editor that will not start. What survives is the part
        // this editor can honour.
        Assert.Equal(KeyMapPresets.Vixen, keys.PresetName);
        Assert.Equal(new KeyChord(InputKey.K, ModifierKeys.Control), keys.ChordFor("file.save"));
    }

    [Fact]
    public void Resetting_one_row_puts_it_back_to_the_layer_underneath() {
        var keys = Shipped();

        keys.UsePreset(KeyMapPreset.Parse("Test", "bindings:\n  file.save: \"Ctrl+W\"\n"));
        keys.Bind("file.save", new KeyChord(InputKey.K, ModifierKeys.Control));

        Assert.True(keys.ResetBinding("file.save"));

        Assert.Equal(new KeyChord(InputKey.W, ModifierKeys.Control), keys.ChordFor("file.save"));
        Assert.Equal(BindingSource.Preset, keys.SourceOf("file.save"));
        Assert.False(keys.ResetBinding("file.save"));
    }

    [Fact]
    public void The_Vixen_preset_is_the_absence_of_one() {
        var keys = Shipped();

        Assert.True(keys.UsePreset(KeyMapPresets.Vixen));

        Assert.Null(keys.Preset);
        Assert.Equal(Save, keys.ChordFor("file.save"));
    }

    [Theory]
    [InlineData(KeyMapPresets.Unity)]
    [InlineData(KeyMapPresets.Unreal)]
    public void Every_shipped_preset_parses_and_binds_something(string name) {
        var preset = KeyMapPresets.Find(name);

        Assert.NotNull(preset);
        Assert.Equal(name, preset.Name);
        Assert.NotEmpty(preset.Bindings);

        // A chord that would not parse is silently dropped by `Parse`, so an entry that survived is
        // one that means something — and every one of ours has to.
        Assert.All(preset.Bindings.Values, chord => Assert.True(chord.IsBound));
    }

    /// <summary>
    ///     Doc 20's Part F: "a preset that silently drops a binding is worse than no preset".
    /// </summary>
    [Theory]
    [InlineData(KeyMapPresets.Unity)]
    [InlineData(KeyMapPresets.Unreal)]
    public void No_shipped_preset_binds_one_chord_to_two_commands(string name) {
        var preset = KeyMapPresets.Find(name)!;
        var seen = new Dictionary<KeyChord, string>();

        foreach (var (id, chord) in preset.Bindings) {
            Assert.False(
                seen.TryGetValue(chord, out var holder),
                $"{name} binds {chord.Save()} to both {holder} and {id}"
            );

            seen[chord] = id;
        }
    }
}
