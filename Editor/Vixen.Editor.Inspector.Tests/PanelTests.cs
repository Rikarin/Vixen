// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>The two affordances the panel itself has: the lock, and the menu on a row.</summary>
public class PanelTests : IDisposable {
    readonly UiDocument document = new(400f, 800f);

    /// <remarks>
    ///     ⚠ <b>The sheet is installed, unlike in the drawer tests.</b> These aim a pointer at a row,
    ///     and without the stylesheet the panel has no width and no row height — every rectangle is
    ///     empty, so every click lands at the origin and the menu is asked about no row at all.
    /// </remarks>
    public PanelTests() => InspectorTheme.Install(document);

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    InspectorView View() {
        var view = document.Root.Add<InspectorView>();

        document.Update();
        return view;
    }

    /// <summary>
    ///     ⚠ <b>The two-handed operation the lock exists for.</b> Dragging an asset into a field on
    ///     one object means selecting another to find it, at which point the first is gone — so
    ///     without a lock the gesture is impossible rather than merely awkward.
    /// </summary>
    [Fact]
    public void A_locked_inspector_keeps_what_it_is_showing() {
        var view = View();
        var water = new WaterMaterial();

        view.Inspect(water);
        Assert.Same(water, Assert.Single(view.Targets));

        view.IsLocked = true;
        view.Inspect(new SparkEmitter());

        Assert.Same(water, Assert.Single(view.Targets));

        // Including the clear, which is the case that would otherwise empty a locked panel the
        // moment somebody clicked on nothing.
        view.Clear();
        Assert.Same(water, Assert.Single(view.Targets));
    }

    [Fact]
    public void Unlocking_lets_the_next_selection_through_and_says_so() {
        var view = View();
        var changes = 0;

        view.LockChanged += _ => changes++;
        view.Inspect(new WaterMaterial());

        view.IsLocked = true;
        view.IsLocked = false;

        Assert.Equal(2, changes);

        var emitter = new SparkEmitter();

        view.Inspect(emitter);
        Assert.Same(emitter, Assert.Single(view.Targets));
    }

    [Fact]
    public void The_lock_is_the_toggle_button_rather_than_a_second_copy_of_the_state() {
        var view = View();

        view.Lock.Activate();

        Assert.True(view.IsLocked);
        Assert.True(view.Lock.IsChecked);
    }

    /// <summary>
    ///     ⚠ <b>Four verbs that existed as methods nothing could reach.</b> Copy, Paste, Reset and
    ///     Revert have been on the view since it was written; the reset button was the only one with
    ///     an affordance, and paste — the one that saves the most time — had none at all.
    /// </summary>
    [Fact]
    public void The_row_menu_offers_the_four_verbs() {
        var view = View();

        view.Inspect(new WaterMaterial());

        var menu = view.Contextualise();

        Assert.Equal(
            ["Copy", "Paste", "Reset to Default", "Revert to Prefab"],
            menu.Items.Select(item => item.Label)
        );
    }

    [Fact]
    public void Asking_for_the_menu_twice_gives_the_same_one() {
        var view = View();

        Assert.Same(view.Contextualise(), view.Contextualise());
    }

    /// <summary>
    ///     ⚠ Enablement is asked on the way open rather than pushed. Whether there is something on
    ///     the clipboard, whether this member differs from its default and whether it came from a
    ///     prefab are three questions with three answers per row.
    /// </summary>
    [Fact]
    public void A_line_is_disabled_when_it_would_do_nothing() {
        var view = View();

        view.Clipboard = new PropertyClipboard();
        view.Inspect(new WaterMaterial());

        var menu = view.Contextualise();

        Open(view, menu, row: null);

        // No row under the pointer: every line is about a member, so none of them mean anything.
        Assert.All(menu.Items, item => Assert.True(item.Disabled));

        var roughness = view.Rows.Single(row => row.Field.Member.Name == "Roughness");

        Open(view, menu, roughness);

        Assert.False(menu.Items[0].Disabled);

        // Nothing has been copied yet, and the value is still the type's own default.
        Assert.True(menu.Items[1].Disabled);
        Assert.True(menu.Items[2].Disabled);

        // Nothing came from a prefab, so revert stays out of reach however the row is edited.
        Assert.True(menu.Items[3].Disabled);
    }

    [Fact]
    public void Copying_a_row_and_pasting_into_another_goes_through_the_menu() {
        var view = View();

        view.Clipboard = new PropertyClipboard();
        view.Inspect(new WaterMaterial());

        var menu = view.Contextualise();
        var roughness = view.Rows.Single(row => row.Field.Member.Name == "Roughness");

        Assert.True(roughness.Field.Write(0.75f));

        Open(view, menu, roughness);
        menu.Items[0].Activate();

        var level = view.Rows.Single(row => row.Field.Member.Name == "Level");

        Open(view, menu, level);

        Assert.False(menu.Items[1].Disabled);
        menu.Items[1].Activate();

        Assert.Equal(0.75f, view.Targets.OfType<WaterMaterial>().Single().Level);
    }

    /// <summary>Aims the menu at a row the way a secondary click does, then opens it.</summary>
    static void Open(InspectorView view, ContextMenu menu, InspectorRow? row) {
        // ⚠ Laid out first. A row built since the last pass has an empty rectangle, and a click at
        // the centre of one of those is a click at the origin — which is how a test aims a menu at
        // a row and gets the answer for no row at all.
        view.Document.Update();

        var (x, y) = row is null ? (-1f, -1f) : Centre(row);

        view.Document.Dispatch(
            new PointerEvent {
                X = x,
                Y = y,
                Action = PointerAction.Pressed,
                Button = PointerButton.Secondary,
                Modifiers = ModifierKeys.None,
                Timestamp = TimeSpan.Zero
            }
        );

        menu.Close(CloseReason.Code);
        menu.OpenAt(Math.Max(x, 0f), Math.Max(y, 0f));
    }

    static (float X, float Y) Centre(UiElement element) =>
        (element.Bounds.X + (element.Bounds.Width * 0.5f), element.Bounds.Y + (element.Bounds.Height * 0.5f));
}
