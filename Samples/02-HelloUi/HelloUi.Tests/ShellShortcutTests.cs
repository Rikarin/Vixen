// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Samples.HelloUi.Tests;

/// <summary>The sample's File ▸ Save chord: drawn from its keymap, and answered by its document.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#650's sample half, which was compile-verified only.</b> The shell draws Save's chord
///         out of a <c>KeyMap</c> and dispatches the keystroke against the same table, through the
///         command route, to the <c>MaterialDocument</c> it hosts — the first chord any framework
///         application dispatched. Every piece of that had a test in <c>Vixen.Ui.Controls.Tests</c>;
///         the arrangement in <c>Shell.vxml</c> had none, because nothing referenced the sample. A
///         line dropped from <c>OnComposed</c> would have compiled and left every suite green.
///     </para>
///     <para>
///         <b>The shell is built into a test document, not opened in a window</b>, the way
///         <c>Mmo.Ui.Tests</c> builds its HUD: the same stylesheets <c>Program.cs</c> hands
///         <c>UiApplication</c>, the same factory, no GPU.
///     </para>
///     <para>
///         ⚠ <b>The chord pressed is read back off the drawn label</b>, so the test presses what the
///         menu shows rather than a second spelling of it that could agree by accident.
///     </para>
/// </remarks>
public sealed class ShellShortcutTests : IDisposable {
    readonly UiTest ui = UiTest.Create(1280f, 800f);
    readonly ShellModel model = new();
    readonly Shell shell;

    public ShellShortcutTests() {
        ControlTheme.Install(ui.Document);
        AdvancedTheme.Install(ui.Document);
        ui.Load(VixenUtilityStyles.Css);

        shell = new Shell { Model = model };
        BuildContext.BuildInto(shell, ui.Document, ui.Document.Root);
        ui.Frame();
    }

    public void Dispose() => ui.Dispose();

    /// <summary>Save and Close draw the chords the keymap binds; New and Open, bound to nothing, draw none.</summary>
    [Fact]
    public void The_file_menu_draws_the_chords_its_keymap_binds_and_no_others() {
        var save = Item("Save");
        var close = Item("Close");

        Assert.NotNull(save.Shortcut);
        Assert.NotNull(close.Shortcut);
        Assert.Equal(new KeyChord(InputKey.S, ModifierKeys.Control).ForPlatform(), Drawn(save));
        Assert.Equal(new KeyChord(InputKey.W, ModifierKeys.Control).ForPlatform(), Drawn(close));

        Assert.Null(Item("New").Shortcut);
        Assert.Null(Item("Open…").Shortcut);
    }

    /// <summary>The chord Save draws, pressed inside the shell, saves the material.</summary>
    [Fact]
    public void The_drawn_save_chord_saves_the_hosted_material() {
        var document = Assert.IsAssignableFrom<IEditableDocument>(shell.Root.HostedDocument);
        var chord = Drawn(Item("Save"));

        Assert.False(document.IsDirty.Value);

        model.Name.Value = "Brushed Steel";
        ui.Frame();

        Assert.True(document.IsDirty.Value, "an edit to the material did not make the document dirty");

        // Anywhere inside the shell: the route walks up from the focus to the element that hosts
        // the document, which is the shell's root.
        ui.Document.Focus(Focusable(shell.Root));
        Press(chord);
        ui.Frame();

        Assert.False(document.IsDirty.Value, $"{chord} did not save the material");
    }

    MenuItem Item(string label) =>
        Descendants(ui.Document.Root).OfType<MenuItem>().FirstOrDefault(item => item.Label == label)
        ?? throw new InvalidOperationException(
            $"no menu item is labelled '{label}'; the items are: "
            + string.Join(", ", Descendants(ui.Document.Root).OfType<MenuItem>().Select(item => item.Label))
        );

    static KeyChord Drawn(MenuItem item) {
        var label = item.Shortcut ?? throw new InvalidOperationException($"'{item.Label}' draws no chord");
        return new KeyChord(label.Key, label.Modifiers);
    }

    void Press(KeyChord chord) {
        ui.Hold(chord.Modifiers);
        ui.PressKey(chord.Key);
        ui.Hold(ModifierKeys.None);
    }

    static UiElement Focusable(UiElement under) =>
        Descendants(under).FirstOrDefault(element => element is TextBox)
        ?? throw new InvalidOperationException("the shell has no text box to put the focus in");

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var below in Descendants(child)) {
                yield return below;
            }
        }
    }
}
