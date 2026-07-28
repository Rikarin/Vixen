// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Input;

/// <summary>Every device the process can see, and the frame's worth of change to them.</summary>
/// <remarks>
///     <para>
///         <b>The bottom of this assembly, and it is fed rather than polling.</b> Nothing here knows
///         what a <c>PlatformEvent</c> is: <c>Vixen.Input</c> is a <c>Core/</c> assembly and
///         <c>Vixen.Platform</c> is above it, so the host owns the twenty-line translation and this
///         owns the state machine. That is also what makes a headless test possible — a test submits
///         a key press directly, with no platform, no window and no event loop.
///     </para>
///     <para>
///         <b>The frame is the unit.</b> <see cref="BeginFrame" /> clears the motion deltas and the
///         change flags, then the host submits everything that happened, then the actions read. A
///         value read twice in a frame gives the same answer twice, which is what a simulation
///         wants and what an event stream on its own cannot promise.
///     </para>
///     <para>
///         Belongs to the thread that pumps it, which is the thread that owns the platform.
///     </para>
/// </remarks>
public sealed class InputDeviceSet {
    readonly List<GamepadDevice> gamepads = [];
    readonly List<InputControl> actuated = [];

    /// <summary>The keyboard.</summary>
    public KeyboardDevice Keyboard { get; } = new();

    /// <summary>The pointer.</summary>
    public MouseDevice Mouse { get; } = new();

    /// <summary>The touchscreen.</summary>
    public TouchDevice Touch { get; } = new();

    /// <summary>Every connected gamepad, in the order they arrived.</summary>
    /// <remarks>
    ///     The index into this list is the "slot" a binding's <see cref="InputControl.Index" /> and an
    ///     <see cref="InputActions.GamepadSlot" /> both mean, and it is stable while a pad stays
    ///     plugged in: a disconnected pad keeps its place until <see cref="RemoveDisconnected" /> is
    ///     called, so player two's pad does not become player one's because player one's ran out of
    ///     battery mid-match.
    /// </remarks>
    public IReadOnlyList<GamepadDevice> Gamepads => gamepads;

    /// <summary>Which family of device was last used.</summary>
    /// <remarks>What control-scheme auto-switching watches. Set by any submission that actually
    /// changed something, so a stick resting at 0.0001 does not count as using a gamepad.</remarks>
    public InputDeviceKind LastUsedDevice { get; private set; }

    /// <summary>The slot of the gamepad last used, or <c>-1</c>.</summary>
    public int LastUsedGamepadSlot { get; private set; } = -1;

    /// <summary>Whether anything at all changed since the last <see cref="BeginFrame" />.</summary>
    public bool ChangedThisFrame { get; private set; }

    /// <summary>Raised when a gamepad is connected or disconnected.</summary>
    public event Action<GamepadDevice>? DeviceChanged;

    /// <summary>Starts a frame: clears the motion deltas and the change flags.</summary>
    public void BeginFrame() {
        Mouse.Delta = Vector2.Zero;
        Mouse.Scroll = Vector2.Zero;
        Touch.ClearDeltas();
        ChangedThisFrame = false;
    }

    /// <summary>Submits a key press or release.</summary>
    /// <param name="key">Which key.</param>
    /// <param name="pressed">Whether it is now down.</param>
    /// <remarks>Auto-repeat is the caller's to filter. A binding wants presses; a text field is not
    /// bound at all.</remarks>
    public void SubmitKey(InputKey key, bool pressed) {
        if (Keyboard.Set(key, pressed)) {
            Used(InputDeviceKind.Keyboard);
        }
    }

    /// <summary>Submits a mouse button press or release.</summary>
    /// <param name="button">Which button.</param>
    /// <param name="pressed">Whether it is now down.</param>
    public void SubmitMouseButton(MouseControl button, bool pressed) {
        if (Mouse.Set(button, pressed)) {
            Used(InputDeviceKind.Mouse);
        }
    }

    /// <summary>Submits pointer motion.</summary>
    /// <param name="position">Where it is now, in logical points relative to the window.</param>
    /// <param name="delta">How far it moved since the last report.</param>
    /// <remarks>
    ///     Deltas accumulate within a frame rather than replacing each other, because a frame may
    ///     drain several motion events and a camera that used only the last one would be reading a
    ///     fraction of the movement at high pointer rates.
    /// </remarks>
    public void SubmitMouseMove(Vector2 position, Vector2 delta) {
        Mouse.Position = position;
        Mouse.Delta += delta;

        if (delta != Vector2.Zero) {
            Used(InputDeviceKind.Mouse);
        }
    }

