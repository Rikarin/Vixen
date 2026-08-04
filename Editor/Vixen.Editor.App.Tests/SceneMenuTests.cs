// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The viewport's two summoned menus, and the one registration behind both.</summary>
public class SceneMenuTests {
    static EditorSession Scene() {
        var editor = EditorSession.Start();

        editor.Open("scene");
        editor.Frames(2);

        return editor;
    }

    static RadialMenu Radial(EditorSession editor) {
        editor.Run("scene.radial-menu");
        editor.Frames(2);

        return Descendants(editor.Document.Root).OfType<RadialMenu>().FirstOrDefault(menu => menu.IsOpen)
            ?? throw editor.Fail("the radial menu did not open");
    }

    [Fact]
    public void The_radial_menu_opens_at_the_cursor_with_a_wedge_per_entry() {
        using var editor = Scene();

        var expected = editor.Extensions.All<SceneMenuItem>()
            .Count(entry => (entry.Surface & SceneMenuSurface.Radial) != 0 && entry.Mode is null);

        var menu = Radial(editor);

        Assert.Equal(expected, menu.Items.Count);
        Assert.True(menu.Items.Count > 0, "the editor should ship some radial entries of its own");

        // Centred on the pointer rather than starting at it — a pie's centre is the origin every
        // direction is measured from.
        var pointer = editor.Viewport!.PointerPosition;

        Assert.Equal(pointer.X, menu.Bounds.X + (menu.Bounds.Width * 0.5f), 1);
        Assert.Equal(pointer.Y, menu.Bounds.Y + (menu.Bounds.Height * 0.5f), 1);
    }

    /// <summary>
    ///     ⚠ <b>The wedges are on a ring, which is arithmetic no stylesheet can do.</b> Each is
    ///     centred on its own point rather than starting at it; an item placed by its top-left corner
    ///     makes a ring that leans down and to the right by half a button.
    /// </summary>
    [Fact]
    public void The_wedges_sit_on_a_ring_clockwise_from_the_top() {
        using var editor = Scene();

        var menu = Radial(editor);
        var centre = new Vector2(
            menu.Bounds.X + (menu.Bounds.Width * 0.5f),
            menu.Bounds.Y + (menu.Bounds.Height * 0.5f)
        );

        // ⚠ To within a pixel rather than exactly. The offsets are written as text and the layout
        // snaps to half-pixels, so a wedge on a diagonal lands about a twelfth of a pixel inside the
        // ring — asserting equality would be asserting the rounding rather than the geometry.
        Assert.All(
            menu.Items,
            item => {
                var middle = new Vector2(
                    item.Bounds.X + (item.Bounds.Width * 0.5f),
                    item.Bounds.Y + (item.Bounds.Height * 0.5f)
                );

                var distance = (middle - centre).Length();

                Assert.True(
                    MathF.Abs(distance - menu.Radius) <= 1f,
                    $"'{item.Label}' is {distance} from the centre and the ring is at {menu.Radius}"
                );
            }
        );

        // The first is at the top, which is what "clockwise from the top" has to mean for a flick to
        // be learnable.
        var first = menu.Items[0];

        Assert.Equal(centre.X, first.Bounds.X + (first.Bounds.Width * 0.5f), 1);
        Assert.True(first.Bounds.Y < centre.Y, "the first wedge should be above the centre");
    }

    /// <summary>A direction picks a wedge, and the middle picks none.</summary>
    /// <remarks>
    ///     ⚠ <b>Nearest by angle rather than by hit test.</b> A pie is aimed with a flick that
    ///     routinely overshoots the ring by a long way — a test against the buttons' own bounds would
    ///     miss every fast gesture, which is the one this menu exists for.
    /// </remarks>
    [Fact]
    public void Aiming_is_by_direction_and_the_middle_is_a_dead_zone() {
        using var editor = Scene();

        var menu = Radial(editor);
        var count = menu.Items.Count;

        Assert.Equal(0, menu.WedgeAt(new Vector2(0f, -400f)));
        Assert.Equal(count / 4, menu.WedgeAt(new Vector2(400f, 0f)));
        Assert.Equal(count / 2, menu.WedgeAt(new Vector2(0f, 400f)));

        // Nothing at all near the centre, which is what makes releasing without moving safe.
        Assert.Equal(-1, menu.WedgeAt(Vector2.Zero));
        Assert.Equal(-1, menu.WedgeAt(new Vector2(menu.DeadZone * 0.5f, 0f)));
    }

