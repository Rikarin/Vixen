// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     The keybinding panel doc 36 § F7 wave 1b moved into <c>.vxml</c>, asserted through the
///     elements it built.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here reads an element.</b> The panel's strip and its status line used
///         to be repainted by a hand-written <c>Restate</c> and are now five signals and four
///         bindings; a port that replaced any of them with a plain field would pass
///         <c>KeyBindingsPanelTests</c> — which reads properties — and would draw its first answer for
///         ever.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-verified.</b> <c>capturing</c> as a plain <c>bool</c> fails
///         <see cref="Recording_renames_the_button_and_says_what_it_is_waiting_for" />;
///         <c>selected</c> as a plain field fails
///         <see cref="Choosing_a_row_ungreys_record_and_changes_the_line" />; <c>conflict</c> as a
///         plain field fails <see cref="A_refused_chord_says_who_has_it_and_reddens_the_line" />;
///         and <c>complaint</c> as a direct write to <c>Status.Text</c> — which is what the C# did —
///         fails <see cref="A_preset_that_does_not_exist_is_complained_about" /> on the next flush,
///         because the binding paints over it.
///     </para>
/// </remarks>
public class KeyBindingsViewTests : IDisposable {
    readonly UiDocument document = new(900f, 600f);
    readonly CommandRegistry commands = new();
    readonly KeyMap keys = new();
    readonly KeyBindingsView view;

    public KeyBindingsViewTests() {
        ControlTheme.Install(document);
        EditorTheme.Install(document);

        commands.Add("file.save", new StringId("cmd.save", "Save Scene"), static () => { });
        commands.Add("scene.frame-all", new StringId("cmd.frame", "Frame All"), static () => { });

        keys.Bind("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

        view = document.Root.Add<KeyBindingsView>();
        view.Show(commands, keys);

        Settle();
    }

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The tag the stylesheet names, and the three parts under it.</summary>
    [Fact]
    public void The_panel_answers_to_the_tag_its_stylesheet_names() {
        Assert.Equal("keybindings-view", view.Tag);
        Assert.Equal("keybindings-toolbar", view.Children[0].Tag);
        Assert.Equal("keybindings-status", view.Children[^1].Tag);
    }

    /// <summary>With nothing chosen the strip is greyed and the line says to choose something.</summary>
    [Fact]
    public void With_no_row_chosen_the_line_says_to_choose_one() {
        Assert.True(view.Record.Disabled);
        Assert.Equal(EditorStrings.KeysPickRow.Text, Shown(view.Status));
        Assert.False(view.Status.HasClass("conflict"));
    }

    /// <summary>
    ///     ⚠ Choosing a row is a `DataGrid` event rather than a signal write, so this is the
    ///     assertion that the panel puts the grid's answer somewhere the markup can read.
    /// </summary>
    [Fact]
    public void Choosing_a_row_ungreys_record_and_changes_the_line() {
        Choose("file.save");

        Assert.Equal("file.save", view.Selected);
        Assert.False(view.Record.Disabled);
        Assert.Equal(EditorStrings.KeysReady.Text, Shown(view.Status));
    }

    /// <summary>Capture renames the button, ticks it, and says what it is waiting for.</summary>
    [Fact]
    public void Recording_renames_the_button_and_says_what_it_is_waiting_for() {
        Choose("scene.frame-all");

        view.Capture(true);
        Settle();

        Assert.Equal(EditorStrings.KeysRecording.Text, view.Record.Label);
        Assert.True(view.Record.State.HasFlag(ElementState.Checked));
        Assert.Equal(EditorStrings.KeysWaiting.Text, Shown(view.Status));

        view.Capture(false);
        Settle();

        Assert.Equal(EditorStrings.KeysRecord.Text, view.Record.Label);
        Assert.False(view.Record.State.HasFlag(ElementState.Checked));
    }

    /// <summary>
    ///     ⚠ A conflict is the one thing on this panel that is red, and the class is what makes it
    ///     so — <c>keybindings-status.conflict</c> in <c>EditorTheme.vcss</c>.
    /// </summary>
    [Fact]
    public void A_refused_chord_says_who_has_it_and_reddens_the_line() {
        Choose("scene.frame-all");

        Assert.Equal(BindResult.Conflict, view.Rebind(new KeyChord(InputKey.S, ModifierKeys.Control)));

        Settle();

        Assert.Equal("file.save", view.Conflict);
        Assert.True(view.Status.HasClass("conflict"));
        Assert.Contains("Save Scene", Shown(view.Status), StringComparison.Ordinal);

        // The second press takes it, and the line goes back to black.
        Assert.NotEqual(BindResult.Conflict, view.Rebind(new KeyChord(InputKey.S, ModifierKeys.Control), replace: true));

        Settle();

        Assert.Null(view.Conflict);
        Assert.False(view.Status.HasClass("conflict"));
    }

    /// <summary>
    ///     ⚠ The C# wrote this sentence straight into <c>Status.Text</c>, which a bound line paints
    ///     over on the next flush — so the complaint had to become part of the state the line is a
    ///     function of. This is the test that says it did.
    /// </summary>
    [Fact]
    public void A_preset_that_does_not_exist_is_complained_about() {
        view.Presets.AddOption("Emacs", "Emacs");
        view.Presets.Value = "Emacs";

        Settle();

        Assert.Contains("Emacs", Shown(view.Status), StringComparison.Ordinal);

        // And it is transient: the next thing the user does is what clears it, exactly as the next
        // `Restate` used to.
        Choose("file.save");

        Assert.Equal(EditorStrings.KeysReady.Text, Shown(view.Status));
    }

    /// <summary>The filter narrows the grid, and the panel keeps working after it does.</summary>
    [Fact]
    public void The_filter_narrows_the_grid() {
        view.Search.Value = "frame";
        Settle();

        Assert.Equal("scene.frame-all", Assert.IsType<KeyBindingRow>(Assert.Single(view.Grid.Items)).Id);
    }

    void Choose(string id) {
        var index = view.Grid.Items
            .Select((item, at) => (Row: item as KeyBindingRow, At: at))
            .First(entry => entry.Row?.Id == id)
            .At;

        view.Grid.Select(index);
        Settle();
    }

    void Settle() {
        document.Update();
        document.Update();
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
