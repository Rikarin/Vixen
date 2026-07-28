// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>The actions, judged by what the framework saw rather than by what was called.</summary>
/// <remarks>
///     ⚠ Every one of these asserts on an event the document routed. A harness that reached in and
///     invoked a handler directly would pass all of them and would still be useless, because the
///     failures worth catching — a modal in the way, <c>pointer-events</c> on an overlay, a capture
///     that never released — all live in the routing that such a harness skips.
/// </remarks>
public class InteractionTests {
    static UiTest Fixture(out UiElement button) {
        var ui = UiTest.Create(400f, 300f);

        ui.Load("""
            root { width: 400px; height: 300px; }
            .btn { width: 100px; height: 40px; }
        """);

        button = ui.Create("button", ui.Document.Root, "save", "btn");
        button.Text = "Save";
        button.Focusable = true;
        ui.Frame();
        return ui;
    }

    [Fact]
    public void A_click_arrives_as_routed_pointer_events_at_the_element_centre() {
        using var ui = Fixture(out var button);

        var seen = new List<PointerEvent>();
        button.AddHandler<PointerEvent>((_, args) => seen.Add(args));

        ui.Get("#save").Click();

        Assert.Equal(
            [PointerAction.Moved, PointerAction.Pressed, PointerAction.Released],
            seen.Select(args => args.Action)
        );

        // The centre of a hundred-by-forty box at the origin.
        Assert.All(seen, args => Assert.Equal(50f, args.X, 0.001f));
        Assert.All(seen, args => Assert.Equal(20f, args.Y, 0.001f));
    }

    [Fact]
    public void A_click_is_refused_when_something_is_on_top_of_it() {
        using var ui = Fixture(out _);
        ui.Load(".modal { position: absolute; left: 0; top: 0; width: 400px; height: 300px; }");
        ui.Create("div", ui.Document.Root, "backdrop", "modal");
        ui.Frame();

        var clicked = false;
        ui.Get("#save").Elements[0].AddHandler<PointerEvent>((_, _) => clicked = true);

        var failure = Assert.Throws<UiTestException>(() => ui.Get("#save").Click());

        // ⚠ The message names what was in the way. "The click did nothing" is the bug report this
        // assertion exists to turn into a sentence.
        Assert.Contains("#backdrop", failure.Message, StringComparison.Ordinal);
        Assert.Contains("on top of", failure.Message, StringComparison.Ordinal);
        Assert.False(clicked);
    }

    [Fact]
    public void Force_clicks_through_it_and_the_click_goes_where_the_geometry_says() {
        using var ui = Fixture(out _);
        ui.Load(".modal { position: absolute; left: 0; top: 0; width: 400px; height: 300px; }");
        var backdrop = ui.Create("div", ui.Document.Root, "backdrop", "modal");
        ui.Frame();

        var reached = false;
        backdrop.AddHandler<PointerEvent>((_, _) => reached = true);

        ui.Get("#save").Click(force: true);

        // Which is the point: forcing does not redirect the event, it only stops the harness
        // complaining. The click still lands on the backdrop.
        Assert.True(reached);
    }

    [Fact]
    public void An_overlay_that_ignores_the_pointer_does_not_block_a_click() {
        using var ui = Fixture(out var button);
        ui.Load(".ghost { position: absolute; left: 0; top: 0; width: 400px; height: 300px; pointer-events: none; }");
        ui.Create("div", ui.Document.Root, "tooltip", "ghost");
        ui.Frame();

        var clicked = false;
        button.AddHandler<PointerEvent>((_, args) => clicked |= args.Action == PointerAction.Pressed);

        ui.Get("#save").Click();
        Assert.True(clicked);
    }

    [Fact]
    public void Hovering_sets_the_state_a_stylesheet_reads() {
        using var ui = Fixture(out var button);

        ui.Get("#save").Hover();

        Assert.True((button.State & ElementState.Hover) != 0);
        ui.Get("#save").ShouldHaveState(ElementState.Hover);
    }

