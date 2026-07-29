// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Web;

/// <summary>Polled keyboard, pointer and gamepad state.</summary>
/// <remarks>
///     <para>
///         Key and button state is tracked here from the same events the application sees, because
///         nothing else can answer it: the DOM has no "is this key down" query, only
///         <c>KeyboardEvent.getModifierState</c> for the modifiers. Reconstructing it from events is
///         therefore the only option, and the one thing that makes the reconstruction wrong — a key
///         released while the page did not have focus — is handled by clearing everything on
///         <see cref="PlatformEventKind.WindowFocusLost" />. Without that a player who alt-tabs
///         mid-stride comes back to a page that believes <c>W</c> is still down.
///     </para>
///     <para>
///         Gamepads are polled rather than evented, because the Gamepad API is: there is no way to
///         be told a button changed, only <c>navigator.getGamepads()</c> returning a snapshot. The
///         snapshot crosses once per pump and the diff that turns it into
///         <see cref="PlatformEventKind.GamepadButtonDown" /> and friends happens here, in C#, where
///         it can be tested without a browser and without a physical pad.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebInput : IInputSource {
    /// <summary>How many pads to poll. The API indexes them densely from zero and four is what
    /// every console-shaped game asks for.</summary>
    internal const int MaxGamepads = 4;

    // Keyed by physical position, which is what Key is. A HashSet rather than a bitmap because Key
    // is sparse — it runs to 512 for the back button — and a frame never holds more than a handful.
    readonly HashSet<Key> heldKeys = [];
    readonly HashSet<MouseButton> heldButtons = [];
    readonly WebGamepad?[] gamepads = new WebGamepad?[MaxGamepads];
    readonly List<IGamepad> connected = [];
    readonly double[] snapshot;
    readonly int stride;

    internal WebInput() {
        stride = WebInterop.GamepadStride();
        snapshot = new double[stride * MaxGamepads];
    }

    /// <inheritdoc />
    public IReadOnlyList<IGamepad> Gamepads => connected;

    /// <inheritdoc />
    /// <remarks>
    ///     Latched from the last event that carried modifiers, which is what the browser gives: a
    ///     DOM event reports what was held when it happened, and there is no way to ask between
    ///     events.
    /// </remarks>
    public KeyModifiers Modifiers { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Canvas-relative rather than desktop-relative, because a page is not told where its window
    ///     is and therefore cannot convert. The interface asks for desktop coordinates so that a
    ///     pointer outside every window still has a position; here there is one surface and the
    ///     distinction has nowhere to land.
    /// </remarks>
    public Vector2 PointerPosition { get; private set; }

    /// <inheritdoc />
    public bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out IGamepad? gamepad) {
        if (deviceId >= 0 && deviceId < gamepads.Length && gamepads[deviceId] is { IsConnected: true } found) {
            gamepad = found;
            return true;
        }

        gamepad = null;
        return false;
    }

    /// <inheritdoc />
    public bool IsKeyDown(Key key) => heldKeys.Contains(key);

    /// <inheritdoc />
    public bool IsMouseButtonDown(MouseButton button) => heldButtons.Contains(button);

    /// <summary>Folds one drained event into the polled state.</summary>
    internal void Observe(in PlatformEvent platformEvent) {
        switch (platformEvent.Kind) {
            case PlatformEventKind.KeyDown:
                Modifiers = platformEvent.Modifiers;
                heldKeys.Add(platformEvent.Key);
                break;

            case PlatformEventKind.KeyUp:
                Modifiers = platformEvent.Modifiers;
                heldKeys.Remove(platformEvent.Key);
                break;

            case PlatformEventKind.MouseButtonDown:
                Modifiers = platformEvent.Modifiers;
                PointerPosition = platformEvent.Position;
                heldButtons.Add(platformEvent.MouseButton);
                break;

            case PlatformEventKind.MouseButtonUp:
                Modifiers = platformEvent.Modifiers;
                PointerPosition = platformEvent.Position;
                heldButtons.Remove(platformEvent.MouseButton);
                break;

            case PlatformEventKind.MouseMoved:
                Modifiers = platformEvent.Modifiers;
                PointerPosition = platformEvent.Position;
                break;

            case PlatformEventKind.WindowFocusLost:
                // Everything, not just the modifiers. A key released while another tab had focus
                // never produces a keyup here, and the alternative to clearing is a key that is
                // held forever.
                Modifiers = KeyModifiers.None;
                heldKeys.Clear();
                heldButtons.Clear();
                break;

            default:
                break;
        }
    }

    /// <summary>Reads the pads and posts whatever changed.</summary>
    /// <param name="events">Where to post.</param>
    /// <param name="timestamp">The pump's timestamp; the API carries no per-change time.</param>
    internal void PollGamepads(PlatformEventBuffer events, long timestamp) {
        var count = WebInterop.PollGamepads(snapshot);

        for (var record = 0; record < count; record++) {
            var at = record * stride;
            var index = (int)snapshot[at];

            if (index < 0 || index >= gamepads.Length) {
                continue;
            }

            var isConnected = snapshot[at + 1] != 0;
            var existing = gamepads[index];

            if (!isConnected) {
                if (existing is { IsConnected: true }) {
                    existing.Disconnect();
                    connected.Remove(existing);
                    events.Post(
                        PlatformEvent.GamepadConnection(PlatformEventKind.GamepadDisconnected, timestamp, index)
                    );
                }

                continue;
            }

            if (existing is not { IsConnected: true }) {
                // A fresh object rather than a revived one: the API reuses slot indices for a
                // different physical pad, and IGamepad.Name and Kind are read at connection.
                existing = new(index, WebInterop.GamepadName(index));
                gamepads[index] = existing;
                connected.Add(existing);
                events.Post(PlatformEvent.GamepadConnection(PlatformEventKind.GamepadConnected, timestamp, index));
            }

            existing.Update(snapshot.AsSpan(at, stride), events, timestamp);
        }
    }

    /// <summary>Drops every pad, because the page is going away.</summary>
    internal void Clear() {
        heldKeys.Clear();
        heldButtons.Clear();
        Modifiers = KeyModifiers.None;
        connected.Clear();
        Array.Clear(gamepads);
    }
}

