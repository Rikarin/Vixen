// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>Force feedback on one device.</summary>
/// <remarks>
///     Every method is best-effort and silently does nothing on a device that cannot do it, because
///     the alternative is every caller writing the same capability check before every rumble. Ask
///     <see cref="SupportsRumble" /> when the answer changes what the game does — offering the
///     setting at all, say — and not before each call.
/// </remarks>
public interface IHaptics {
    /// <summary>Whether the device has rumble motors.</summary>
    bool SupportsRumble { get; }

    /// <summary>Whether the device has motors in the triggers, as Xbox One and later pads do.</summary>
    bool SupportsTriggerRumble { get; }

    /// <summary>Runs the main motors.</summary>
    /// <param name="lowFrequency">The heavy motor, <c>[0, 1]</c>.</param>
    /// <param name="highFrequency">The light motor, <c>[0, 1]</c>.</param>
    /// <param name="duration">How long to run for. A later call replaces an earlier one.</param>
    void Rumble(float lowFrequency, float highFrequency, TimeSpan duration);

    /// <summary>Runs the trigger motors.</summary>
    /// <param name="left">The left trigger's motor, <c>[0, 1]</c>.</param>
    /// <param name="right">The right trigger's motor, <c>[0, 1]</c>.</param>
    /// <param name="duration">How long to run for.</param>
    void RumbleTriggers(float left, float right, TimeSpan duration);

    /// <summary>Stops everything immediately.</summary>
    /// <remarks>
    ///     Worth calling on focus loss and on shutdown. A pad left rumbling because the process
    ///     exited keeps rumbling until it is unplugged, which users remember.
    /// </remarks>
    void StopRumble();
}

/// <summary>One connected gamepad.</summary>
/// <remarks>
///     State here is a snapshot of what the last pump saw, so reading an axis twice in a frame gives
///     the same answer twice — which is what a simulation wants. Changes also arrive as events, and
///     the two are consistent because they come from the same pump.
/// </remarks>
public interface IGamepad {
    /// <summary>The id this device appears under in <see cref="PlatformEvent.DeviceId" />.</summary>
    /// <remarks>Not reused after the device is unplugged, so a stale id never silently refers to a
    /// different pad.</remarks>
    int DeviceId { get; }

    /// <summary>The device's name, for a settings screen.</summary>
    string Name { get; }

    /// <summary>Which family it belongs to, for drawing the right button glyphs.</summary>
    GamepadKind Kind { get; }

    /// <summary>Which player slot the OS has assigned, or <c>-1</c> where there is no such
    /// concept.</summary>
    int PlayerIndex { get; }

    /// <summary>Whether it is still plugged in.</summary>
    bool IsConnected { get; }

    /// <summary>Force feedback, or <see langword="null" /> if it has none.</summary>
    IHaptics? Haptics { get; }

    /// <summary>Reads an axis: <c>[-1, 1]</c> for sticks, <c>[0, 1]</c> for triggers.</summary>
    /// <param name="axis">Which axis.</param>
    /// <returns>Its position, raw — no dead zone has been applied.</returns>
    /// <remarks>
    ///     Deliberately raw. A dead zone is a gameplay decision (radial for a camera, axial for a
    ///     menu, adjustable for accessibility) and applying one here would destroy information the
    ///     layer above cannot get back. <c>Vixen.Input</c> owns that choice.
    /// </remarks>
    float GetAxis(GamepadAxis axis);

    /// <summary>Whether a button is held.</summary>
    /// <param name="button">Which button.</param>
    bool IsButtonDown(GamepadButton button);
}

/// <summary>Raw input devices and the state of the keyboard and pointer.</summary>
/// <remarks>
///     <para>
///         This is the bottom of the input stack, not the top. It reports what the hardware is
///         doing; the action maps, rebinding, dead zones and composite bindings are
///         <c>Vixen.Input</c>'s job in Phase 8, and it is built on top of this and the event stream
///         rather than beside them.
///     </para>
///     <para>
///         Key and button state is here as well as in events because the platform is the only thing
///         that knows the truth after focus is lost. Reconstructing "is W down" from events means
///         reconstructing it wrongly the first time the user alt-tabs mid-stride.
///     </para>
/// </remarks>
public interface IInputSource {
    /// <summary>Every connected gamepad.</summary>
    IReadOnlyList<IGamepad> Gamepads { get; }

    /// <summary>The modifier keys held now.</summary>
    KeyModifiers Modifiers { get; }

    /// <summary>Where the pointer is, in desktop coordinates and logical points.</summary>
    /// <remarks>Desktop coordinates rather than window-relative, because the pointer may not be over
    /// any of our windows. <see cref="PlatformEvent.Position" /> is the window-relative one.</remarks>
    Vector2 PointerPosition { get; }

    /// <summary>Finds a gamepad by the id its events carry.</summary>
    /// <param name="deviceId">The id from <see cref="PlatformEvent.DeviceId" />.</param>
    /// <param name="gamepad">The device.</param>
    /// <returns><see langword="false" /> if no such device is connected.</returns>
    bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out IGamepad? gamepad);

    /// <summary>Whether a key is held, by physical position.</summary>
    /// <param name="key">Which key.</param>
    bool IsKeyDown(Key key);

    /// <summary>Whether a mouse button is held.</summary>
    /// <param name="button">Which button.</param>
    bool IsMouseButtonDown(MouseButton button);
}
