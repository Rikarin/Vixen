// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Xunit;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>A pane in a document, driven by events rather than by calling its handlers.</summary>
/// <remarks>
///     ⚠ <b>A real <c>UiDocument</c> and no theme.</b> What flight depends on is the routing — a
///     press focuses the viewport, keys go to the focus and bubble outwards from it — and that is
///     the document's, not a stylesheet's. The one rule loaded is the box, because an element with
///     no size is one no pointer event can land in.
/// </remarks>
sealed class Pane : IDisposable {
    TimeSpan clock;

    public Pane() {
        Document = new UiDocument(800f, 600f);
        Document.Load("root { width: 800px; height: 600px; } viewport { width: 800px; height: 600px; }");

        Control = Document.Root.Add<ViewportControl>();
        Document.Update();

        Control.Refresh();
        Viewport = new SceneViewport(Control, new Selection<Entity>());
    }

    public UiDocument Document { get; }

    public ViewportControl Control { get; }

    public SceneViewport Viewport { get; }

    public EditorCamera Camera => Viewport.Camera;

    public void Press(PointerButton button) => Pointer(PointerAction.Pressed, button);

    public void Release(PointerButton button) => Pointer(PointerAction.Released, button);

    /// <summary>Sends a key and says whether anything took it.</summary>
    public bool Key(InputKey key, KeyAction action, ModifierKeys modifiers = ModifierKeys.None) {
        clock += TimeSpan.FromMilliseconds(16);

        var args = new KeyEvent { Key = key, Action = action, Modifiers = modifiers, Timestamp = clock };
        Document.Dispatch(args);

        return args.Handled;
    }

    public void Hold(InputKey key) => Key(key, KeyAction.Pressed);

    public void Let(InputKey key) => Key(key, KeyAction.Released);

    /// <summary>One frame of the length the host would have reported.</summary>
    public void Frame(float seconds = 1f / 60f) {
        clock += TimeSpan.FromSeconds(seconds);
        Viewport.Update(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose() {
        Viewport.Dispose();
        Document.Dispose();
    }

    void Pointer(PointerAction action, PointerButton button) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new PointerEvent {
                X = 400f,
                Y = 300f,
                Action = action,
                Button = button,
                Timestamp = clock
            }
        );
    }
}

/// <summary>WASDQE flight: what turns it on, what it moves, and what it takes from the shell.</summary>
public class FlightTests {
    [Fact]
    public void The_keys_do_nothing_until_the_fly_button_is_held() {
        using var pane = new Pane();
        var before = pane.Camera.Pivot;

        // W with no button down is the shell's translate-gizmo shortcut, and the viewport must not
        // be quietly eating it — the assertion that matters here is the false, not the position.
        Assert.False(pane.Key(InputKey.W, KeyAction.Pressed));
        pane.Frame();

        Assert.False(pane.Viewport.IsFlying);
        Assert.Equal(before, pane.Camera.Pivot);
    }

    [Fact]
    public void Holding_the_fly_button_and_pressing_forward_moves_along_the_view() {
        using var pane = new Pane();
        var before = pane.Camera.Pivot;

        pane.Press(PointerButton.Secondary);
        Assert.True(pane.Viewport.IsFlying);

        // ⚠ Consumed, and this is the assertion the whole design is for: the same key is the
        // translate gizmo in the shell's keymap, and it must not fire while somebody is flying.
        Assert.True(pane.Key(InputKey.W, KeyAction.Pressed));
        pane.Frame();

        var moved = pane.Camera.Pivot - before;

        Assert.True(moved.Length() > 0f);
        Assert.True(Vector3.Dot(Vector3.Normalize(moved), pane.Camera.Forward) > 0.999f);
    }

    [Fact]
    public void Each_key_goes_the_way_it_is_labelled() {
        // An unturned camera looks down −Z, so the six directions are the world's axes and a test
        // can name them.
        Assert.Equal(new Vector3(0f, 0f, -1f), Direction(InputKey.W));
        Assert.Equal(new Vector3(0f, 0f, 1f), Direction(InputKey.S));
        Assert.Equal(new Vector3(-1f, 0f, 0f), Direction(InputKey.A));
        Assert.Equal(new Vector3(1f, 0f, 0f), Direction(InputKey.D));
        Assert.Equal(new Vector3(0f, -1f, 0f), Direction(InputKey.Q));
        Assert.Equal(new Vector3(0f, 1f, 0f), Direction(InputKey.E));

        static Vector3 Direction(InputKey key) {
            using var pane = new Pane();

            pane.Press(PointerButton.Secondary);
            pane.Hold(key);
            pane.Frame();

            var unit = Vector3.Normalize(pane.Camera.Pivot);

            return new(MathF.Round(unit.X), MathF.Round(unit.Y), MathF.Round(unit.Z));
        }
    }

    [Fact]
    public void Two_keys_at_once_are_not_faster_than_one() {
        using var pane = new Pane();

        pane.Press(PointerButton.Secondary);
        pane.Hold(InputKey.W);
        pane.Frame();

        var straight = pane.Camera.Pivot;

        pane.Hold(InputKey.D);
        pane.Frame();

        // The same step, in a different direction. The diagonal being the quickest way across a
        // level is the oldest bug in flight.
        Assert.Equal(straight.Length(), (pane.Camera.Pivot - straight).Length(), 4);
    }