    /// <summary>Submits a wheel or trackpad scroll, in notches.</summary>
    /// <param name="delta">How far, positive up and right.</param>
    public void SubmitMouseScroll(Vector2 delta) {
        Mouse.Scroll += delta;

        if (delta != Vector2.Zero) {
            Used(InputDeviceKind.Mouse);
        }
    }

    /// <summary>Submits a touch.</summary>
    /// <param name="finger">Which finger, as the platform's touch tracker numbered it.</param>
    /// <param name="phase">What it did.</param>
    /// <param name="position">Where it is, in logical points relative to the window.</param>
    /// <param name="delta">How far it moved since the last report.</param>
    /// <param name="pressure">How hard, <c>[0, 1]</c>, or <c>1</c> where the screen cannot say.</param>
    public void SubmitTouch(
        int finger,
        TouchPhase phase,
        Vector2 position,
        Vector2 delta = default,
        float pressure = 1f
    ) {
        if (Touch.Submit(finger, phase, position, delta, pressure)) {
            Used(InputDeviceKind.Touch);
        }
    }

    /// <summary>Adds a gamepad, or marks a previously seen one connected again.</summary>
    /// <param name="deviceId">The id its events carry.</param>
    /// <param name="name">Its name.</param>
    /// <returns>The device.</returns>
    /// <remarks>
    ///     A pad that reconnects under the same id keeps its slot. Platforms do not reuse an id for a
    ///     different device, so this cannot silently hand player two's controls to a new pad.
    /// </remarks>
    public GamepadDevice ConnectGamepad(int deviceId, string name = "Gamepad") {
        if (TryGetGamepad(deviceId, out var existing)) {
            existing.IsConnected = true;
            existing.Clear();
            Raise(existing);
            return existing;
        }

        var device = new GamepadDevice(deviceId, name);
        gamepads.Add(device);
        Raise(device);
        return device;
    }

    /// <summary>Marks a gamepad gone, keeping its slot.</summary>
    /// <param name="deviceId">The id its events carried.</param>
    /// <returns><see langword="false" /> if no such pad was connected.</returns>
    public bool DisconnectGamepad(int deviceId) {
        if (!TryGetGamepad(deviceId, out var device)) {
            return false;
        }

        // Cleared as well as marked, so that a control held at the moment the cable came out does not
        // stay held. A player whose pad dies mid-sprint should stop, not keep running.
        device.IsConnected = false;
        device.Clear();
        Raise(device);
        return true;
    }

    /// <summary>Drops the gamepads that are gone, renumbering the slots of those that remain.</summary>
    /// <remarks>
    ///     Separate from <see cref="DisconnectGamepad" /> and never automatic, because renumbering is
    ///     what a game must not do in the middle of a match. A menu that offers "re-pair controllers"
    ///     calls it; the frame loop does not.
    /// </remarks>
    public void RemoveDisconnected() => gamepads.RemoveAll(static device => !device.IsConnected);

    /// <summary>Submits a gamepad button press or release.</summary>
    /// <param name="deviceId">Which pad.</param>
    /// <param name="button">Which button.</param>
    /// <param name="pressed">Whether it is now down.</param>
    public void SubmitGamepadButton(int deviceId, GamepadControl button, bool pressed) {
        if (TryGetGamepad(deviceId, out var device) && device.Set(button, pressed)) {
            Used(InputDeviceKind.Gamepad, gamepads.IndexOf(device));
        }
    }

    /// <summary>Submits a gamepad axis position, raw.</summary>
    /// <param name="deviceId">Which pad.</param>
    /// <param name="axis">Which axis.</param>
    /// <param name="value">Its position: <c>[-1, 1]</c> for a stick, <c>[0, 1]</c> for a trigger.</param>
    public void SubmitGamepadAxis(int deviceId, GamepadControl axis, float value) {
        if (!TryGetGamepad(deviceId, out var device) || !device.Set(axis, value)) {
            return;
        }

        ChangedThisFrame = true;

        // An axis only counts as *using* the pad once it is meaningfully away from rest. Otherwise a
        // drifting stick — which every worn controller has — would hold the control scheme on gamepad
        // forever and a player at the keyboard would watch their prompts flicker.
        if (Math.Abs(value) >= DriftThreshold) {
            LastUsedDevice = InputDeviceKind.Gamepad;
            LastUsedGamepadSlot = gamepads.IndexOf(device);
        }
    }

