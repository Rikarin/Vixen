// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>Two fingers: pinch, rotate, and the drags they replace.</summary>
/// <remarks>
///     ⚠ <b>The suppression is as much of the feature as the gesture is.</b> Two fingers that both
///     pan and pinch move a map twice as far as either gesture asked for, so the tests that assert
///     the drags are cancelled are not bookkeeping — they are the difference between a working pinch
///     and one that fights whatever else the same two pointers were doing.
/// </remarks>
public class TransformTests {
    static UiTest Fixture(out UiElement map) {
        var ui = UiTest.Create(400f, 300f);

        ui.Load("""
            root { width: 400px; height: 300px; }
            .map { width: 300px; height: 200px; }
            .tile { position: absolute; left: 0; top: 0; width: 300px; height: 200px; }
        """);

        map = ui.Create("div", ui.Document.Root, "map", "map");
        ui.Create("div", map, "tile", "tile");
        ui.Frame();
        return ui;
    }

    static List<TransformEvent> Watch(UiElement element) {
        var seen = new List<TransformEvent>();
        element.AddHandler<TransformEvent>((_, args) => seen.Add(args));
        return seen;
    }

    [Fact]
    public void Spreading_two_fingers_is_a_pinch_that_scales_up() {
        using var ui = Fixture(out var map);
        var seen = Watch(map);

        ui.Get("#map").Pinch(2f);

        Assert.Equal(TransformStage.Started, seen[0].Stage);
        Assert.Equal(TransformStage.Completed, seen[^1].Stage);

        // Twice as far apart as they started, to within the last step's rounding.
        Assert.Equal(2f, seen[^1].Scale, 0.01f);
        Assert.Equal(0f, seen[^1].Rotation, 0.01f);
    }

    [Fact]
    public void Bringing_them_together_scales_down() {
        using var ui = Fixture(out var map);
        var seen = Watch(map);

        ui.Get("#map").Pinch(0.5f);

        Assert.Equal(0.5f, seen[^1].Scale, 0.01f);
    }

    [Fact]
    public void Turning_them_is_a_rotation_in_radians() {
        using var ui = Fixture(out var map);
        var seen = Watch(map);

        ui.Get("#map").Pinch(1f, rotation: 90f);

        Assert.Equal(MathF.PI / 2f, seen[^1].Rotation, 0.01f);

        // A pure rotation does not change how far apart the fingers are.
        Assert.Equal(1f, seen[^1].Scale, 0.01f);
    }

    [Fact]
    public void A_rotation_past_half_a_turn_accumulates_rather_than_wrapping() {
        using var ui = Fixture(out var map);
        var seen = Watch(map);

        ui.Get("#map").Pinch(1f, rotation: 300f);

        // ⚠ Atan2 comes back in (-π, π], so an angle taken against the start rather than unwrapped
        // against the previous sample reports −60° here. A knob bound to that spins backwards once
        // per revolution.
        Assert.Equal(float.DegreesToRadians(300f), seen[^1].Rotation, 0.05f);
    }

    [Fact]
    public void Two_fingers_on_one_element_target_it_and_bubble_from_there() {
        using var ui = Fixture(out var map);

        var onMap = Watch(map);
        var onTile = new List<TransformEvent>();

        ui.Get("#tile").Elements[0].AddHandler<TransformEvent>(
            (_, args) => onTile.Add(args),
            RoutingStrategy.Direct
        );

        ui.Get("#map").Pinch(1.5f);

        // Both fingers are on the tile, so the tile is what they are agreeing about — and the map
        // hears it anyway, by bubbling, like any other event.
        Assert.NotEmpty(onTile);
        Assert.NotEmpty(onMap);
    }

    [Fact]
    public void Two_fingers_on_different_elements_target_what_contains_both() {
        var ui = UiTest.Create(400f, 300f);

        ui.Load("""
            root { width: 400px; height: 300px; }
            .map { width: 300px; height: 200px; }
            .half { width: 150px; height: 200px; }
        """);

        using var owned = ui;
        var map = ui.Create("div", ui.Document.Root, "map", "map");
        var left = ui.Create("div", map, "left", "half");
        var right = ui.Create("div", map, "right", "half");
        ui.Frame();

        var onMap = new List<TransformEvent>();
        map.AddHandler<TransformEvent>((_, args) => onMap.Add(args), RoutingStrategy.Direct);

        var onHalves = new List<TransformEvent>();
        left.AddHandler<TransformEvent>((_, args) => onHalves.Add(args), RoutingStrategy.Direct);
        right.AddHandler<TransformEvent>((_, args) => onHalves.Add(args), RoutingStrategy.Direct);

        // One finger on each half, then spread.
        ui.MovePointer(75f, 100f, UiTest.PointerId);
        ui.PressPointer(pointer: UiTest.PointerId);
        ui.MovePointer(225f, 100f, UiTest.SecondPointerId);
        ui.PressPointer(pointer: UiTest.SecondPointerId);
        ui.Frame();

        for (var step = 1; step <= 8; step++) {
            ui.MovePointer(75f - (step * 5f), 100f, UiTest.PointerId);
            ui.MovePointer(225f + (step * 5f), 100f, UiTest.SecondPointerId);
            ui.Frame();
        }

        // ⚠ The nearest common ancestor, not the first finger's target. A gesture delivered to the
        // left half is one the map would only hear about if the halves happened to bubble it, and a
        // pinch belongs to neither half — it is the thing containing both that is being pinched.
        Assert.NotEmpty(onMap);
        Assert.Empty(onHalves);
    }

