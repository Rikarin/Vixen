// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>Turns a <see cref="PlatformEvent" /> into a call on an <see cref="InputDeviceSet" />.</summary>
/// <remarks>
///     <para>
///         <b>The whole of the seam between the two halves of the input stack</b>, and it is
///         deliberately this small. <c>Vixen.Input</c> is a <c>Core/</c> assembly and
///         <c>Vixen.Platform</c> sits above it, so the action system cannot reference the event type
///         — a rule <c>CheckArchitecture</c> enforces and which exists for <c>Vixen.Ui</c>, which
///         consumes the action system and must stay usable with no platform backend at all.
///     </para>
///     <para>
///         So the translation lives here, in the host that already pumps the queue. There is nothing
///         clever in it because there is nothing to be clever about: the key codes are the same USB
///         HID values on both sides and the cast is the identity, which <c>InputKeyTests</c> holds
///         member by member. A different host — an editor, a dedicated server replaying a log — either
///         calls this or writes its own twenty lines.
///     </para>
/// </remarks>
public static class PlatformInput {
    /// <summary>Offers one platform event to the device set.</summary>
    /// <param name="devices">The devices.</param>
    /// <param name="platformEvent">What happened.</param>
    /// <param name="source">
    ///     The raw device list, for a connected gamepad's name, or <see langword="null" />.
    /// </param>
    /// <returns><see langword="true" /> if the event was an input event and was consumed.</returns>
    public static bool Submit(this InputDeviceSet devices, in PlatformEvent platformEvent, IInputSource? source = null) {
        ArgumentNullException.ThrowIfNull(devices);

        switch (platformEvent.Kind) {
            case PlatformEventKind.KeyDown:
            case PlatformEventKind.KeyUp:
                // Auto-repeat is not filtered, and does not need to be: a repeat re-asserts a state
                // the device set already holds, which it recognises as no change at all.
                devices.SubmitKey((InputKey)platformEvent.Key, platformEvent.Kind == PlatformEventKind.KeyDown);
                return true;

            case PlatformEventKind.MouseMoved:
                devices.SubmitMouseMove(platformEvent.Position, platformEvent.Delta);
                return true;

            case PlatformEventKind.MouseButtonDown:
            case PlatformEventKind.MouseButtonUp:
                devices.SubmitMouseButton(
                    (MouseControl)platformEvent.MouseButton,
                    platformEvent.Kind == PlatformEventKind.MouseButtonDown
                );

                return true;

            case PlatformEventKind.MouseWheel:
                devices.SubmitMouseScroll(platformEvent.Delta);
                return true;

            case PlatformEventKind.TouchDown:
                devices.SubmitTouch(
                    platformEvent.DeviceId,
                    TouchPhase.Began,
                    platformEvent.Position,
                    pressure: platformEvent.Value
                );

                return true;

            case PlatformEventKind.TouchMoved:
                devices.SubmitTouch(
                    platformEvent.DeviceId,
                    TouchPhase.Moved,
                    platformEvent.Position,
                    platformEvent.Delta,
                    platformEvent.Value
                );

                return true;

            case PlatformEventKind.TouchUp:
                devices.SubmitTouch(platformEvent.DeviceId, TouchPhase.Ended, platformEvent.Position);
                return true;

            case PlatformEventKind.GamepadConnected:
                devices.ConnectGamepad(platformEvent.DeviceId, Name(source, platformEvent.DeviceId));
                return true;

            case PlatformEventKind.GamepadDisconnected:
                devices.DisconnectGamepad(platformEvent.DeviceId);
                return true;

            case PlatformEventKind.GamepadButtonDown:
            case PlatformEventKind.GamepadButtonUp:
                devices.SubmitGamepadButton(
                    platformEvent.DeviceId,
                    (GamepadControl)platformEvent.GamepadButton,
                    platformEvent.Kind == PlatformEventKind.GamepadButtonDown
                );

                return true;

            case PlatformEventKind.GamepadAxisMoved:
                devices.SubmitGamepadAxis(platformEvent.DeviceId, Axis(platformEvent.GamepadAxis), platformEvent.Value);
                return true;

            case PlatformEventKind.WindowFocusLost:
            case PlatformEventKind.Suspending:
                // The platform does not send key-ups for keys held when focus is lost, so without
                // this the player comes back to a game that believes W is still down. Doc 10 says as
                // much on PlatformEventKind.WindowFocusLost, and this is where it is cashed in.
                devices.ResetHeldState();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Adds every gamepad the platform already knows about.</summary>
    /// <param name="devices">The devices.</param>
    /// <param name="source">The raw device list.</param>
    /// <remarks>
    ///     Called once at start-up. A pad plugged in before the process started produced no
    ///     <see cref="PlatformEventKind.GamepadConnected" /> for anyone to hear, so an input layer
    ///     built only from the event stream sees a controller that does nothing until it is unplugged
    ///     and plugged back in.
    /// </remarks>
    public static void SyncGamepads(this InputDeviceSet devices, IInputSource source) {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(source);

        var gamepads = source.Gamepads;

        for (var index = 0; index < gamepads.Count; index++) {
            devices.ConnectGamepad(gamepads[index].DeviceId, gamepads[index].Name);
        }
    }

    /// <summary>
    ///     The two axis enumerations are ordered the same and numbered differently, so this is
    ///     arithmetic rather than a table.
    /// </summary>
    /// <remarks>
    ///     <c>Vixen.Platform.GamepadAxis</c> counts from one and <c>GamepadControl</c> puts its axes
    ///     above its buttons in one enum, which is what lets a control path name either without
    ///     knowing which half to look in. <c>InputKeyTests</c> holds the two orders together.
    /// </remarks>
    static GamepadControl Axis(GamepadAxis axis) =>
        axis == GamepadAxis.None
            ? GamepadControl.None
            : GamepadControl.LeftStickX + (axis - GamepadAxis.LeftStickX);

    static string Name(IInputSource? source, int deviceId) =>
        source is not null && source.TryGetGamepad(deviceId, out var gamepad) ? gamepad.Name : "Gamepad";
}
