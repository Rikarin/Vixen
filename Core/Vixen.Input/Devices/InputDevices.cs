// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Input;

/// <summary>What a finger just did.</summary>
public enum TouchPhase : byte {
    /// <summary>It went down.</summary>
    Began = 0,

    /// <summary>It moved while down.</summary>
    Moved = 1,

    /// <summary>It lifted, or the gesture was cancelled.</summary>
    Ended = 2
}

/// <summary>One connected device, as the action system sees it.</summary>
public interface IInputDevice {
    /// <summary>Which family it belongs to.</summary>
    InputDeviceKind Kind { get; }

    /// <summary>The id its events arrive under, or <c>0</c> for the singleton devices.</summary>
    int DeviceId { get; }

    /// <summary>Its name, for a settings screen.</summary>
    string Name { get; }

    /// <summary>Whether it is still there.</summary>
    bool IsConnected { get; }

    /// <summary>Reads one of its controls.</summary>
    /// <param name="control">Which control. Its <see cref="InputControl.Index" /> is ignored — the
    /// caller has already chosen the device.</param>
    /// <returns>The value: <c>[0, 1]</c> for a button, whatever the control's range is otherwise.</returns>
    float ReadValue(InputControl control);
}

/// <summary>The keyboard, as a set of physical key positions that are down or not.</summary>
/// <remarks>
///     No layout, no characters, no modifier flags: a modifier is a key like any other and
///     <c>&lt;Keyboard&gt;/leftShift</c> is how a binding names it. Typed text is
///     <c>ITextInput</c>'s job and never arrives here.
/// </remarks>
public sealed class KeyboardDevice : IInputDevice {
    readonly bool[] down = new bool[(int)InputKey.Back + 1];

    /// <inheritdoc />
    public InputDeviceKind Kind => InputDeviceKind.Keyboard;

    /// <inheritdoc />
    public int DeviceId => 0;

    /// <inheritdoc />
    public string Name => "Keyboard";

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <summary>Whether a key is held.</summary>
    /// <param name="key">Which key.</param>
    public bool IsDown(InputKey key) => (uint)key < (uint)down.Length && down[(int)key];

    /// <inheritdoc />
    public float ReadValue(InputControl control) => IsDown((InputKey)control.Code) ? 1f : 0f;

    internal bool Set(InputKey key, bool pressed) {
        if ((uint)key >= (uint)down.Length || down[(int)key] == pressed) {
            return false;
        }

        down[(int)key] = pressed;
        return true;
    }

    internal void Clear() => Array.Clear(down);

    internal void CollectActuated(List<InputControl> into) {
        for (var code = 0; code < down.Length; code++) {
            if (down[code]) {
                into.Add(InputControl.Key((InputKey)code));
            }
        }
    }
}

/// <summary>The pointer: its buttons, where it is, and how far it moved this frame.</summary>
/// <remarks>
///     <b>Motion is per frame and is cleared by <see cref="InputDeviceSet.BeginFrame" />.</b> A camera
///     reads a delta, and a delta that kept its last value once the mouse stopped would spin the
///     camera forever. Position is <em>not</em> cleared, because a position is a state.
/// </remarks>
public sealed class MouseDevice : IInputDevice {
    readonly bool[] down = new bool[(int)MouseControl.Extra2 + 1];

    /// <inheritdoc />
    public InputDeviceKind Kind => InputDeviceKind.Mouse;

    /// <inheritdoc />
    public int DeviceId => 0;

    /// <inheritdoc />
    public string Name => "Mouse";

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <summary>Where the pointer is within the window, in logical points.</summary>
    public Vector2 Position { get; internal set; }

    /// <summary>How far it moved this frame, in logical points.</summary>
    public Vector2 Delta { get; internal set; }

    /// <summary>How far the wheel turned this frame, in notches.</summary>
    public Vector2 Scroll { get; internal set; }