    [Fact]
    public void A_second_finger_arriving_mid_drag_cancels_the_drag_and_takes_over() {
        using var ui = Fixture(out var map);

        var drags = new List<DragStage>();
        map.AddHandler<DragEvent>((_, args) => drags.Add(args.Stage));

        var transforms = Watch(map);

        // One finger, dragging properly — well past the slop, so this is a drag by any measure.
        ui.MovePointer(100f, 100f, UiTest.PointerId);
        ui.PressPointer(pointer: UiTest.PointerId);
        ui.Frame();

        for (var step = 1; step <= 4; step++) {
            ui.MovePointer(100f + (step * 10f), 100f, UiTest.PointerId);
            ui.Frame();
        }

        Assert.Contains(DragStage.Started, drags);
        Assert.Empty(transforms);

        // A second finger lands, and the two start spreading.
        ui.MovePointer(220f, 100f, UiTest.SecondPointerId);
        ui.PressPointer(pointer: UiTest.SecondPointerId);
        ui.Frame();

        for (var step = 1; step <= 4; step++) {
            ui.MovePointer(140f - (step * 10f), 100f, UiTest.PointerId);
            ui.MovePointer(220f + (step * 10f), 100f, UiTest.SecondPointerId);
            ui.Frame();
        }

        ui.ReleasePointer(pointer: UiTest.SecondPointerId);
        ui.ReleasePointer(pointer: UiTest.PointerId);
        ui.Frame();

        Assert.NotEmpty(transforms);

        // ⚠ The drag in progress is Cancelled, not Completed, and nothing moves it afterwards. A
        // completed drag beside a completed pinch is a map that panned and zoomed from one pair of
        // fingers and moved twice as far as either gesture asked for.
        Assert.Contains(DragStage.Cancelled, drags);
        Assert.DoesNotContain(DragStage.Completed, drags);
        Assert.DoesNotContain(DragStage.Moved, drags.Skip(drags.IndexOf(DragStage.Cancelled) + 1));
    }

    [Fact]
    public void Fingers_taken_by_a_pinch_do_not_also_tap() {
        using var ui = Fixture(out var map);

        var taps = 0;
        map.AddHandler<TapEvent>((_, _) => taps++);

        ui.Get("#map").Pinch(1.6f);

        // Two fingers pinching and lifting is not also two taps, however still they were at the end.
        Assert.Equal(0, taps);
    }

    [Fact]
    public void Two_fingers_that_do_not_move_relative_to_each_other_stay_two_drags() {
        using var ui = Fixture(out var map);

        var drags = new List<DragStage>();
        map.AddHandler<DragEvent>((_, args) => drags.Add(args.Stage));

        var transforms = Watch(map);

        // Both fingers translate by the same amount, so their separation and angle never change.
        ui.MovePointer(120f, 100f, UiTest.PointerId);
        ui.PressPointer(pointer: UiTest.PointerId);
        ui.MovePointer(180f, 100f, UiTest.SecondPointerId);
        ui.PressPointer(pointer: UiTest.SecondPointerId);
        ui.Frame();

        for (var step = 1; step <= 8; step++) {
            ui.MovePointer(120f + (step * 5f), 100f, UiTest.PointerId);
            ui.MovePointer(180f + (step * 5f), 100f, UiTest.SecondPointerId);
            ui.Frame();
        }

        ui.ReleasePointer(pointer: UiTest.SecondPointerId);
        ui.ReleasePointer(pointer: UiTest.PointerId);
        ui.Frame();

        // ⚠ Which is right, and is the reason the slop exists. A two-finger pan is two drags until
        // something about their relationship changes; treating any second finger as a pinch would
        // make every two-handed scroll a zoom.
        Assert.Empty(transforms);
        Assert.Contains(DragStage.Started, drags);
        Assert.Contains(DragStage.Completed, drags);
    }

    [Fact]
    public void A_finger_held_still_during_a_pinch_is_not_a_long_press() {
        using var ui = Fixture(out var map);

        var longPresses = 0;
        map.AddHandler<LongPressEvent>((_, _) => longPresses++);

        // ⚠ The fingers stay down while the clock runs, which is the whole test. Advancing after
        // they lift asserts nothing: there is no press left to become a long one.
        ui.MovePointer(130f, 100f, UiTest.PointerId);
        ui.PressPointer(pointer: UiTest.PointerId);
        ui.MovePointer(170f, 100f, UiTest.SecondPointerId);
        ui.PressPointer(pointer: UiTest.SecondPointerId);
        ui.Frame();

        for (var step = 1; step <= 4; step++) {
            ui.MovePointer(130f - (step * 5f), 100f, UiTest.PointerId);
            ui.MovePointer(170f + (step * 5f), 100f, UiTest.SecondPointerId);
            ui.Frame();
        }

        // Both fingers now still, and well past the half-second threshold.
        ui.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, longPresses);
    }

    [Fact]
    public void The_midpoint_travels_with_the_fingers() {
        using var ui = Fixture(out var map);
        var seen = Watch(map);

        var (x, y) = (150f, 100f);

        ui.MovePointer(x - 20f, y, UiTest.PointerId);
        ui.PressPointer(pointer: UiTest.PointerId);
        ui.MovePointer(x + 20f, y, UiTest.SecondPointerId);
        ui.PressPointer(pointer: UiTest.SecondPointerId);
        ui.Frame();

        // Spread and shift right by forty at the same time.
        ui.MovePointer(x + 20f, y, UiTest.PointerId);
        ui.MovePointer(x + 100f, y, UiTest.SecondPointerId);
        ui.Frame();

        Assert.NotEmpty(seen);
        Assert.Equal(x + 60f, seen[^1].X, 0.01f);
        Assert.Equal(2f, seen[^1].Scale, 0.01f);
    }
}
