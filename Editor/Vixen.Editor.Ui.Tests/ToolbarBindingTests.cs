// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Doc 45 step 4: the editor's strips are views over the route, not over a registry.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both directions, and the second is the one a stale toolbar hides behind.</b> A strip
///         that never greys and a strip with nothing to grey look identical, and so do a strip that
///         follows the invalidation and one that is being polled by somebody else. So every test
///         here asserts the change <i>and</i> counts the predicate: an item greys and un-greys on an
///         invalidation nobody helped along, and a quiet stretch of frames asks nothing at all.
///     </para>
///     <para>
///         ⚠ <b>Nothing here calls <c>ToolbarPresenter.Refresh</c>.</b> That is the point: the strip
///         is bound rather than maintained, and the poll `EditorShell.Tick` still runs is about two
///         predicates that report no change of their own — see its remarks and #430 — not about the
///         binding needing help.
///     </para>
/// </remarks>
public class ToolbarBindingTests : IDisposable {
    readonly UiDocument document = new(1280f, 800f);
    readonly CommandRegistry commands = new();
    readonly KeyMap keys = new();

    TimeSpan clock;

    public ToolbarBindingTests() => ControlTheme.Install(document);

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    static StringId Title(string text) => new("test." + text, text);

    /// <summary>One frame of a host's loop, which is what raises the coalesced invalidation.</summary>
    void Frame() {
        clock += TimeSpan.FromMilliseconds(16);

        document.Tick(clock);
        document.Update();
    }

    ToolbarPresenter Toolbar() => new(document.Root, commands, keys);

    [Fact]
    public void A_button_follows_the_invalidation_and_a_quiet_stretch_asks_no_predicate() {
        var asked = 0;
        var enabled = true;

        commands.Add(
            new EditorCommand("file.save", Title("Save"), () => { }) {
                Enablement = () => {
                    asked++;

                    return enabled;
                }
            }
        );

        var toolbar = Toolbar();
        toolbar.Show("file.save");

        var button = Assert.IsAssignableFrom<ButtonBase>(toolbar.Strip.Children[0]);

        Frame();
        Assert.False(button.Disabled);

        // ⚠ Ten frames in which nothing said anything. The strip is on screen the whole time and
        // costs nothing — which is the property `EditorShell.Tick`'s `Toolbar.Refresh()` used to
        // make impossible, and which this file would go on passing if the binding had been wired
        // and something were still polling behind it.
        var settled = asked;

        for (var i = 0; i < 10; i++) {
            Frame();
        }

        Assert.Equal(settled, asked);

        // And the other direction, with nobody touching the button: the state changes, something
        // says so once, and the strip is right on the next frame.
        enabled = false;
        document.InvalidateCommands();
        Frame();

        Assert.True(asked > settled);
        Assert.True(button.Disabled);

        enabled = true;
        document.InvalidateCommands();
        Frame();

        Assert.False(button.Disabled);
    }

    [Fact]
    public void A_mode_buttons_check_state_is_drawn_by_the_binding() {
        var active = "select";

        commands.Add(
            new EditorCommand("mode.blockout", Title("Blockout"), () => { }) {
                Checked = () => active == "blockout"
            }
        );

        var toolbar = Toolbar();
        toolbar.Show("mode.blockout");

        var button = Assert.IsAssignableFrom<ButtonBase>(toolbar.Strip.Children[0]);

        Frame();
        Assert.False(button.State.HasFlag(ElementState.Checked));

        // ⚠ The case a port that only bound `Disabled` would break in silence: which mode you are
        // in is drawn by a `Checked` predicate and nothing fails when it stops being asked.
        active = "blockout";
        document.InvalidateCommands();
        Frame();

        Assert.True(button.State.HasFlag(ElementState.Checked));

        active = "select";
        document.InvalidateCommands();
        Frame();

        Assert.False(button.State.HasFlag(ElementState.Checked));
    }

    [Fact]
    public void A_command_whose_name_is_its_state_renames_its_own_button() {
        var world = false;

        commands.Add(
            new EditorCommand("scene.toggle-space", Title("Local Space"), () => { }) {
                Caption = () => world
                    ? new StringId("test.space", "World Space")
                    : new StringId("test.space", "Local Space")
            }
        );

        var toolbar = Toolbar();
        toolbar.Show("scene.toggle-space");

        var button = Assert.IsAssignableFrom<ButtonBase>(toolbar.Strip.Children[0]);

        Frame();
        Assert.Equal("Local Space", button.Label);

        // ⚠ `CommandRegistry` used to supply no title at all, on the written grounds that resolving
        // a `StringId` "would need a catalogue this table does not have" — and `Strings` is static,
        // so it always had one. Without this the gizmo's space button reads "Local Space" in both
        // states, which is the exact defect `EditorCommand.Caption` was added to fix.
        world = true;
        document.InvalidateCommands();
        Frame();

        Assert.Equal("World Space", button.Label);
    }

    [Fact]
    public void A_command_that_is_not_captioned_keeps_the_label_the_strip_gave_it() {
        commands.Add("file.save", Title("Save"), () => { });

        var toolbar = Toolbar();
        toolbar.Show("file.save");

        var button = Assert.IsAssignableFrom<ButtonBase>(toolbar.Strip.Children[0]);

        Frame();

        // The counter-assertion to the test above: a handler that offered a title unconditionally
        // would be indistinguishable here until a refresh, and then it would still say "Save" — so
        // the thing to check is that a refresh does not blank it, which is what `null` buys.
        document.InvalidateCommands();
        Frame();

        Assert.Equal("Save", button.Label);
    }

    [Fact]
    public void An_id_the_registry_does_not_know_greys_a_button_bound_to_it() {
        commands.Add("file.save", Title("Save"), () => { });

        var toolbar = Toolbar();
        toolbar.Show("file.save");

        var button = Assert.IsAssignableFrom<ButtonBase>(toolbar.Strip.Children[0]);

        Frame();
        Assert.False(button.Disabled);

        // ⚠ Unloading a plugin is this, and the strip is not rebuilt in between. Nothing responds,
        // so the button greys — with no rule written anywhere about what an unknown id means.
        //
        // The invalidation is explicit here because this fixture is a bare document; `EditorShell`
        // subscribes `CommandRegistry.Changed` to it, so a real unload needs nobody to say so.
        commands.Remove("file.save");
        document.InvalidateCommands();
        Frame();

        Assert.True(button.Disabled);
    }
}