    [Fact]
    public void Opposite_keys_cancel_rather_than_drift() {
        using var pane = new Pane();

        pane.Press(PointerButton.Secondary);
        pane.Hold(InputKey.W);
        pane.Hold(InputKey.S);
        pane.Frame();

        Assert.Equal(Vector3.Zero, pane.Camera.Pivot);
        Assert.False(pane.Camera.Pivot.IsNaN);
    }

    [Fact]
    public void Shift_is_faster_and_can_be_pressed_after_the_direction() {
        using var pane = new Pane();

        pane.Press(PointerButton.Secondary);
        pane.Hold(InputKey.W);
        pane.Frame();

        var ordinary = pane.Camera.Pivot.Length();

        // ⚠ Shift arrives on its own, with W still down. Read only off the modifiers of *other*
        // keys, holding shift mid-flight would do nothing until the next thing that was pressed.
        pane.Hold(InputKey.LeftShift);
        pane.Frame();

        Assert.Equal(ordinary * 4f, pane.Camera.Pivot.Length() - ordinary, 4);
    }

    [Fact]
    public void Letting_go_of_a_key_stops_that_direction() {
        using var pane = new Pane();

        pane.Press(PointerButton.Secondary);
        pane.Hold(InputKey.W);
        pane.Frame();

        var stopped = pane.Camera.Pivot;

        pane.Let(InputKey.W);
        pane.Frame();

        Assert.True(pane.Viewport.IsFlying);
        Assert.Equal(stopped, pane.Camera.Pivot);
    }

    [Fact]
    public void Letting_go_of_the_button_stops_the_camera_even_with_a_key_still_down() {
        using var pane = new Pane();

        pane.Press(PointerButton.Secondary);
        pane.Hold(InputKey.W);
        pane.Frame();

        // ⚠ The mouse first and the key never — which is how most people end a flight. A held bit
        // left behind is a camera that sets off by itself the next time the button goes down.
        pane.Release(PointerButton.Secondary);

        var stopped = pane.Camera.Pivot;
        pane.Frame();

        Assert.False(pane.Viewport.IsFlying);
        Assert.Equal(stopped, pane.Camera.Pivot);

        pane.Press(PointerButton.Secondary);
        pane.Frame();

        Assert.Equal(stopped, pane.Camera.Pivot);
    }

    [Fact]
    public void Losing_the_focus_ends_the_flight() {
        using var pane = new Pane();

        pane.Press(PointerButton.Secondary);
        pane.Hold(InputKey.W);

        pane.Document.Focus(null);

        Assert.False(pane.Viewport.IsFlying);

        var stopped = pane.Camera.Pivot;
        pane.Frame();

        Assert.Equal(stopped, pane.Camera.Pivot);
    }

    [Fact]
    public void A_stalled_frame_does_not_launch_the_camera_across_the_level() {
        using var pane = new Pane();

        pane.Press(PointerButton.Secondary);
        pane.Hold(InputKey.W);

        // A shader compile, a scene load, a window dragged between displays.
        pane.Frame(seconds: 4f);

        var far = pane.Camera.Pivot.Length();
        var limit = pane.Camera.FlySpeed * pane.Camera.Distance * SceneViewport.MaximumFlyStep;

        Assert.Equal(limit, far, 3);
    }

    [Fact]
    public void The_bindings_are_positions_and_can_be_changed() {
        Assert.Equal(FlyAxis.Forward, FlyBindings.Wasdqe.Axis(InputKey.W));
        Assert.Equal(FlyAxis.Down, FlyBindings.Wasdqe.Axis(InputKey.Q));
        Assert.Equal(FlyAxis.None, FlyBindings.Wasdqe.Axis(InputKey.R));
        Assert.Equal(FlyAxis.None, FlyBindings.Wasdqe.Axis(InputKey.Unknown));

        using var pane = new Pane();
        pane.Viewport.FlyKeys = FlyBindings.Wasdqe with { Forward = InputKey.Up, Back = InputKey.Down };

        pane.Press(PointerButton.Secondary);

        Assert.False(pane.Key(InputKey.W, KeyAction.Pressed));
        Assert.True(pane.Key(InputKey.Up, KeyAction.Pressed));

        pane.Frame();

        Assert.True(Vector3.Dot(Vector3.Normalize(pane.Camera.Pivot), pane.Camera.Forward) > 0.999f);
    }

    [Fact]
    public void Only_the_right_button_flies() {
        Assert.True(SceneViewport.Flies(PointerButton.Secondary));
        Assert.False(SceneViewport.Flies(PointerButton.Primary));
        Assert.False(SceneViewport.Flies(PointerButton.Middle));

        using var pane = new Pane();
        pane.Press(PointerButton.Middle);

        Assert.False(pane.Viewport.IsFlying);
        Assert.False(pane.Key(InputKey.W, KeyAction.Pressed));
    }
}