    /// <summary>Finds a gamepad by the id its events carry.</summary>
    /// <param name="deviceId">The id.</param>
    /// <param name="device">The pad.</param>
    /// <returns><see langword="false" /> if there is no such pad.</returns>
    public bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out GamepadDevice? device) {
        for (var index = 0; index < gamepads.Count; index++) {
            if (gamepads[index].DeviceId == deviceId) {
                device = gamepads[index];
                return true;
            }
        }

        device = null;
        return false;
    }

    /// <summary>Reads one control, for the gamepad slot the caller is paired to.</summary>
    /// <param name="control">Which control.</param>
    /// <param name="gamepadSlot">
    ///     Which pad a gamepad control with no explicit index reads. <c>0</c> is the first.
    /// </param>
    /// <returns>Its value, or <c>0</c> if nothing supplies it.</returns>
    public float ReadValue(InputControl control, int gamepadSlot = 0) {
        switch (control.Device) {
            case InputDeviceKind.Keyboard:
                return Keyboard.ReadValue(control);

            case InputDeviceKind.Mouse:
                return Mouse.ReadValue(control);

            case InputDeviceKind.Touch:
                return Touch.ReadValue(control);

            case InputDeviceKind.Gamepad: {
                // Index 0 means "the reading player's pad" and anything else names a slot outright,
                // which is what lets one .vxinput serve both players of a couch game and one binding
                // still say "always the second pad" when a game genuinely means that.
                var slot = control.Index == 0 ? gamepadSlot : control.Index - 1;
                return (uint)slot < (uint)gamepads.Count ? gamepads[slot].ReadValue(control) : 0f;
            }

            default:
                return 0f;
        }
    }

    /// <summary>Releases everything held, without reporting a release for any of it.</summary>
    /// <remarks>
    ///     What a host calls on focus loss. The platform does not send key-ups for keys held when a
    ///     window loses focus, so an input layer that did not do this would have the player return to
    ///     a game that believes <c>W</c> is still down — which is the single most common bug in this
    ///     part of an engine.
    /// </remarks>
    public void ResetHeldState() {
        Keyboard.Clear();
        Mouse.Clear();
        Touch.Clear();

        for (var index = 0; index < gamepads.Count; index++) {
            gamepads[index].Clear();
        }

        ChangedThisFrame = true;
    }

    /// <summary>Every control that is actuated right now.</summary>
    /// <param name="threshold">How far an analogue control must be from rest to count.</param>
    /// <returns>
    ///     The controls, reused between calls — copy what you keep. Motion deltas and positions are
    ///     left out: a rebinding screen that offered <c>&lt;Mouse&gt;/position/x</c> because the
    ///     player moved the pointer towards the button would be unusable.
    /// </returns>
    public IReadOnlyList<InputControl> Actuated(float threshold = 0.5f) {
        actuated.Clear();
        Keyboard.CollectActuated(actuated);
        Mouse.CollectActuated(actuated);
        Touch.CollectActuated(actuated);

        for (var index = 0; index < gamepads.Count; index++) {
            if (gamepads[index].IsConnected) {
                gamepads[index].CollectActuated(actuated, threshold, (byte)(index + 1));
            }
        }

        return actuated;
    }

    /// <summary>Whether a family of device is present.</summary>
    /// <param name="kind">Which family.</param>
    /// <remarks>
    ///     The keyboard, mouse and touchscreen are reported present unconditionally. A desktop
    ///     platform cannot tell whether a keyboard is plugged in, and a control scheme that refused
    ///     to activate because it could not prove one exists would leave a player with no controls
    ///     at all — the failure that matters here is one-sided.
    /// </remarks>
    public bool Has(InputDeviceKind kind) => kind switch {
        InputDeviceKind.Keyboard or InputDeviceKind.Mouse or InputDeviceKind.Touch => true,
        InputDeviceKind.Gamepad => gamepads.Exists(static device => device.IsConnected),
        _ => false
    };

    /// <summary>How far a stick must be from centre before it counts as having been used.</summary>
    /// <remarks>
    ///     Larger than any dead zone a game would set, because this decides "did the player pick the
    ///     gamepad up", not "how should the character move".
    /// </remarks>
    const float DriftThreshold = 0.25f;

    void Used(InputDeviceKind kind, int slot = -1) {
        ChangedThisFrame = true;
        LastUsedDevice = kind;

        if (kind == InputDeviceKind.Gamepad) {
            LastUsedGamepadSlot = slot;
        }
    }

    void Raise(GamepadDevice device) {
        ChangedThisFrame = true;
        DeviceChanged?.Invoke(device);
    }
}