    /// <summary>Whether a button is held.</summary>
    /// <param name="button">Which button.</param>
    public bool IsDown(MouseControl button) => (uint)button < (uint)down.Length && down[(int)button];

    /// <inheritdoc />
    public float ReadValue(InputControl control) => (MouseControl)control.Code switch {
        MouseControl.DeltaX => Delta.X,
        MouseControl.DeltaY => Delta.Y,
        MouseControl.ScrollX => Scroll.X,
        MouseControl.ScrollY => Scroll.Y,
        MouseControl.PositionX => Position.X,
        MouseControl.PositionY => Position.Y,
        var button => IsDown(button) ? 1f : 0f
    };

    internal bool Set(MouseControl button, bool pressed) {
        if ((uint)button >= (uint)down.Length || down[(int)button] == pressed) {
            return false;
        }

        down[(int)button] = pressed;
        return true;
    }

    internal void Clear() {
        Array.Clear(down);
        Delta = Vector2.Zero;
        Scroll = Vector2.Zero;
    }

    internal void CollectActuated(List<InputControl> into) {
        for (var code = 1; code < down.Length; code++) {
            if (down[code]) {
                into.Add(InputControl.Mouse((MouseControl)code));
            }
        }
    }
}

/// <summary>A touchscreen, as up to ten fingers.</summary>
public sealed class TouchDevice : IInputDevice {
    /// <summary>How many fingers are tracked, matching <c>TouchTracker.MaximumTouches</c>.</summary>
    public const int MaximumTouches = 10;

    readonly Finger[] fingers = new Finger[MaximumTouches];

    /// <inheritdoc />
    public InputDeviceKind Kind => InputDeviceKind.Touch;

    /// <inheritdoc />
    public int DeviceId => 0;