    [Fact]
    public void Typing_arrives_one_character_at_a_time() {
        using var ui = Fixture(out var button);

        var typed = new List<string>();
        button.AddHandler<TextInputEvent>((_, args) => typed.Add(args.Text));

        ui.Get("#save").Type("Hi!");

        // ⚠ Three events, not one. A text box that appends what it is given passes either way; one
        // that maintains a caret or an undo stack does not.
        Assert.Equal(["H", "i", "!"], typed);
        ui.Get("#save").ShouldBeFocused();
    }

    [Fact]
    public void A_key_press_reaches_the_focus_with_the_modifiers_being_held() {
        using var ui = Fixture(out var button);

        var seen = new List<KeyEvent>();
        button.AddHandler<KeyEvent>((_, args) => seen.Add(args));

        ui.Hold(ModifierKeys.Control);
        ui.Get("#save").PressKey(InputKey.S);

        Assert.Equal([KeyAction.Pressed, KeyAction.Released], seen.Select(args => args.Action));
        Assert.All(seen, args => Assert.Equal(InputKey.S, args.Key));
        Assert.All(seen, args => Assert.Equal(ModifierKeys.Control, args.Modifiers));
    }

    [Fact]
    public void Tab_moves_the_focus_in_document_order() {
        using var ui = Fixture(out var first);
        var second = ui.Create("button", ui.Document.Root, "cancel", "btn");
        second.Focusable = true;
        second.Text = "Cancel";
        ui.Frame();

        ui.Get("#save").Focus().ShouldBeFocused();

        Assert.True(ui.Tab());
        ui.Get("#cancel").ShouldBeFocused();

        Assert.True(ui.Tab(backwards: true));
        Assert.True(first.IsFocused);
    }

    [Fact]
    public void A_wheel_is_hit_tested_and_bubbles() {
        using var ui = Fixture(out var button);

        var seen = new List<WheelEvent>();
        ui.Document.Root.AddHandler<WheelEvent>((_, args) => seen.Add(args));

        ui.Get("#save").Scroll(0f, 120f);

        var wheel = Assert.Single(seen);
        Assert.Equal(120f, wheel.DeltaY, 0.001f);
    }

    [Fact]
    public void A_drag_is_broken_into_moves_so_it_reads_as_one() {
        using var ui = Fixture(out var button);

        var stages = new List<DragStage>();
        button.AddHandler<DragEvent>((_, args) => stages.Add(args.Stage));

        ui.Get("#save").DragBy(120f, 0f);

        // ⚠ A press, one jump and a release is a tap somewhere else as far as the recogniser is
        // concerned. This is the assertion that stops DragSteps quietly becoming one.
        Assert.Contains(DragStage.Started, stages);
        Assert.Contains(DragStage.Completed, stages);
    }

    [Fact]
    public void A_long_press_needs_the_clock_to_move_and_nothing_else() {
        using var ui = Fixture(out var button);

        var pressed = 0;
        button.AddHandler<LongPressEvent>((_, _) => pressed++);

        ui.Get("#save").Press();
        ui.Advance(TimeSpan.FromSeconds(1));

        // The gesture that fires because nothing happened, which is why the harness ticks the
        // recogniser rather than only feeding it input.
        Assert.Equal(1, pressed);
    }

    [Fact]
    public void An_action_refuses_to_pick_one_of_several() {
        using var ui = Fixture(out _);
        ui.Create("button", ui.Document.Root, "cancel", "btn");
        ui.Frame();

        // ⚠ A selector that matched two buttons and clicked the first is a test that keeps passing
        // after somebody adds a third in front, having silently changed what it tests.
        var failure = Assert.Throws<UiTestException>(() => ui.Get(".btn").Click());
        Assert.Contains("2 elements", failure.Message, StringComparison.Ordinal);

        ui.Get(".btn").First().Click();
    }
}