    /// <summary>Pressing the key, moving, and letting go runs the wedge that was aimed at.</summary>
    [Fact]
    public void Hold_flick_and_release_runs_what_it_was_aimed_at() {
        using var editor = Scene();

        var menu = Radial(editor);
        var chosen = string.Empty;

        editor.Shell.Commands.Executed += command => chosen = command.Id;

        Assert.True(menu.Hold, "a menu opened from a key press is a gesture in progress");

        // The flick: a move out to a direction, then the key going up.
        menu.Aim(0);
        editor.Ui.KeyUp(InputKey.Q);
        editor.Frames(2);

        Assert.False(menu.IsOpen);
        Assert.NotEmpty(chosen);
    }

    /// <summary>
    ///     ⚠ <b>And letting go without aiming leaves the menu up rather than running something.</b>
    ///     Somebody who tapped the key to look at the menu has not chosen anything, and a pie that
    ///     ran whatever the cursor was nearest would be a menu that fires commands nobody asked for.
    /// </summary>
    [Fact]
    public void A_release_with_nothing_aimed_at_leaves_the_menu_up() {
        using var editor = Scene();

        var menu = Radial(editor);
        var ran = 0;

        editor.Shell.Commands.Executed += _ => ran++;

        Assert.Equal(-1, menu.Highlighted);

        editor.Ui.KeyUp(InputKey.Q);
        editor.Frames(2);

        Assert.True(menu.IsOpen, "the menu should stay up to be clicked");
        Assert.False(menu.Hold, "and it should have stopped being a held gesture");
        Assert.Equal(0, ran);
    }

    [Fact]
    public void Clicking_a_wedge_runs_it_and_closes() {
        using var editor = Scene();

        var menu = Radial(editor);
        var chosen = string.Empty;

        editor.Shell.Commands.Executed += command => chosen = command.Id;

        menu.Items[1].Activate();
        editor.Frames(2);

        Assert.False(menu.IsOpen);
        Assert.NotEmpty(chosen);
    }

    [Fact]
    public void The_context_menu_opens_at_the_cursor_and_lists_its_entries() {
        using var editor = Scene();

        editor.Run("scene.context-menu");
        editor.Frames(2);

        var menu = Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen && candidate.Items.Count > 0)
            ?? throw editor.Fail("the scene context menu did not open");

        var expected = editor.Extensions.All<SceneMenuItem>()
            .Count(entry => (entry.Surface & SceneMenuSurface.Context) != 0 && entry.Mode is null);

        Assert.Equal(expected, menu.Items.Count);

        menu.Close(CloseReason.Code);
        editor.Settle();
    }

    /// <summary>
    ///     ⚠ <b>The claim the whole registration exists for.</b> An entry naming a mode is offered
    ///     only while that mode is active — without it every module's tools would be in every mode's
    ///     pie, which is the wall of buttons modes exist to prevent.
    /// </summary>
    [Fact]
    public void An_entry_for_a_mode_is_offered_only_in_that_mode() {
        using var editor = Scene();

        var before = Radial(editor).Items.Count;

        editor.Document.Root.Children.OfType<RadialMenu>().First().Close(CloseReason.Code);
        editor.Settle();

        // A verb belonging to a mode nothing has activated.
        editor.Shell.Commands.Add(
            new EditorCommand("test.carve", new StringId("test.carve", "Carve"), static () => { })
        );

        editor.Extensions.Add(new SceneMenuItem("test.carve", SceneMenuSurface.Radial) { Mode = "test.sculpting" });
        editor.Settle();

        Assert.Equal(before, Radial(editor).Items.Count);

        editor.Document.Root.Children.OfType<RadialMenu>().First().Close(CloseReason.Code);
        editor.Settle();

        // And the same entry, once its mode is the active one.
        editor.Shell.Modes.Add(new Mode("test.sculpting", "Sculpting"));

        Assert.True(editor.Shell.Modes.Activate("test.sculpting"));
        editor.Settle();

        var after = Radial(editor);

        Assert.Equal(before + 1, after.Items.Count);
        Assert.Contains(after.Items, item => item.Label == "Carve");
    }

    /// <summary>A mode with nothing but an id and a name, for the test above.</summary>
    sealed class Mode(string id, string title) : IEditorMode {
        public string Id { get; } = id;

        public StringId Title { get; } = new(id, title);

        public PathBuilder? Icon => null;

        public string? Context => null;

        public string? Panel => null;

        public IReadOnlyList<ToolbarEntry> Toolbar => [];

        public void Register(EditorShell shell) { }

        public void Unregister(EditorShell shell) { }

        public void Activated() { }

        public void Deactivated() { }

        public bool Pointer(PointerEvent args) => false;

        public bool Key(KeyEvent args) => false;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