    /// <inheritdoc />
    public string Name => "Touchscreen";

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <summary>How many fingers are down.</summary>
    public int ActiveTouches {
        get {
            var count = 0;

            for (var index = 0; index < fingers.Length; index++) {
                if (fingers[index].Down) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Where a finger is, if it is down.</summary>
    /// <param name="finger">Which finger.</param>
    /// <param name="position">Its position in logical points.</param>
    /// <returns><see langword="false" /> if that finger is not down.</returns>
    public bool TryGetPosition(int finger, [NotNullWhen(true)] out Vector2? position) {
        if ((uint)finger < (uint)fingers.Length && fingers[finger].Down) {
            position = fingers[finger].Position;
            return true;
        }

        position = null;
        return false;
    }

    /// <inheritdoc />
    public float ReadValue(InputControl control) {
        if ((uint)control.Index >= (uint)fingers.Length) {
            return 0f;
        }

        ref var finger = ref fingers[control.Index];

        return (TouchControl)control.Code switch {
            TouchControl.Press => finger.Down ? finger.Pressure : 0f,
            TouchControl.DeltaX => finger.Delta.X,
            TouchControl.DeltaY => finger.Delta.Y,
            TouchControl.PositionX => finger.Position.X,
            TouchControl.PositionY => finger.Position.Y,
            _ => 0f
        };
    }

    internal bool Submit(int finger, TouchPhase phase, Vector2 position, Vector2 delta, float pressure) {
        if ((uint)finger >= (uint)fingers.Length) {
            return false;
        }

        fingers[finger] = phase == TouchPhase.Ended
            ? new(false, position, delta, 0f)
            : new(true, position, delta, pressure);

        return true;
    }

    internal void Clear() => Array.Clear(fingers);

    internal void ClearDeltas() {
        for (var index = 0; index < fingers.Length; index++) {
            fingers[index] = fingers[index] with { Delta = Vector2.Zero };
        }
    }

    internal void CollectActuated(List<InputControl> into) {
        for (var index = 0; index < fingers.Length; index++) {
            if (fingers[index].Down) {
                into.Add(InputControl.Touch(TouchControl.Press, (byte)index));
            }
        }
    }

    readonly record struct Finger(bool Down, Vector2 Position, Vector2 Delta, float Pressure);
}

/// <summary>One gamepad: its buttons, its axes, and the identity a settings screen shows.</summary>
/// <remarks>
///     Axes are stored raw. A dead zone is a processor on the binding, because it is a gameplay and
///     accessibility decision and applying one here would destroy information the layer above cannot
///     get back — the same reason <c>IGamepad.GetAxis</c> gives one layer down.
/// </remarks>
public sealed class GamepadDevice : IInputDevice {
    const int FirstAxis = (int)GamepadControl.LeftStickX;

    readonly bool[] down = new bool[(int)GamepadControl.Touchpad + 1];
    readonly float[] axes = new float[(int)GamepadControl.DPadY - FirstAxis + 1];

    internal GamepadDevice(int deviceId, string name) {
        DeviceId = deviceId;
        Name = name;
        IsConnected = true;
    }

    /// <inheritdoc />
    public InputDeviceKind Kind => InputDeviceKind.Gamepad;

    /// <inheritdoc />
    public int DeviceId { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsConnected { get; internal set; }

    /// <summary>Whether a button is held.</summary>
    /// <param name="button">Which button.</param>
    public bool IsDown(GamepadControl button) => (uint)button < (uint)down.Length && down[(int)button];

    /// <summary>Reads an axis, with no dead zone applied.</summary>
    /// <param name="axis">Which axis.</param>
    public float GetAxis(GamepadControl axis) {
        var index = (int)axis - FirstAxis;
        return (uint)index < (uint)axes.Length ? axes[index] : 0f;
    }

    /// <inheritdoc />
    public float ReadValue(InputControl control) {
        var code = (GamepadControl)control.Code;
        return (int)code >= FirstAxis ? GetAxis(code) : IsDown(code) ? 1f : 0f;
    }

    internal bool Set(GamepadControl button, bool pressed) {
        if ((uint)button >= (uint)down.Length || down[(int)button] == pressed) {
            return false;
        }

        down[(int)button] = pressed;
        SyncDPad();
        return true;
    }

    internal bool Set(GamepadControl axis, float value) {
        var index = (int)axis - FirstAxis;

        if ((uint)index >= (uint)axes.Length || Math.Abs(axes[index] - value) < float.Epsilon) {
            return false;
        }

        axes[index] = value;
        return true;
    }

    internal void Clear() {
        Array.Clear(down);
        Array.Clear(axes);
    }

    internal void CollectActuated(List<InputControl> into, float threshold, byte slot) {
        for (var code = 1; code < down.Length; code++) {
            if (down[code]) {
                into.Add(InputControl.Gamepad((GamepadControl)code, slot));
            }
        }

        for (var index = 0; index < axes.Length; index++) {
            var control = (GamepadControl)(FirstAxis + index);

            // The synthesised d-pad axes are left out: the four buttons they are made of are already
            // in the list, and offering both means a rebinding screen shows one press twice.
            if (control is GamepadControl.DPadX or GamepadControl.DPadY) {
                continue;
            }

            if (Math.Abs(axes[index]) >= threshold) {
                into.Add(InputControl.Gamepad(control, slot));
            }
        }
    }

    /// <summary>Keeps the two synthesised d-pad axes agreeing with the four buttons.</summary>
    void SyncDPad() {
        var horizontal = (IsDown(GamepadControl.DPadRight) ? 1f : 0f) - (IsDown(GamepadControl.DPadLeft) ? 1f : 0f);

        // Up is negative, matching the sticks and the engine's screen convention.
        var vertical = (IsDown(GamepadControl.DPadDown) ? 1f : 0f) - (IsDown(GamepadControl.DPadUp) ? 1f : 0f);

        axes[(int)GamepadControl.DPadX - FirstAxis] = horizontal;
        axes[(int)GamepadControl.DPadY - FirstAxis] = vertical;
    }
}