/// <summary>One pad, as the Gamepad API's standard mapping describes it.</summary>
/// <remarks>
///     <para>
///         <b>The standard mapping is a real specification, not a guess.</b> A browser that reports
///         <c>mapping: "standard"</c> promises buttons 0–16 and axes 0–3 in a fixed order that
///         matches an Xbox pad's layout, which is the same order
///         <see cref="GamepadButton" /> and <see cref="GamepadAxis" /> use. A pad the browser could
///         not identify reports an empty mapping, and its indices mean nothing — that one is
///         reported as connected with <see cref="GamepadKind.Unknown" /> and its raw axes, because
///         a remapping screen needs to see the buttons in order to let the user bind them.
///     </para>
///     <para>
///         Triggers are buttons 6 and 7 <em>and</em> analogue: the API gives each button a
///         <c>value</c> as well as <c>pressed</c>, which is why the snapshot carries floats and why
///         <see cref="GamepadAxis.LeftTrigger" /> reads from the button array.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebGamepad(int index, string name) : IGamepad, IHaptics {
    const int AxisCount = 8;
    const int ButtonCount = 24;
    const int HeaderLength = 4;

    readonly float[] axes = new float[AxisCount];
    readonly float[] buttons = new float[ButtonCount];

    /// <summary>Where a button stops being off and starts being on.</summary>
    /// <remarks>
    ///     Half. The API reports <c>pressed</c> as well as <c>value</c>, and they disagree on
    ///     triggers — <c>pressed</c> is the browser's own threshold and differs between them. One
    ///     threshold, applied here, is the same on every browser.
    /// </remarks>
    const float PressThreshold = 0.5f;

    /// <inheritdoc />
    public int DeviceId => index;

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    /// <remarks>
    ///     From the USB vendor id in the pad's <c>id</c> string, which Chromium and Firefox both put
    ///     there for a standard-mapped pad and Safari does not. Absent, the answer is
    ///     <see cref="GamepadKind.Unknown" />, whose documentation says to draw the positional names
    ///     — which is the right outcome for a pad nobody recognised, and the reason nothing in the
    ///     input path branches on this value.
    /// </remarks>
    public GamepadKind Kind { get; } = KindOf(name);

    /// <inheritdoc />
    /// <remarks>The slot index. The browser assigns no player number, and the slot is the only
    /// stable ordering there is.</remarks>
    public int PlayerIndex => index;

    /// <inheritdoc />
    public bool IsConnected { get; private set; } = true;

    /// <inheritdoc />
    public IHaptics? Haptics => WebInterop.HasRumble(index) ? this : null;

    /// <inheritdoc />
    public bool SupportsRumble => WebInterop.HasRumble(index);

    /// <inheritdoc />
    /// <remarks>Never. <c>dual-rumble</c> is the only effect the Gamepad Extensions define; a
    /// page is not given the trigger motors even on a pad that has them.</remarks>
    public bool SupportsTriggerRumble => false;

    /// <inheritdoc />
    /// <remarks>
    ///     Raw, as the interface requires — no dead zone has been applied. The sign is passed
    ///     through: the Gamepad API reports up as negative, and so does
    ///     <see cref="GamepadAxis.LeftStickY" />, so there is nothing to convert and inverting here
    ///     would be inverting twice.
    /// </remarks>
    public float GetAxis(GamepadAxis axis) => axis switch {
        GamepadAxis.LeftStickX => axes[0],
        GamepadAxis.LeftStickY => axes[1],
        GamepadAxis.RightStickX => axes[2],
        GamepadAxis.RightStickY => axes[3],

        // Triggers are buttons 6 and 7 in the standard mapping, and analogue: every button carries
        // a value as well as a pressed flag, which is where a trigger's travel lives.
        GamepadAxis.LeftTrigger => buttons[6],
        GamepadAxis.RightTrigger => buttons[7],
        _ => 0f
    };

    /// <inheritdoc />
    public bool IsButtonDown(GamepadButton button) {
        var slot = SlotOf(button);
        return slot >= 0 && buttons[slot] >= PressThreshold;
    }

    /// <inheritdoc />
    public void Rumble(float lowFrequency, float highFrequency, TimeSpan duration) =>
        WebInterop.Rumble(
            index,
            Math.Clamp(highFrequency, 0f, 1f),
            Math.Clamp(lowFrequency, 0f, 1f),
            Math.Max(0, duration.TotalMilliseconds)
        );

    /// <inheritdoc />
    /// <remarks>Nothing. See <see cref="SupportsTriggerRumble" />.</remarks>
    public void RumbleTriggers(float left, float right, TimeSpan duration) { }

    /// <inheritdoc />
    public void StopRumble() => WebInterop.StopRumble(index);

    internal void Disconnect() => IsConnected = false;

    /// <summary>Whether the browser recognised the pad and put its buttons in the standard order.</summary>
    /// <remarks>
    ///     False means the indices mean nothing in particular — a generic HID pad the browser could
    ///     not identify. Its axes and buttons are still reported, because a rebinding screen needs
    ///     to see them in order to let the user bind them, and a pad reported as absent is a pad
    ///     that cannot be configured at all.
    /// </remarks>
    public bool HasStandardMapping { get; private set; }

    /// <summary>Folds one snapshot in and posts what changed.</summary>
    internal void Update(ReadOnlySpan<double> record, PlatformEventBuffer events, long timestamp) {
        HasStandardMapping = record[2] != 0;
        var reported = Math.Min(ButtonCount, (int)record[3]);

        var axisAt = HeaderLength;
        var buttonAt = HeaderLength + AxisCount;

        for (var axis = 0; axis < AxisCount; axis++) {
            var value = (float)record[axisAt + axis];

            if (Math.Abs(value - axes[axis]) < AxisEpsilon) {
                continue;
            }

            axes[axis] = value;

            if (AxisOf(axis) is { } named) {
                events.Post(PlatformEvent.GamepadAxisMoved(timestamp, index, named, GetAxis(named)));
            }
        }

        for (var button = 0; button < reported; button++) {
            var value = (float)record[buttonAt + button];
            var wasDown = buttons[button] >= PressThreshold;
            var isDown = value >= PressThreshold;
            buttons[button] = value;

            // Triggers are axes as well as buttons, and the axis event is what an analogue reader
            // wants. Posted from here because the button array is where the value lives.
            if (button is 6 or 7) {
                var trigger = button == 6 ? GamepadAxis.LeftTrigger : GamepadAxis.RightTrigger;
                events.Post(PlatformEvent.GamepadAxisMoved(timestamp, index, trigger, value));
            }

            if (wasDown == isDown || ButtonOf(button) is not { } named) {
                continue;
            }

            events.Post(
                PlatformEvent.GamepadButtonChanged(
                    isDown ? PlatformEventKind.GamepadButtonDown : PlatformEventKind.GamepadButtonUp,
                    timestamp,
                    index,
                    named
                )
            );
        }
    }

    /// <summary>How far a stick must move before it is worth an event.</summary>
    /// <remarks>
    ///     Not a dead zone — that is a gameplay decision <c>Vixen.Input</c> owns, and applying one
    ///     here would destroy information the layer above cannot get back. This is the resolution
    ///     below which a resting stick's noise would post an event every frame forever.
    /// </remarks>
    const float AxisEpsilon = 1f / 512f;

    /// <summary>The standard mapping's axis order, which matches <see cref="GamepadAxis" />'s.</summary>
    static GamepadAxis? AxisOf(int slot) => slot switch {
        0 => GamepadAxis.LeftStickX,
        1 => GamepadAxis.LeftStickY,
        2 => GamepadAxis.RightStickX,
        3 => GamepadAxis.RightStickY,
        _ => null
    };

    /// <summary>The standard mapping's button order.</summary>
    /// <remarks>
    ///     <para>
    ///         6 and 7 are the triggers, which <see cref="GamepadButton" /> does not have — a
    ///         trigger is an axis here, and it is reported as one from
    ///         <see cref="Update" />. They map to nothing rather than to the shoulder buttons,
    ///         which are 4 and 5 and are genuinely different controls.
    ///     </para>
    ///     <para>
    ///         17 is Chromium's extension for a DualSense's touchpad click. Firefox and Safari do
    ///         not report it, so a game that binds <see cref="GamepadButton.Touchpad" /> works in
    ///         one browser and silently does not in the others — which is what
    ///         <see cref="IGamepad.IsButtonDown" /> returning <see langword="false" /> already
    ///         means, and is better than not mapping it anywhere.
    ///     </para>
    /// </remarks>
    static GamepadButton? ButtonOf(int slot) => slot switch {
        0 => GamepadButton.South,
        1 => GamepadButton.East,
        2 => GamepadButton.West,
        3 => GamepadButton.North,
        4 => GamepadButton.LeftShoulder,
        5 => GamepadButton.RightShoulder,
        8 => GamepadButton.Back,
        9 => GamepadButton.Start,
        10 => GamepadButton.LeftStick,
        11 => GamepadButton.RightStick,
        12 => GamepadButton.DPadUp,
        13 => GamepadButton.DPadDown,
        14 => GamepadButton.DPadLeft,
        15 => GamepadButton.DPadRight,
        16 => GamepadButton.Guide,
        17 => GamepadButton.Touchpad,
        _ => null
    };

    static int SlotOf(GamepadButton button) => button switch {
        GamepadButton.South => 0,
        GamepadButton.East => 1,
        GamepadButton.West => 2,
        GamepadButton.North => 3,
        GamepadButton.LeftShoulder => 4,
        GamepadButton.RightShoulder => 5,
        GamepadButton.Back => 8,
        GamepadButton.Start => 9,
        GamepadButton.LeftStick => 10,
        GamepadButton.RightStick => 11,
        GamepadButton.DPadUp => 12,
        GamepadButton.DPadDown => 13,
        GamepadButton.DPadLeft => 14,
        GamepadButton.DPadRight => 15,
        GamepadButton.Guide => 16,
        GamepadButton.Touchpad => 17,
        _ => -1
    };

    /// <summary>Which family a pad belongs to, from the USB vendor id in its name.</summary>
    /// <remarks>
    ///     Chromium and Firefox both put <c>Vendor: xxxx Product: xxxx</c> in the <c>id</c> string
    ///     for a standard-mapped pad; Safari does not, and reports a marketing name instead, which
    ///     is why the name is also matched. Only used to pick a glyph set — a wrong answer draws an
    ///     <c>A</c> where a <c>✕</c> belonged and breaks nothing.
    /// </remarks>
    static GamepadKind KindOf(string id) {
        if (id.Contains("045e", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) {
            return GamepadKind.Xbox;
        }

        if (id.Contains("054c", StringComparison.OrdinalIgnoreCase)
            || id.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
            || id.Contains("DualShock", StringComparison.OrdinalIgnoreCase)) {
            return GamepadKind.PlayStation;
        }

        if (id.Contains("057e", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Nintendo", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Joy-Con", StringComparison.OrdinalIgnoreCase)) {
            return GamepadKind.Nintendo;
        }

        return GamepadKind.Unknown;
    }
}
