// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>A keymap kept between runs by an application that is not the editor (#650).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Which assembly this file is in is half of what it asserts.</b> This project cannot
///         reference <c>Vixen.Editor.Ui</c>, so a <c>KeyMapYaml</c> that drifted back into the editor
///         fails to compile here rather than failing an assertion — and nothing below touches the
///         editor's three presets, because an application that is not the editor has none of them.
///         The preset is one the test's own <see cref="KeyMap.PresetSource" /> supplies, which is
///         the seam an application uses.
///     </para>
/// </remarks>
public sealed class KeyMapYamlTests {
    static readonly KeyChord Save = new(InputKey.S, ModifierKeys.Control);
    static readonly KeyChord Palette = new(InputKey.P, ModifierKeys.Control | ModifierKeys.Shift);
    static readonly KeyChord Mine = new(InputKey.J, ModifierKeys.Control);

    /// <summary>A preset of the application's own, found by name the way a loaded file finds it.</summary>
    static readonly KeyMapPreset Studio = KeyMapPreset.Of(
        "Studio",
        new Dictionary<string, KeyChord>(StringComparer.Ordinal) { ["view.palette"] = Palette }
    );

    static KeyMap Application() {
        var keys = new KeyMap {
            PresetSource = name => string.Equals(name, Studio.Name, StringComparison.Ordinal) ? Studio : null
        };

        keys.SetDefault("file.save", Save);
        keys.SetDefault("view.palette", new KeyChord(InputKey.K, ModifierKeys.Control));

        return keys;
    }

    [Fact]
    public void The_format_is_the_control_librarys_and_not_the_editors() =>
        Assert.Equal("Vixen.Ui.Controls.Advanced", typeof(KeyMapYaml).Assembly.GetName().Name);

    /// <summary>The preset's name and the user's own moves survive, and nothing else is written.</summary>
    [Fact]
    public void The_chosen_preset_and_the_users_overrides_survive_a_restart() {
        var keys = Application();

        Assert.True(keys.UsePreset(Studio.Name));
        Assert.Equal(BindResult.Bound, keys.Bind("file.save", Mine));

        var text = KeyMapYaml.Write(keys);

        // Only what the user did: a file holding the defaults or the preset's bindings would freeze
        // them at the version that wrote it.
        Assert.Contains("Studio", text, StringComparison.Ordinal);
        Assert.DoesNotContain("view.palette", text, StringComparison.Ordinal);

        var reloaded = Application();
        KeyMapYaml.Read(reloaded, text);

        Assert.Equal(Studio.Name, reloaded.PresetName);
        Assert.Equal(Mine, reloaded.ChordFor("file.save"));
        Assert.Equal(Palette, reloaded.ChordFor("view.palette"));
        Assert.True(reloaded.IsCustomised("file.save"));
    }

    /// <summary>
    ///     ⚠ A preferences file somebody edited by hand is read as far as it can be, not refused —
    ///     the alternative is an application that will not start over one mistyped line.
    /// </summary>
    [Fact]
    public void A_line_that_will_not_parse_is_dropped_and_the_rest_is_read() {
        var keys = Application();

        KeyMapYaml.Read(keys, "preset: \"Nowhere\"\nbindings:\n  file.save: \"Ctrl+Nonsense+\"\n  view.palette: \"Ctrl+L\"\n");

        Assert.Equal(KeyMap.NoPreset, keys.PresetName);
        Assert.Equal(Save, keys.ChordFor("file.save"));
        Assert.Equal(new KeyChord(InputKey.L, ModifierKeys.Control), keys.ChordFor("view.palette"));
    }
}
